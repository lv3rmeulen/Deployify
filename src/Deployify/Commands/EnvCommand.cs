using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Deployify.Core.State;
using Deployify.Target.AwsEcs;
using Deployify.Target.AzureContainerApps;
using Deployify.Target.Compose;
using Deployify.Target.GitOps;
using Deployify.Target.Helm;
using Deployify.Target.Iis;
using Deployify.Target.Kubernetes;
using Deployify.Target.OvhCloud;
using Deployify.Target.Nomad;
using Deployify.Target.NomadConsul;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Deployify.Commands;

public static class EnvCommand
{
    public static Command Build()
    {
        var cmd = new Command("env", "Manage deployment environments.");
        cmd.Subcommands.Add(ListCommand());
        cmd.Subcommands.Add(UseCommand());
        cmd.Subcommands.Add(AddCommand());
        cmd.Subcommands.Add(SetCommand());
        cmd.Subcommands.Add(RemoveCommand());
        return cmd;
    }

    // ── list ──────────────────────────────────────────────────────────────────

    private static Command ListCommand()
    {
        var cmd = new Command("list", "List all configured environments.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var directory = Resolve(parseResult.GetValue(GlobalOptions.ProjectPath)!);
            var stateManager = BuildStateManager();
            var stateFile = await stateManager.LoadFileAsync(directory, ct);

            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Environment").AddColumn("Target")
                .AddColumn("Address / Config").AddColumn("Registry");

            foreach (var (name, cfg) in stateFile.Environments)
            {
                var isActive = name == stateFile.ActiveEnvironment;
                var label = isActive ? $"[bold cyan]▶ {Markup.Escape(name)}[/]" : $"  {Markup.Escape(name)}";
                if (cfg.Extends is { } extends)
                {
                    label += $" [dim]← {Markup.Escape(extends)}[/]";
                }

                if (cfg.Abstract)
                {
                    label += " [dim](abstract)[/]";
                }

                var address = Markup.Escape(GetDisplayAddress(cfg));
                table.AddRow(label, cfg.Target.ToString(), address,
                    cfg.ContainerRegistry is { } registry ? Markup.Escape(registry) : "[dim]-[/]");
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLineInterpolated($"\nActive: [bold cyan]{stateFile.ActiveEnvironment}[/]");
        });
        return cmd;
    }

    // ── use ───────────────────────────────────────────────────────────────────

