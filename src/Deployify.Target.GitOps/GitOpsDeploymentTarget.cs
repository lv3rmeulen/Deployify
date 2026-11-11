using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Core.SecretBackends;
using Deployify.Target.Kubernetes;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.GitOps;

/// <summary>
/// Kubernetes, delivered through a GitOps controller. Deployify renders the Kubernetes target's
/// manifests into a folder per service in a config repository, commits and pushes them, and
/// registers each folder with the controller; the controller applies them to the cluster.
/// Everything except the controller's own objects and status is shared by Argo CD and Flux.
/// </summary>
public abstract class GitOpsDeploymentTarget : IDeploymentTarget, ISecretStoreProvider, IResourceLifecycle
{
    private readonly IPublisher _publisher;
    private readonly KubernetesManifestGenerator _generator = new();

    protected ILogger Logger { get; }

    protected IProcessRunner Processes { get; }

    /// <summary>
    /// The Kubernetes target does the parts that are the same with or without GitOps: secret
    /// backends, the out-of-band Secrets and explain.
    /// </summary>
    internal KubernetesDeploymentTarget Kubernetes { get; }

    /// <summary>How often <see cref="GitOpsTargetConfig.Wait"/> asks the controller; replaceable in tests.</summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long destroy waits for the controller to remove one service.</summary>
    private static readonly TimeSpan s_deleteTimeout = TimeSpan.FromMinutes(5);

    protected GitOpsDeploymentTarget(IPublisher publisher, ILoggerFactory loggers, IProcessRunner processes)
    {
        _publisher = publisher;
        Logger = loggers.CreateLogger(GetType());
        Processes = processes;
        Kubernetes = new KubernetesDeploymentTarget(publisher, loggers.CreateLogger<KubernetesDeploymentTarget>())
        {
            Processes = processes
        };
    }

    public abstract DeploymentTargetType TargetType { get; }

    public abstract string DisplayName { get; }

    // ── What differs per controller ───────────────────────────────────────────

    /// <summary>The controller's name in messages, e.g. "Argo CD".</summary>
    protected abstract string ControllerName { get; }

    /// <summary>A CRD that tells whether the controller is installed.</summary>
    protected abstract string ControllerCrd { get; }

    /// <summary>The kubectl resource of the per-service object (an Application, a Kustomization).</summary>
    protected abstract string RegistrationResource { get; }

    /// <summary>The per-service object's kind, for explain and messages.</summary>
    protected abstract string RegistrationKind { get; }

    /// <summary>Namespace the controller's objects live in.</summary>
    protected abstract string RegistrationNamespace(EnvironmentConfig config);

    /// <summary>The per-service object that points the controller at the service's folder.</summary>
    protected abstract string RenderServiceRegistration(KubernetesServiceManifest svc, EnvironmentConfig config, string repoPath);

    /// <summary>Objects shared by every service (a Flux GitRepository), or null when there are none.</summary>
    protected abstract string? RenderEnvironmentRegistration(EnvironmentConfig config);

    /// <summary>Reads the per-service object's status for <paramref name="commit"/>.</summary>
    protected abstract HealthObservation Evaluate(string? json, string commit);

    /// <summary>Asks the controller to look at the repository now rather than at its next poll. Best effort.</summary>
    protected abstract Task RefreshAsync(
        ControllerClient client, EnvironmentConfig config, IReadOnlyList<string> registrationNames, CancellationToken ct);

    // ── Configuration and tools ───────────────────────────────────────────────

    internal static GitOpsTargetConfig GetGitOpsConfig(EnvironmentConfig config)
        => Read<GitOpsTargetConfig>(config, "gitops");

    internal static T Read<T>(EnvironmentConfig config, string key) where T : new()
        => config.TargetConfig.TryGetValue(key, out var raw) && raw is JsonElement el
            ? el.Deserialize<T>(TargetConfigJsonOptions.Default) ?? new T()
            : new T();

    internal static KubernetesTargetConfig KubernetesConfig(EnvironmentConfig config) => KubernetesDeploymentTarget.GetConfig(config);

