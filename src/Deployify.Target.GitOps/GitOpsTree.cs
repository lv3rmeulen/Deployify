using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.SecretBackends;
using Deployify.Target.Kubernetes;

namespace Deployify.Target.GitOps;

/// <summary>
/// The environment's folder as it goes into the config repository: one folder per service with
/// the Kubernetes target's manifests and a <c>kustomization.yaml</c>, the controller's objects
/// under <c>_registration/</c>, and a root <c>kustomization.yaml</c> listing the services.
/// Argo CD and Flux both read plain Kustomize, so the service folders are the same for both.
/// </summary>
public static class GitOpsTree
{
    /// <summary>Folder holding the controller's objects; never a service.</summary>
    public const string RegistrationFolder = "_registration";

    /// <summary>File in <see cref="RegistrationFolder"/> for objects shared by every service (a Flux GitRepository).</summary>
    public const string EnvironmentFile = "_environment.yaml";

    public const string KustomizationFile = "kustomization.yaml";

    /// <summary>Where generate writes the tree, before apply copies it into the clone.</summary>
    public static string TreeDirectory(string outputDirectory) => Path.Combine(outputDirectory, "gitops", "tree");

    public static string RegistrationFile(string environmentDirectory, string service)
        => Path.Combine(environmentDirectory, RegistrationFolder, service + ".yaml");

    /// <summary>
    /// Writes the tree from scratch. Throws when a service would put a secret value in a file,
    /// since everything here ends up in git history.
    /// </summary>
    public static async Task<IReadOnlyList<string>> WriteAsync(
        IReadOnlyList<KubernetesServiceManifest> services,
        Func<KubernetesServiceManifest, string> serviceRegistration,
        string? environmentRegistration,
        string treeDirectory,
        CancellationToken ct = default)
    {
        var inline = services.Where(s => s.WritesSecretManifest).Select(s => s.ServiceName).ToList();
        if (inline.Count > 0)
        {
            throw new InvalidOperationException(
                $"{string.Join(", ", inline)} would commit secret values to git. The GitOps targets need an external " +
                "secret backend: set secrets.backend to KubernetesSecret, Vault or KeyVault.");
        }

        if (Directory.Exists(treeDirectory))
        {
            Directory.Delete(treeDirectory, recursive: true);
        }

        var paths = new List<string>();
        foreach (var svc in services)
        {
            var dir = Path.Combine(treeDirectory, svc.ServiceName);
            Directory.CreateDirectory(dir);
            var files = new List<string>();
            foreach (var (name, content) in KubernetesManifestGenerator.Render(svc))
            {
                files.Add(await KubernetesManifestGenerator.WriteAsync(dir, name, content, ct));
            }

            files.Add(await KubernetesManifestGenerator.WriteAsync(dir, KustomizationFile,
                RenderKustomization(files.Select(f => Path.GetFileName(f)).OfType<string>()), ct));
            if (svc.HasSecret)
            {
                SecretLeakScanner.EnsureClean(files, svc.SecretData);
            }

            paths.AddRange(files);
        }

        var registration = Path.Combine(treeDirectory, RegistrationFolder);
        Directory.CreateDirectory(registration);
        foreach (var svc in services)
        {
            paths.Add(await KubernetesManifestGenerator.WriteAsync(registration, svc.ServiceName + ".yaml", serviceRegistration(svc), ct));
        }

        if (environmentRegistration is not null)
        {
            paths.Add(await KubernetesManifestGenerator.WriteAsync(registration, EnvironmentFile, environmentRegistration, ct));
        }

        paths.Add(await KubernetesManifestGenerator.WriteAsync(treeDirectory, KustomizationFile,
            RenderKustomization(services.Select(s => s.ServiceName)), ct));
        return paths;
    }