    private static Command UseCommand()
    {
        var nameArg = new Argument<string>("name")
        {
            Description = "Environment name to switch to."
        };
        var cmd = new Command("use", "Switch the active environment.") { nameArg };
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var name = parseResult.GetRequiredValue(nameArg);
            var directory = Resolve(parseResult.GetValue(GlobalOptions.ProjectPath)!);
            var stateManager = BuildStateManager();
            var stateFile = await stateManager.LoadFileAsync(directory, ct);

            if (!stateFile.Environments.TryGetValue(name, out var chosen))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] Environment '[bold]{name}[/]' not found. Available: {string.Join(", ", stateFile.Environments.Keys)}");
                return 1;
            }

            if (chosen.Abstract)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] '[bold]{name}[/]' is abstract: it only holds settings other environments extend. Switch to one of those instead.");
                return 1;
            }

            var previous = stateFile.ActiveEnvironment;
            stateFile.ActiveEnvironment = name;
            await stateManager.SaveFileAsync(stateFile, directory, ct);
            await SecretsWarning.PrintAsync(stateFile, directory, ct);

            AnsiConsole.MarkupLineInterpolated($"[green]✓[/] Switched [dim]{previous}[/] → [bold cyan]{name}[/] ({stateFile.Environments[name].Target})");

            return 0;
        });
        return cmd;
    }

    // ── add ───────────────────────────────────────────────────────────────────

    /// <summary>The arguments and options of <c>env add</c>.</summary>
    private sealed class AddOptions
    {
        public readonly Argument<string> Name = new("name") { Description = "New environment name." };

        public readonly Option<DeploymentTargetType> Target = new("--target")
        {
            Description = TargetOption.Description + " Required unless --extends is given.",
            CustomParser = TargetOption.Parse
        };

        public readonly Option<bool> Activate = new("--activate") { Description = "Set as active after adding." };

        public readonly Option<string?> Extends = new("--extends")
        {
            Description = "Build on another environment: start from its settings and keep only what differs. " +
                          "Takes the target and settings given here as overrides; no prompts."
        };

        public readonly Option<bool> Abstract = new("--abstract")
        {
            Description = "Only hold settings for other environments to extend; it can't be deployed or made active."
        };

        public readonly Option<string?> Registry = new("--container-registry") { Description = "Container registry." };
        public readonly Option<string?> ImageTag = new("--image-tag") { Description = "Default image tag." };
        public readonly Option<string?> ContainerOs = new("--container-os") { Description = "Target OS for container images (e.g. linux)." };
        public readonly Option<string?> ContainerArch = new("--container-arch") { Description = "Target CPU architecture: x64 | arm64 | arm." };

        // Nomad
        public readonly Option<string?> NomadAddress = new("--nomad-address") { Description = "Nomad address." };
        public readonly Option<string?> NomadNamespace = new("--nomad-namespace") { Description = "Nomad namespace." };
        public readonly Option<string?> NomadDatacenter = new("--nomad-datacenter") { Description = "Nomad datacenter." };
        public readonly Option<string?> NomadToken = new("--nomad-token") { Description = "Nomad ACL token as env:NAME. Default: NOMAD_TOKEN at run time." };

        // Kubernetes
        public readonly Option<string?> KubernetesContext = new("--kubernetes-context") { Description = "kubectl context." };
        public readonly Option<string?> KubernetesPrefix = new("--kubernetes-namespace-prefix") { Description = "Namespace prefix." };
        public readonly Option<string?> KubernetesDomain = new("--kubernetes-ingress-domain") { Description = "Ingress base domain." };

        // Compose
        public readonly Option<string?> ComposeProject = new("--compose-project") { Description = "Docker Compose project name." };
        public readonly Option<string?> ComposeRestart = new("--compose-restart") { Description = "Restart policy." };

        /// <summary>Every option, in the order help and the CLI reference list them.</summary>
        public Option[] All =>
        [
            GlobalOptions.ProjectPath, GlobalOptions.NonInteractive,
            Target, Extends, Abstract, Activate, Registry, ImageTag, ContainerOs, ContainerArch,
            NomadAddress, NomadNamespace, NomadDatacenter, NomadToken,
            KubernetesContext, KubernetesPrefix, KubernetesDomain,
            ComposeProject, ComposeRestart, PlaintextSecretFlags.AllowOption
        ];

        /// <summary>Options that fill in one target's section of <c>targetConfig</c>.</summary>
        public Option[] TargetSpecific =>
        [
            NomadAddress, NomadNamespace, NomadDatacenter, NomadToken,
            KubernetesContext, KubernetesPrefix, KubernetesDomain, ComposeProject, ComposeRestart
        ];

        public TargetSettings ReadTargetSettings(ParseResult pr) => new(
            NomadAddress: pr.GetValue(NomadAddress),
            NomadToken: pr.GetValue(NomadToken),
            NomadNamespace: pr.GetValue(NomadNamespace),
            NomadDatacenter: pr.GetValue(NomadDatacenter),
            KubernetesContext: pr.GetValue(KubernetesContext),
            KubernetesNamespacePrefix: pr.GetValue(KubernetesPrefix),
            KubernetesIngressDomain: pr.GetValue(KubernetesDomain),
            ComposeProject: pr.GetValue(ComposeProject),
            ComposeRestart: pr.GetValue(ComposeRestart));
    }

    private static Command AddCommand()
    {
        var o = new AddOptions();
        var cmd = new Command("add", "Add a new environment.") { o.Name };
        foreach (var option in o.All)
        {
            cmd.Options.Add(option);
        }

        cmd.Validators.Add(result => ValidateAdd(result, o));
        cmd.SetAction((parseResult, ct) => RunAddAsync(parseResult, o, ct));
        return cmd;
    }

    private static void ValidateAdd(CommandResult result, AddOptions o)
    {
        var extends = result.GetResult(o.Extends) is not null;
        if (!extends && result.GetResult(o.Target) is null)
        {
            result.AddError("Option '--target' is required (or give --extends to build on another environment).");
        }

        if (extends && o.TargetSpecific.FirstOrDefault(option => result.GetResult(option) is not null) is { } given)
        {
            result.AddError(
                $"{given.Name} can't be combined with --extends. Add the environment, then override target " +
                "settings with 'deployify env set'.");
        }

        if (result.GetValue(o.Activate) && result.GetValue(o.Abstract))
        {
            result.AddError("An --abstract environment can't be made active.");
        }
    }

    private static async Task<int> RunAddAsync(ParseResult pr, AddOptions o, CancellationToken ct)
    {
        var name = pr.GetRequiredValue(o.Name);
        var directory = Resolve(pr.GetValue(GlobalOptions.ProjectPath)!);
        var stateManager = BuildStateManager();
        var stateFile = await LoadOrNewAsync(stateManager, directory, ct);

        if (stateFile.Environments.ContainsKey(name))
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]![/] '{name}' already exists. Use [cyan]deployify env set[/] to update.");
            return 1;
        }

        var config = await CreateConfigAsync(pr, o, stateFile, name, directory, ct);
        if (config is null)
        {
            return 1;
        }

        config.Abstract = pr.GetValue(o.Abstract);
        stateFile.Environments[name] = config;
        var activate = pr.GetValue(o.Activate);
        if (activate || (stateFile.Environments.Count == 1 && !config.Abstract))
        {
            stateFile.ActiveEnvironment = name;
        }

        await stateManager.SaveFileAsync(stateFile, directory, ct);
        await SecretsWarning.PrintAsync(stateFile, directory, ct);
        PrintAdded(name, config, activate);
        return 0;
    }

    private static async Task<DeployifyStateFile> LoadOrNewAsync(IStateManager stateManager, string directory, CancellationToken ct)
    {
        try
        {
            return await stateManager.LoadFileAsync(directory, ct);
        }
        catch (FileNotFoundException)
        {
            return new DeployifyStateFile { Environments = [] };
        }
    }

    /// <summary>The new environment's settings, or null after printing why there are none.</summary>
    private static async Task<EnvironmentConfig?> CreateConfigAsync(
        ParseResult pr, AddOptions o, DeployifyStateFile stateFile, string name, string directory, CancellationToken ct)
    {
        if (pr.GetValue(o.Extends) is { } baseName)
        {
            return CreateExtending(pr, o, stateFile, baseName);
        }

        if (pr.GetValue(GlobalOptions.NonInteractive))
        {
            return CreateFromOptions(pr, o);
        }

        return await InitCommand.PromptEnvironmentConfigAsync(name, directory, ct);
    }

    private static EnvironmentConfig? CreateExtending(ParseResult pr, AddOptions o, DeployifyStateFile stateFile, string baseName)
    {
        if (!stateFile.Environments.TryGetValue(baseName, out var baseConfig))
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]✗[/] Can't extend '{baseName}': no such environment. Available: {string.Join(", ", stateFile.Environments.Keys)}");
            return null;
        }

        var config = StateManager.CreateExtending(baseConfig, baseName);
        if (pr.GetResult(o.Target) is not null)
        {
            ChangeTarget(config, pr.GetValue(o.Target));
        }

        var changes = new Changes(pr);
        changes.Set(o.Registry, v => config.ContainerRegistry = v);
        changes.Set(o.ImageTag, v => config.ImageTag = v);
        changes.Set(o.ContainerOs, v => config.ContainerOs = v);
        changes.Set(o.ContainerArch, v => config.ContainerArch = v);
        return config;
    }

    /// <summary>
    /// Switches an extending environment to another target, adding default settings for it
    /// where the base has none. The base's sections for other targets stay inherited.
    /// </summary>
    private static void ChangeTarget(EnvironmentConfig config, DeploymentTargetType target)
    {
        if (target == config.Target)
        {
            return;
        }

        config.Target = target;
        foreach (var (key, section) in BuildTargetConfig(target, TargetSettings.Defaults))
        {
            config.TargetConfig.TryAdd(key, section);
        }
    }

    private static EnvironmentConfig? CreateFromOptions(ParseResult pr, AddOptions o)
    {
        if (!PlaintextSecretFlags.Check(pr, (o.NomadToken, "NOMAD_TOKEN", true)))
        {
            return null;
        }

        var target = pr.GetValue(o.Target);
        return new EnvironmentConfig
        {
            Target = target,
            ContainerRegistry = pr.GetValue(o.Registry),
            ImageTag = pr.GetValue(o.ImageTag) ?? "latest",
            TargetConfig = BuildTargetConfig(target, o.ReadTargetSettings(pr))
        };
    }

    private static void PrintAdded(string name, EnvironmentConfig config, bool activated)
    {
        var details = new List<string> { config.Target.ToString() };
        if (config.Extends is { } extends)
        {
            details.Add($"extends {extends}");
        }

        if (config.Abstract)
        {
            details.Add("abstract");
        }

        AnsiConsole.MarkupLineInterpolated($"[green]✓[/] Environment [bold cyan]{name}[/] added ({string.Join(", ", details)}).");
        if (activated)
        {
            AnsiConsole.MarkupLineInterpolated($"  Now active: [bold cyan]{name}[/]");
        }
    }

    // ── set ───────────────────────────────────────────────────────────────────

    /// <summary>The arguments and options of <c>env set</c>.</summary>
    private sealed class SetOptions
    {
        public readonly Argument<string> Name = new("name") { Description = "Environment to update." };

        public readonly Option<string?> Registry = new("--container-registry") { Description = "Update container registry." };
        public readonly Option<string?> ImageTag = new("--image-tag") { Description = "Update default image tag." };
        public readonly Option<string?> ContainerOs = new("--container-os") { Description = "Target OS for container images (e.g. linux)." };
        public readonly Option<string?> ContainerArch = new("--container-arch") { Description = "Target CPU architecture: x64 | arm64 | arm." };

        // Nomad
        public readonly Option<string?> NomadAddress = new("--nomad-address") { Description = "Update Nomad address." };
        public readonly Option<string?> NomadToken = new("--nomad-token") { Description = "Update the Nomad token reference (env:NAME)." };
        public readonly Option<string?> NomadNamespace = new("--nomad-namespace") { Description = "Update Nomad namespace." };
        public readonly Option<string?> NomadDatacenter = new("--nomad-datacenter") { Description = "Update Nomad datacenter." };
        public readonly Option<string?> NomadRegistryUser = new("--nomad-registry-username") { Description = "Docker registry username (empty string to remove)." };
        public readonly Option<string?> NomadRegistryPassword = new("--nomad-registry-password") { Description = "Docker registry password or token as env:NAME (empty string to remove)." };
        public readonly Option<bool?> NomadRegisterServices = new("--nomad-register-services") { Description = "Enable (true) or disable (false) service registration." };
        public readonly Option<string?> NomadServiceProvider = new("--nomad-service-provider") { Description = "Service provider: nomad | consul." };
        public readonly Option<string?> NomadHealthCheckPath = new("--nomad-health-check-path") { Description = "HTTP path for service health checks." };

        // Aspire Dashboard
        public readonly Option<bool?> DeployDashboard = new("--deploy-dashboard") { Description = "Enable (true) or disable (false) the Aspire Dashboard." };
        public readonly Option<string?> DashboardTag = new("--dashboard-tag") { Description = "Image tag for the Aspire Dashboard." };
        public readonly Option<int?> DashboardPort = new("--dashboard-port") { Description = "Host port for the dashboard UI." };
        public readonly Option<int?> DashboardOtlpPort = new("--dashboard-otlp-port") { Description = "Host port for the OTLP gRPC endpoint." };
        public readonly Option<string?> DashboardToken = new("--dashboard-token") { Description = "Browser auth token as env:NAME (empty string to remove)." };

        // Dapr
        public readonly Option<bool?> DeployDapr = new("--deploy-dapr") { Description = "Enable (true) or disable (false) Dapr sidecar injection." };
        public readonly Option<string?> DaprVersion = new("--dapr-version") { Description = "Dapr sidecar image tag." };
        public readonly Option<string?> DaprComponents = new("--dapr-components-path") { Description = "Path to Dapr components directory." };
        public readonly Option<string?> DaprConfigPath = new("--dapr-config-path") { Description = "Path to Dapr config file." };
        public readonly Option<string?> DaprLogLevel = new("--dapr-log-level") { Description = "Dapr sidecar log level." };

        /// <summary>Every option, in the order help and the CLI reference list them.</summary>
        public Option[] All =>
        [
            GlobalOptions.ProjectPath, Registry, ImageTag, ContainerOs, ContainerArch,
            NomadAddress, NomadToken, NomadNamespace, NomadDatacenter,
            NomadRegistryUser, NomadRegistryPassword,
            NomadRegisterServices, NomadServiceProvider, NomadHealthCheckPath,
            DeployDashboard, DashboardTag, DashboardPort, DashboardOtlpPort, DashboardToken,
            DeployDapr, DaprVersion, DaprComponents, DaprConfigPath, DaprLogLevel,
            PlaintextSecretFlags.AllowOption
        ];
    }

    /// <summary>
    /// Applies the options that were given and records their names, without the leading dashes,
    /// for the summary line.
    /// </summary>
    private sealed class Changes(ParseResult pr)
    {
        public List<string> Names { get; } = [];

        public void Set<T>(Option<T?> option, Action<T> apply) where T : class
        {
            if (pr.GetValue(option) is { } value)
            {
                apply(value);
                Names.Add(option.Name.TrimStart('-'));
            }
        }

        public void Set<T>(Option<T?> option, Action<T> apply) where T : struct
        {
            if (pr.GetValue(option) is { } value)
            {
                apply(value);
                Names.Add(option.Name.TrimStart('-'));
            }
        }

        /// <summary>Like <c>Set</c>, with an empty string meaning "remove" (null).</summary>
        public void SetOrClear(Option<string?> option, Action<string?> apply)
            => Set(option, v => apply(string.IsNullOrEmpty(v) ? null : v));
    }

    private static Command SetCommand()
    {
        var o = new SetOptions();
        var cmd = new Command("set", "Update settings on an existing environment.") { o.Name };
        foreach (var option in o.All)
        {
            cmd.Options.Add(option);
        }

        cmd.SetAction((parseResult, ct) => RunSetAsync(parseResult, o, ct));
        return cmd;
    }

    private static async Task<int> RunSetAsync(ParseResult pr, SetOptions o, CancellationToken ct)
    {
        var name = pr.GetRequiredValue(o.Name);
        var directory = Resolve(pr.GetValue(GlobalOptions.ProjectPath)!);
        var stateManager = BuildStateManager();
        var stateFile = await stateManager.LoadFileAsync(directory, ct);

        if (!stateFile.Environments.TryGetValue(name, out var config))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]✗[/] Environment '{name}' not found.");
            return 1;
        }

        if (!PlaintextSecretFlags.Check(pr,
                (o.NomadToken, "NOMAD_TOKEN", true),
                (o.NomadRegistryPassword, "DEPLOYIFY_REGISTRY_PASSWORD", true),
                (o.DashboardToken, "DEPLOYIFY_DASHBOARD_TOKEN", false)))
        {
            return 1;
        }

        var changes = new Changes(pr);
        SetImageSettings(changes, o, config);
        SetDapr(changes, o, config);
        SetDashboard(changes, o, config);
        SetNomad(changes, o, config);

        if (changes.Names.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No changes specified.[/]");
            return 0;
        }

        await stateManager.SaveFileAsync(stateFile, directory, ct);
        await SecretsWarning.PrintAsync(stateFile, directory, ct);
        AnsiConsole.MarkupLineInterpolated($"[green]✓[/] [bold cyan]{name}[/] updated: {string.Join(", ", changes.Names)}");
        return 0;
    }

    private static void SetImageSettings(Changes changes, SetOptions o, EnvironmentConfig config)
    {
        changes.Set(o.Registry, v => config.ContainerRegistry = v);
        changes.Set(o.ImageTag, v => config.ImageTag = v);
        changes.Set(o.ContainerOs, v => config.ContainerOs = v);
        changes.Set(o.ContainerArch, v => config.ContainerArch = v);
    }

    private static void SetDapr(Changes changes, SetOptions o, EnvironmentConfig config)
    {
        changes.Set(o.DeployDapr, v => config.DeployDapr = v);
        changes.Set(o.DaprVersion, v => config.Dapr.DaprVersion = v);
        changes.SetOrClear(o.DaprComponents, v => config.Dapr.ComponentsPath = v);
        changes.SetOrClear(o.DaprConfigPath, v => config.Dapr.ConfigPath = v);
        changes.Set(o.DaprLogLevel, v => config.Dapr.LogLevel = v);
    }

    private static void SetDashboard(Changes changes, SetOptions o, EnvironmentConfig config)
    {
        changes.Set(o.DeployDashboard, v => config.DeployDashboard = v);
        changes.Set(o.DashboardTag, v => config.Dashboard.ImageTag = v);
        changes.Set(o.DashboardPort, v => config.Dashboard.Port = v);
        changes.Set(o.DashboardOtlpPort, v => config.Dashboard.OtlpPort = v);
        changes.SetOrClear(o.DashboardToken, v => config.Dashboard.BrowserToken = v);
    }

    /// <summary>Applies the Nomad options to a Nomad environment's <c>targetConfig.nomad</c>; other targets ignore them.</summary>
    private static void SetNomad(Changes changes, SetOptions o, EnvironmentConfig config)
    {
        if (config.Target != DeploymentTargetType.Nomad ||
            !config.TargetConfig.TryGetValue("nomad", out var raw) || raw is not JsonElement element)
        {
            return;
        }

        // Same options init uses, so enum values stored as strings ("Docker") deserialize.
        var nomad = element.Deserialize<NomadTargetConfig>(TargetConfigJsonOptions.Default)!;
        var before = changes.Names.Count;
        changes.Set(o.NomadAddress, v => nomad.Address = v);
        changes.Set(o.NomadToken, v => nomad.Token = v);
        changes.Set(o.NomadNamespace, v => nomad.Namespace = v);
        changes.Set(o.NomadDatacenter, v => nomad.Datacenters = [v]);
        changes.SetOrClear(o.NomadRegistryUser, v => nomad.RegistryUsername = v);
        changes.SetOrClear(o.NomadRegistryPassword, v => nomad.RegistryPassword = v);
        changes.Set(o.NomadRegisterServices, v => nomad.RegisterServices = v);
        changes.Set(o.NomadServiceProvider, v => nomad.ServiceProvider = v);
        changes.Set(o.NomadHealthCheckPath, v => nomad.HealthCheckPath = v);

        if (changes.Names.Count > before)
        {
            config.TargetConfig["nomad"] = JsonSerializer.SerializeToElement(nomad, TargetConfigJsonOptions.Default);
        }
    }

    // ── remove ────────────────────────────────────────────────────────────────

    private static Command RemoveCommand()
    {
        var nameArg = new Argument<string>("name")
        {
            Description = "Environment to remove."
        };
        var forceOpt = new Option<bool>("--force")
        {
            Description = "Skip confirmation prompt."
        };
        var cmd = new Command("remove", "Remove an environment.") { nameArg };
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(forceOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var name = parseResult.GetRequiredValue(nameArg);
            var force = parseResult.GetValue(forceOpt);
            var directory = Resolve(parseResult.GetValue(GlobalOptions.ProjectPath)!);
            var stateManager = BuildStateManager();
            var stateFile = await stateManager.LoadFileAsync(directory, ct);

            if (!stateFile.Environments.ContainsKey(name))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] '{name}' not found.");
                return 1;
            }

            if (stateFile.Environments.Count == 1)
            {
                AnsiConsole.MarkupLine("[red]✗[/] Cannot remove the only environment.");
                return 1;
            }

            if (stateFile.ActiveEnvironment == name)
            {
                AnsiConsole.MarkupLine("[red]✗[/] Cannot remove the active environment. Switch first.");
                return 1;
            }

            if (EnvironmentInheritance.ExtendedBy(name, stateFile.Environments) is { Count: > 0 } children)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[red]✗[/] Cannot remove '{name}': {string.Join(", ", children)} extend{(children.Count == 1 ? "s" : "")} it. Point them elsewhere or remove them first.");
                return 1;
            }

            if (!force && !await ConsolePrompts.ConfirmAsync($"Remove '[bold]{Markup.Escape(name)}[/]'?", defaultValue: false, ct))
            {
                return 0;
            }

            stateFile.Environments.Remove(name);
            await stateManager.SaveFileAsync(stateFile, directory, ct);
            await SecretsWarning.PrintAsync(stateFile, directory, ct);
            AnsiConsole.MarkupLineInterpolated($"[green]✓[/] Environment [bold]{name}[/] removed.");

            return 0;
        });
        return cmd;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Settings a new environment's <c>targetConfig</c> starts from; null takes the default.
    /// Each target reads only its own.
    /// </summary>
    internal sealed record TargetSettings(
        string? NomadAddress = null,
        string? NomadToken = null,
        string? NomadNamespace = null,
        string? NomadDatacenter = null,
        string? KubernetesContext = null,
        string? KubernetesNamespacePrefix = null,
        string? KubernetesIngressDomain = null,
        string? ComposeProject = null,
        string? ComposeRestart = null)
    {
        public static readonly TargetSettings Defaults = new();
    }

    internal static Dictionary<string, object?> BuildTargetConfig(DeploymentTargetType target, TargetSettings settings)
        => target.Value switch
        {
            DeploymentTargetType.Names.Nomad => Sections(("nomad", NomadSettings(settings))),
            DeploymentTargetType.Names.Kubernetes => Sections(("kubernetes", KubernetesSettings(settings))),
            DeploymentTargetType.Names.Compose => Sections(("compose", ComposeSettings(settings))),
            DeploymentTargetType.Names.AwsEcs => Sections(("ecs", new EcsTargetConfig())),
            DeploymentTargetType.Names.AzureContainerApps => Sections(("aca", new AcaTargetConfig())),
            DeploymentTargetType.Names.NomadConsul => Sections(("nomadconsul", new NomadConsulTargetConfig())),
            // Helm deploys the Kubernetes target's manifests, so it reads the same settings.
            DeploymentTargetType.Names.Helm => Sections(
                ("kubernetes", KubernetesSettings(settings)), ("helm", new HelmTargetConfig())),
            // The GitOps targets render the Kubernetes target's manifests; set gitops.repoUrl before applying.
            DeploymentTargetType.Names.ArgoCd => Sections(
                ("kubernetes", KubernetesSettings(settings)), ("gitops", new GitOpsTargetConfig()), ("argocd", new ArgoCdTargetConfig())),
            DeploymentTargetType.Names.Flux => Sections(
                ("kubernetes", KubernetesSettings(settings)), ("gitops", new GitOpsTargetConfig()), ("flux", new FluxTargetConfig())),
            // OVHcloud deploys the Kubernetes target's manifests with the kubeconfig it fetches, so no
            // context; set ovhcloud.projectId and clusterId.
            DeploymentTargetType.Names.OvhCloud => Sections(
                ("kubernetes", KubernetesSettings(settings with { KubernetesContext = null })),
                ("ovhcloud", new OvhCloudTargetConfig { Endpoint = "ovh-eu" })),
            // Deploys to this machine until iis.host is set.
            DeploymentTargetType.Names.Iis => Sections(("iis", new IisTargetConfig())),
            _ => []
        };

    private static NomadTargetConfig NomadSettings(TargetSettings s) => new()
    {
        Address = s.NomadAddress ?? "http://localhost:4646",
        Token = NullIfBlank(s.NomadToken),
        Namespace = s.NomadNamespace ?? "default",
        Datacenters = [s.NomadDatacenter ?? "dc1"]
    };

    private static KubernetesTargetConfig KubernetesSettings(TargetSettings s) => new()
    {
        Context = NullIfBlank(s.KubernetesContext),
        NamespacePrefix = s.KubernetesNamespacePrefix ?? "deployify",
        IngressBaseDomain = NullIfBlank(s.KubernetesIngressDomain),
        GenerateIngress = NullIfBlank(s.KubernetesIngressDomain) is not null
    };

    private static ComposeTargetConfig ComposeSettings(TargetSettings s) => new()
    {
        ProjectName = s.ComposeProject ?? "deployify",
        RestartPolicy = s.ComposeRestart ?? "unless-stopped",
        GenerateProfiles = true,
        GenerateEnvFile = true,
        GenerateMergedFile = true
    };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static Dictionary<string, object?> Sections(params (string Key, object Config)[] sections)
        => sections.ToDictionary(
            s => s.Key, object? (s) => JsonSerializer.SerializeToElement(s.Config, TargetConfigJsonOptions.Default));

    /// <summary>Per target: the <c>targetConfig</c> section and key <c>env list</c> shows, and what to show when it's unset.</summary>
    private static readonly Dictionary<string, (string Section, string Key, string Unset)> s_displayAddress = new(StringComparer.Ordinal)
    {
        [DeploymentTargetType.Names.Nomad] = ("nomad", "address", "-"),
        [DeploymentTargetType.Names.Kubernetes] = ("kubernetes", "context", "(current)"),
        [DeploymentTargetType.Names.Helm] = ("kubernetes", "context", "(current)"),
        [DeploymentTargetType.Names.Compose] = ("compose", "projectName", "-"),
        [DeploymentTargetType.Names.AwsEcs] = ("ecs", "region", "-"),
        [DeploymentTargetType.Names.AzureContainerApps] = ("aca", "location", "-"),
        [DeploymentTargetType.Names.NomadConsul] = ("nomadconsul", "nomadAddress", "-"),
        [DeploymentTargetType.Names.ArgoCd] = ("gitops", "repoUrl", "-"),
        [DeploymentTargetType.Names.Flux] = ("gitops", "repoUrl", "-"),
        [DeploymentTargetType.Names.OvhCloud] = ("ovhcloud", "clusterId", "-"),
        [DeploymentTargetType.Names.Iis] = ("iis", "host", "localhost")
    };

    internal static string GetDisplayAddress(EnvironmentConfig cfg)
    {
        if (!s_displayAddress.TryGetValue(cfg.Target.Value, out var shown) ||
            !cfg.TargetConfig.TryGetValue(shown.Section, out var raw) || raw is not JsonElement section)
        {
            return "-";
        }

        return GetString(section, shown.Key) ?? shown.Unset;
    }

    /// <summary>
    /// A string property of <paramref name="element"/>, matching its name in any case like every
    /// key in the state file: init and env set write "Address", hand-written files often "address".
    /// </summary>
    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var property = element.EnumerateObject()
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
    }

    private static IStateManager BuildStateManager()
        => new ServiceCollection()
            .AddSingleton<IStateManager, StateManager>()
            .BuildServiceProvider()
            .GetRequiredService<IStateManager>();

    private static string Resolve(string p)
        => File.Exists(p) ? Path.GetDirectoryName(Path.GetFullPath(p))! : Path.GetFullPath(p);
}