    /// <summary>
    /// The environment's folder in the repository, with forward slashes and no leading or
    /// trailing slash. Throws for a path that would leave the repository.
    /// </summary>
    internal static string RepoPath(EnvironmentConfig config)
    {
        var raw = GetGitOpsConfig(config).Path;
        var path = (string.IsNullOrWhiteSpace(raw) ? $"deployify/{config.Name ?? "default"}" : raw)
            .Trim().Replace('\\', '/').Trim('/');
        var segments = path.Split('/');
        if (path.Length == 0 || path.Contains(':') || segments.Any(s => s is "" or "." or ".."))
        {
            throw new InvalidOperationException(
                $"targetConfig.gitops.path '{raw}' must be a relative folder in the repository, such as 'clusters/prod'.");
        }

        return path;
    }

    /// <summary>Deployify's clone: the configured folder, or one per environment and repository under the temp folder.</summary>
    internal static string CheckoutDirectory(EnvironmentConfig config)
    {
        var gitops = GetGitOpsConfig(config);
        if (!string.IsNullOrWhiteSpace(gitops.CheckoutDirectory))
        {
            return Path.GetFullPath(gitops.CheckoutDirectory);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(gitops.RepoUrl)))[..8];
        var name = new string((config.Name ?? "default").Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return Path.Combine(Path.GetTempPath(), "deployify", "gitops", $"{name}-{hash}");
    }

    private static string EnvironmentDirectory(GitRunner git, string repoPath)
        => Path.Combine(git.CheckoutDirectory, repoPath.Replace('/', Path.DirectorySeparatorChar));

    private static GitOpsTargetConfig RequireRepo(EnvironmentConfig config)
    {
        var gitops = GetGitOpsConfig(config);
        return string.IsNullOrWhiteSpace(gitops.RepoUrl)
            ? throw new InvalidOperationException("targetConfig.gitops.repoUrl is not set: the GitOps targets need a config repository.")
            : gitops;
    }

    /// <summary>The name of a service's Application or Kustomization: its namespace.</summary>
    protected static string RegistrationName(EnvironmentConfig config, string service)
        => KubernetesManifestGenerator.NamespaceFor(KubernetesConfig(config), service);

    private GitRunner Git(EnvironmentConfig config) => new(CheckoutDirectory(config), Logger, Processes);

    private KubectlRunner Kubectl(EnvironmentConfig config) => new(KubernetesConfig(config), Logger, Processes);

    private ControllerClient Controller(EnvironmentConfig config) => new(KubernetesConfig(config), Logger, Processes);

    private Task<bool> GitAvailableAsync(CancellationToken ct) => GitRunner.IsAvailableAsync(Processes, ct);

    private Task<bool> KubectlAvailableAsync(CancellationToken ct) => KubectlRunner.IsAvailableAsync(Processes, ct);

    /// <summary>
    /// Why the tools apply needs aren't there, or null. kubectl is needed to register the
    /// services and to put secrets in the cluster; git always.
    /// </summary>
    private async Task<string?> MissingToolsAsync(EnvironmentConfig config, bool needKubectl, CancellationToken ct)
    {
        var missing = new List<string>();
        if (!await GitAvailableAsync(ct))
        {
            missing.Add("git");
        }

        if (needKubectl && !await KubectlAvailableAsync(ct))
        {
            missing.Add("kubectl");
        }

        return missing.Count == 0
            ? null
            : $"The {DisplayName} target needs {string.Join(" and ", missing)} on PATH.";
    }

    private static bool NeedsKubectl(EnvironmentConfig config, IReadOnlyList<KubernetesServiceManifest> services)
        => GetGitOpsConfig(config).Register || GetGitOpsConfig(config).Wait ||
           services.Any(s => (s.UsesSecretRefs && !s.UsesKeyVaultCsi) || s.RegistrySecret is not null);