    /// <summary>A Kustomization listing <paramref name="resources"/>; files that aren't YAML (a Vault policy) are left out.</summary>
    internal static string RenderKustomization(IEnumerable<string> resources)
    {
        var sb = new StringBuilder();
        sb.Append("apiVersion: kustomize.config.k8s.io/v1beta1\n");
        sb.Append("kind: Kustomization\n");
        var list = resources
            .Where(r => r != KustomizationFile)
            .Where(r => !Path.HasExtension(r) || r.EndsWith(".yaml", StringComparison.Ordinal))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();
        if (list.Count == 0)
        {
            sb.Append("resources: []\n");
            return sb.ToString();
        }

        sb.Append("resources:\n");
        foreach (var r in list)
        {
            sb.Append("  - ").Append(r).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>The service folders in an environment directory (those with a kustomization.yaml).</summary>
    public static IReadOnlyList<string> ListServices(string environmentDirectory)
    {
        if (!Directory.Exists(environmentDirectory))
        {
            return [];
        }

        return
        [
            .. Directory.GetDirectories(environmentDirectory)
                .Select(f => Path.GetFileName(f))
                .OfType<string>()
                .Where(n => n != RegistrationFolder && !n.StartsWith('.'))
                .Where(n => File.Exists(Path.Combine(environmentDirectory, n, KustomizationFile)))
                .OrderBy(n => n, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// Files that differ between a service as generated and as committed: added, changed or
    /// removed, by name. Line endings don't count, so a checkout with autocrlf compares equal.
    /// </summary>
    public static IReadOnlyList<string> Compare(string treeDirectory, string environmentDirectory, string service)
    {
        var changed = new SortedSet<string>(StringComparer.Ordinal);
        var desired = Files(Path.Combine(treeDirectory, service));
        var current = Files(Path.Combine(environmentDirectory, service));
        foreach (var name in desired.Keys.Union(current.Keys))
        {
            if (!desired.TryGetValue(name, out var a) || !current.TryGetValue(name, out var b) || !SameText(a, b))
            {
                changed.Add(name);
            }
        }

        var desiredRegistration = RegistrationFile(treeDirectory, service);
        var currentRegistration = RegistrationFile(environmentDirectory, service);
        if (File.Exists(desiredRegistration) != File.Exists(currentRegistration) ||
            (File.Exists(desiredRegistration) && !SameText(desiredRegistration, currentRegistration)))
        {
            changed.Add($"{RegistrationFolder}/{service}.yaml");
        }

        return [.. changed];

        static Dictionary<string, string> Files(string dir)
            => Directory.Exists(dir)
                ? Directory.GetFiles(dir).ToDictionary(f => Path.GetFileName(f), f => f, StringComparer.Ordinal)
                : [];
    }

    /// <summary>
    /// Puts the generated services into the clone: each service folder and its registration file
    /// are replaced; services the manifest no longer has are left alone (prune removes them).
    /// The root kustomization.yaml is rewritten to list every service folder present.
    /// </summary>
    public static async Task CopyIntoAsync(
        string treeDirectory, string environmentDirectory, IEnumerable<string> services, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.Combine(environmentDirectory, RegistrationFolder));
        foreach (var service in services)
        {
            var target = Path.Combine(environmentDirectory, service);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(Path.Combine(treeDirectory, service)))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }

            File.Copy(RegistrationFile(treeDirectory, service), RegistrationFile(environmentDirectory, service), overwrite: true);
        }

        var environmentFile = Path.Combine(treeDirectory, RegistrationFolder, EnvironmentFile);
        var committedEnvironmentFile = Path.Combine(environmentDirectory, RegistrationFolder, EnvironmentFile);
        if (File.Exists(environmentFile))
        {
            File.Copy(environmentFile, committedEnvironmentFile, overwrite: true);
        }
        else if (File.Exists(committedEnvironmentFile))
        {
            File.Delete(committedEnvironmentFile);
        }

        await WriteRootKustomizationAsync(environmentDirectory, ct);
    }

    /// <summary>Removes a service's folder and registration file from the clone and relists the rest.</summary>
    public static async Task RemoveAsync(string environmentDirectory, string service, CancellationToken ct = default)
    {
        var folder = Path.Combine(environmentDirectory, service);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        var registration = RegistrationFile(environmentDirectory, service);
        if (File.Exists(registration))
        {
            File.Delete(registration);
        }

        await WriteRootKustomizationAsync(environmentDirectory, ct);
    }

    private static async Task WriteRootKustomizationAsync(string environmentDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(environmentDirectory);
        await KubernetesManifestGenerator.WriteAsync(environmentDirectory, KustomizationFile,
            RenderKustomization(ListServices(environmentDirectory)), ct);
    }

    private static bool SameText(string pathA, string pathB)
        => File.ReadAllText(pathA).ReplaceLineEndings("\n") == File.ReadAllText(pathB).ReplaceLineEndings("\n");
}