    private Task<IReadOnlyList<KubernetesServiceManifest>> GenerateServicesAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct)
        => _generator.GenerateAsync(manifest, config, KubernetesConfig(config), ct);

    private async Task<IReadOnlyList<string>> WriteTreeAsync(
        IReadOnlyList<KubernetesServiceManifest> services, EnvironmentConfig config, string outputDirectory, CancellationToken ct)
    {
        RequireRepo(config);
        var repoPath = RepoPath(config);
        return await GitOpsTree.WriteAsync(
            services, svc => RenderServiceRegistration(svc, config, repoPath), RenderEnvironmentRegistration(config),
            GitOpsTree.TreeDirectory(outputDirectory), ct);
    }

    // ── Secrets ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The Kubernetes target's external backends. Inline isn't listed because it is always
    /// accepted; generate refuses it for a service with secrets, as they'd be committed.
    /// </summary>
    public IReadOnlyCollection<SecretBackendType> SupportedSecretBackends => Kubernetes.SupportedSecretBackends;

    public ISecretStore CreateSecretStore(EnvironmentConfig config, ILogger storeLogger)
        => Kubernetes.CreateSecretStore(config, storeLogger);

    // ── IDeploymentTarget ─────────────────────────────────────────────────────

    /// <summary>Checks git can read the repository and, when Deployify registers services, that the controller is installed.</summary>
    public async Task<(bool Ok, string? Error)> PingAsync(EnvironmentConfig config, CancellationToken ct = default)
    {
        var gitops = GetGitOpsConfig(config);
        if (string.IsNullOrWhiteSpace(gitops.RepoUrl))
        {
            return (false, "targetConfig.gitops.repoUrl is not set.");
        }

        if (await MissingToolsAsync(config, gitops.Register, ct) is { } reason)
        {
            return (false, reason);
        }

        var (ok, error) = await Git(config).CanReadAsync(gitops.RepoUrl, ct);
        if (!ok || !gitops.Register)
        {
            return (ok, error);
        }

        var cluster = await Kubectl(config).PingAsync(ct);
        if (!cluster.Ok)
        {
            return cluster;
        }

        try
        {
            return await Controller(config).HasCrdAsync(ControllerCrd, ct)
                ? (true, null)
                : (false, $"{ControllerName} is not installed in the cluster (no {ControllerCrd} CRD).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Writes the environment's folder as it will be committed to <c>{output}/gitops/tree</c>:
    /// a folder per service and the controller's objects under <c>_registration/</c>. Contacts nothing.
    /// </summary>
    public async Task<IReadOnlyList<string>> GenerateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => await WriteTreeAsync(await GenerateServicesAsync(manifest, config, ct), config, outputDirectory, ct);

    /// <summary>Builds every service folder with <c>kubectl kustomize</c> (offline); without kubectl, only writes them.</summary>
    public async Task<IReadOnlyList<ValidationResult>> ValidateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var services = await GenerateServicesAsync(manifest, config, ct);
        await WriteTreeAsync(services, config, outputDirectory, ct);

        var build = await KubectlAvailableAsync(ct);
        if (!build)
        {
            Logger.LogWarning("kubectl not found - skipping kustomize build.");
        }

        var controller = Controller(config);
        var tree = GitOpsTree.TreeDirectory(outputDirectory);
        var results = new List<ValidationResult>();
        foreach (var svc in services)
        {
            var folder = Path.Combine(tree, svc.ServiceName);
            results.Add(new ValidationResult
            {
                ArtifactName = folder,
                Errors = build ? await controller.BuildKustomizationAsync(folder, ct) : []
            });
        }

        return results;
    }

    public async Task<PlanResult> PlanAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => PlanResult.FromDiff(await DiffAsync(manifest, config, outputDirectory, ct));

    /// <summary>
    /// Compares the generated folders with what is committed on the branch: the desired state the
    /// controller works from, not the cluster (drift there is the controller's to report). A
    /// service without a folder is Added, one whose files differ is Changed, and a folder the
    /// manifest no longer has is Removed. When the repository can't be read, every service is Unknown.
    /// </summary>
    public async Task<IReadOnlyList<DiffEntry>> DiffAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var services = await GenerateServicesAsync(manifest, config, ct);
        await WriteTreeAsync(services, config, outputDirectory, ct);

        if (!await GitAvailableAsync(ct))
        {
            return [.. services.Select(s => Unknown(s.ServiceName, "git is not installed"))];
        }

        var gitops = GetGitOpsConfig(config);
        var git = Git(config);
        try
        {
            await git.SyncAsync(gitops.RepoUrl, gitops.Branch, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning("Could not read {Repo}: {Message}", gitops.RepoUrl, ex.Message);
            return [.. services.Select(s => Unknown(s.ServiceName, $"could not read the repository: {ex.Message}"))];
        }

        var tree = GitOpsTree.TreeDirectory(outputDirectory);
        var environment = EnvironmentDirectory(git, RepoPath(config));
        var committed = GitOpsTree.ListServices(environment);
        var entries = new List<DiffEntry>();
        foreach (var svc in services)
        {
            if (!committed.Contains(svc.ServiceName))
            {
                entries.Add(new DiffEntry { ServiceName = svc.ServiceName, Kind = DiffKind.Added });
                continue;
            }

            var changed = GitOpsTree.Compare(tree, environment, svc.ServiceName);
            entries.Add(changed.Count == 0
                ? new DiffEntry { ServiceName = svc.ServiceName, Kind = DiffKind.Unchanged }
                : new DiffEntry { ServiceName = svc.ServiceName, Kind = DiffKind.Changed, Field = "files", NewValue = string.Join(", ", changed) });
        }

        var desired = services.Select(s => s.ServiceName).ToHashSet(StringComparer.Ordinal);
        entries.AddRange(committed.Where(c => !desired.Contains(c)).Select(c => new DiffEntry { ServiceName = c, Kind = DiffKind.Removed }));
        return entries;

        static DiffEntry Unknown(string service, string reason)
            => new() { ServiceName = service, Kind = DiffKind.Unknown, Reason = reason };
    }

    /// <summary>
    /// Puts secrets in their backend, commits the folders plan wrote and pushes them, then
    /// registers each service with the controller and, when configured, waits until it has
    /// applied the commit. Services the manifest no longer has stay in the repository; prune
    /// removes them.
    /// </summary>
    public async Task<IReadOnlyList<DeploymentResult>> ApplyAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var services = await GenerateServicesAsync(manifest, config, ct);
        var tree = GitOpsTree.TreeDirectory(outputDirectory);
        if (services.Any(s => !Directory.Exists(Path.Combine(tree, s.ServiceName))))
        {
            return Fail(services, $"Generated files not found in {tree}. Run 'deployify plan' first.");
        }

        if (await MissingToolsAsync(config, NeedsKubectl(config, services), ct) is { } reason)
        {
            return Fail(services, reason);
        }

        string commit;
        var secretsChanged = new List<KubernetesServiceManifest>();
        var gitops = GetGitOpsConfig(config);
        try
        {
            RequireRepo(config);
            var repoPath = RepoPath(config);

            // Secrets first, so the pods find them when the controller starts them.
            var kubectl = Kubectl(config);
            foreach (var svc in services)
            {
                if (await Kubernetes.SyncSecretsAsync(svc, config, kubectl, ct))
                {
                    secretsChanged.Add(svc);
                }
            }

            var git = Git(config);
            await git.SyncAsync(gitops.RepoUrl, gitops.Branch, ct);
            var environment = EnvironmentDirectory(git, repoPath);
            await GitOpsTree.CopyIntoAsync(tree, environment, services.Select(s => s.ServiceName), ct);

            var created = await git.CommitAsync(repoPath, CommitMessage(config, "update", services.Select(s => s.ServiceName)),
                gitops.AuthorName, gitops.AuthorEmail, ct);
            if (created is null)
            {
                Logger.LogInformation("[git] {Path} is already up to date on {Branch}.", repoPath, gitops.Branch);
            }
            else if (gitops.Push)
            {
                await git.PushAsync(gitops.Branch, ct);
                Logger.LogInformation("[git] Pushed {Commit} to {Branch}.", ArgoCdManifests.Short(created), gitops.Branch);
            }
            else
            {
                Logger.LogWarning("[git] Committed {Commit} without pushing (gitops.push is off); {Controller} won't see it until it is pushed.",
                    ArgoCdManifests.Short(created), ControllerName);
            }

            commit = created ?? await git.HeadAsync(ct) ?? throw new InvalidOperationException("The branch has no commit.");

            if (gitops.Register)
            {
                await RegisterAsync(config, environment, services, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(services, ex.Message);
        }

        var names = services.Select(s => RegistrationName(config, s.ServiceName)).ToList();
        if (gitops.Register || gitops.Wait)
        {
            await RefreshAsync(Controller(config), config, names, ct);
        }

        var results = gitops.Wait && gitops.Push
            ? await WaitAsync(config, services, commit, ct)
            : [.. services.Select(s => Succeeded(s, commit))];

        if (gitops.Wait && !gitops.Push)
        {
            Logger.LogWarning("Not waiting for {Controller}: the commit wasn't pushed.", ControllerName);
        }

        // The controller can't see a change to an out-of-band Secret: restart the pods that read it.
        var kube = Kubectl(config);
        foreach (var svc in secretsChanged)
        {
            if (!await kube.RolloutRestartAsync(svc.ServiceName, svc.Namespace, ct))
            {
                Logger.LogWarning("Secrets of {Name} changed but its pods could not be restarted (is it deployed yet?).", svc.ServiceName);
            }
        }

        return results;

        DeploymentResult Succeeded(KubernetesServiceManifest svc, string sha)
            => new() { ServiceName = svc.ServiceName, DeploymentId = $"commit/{ArgoCdManifests.Short(sha)}", Success = true };
    }

    private static List<DeploymentResult> Fail(IEnumerable<KubernetesServiceManifest> services, string error)
        => [.. services.Select(s => new DeploymentResult { ServiceName = s.ServiceName, Success = false, Error = error })];

    private static string CommitMessage(EnvironmentConfig config, string verb, IEnumerable<string> services)
        => $"deployify: {verb} {config.Name ?? "environment"} ({string.Join(", ", services)})";

    /// <summary>Applies the shared objects, then each service's, from the committed folder.</summary>
    private async Task RegisterAsync(
        EnvironmentConfig config, string environment, IReadOnlyList<KubernetesServiceManifest> services, CancellationToken ct)
    {
        var controller = Controller(config);
        if (!await controller.HasCrdAsync(ControllerCrd, ct))
        {
            throw new InvalidOperationException(
                $"{ControllerName} is not installed in the cluster (no {ControllerCrd} CRD). Install it, or set " +
                "targetConfig.gitops.register to false if its objects are applied another way.");
        }

        var shared = Path.Combine(environment, GitOpsTree.RegistrationFolder, GitOpsTree.EnvironmentFile);
        if (File.Exists(shared))
        {
            await controller.ApplyFileAsync(shared, ct);
        }

        foreach (var svc in services)
        {
            await controller.ApplyFileAsync(GitOpsTree.RegistrationFile(environment, svc.ServiceName), ct);
        }

        Logger.LogInformation("[{Controller}] Registered {Count} service(s) in namespace {Namespace}.",
            ControllerName, services.Count, RegistrationNamespace(config));
    }

    /// <summary>Polls every service's object until all have applied <paramref name="commit"/> and are healthy, one failed, or time is up.</summary>
    private async Task<List<DeploymentResult>> WaitAsync(
        EnvironmentConfig config, IReadOnlyList<KubernetesServiceManifest> services, string commit, CancellationToken ct)
    {
        var controller = Controller(config);
        var ns = RegistrationNamespace(config);
        var timeout = TimeSpan.FromSeconds(Math.Max(1, GetGitOpsConfig(config).WaitTimeoutSeconds));
        var clock = Stopwatch.StartNew();
        var done = new Dictionary<string, DeploymentResult>(StringComparer.Ordinal);
        var last = new Dictionary<string, string?>(StringComparer.Ordinal);

        while (true)
        {
            foreach (var svc in services.Where(s => !done.ContainsKey(s.ServiceName)))
            {
                HealthObservation seen;
                try
                {
                    seen = Evaluate(await controller.GetJsonAsync(RegistrationResource, ns, RegistrationName(config, svc.ServiceName), ct), commit);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    seen = HealthObservation.Progressing(ex.Message);
                }

                last[svc.ServiceName] = seen.Detail;
                if (seen.State == HealthState.Healthy)
                {
                    Logger.LogInformation("[{Controller}] {Name}: {Detail}", ControllerName, svc.ServiceName, seen.Detail);
                    done[svc.ServiceName] = new DeploymentResult
                    {
                        ServiceName = svc.ServiceName, DeploymentId = $"commit/{ArgoCdManifests.Short(commit)}", Success = true
                    };
                }
                else if (seen.State == HealthState.Failed)
                {
                    done[svc.ServiceName] = new DeploymentResult
                    {
                        ServiceName = svc.ServiceName, Success = false, Error = $"{ControllerName}: {seen.Detail}"
                    };
                }
            }

            if (done.Count == services.Count)
            {
                break;
            }

            if (clock.Elapsed >= timeout)
            {
                foreach (var svc in services.Where(s => !done.ContainsKey(s.ServiceName)))
                {
                    done[svc.ServiceName] = new DeploymentResult
                    {
                        ServiceName = svc.ServiceName,
                        Success = false,
                        Error = $"{ControllerName} did not finish within {timeout.TotalSeconds:0}s ({last.GetValueOrDefault(svc.ServiceName) ?? "no status"})."
                    };
                }

                break;
            }

            await Task.Delay(PollInterval, ct);
        }

        return [.. services.Select(s => done[s.ServiceName])];
    }

    public Task PublishAsync(AspireManifest manifest, EnvironmentConfig config, string appHostDirectory, CancellationToken ct = default)
        => _publisher.PublishAsync(manifest, config, appHostDirectory, ct);

    public async Task<IReadOnlyList<ServiceExplanation>> ExplainAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
    {
        var explanations = await Kubernetes.ExplainAsync(manifest, config, ct);
        var gitops = GetGitOpsConfig(config);
        var repoPath = RepoPath(config);
        foreach (var e in explanations)
        {
            e.AdditionalInfo["repository"] = $"{gitops.RepoUrl}@{gitops.Branch}:{repoPath}/{e.Name}";
            e.AdditionalInfo["registration"] = $"{RegistrationKind} {RegistrationNamespace(config)}/{RegistrationName(config, e.Name)}";
        }

        return explanations;
    }

    public Task<IReadOnlyList<ServiceDependency>> GetDependenciesAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
        => Kubernetes.GetDependenciesAsync(manifest, config, ct);

    // ── Lifecycle (destroy / prune) ───────────────────────────────────────────

    /// <summary>The service folders committed under the environment's path.</summary>
    public async Task<IReadOnlyList<ManagedService>> ListManagedAsync(EnvironmentConfig config, CancellationToken ct = default)
    {
        var gitops = RequireRepo(config);
        if (await MissingToolsAsync(config, needKubectl: false, ct) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        var repoPath = RepoPath(config);
        var git = Git(config);
        await git.SyncAsync(gitops.RepoUrl, gitops.Branch, ct);
        return [.. GitOpsTree.ListServices(EnvironmentDirectory(git, repoPath)).Select(s => new ManagedService(s, TargetId: $"{repoPath}/{s}"))];
    }

    /// <summary>
    /// Deletes the service's Application or Kustomization and waits while the controller removes
    /// what it deployed, then removes the folder from the repository and pushes. Without
    /// registration, only the folder goes; the controller's object is left to whoever manages it.
    /// </summary>
    public async Task DeleteServiceAsync(EnvironmentConfig config, string service, DeleteOptions options, CancellationToken ct = default)
    {
        var gitops = RequireRepo(config);
        if (await MissingToolsAsync(config, gitops.Register, ct) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        if (gitops.Register)
        {
            var name = RegistrationName(config, service);
            await Controller(config).DeleteAsync(RegistrationResource, RegistrationNamespace(config), name, s_deleteTimeout, ct);
            Logger.LogInformation("[{Controller}] Deleted {Kind} {Namespace}/{Name}.", ControllerName, RegistrationKind,
                RegistrationNamespace(config), name);
        }
        else
        {
            Logger.LogWarning("gitops.register is off: remove the {Kind} for {Name} where it is managed, or {Controller} will report its folder missing.",
                RegistrationKind, service, ControllerName);
        }

        var repoPath = RepoPath(config);
        var git = Git(config);
        await git.SyncAsync(gitops.RepoUrl, gitops.Branch, ct);
        await GitOpsTree.RemoveAsync(EnvironmentDirectory(git, repoPath), service, ct);
        var commit = await git.CommitAsync(repoPath, CommitMessage(config, "remove", [service]), gitops.AuthorName, gitops.AuthorEmail, ct);
        if (commit is not null && gitops.Push)
        {
            await git.PushAsync(gitops.Branch, ct);
        }

        Logger.LogInformation("[git] Removed {Path}/{Name}{Pushed}.", repoPath, service, commit is not null && gitops.Push ? " and pushed" : string.Empty);
    }

    public async Task<IReadOnlyList<string>> DescribeKeptAsync(
        EnvironmentConfig config, DeleteOptions options, CancellationToken ct = default)
        => [.. await Kubernetes.DescribeKeptAsync(config, options, ct), $"the services' history in {GetGitOpsConfig(config).RepoUrl}"];
}
