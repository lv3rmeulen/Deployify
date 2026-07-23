using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Deployify.Target.AzureContainerApps;
using Deployify.Target.AwsEcs;
using Deployify.Target.NomadConsul;
using Deployify.Target.Compose;
using Deployify.Target.Kubernetes;
using Deployify.Target.Nomad;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Deployify.Commands;

public static class EnvCommand
{
    public static Command Build()
    {
        var cmd = new Command("env", "Manage deployment environments.");
        cmd.AddCommand(ListCommand());
        cmd.AddCommand(UseCommand());
        cmd.AddCommand(AddCommand());
        cmd.AddCommand(SetCommand());
        cmd.AddCommand(RemoveCommand());
        return cmd;
    }

    // ── list ──────────────────────────────────────────────────────────────────

    private static Command ListCommand()
    {
        var cmd = new Command("list", "List all configured environments.");
        cmd.AddOption(GlobalOptions.ProjectPath);
        cmd.SetHandler(async ctx =>
        {
            var directory = Resolve(ctx.ParseResult.GetValueForOption(GlobalOptions.ProjectPath)!);
            var stateManager = BuildStateManager();
            var stateFile = await stateManager.LoadFileAsync(directory);

            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Environment").AddColumn("Target")
                .AddColumn("Address / Config").AddColumn("Registry");

            foreach (var (name, cfg) in stateFile.Environments)
            {
                var isActive = name == stateFile.ActiveEnvironment;
                var label = isActive ? $"[bold cyan]▶ {name}[/]" : $"  {name}";
                var address = GetDisplayAddress(cfg);
                table.AddRow(label, cfg.Target.ToString(), address, cfg.ContainerRegistry ?? "[dim]-[/]");
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"\nActive: [bold cyan]{stateFile.ActiveEnvironment}[/]");
        });
        return cmd;
    }

    // ── use ───────────────────────────────────────────────────────────────────

    private static Command UseCommand()
    {
        var nameArg = new Argument<string>("name", "Environment name to switch to.");
        var cmd = new Command("use", "Switch the active environment.") { nameArg };
        cmd.AddOption(GlobalOptions.ProjectPath);
        cmd.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(nameArg);
            var directory = Resolve(ctx.ParseResult.GetValueForOption(GlobalOptions.ProjectPath)!);
            var stateManager = BuildStateManager();
            var stateFile = await stateManager.LoadFileAsync(directory);

            if (!stateFile.Environments.ContainsKey(name))
            {
                AnsiConsole.MarkupLine($"[red]✗[/] Environment '[bold]{name}[/]' not found. Available: {string.Join(", ", stateFile.Environments.Keys)}");
                ctx.ExitCode = 1; return;
            }

            var previous = stateFile.ActiveEnvironment;
            stateFile.ActiveEnvironment = name;
            await stateManager.SaveFileAsync(stateFile, directory);

            AnsiConsole.MarkupLine($"[green]✓[/] Switched [dim]{previous}[/] → [bold cyan]{name}[/] ({stateFile.Environments[name].Target})");
        });
        return cmd;
    }

    // ── add ───────────────────────────────────────────────────────────────────

    private static Command AddCommand()
    {
        var nameArg = new Argument<string>("name", "New environment name.");
        var targetOpt = new Option<DeploymentTargetType>("--target", "Deployment target.") { IsRequired = true };
        var activateOpt = new Option<bool>("--activate", "Set as active after adding.");
        var registryOpt = new Option<string?>("--container-registry", "Container registry.");
        var tagOpt = new Option<string?>("--image-tag", "Default image tag.");
        var containerOsOpt = new Option<string?>("--container-os", "Target OS for container images (e.g. linux).");
        var containerArchOpt = new Option<string?>("--container-arch", "Target CPU architecture: x64 | arm64 | arm.");
        // Nomad
        var nomadAddrOpt = new Option<string?>("--nomad-address", "Nomad address.");
        var nomadNsOpt = new Option<string?>("--nomad-namespace", "Nomad namespace.");
        var nomadDcOpt = new Option<string?>("--nomad-datacenter", "Nomad datacenter.");
        var nomadTokOpt = new Option<string?>("--nomad-token", "Nomad ACL token.");
        // Kubernetes
        var k8SCtxOpt = new Option<string?>("--k8s-context", "kubectl context.");
        var k8SPrefOpt = new Option<string?>("--k8s-namespace-prefix", "Namespace prefix.");
        var k8SDomOpt = new Option<string?>("--k8s-ingress-domain", "Ingress base domain.");
        // Compose
        var cmpProjOpt = new Option<string?>("--compose-project", "Docker Compose project name.");
        var cmpRestOpt = new Option<string?>("--compose-restart", "Restart policy.");

        var cmd = new Command("add", "Add a new environment.") { nameArg };
        foreach (var o in new Option[] {
            GlobalOptions.ProjectPath, GlobalOptions.NonInteractive,
            targetOpt, activateOpt, registryOpt, tagOpt, containerOsOpt, containerArchOpt,
            nomadAddrOpt, nomadNsOpt, nomadDcOpt, nomadTokOpt,
            k8SCtxOpt, k8SPrefOpt, k8SDomOpt,
            cmpProjOpt, cmpRestOpt })
        {
	        cmd.AddOption(o);
        }

        cmd.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(nameArg);
            var nonInteractive = ctx.ParseResult.GetValueForOption(GlobalOptions.NonInteractive);
            var directory = Resolve(ctx.ParseResult.GetValueForOption(GlobalOptions.ProjectPath)!);
            var activate = ctx.ParseResult.GetValueForOption(activateOpt);
            var stateManager = BuildStateManager();

            DeployifyStateFile stateFile;
            try { stateFile = await stateManager.LoadFileAsync(directory); }
            catch (FileNotFoundException) { stateFile = new DeployifyStateFile { Environments = [] }; }

            if (stateFile.Environments.ContainsKey(name))
            {
                AnsiConsole.MarkupLine($"[yellow]![/] '{name}' already exists. Use [cyan]deployify env set[/] to update.");
                ctx.ExitCode = 1; return;
            }

            EnvironmentConfig config;
            if (nonInteractive)
            {
                var pr = ctx.ParseResult;
                var target = pr.GetValueForOption(targetOpt);
                config = new EnvironmentConfig
                {
                    Target = target,
                    ContainerRegistry = pr.GetValueForOption(registryOpt),
                    ImageTag = pr.GetValueForOption(tagOpt) ?? "latest",
                    TargetConfig = BuildTargetConfig(target,
                        pr.GetValueForOption(nomadAddrOpt),
                        pr.GetValueForOption(nomadTokOpt),
                        pr.GetValueForOption(nomadNsOpt),
                        pr.GetValueForOption(nomadDcOpt),
                        pr.GetValueForOption(k8SCtxOpt),
                        pr.GetValueForOption(k8SPrefOpt),
                        pr.GetValueForOption(k8SDomOpt),
                        pr.GetValueForOption(cmpProjOpt),
                        pr.GetValueForOption(cmpRestOpt))
                };
            }
            else
            {
                config = InitCommand.PromptEnvironmentConfig(name, directory);
            }

            stateFile.Environments[name] = config;
            if (activate || stateFile.Environments.Count == 1)
            {
	            stateFile.ActiveEnvironment = name;
            }

            await stateManager.SaveFileAsync(stateFile, directory);
            AnsiConsole.MarkupLine($"[green]✓[/] Environment [bold cyan]{name}[/] added ({config.Target}).");
            if (activate)
            {
	            AnsiConsole.MarkupLine($"  Now active: [bold cyan]{name}[/]");
            }
        });
        return cmd;
    }

    // ── set ───────────────────────────────────────────────────────────────────

    private static Command SetCommand()
    {
        var nameArg = new Argument<string>("name", "Environment to update.");
        var registryOpt = new Option<string?>("--container-registry", "Update container registry.");
        var tagOpt = new Option<string?>("--image-tag", "Update default image tag.");
        var containerOsOpt = new Option<string?>("--container-os", "Target OS for container images (e.g. linux).");
        var containerArchOpt = new Option<string?>("--container-arch", "Target CPU architecture: x64 | arm64 | arm.");
        var nomadAddrOpt = new Option<string?>("--nomad-address", "Update Nomad address.");
        var nomadTokOpt = new Option<string?>("--nomad-token", "Update Nomad token.");
        var nomadNsOpt = new Option<string?>("--nomad-namespace", "Update Nomad namespace.");
        var nomadDcOpt = new Option<string?>("--nomad-datacenter", "Update Nomad datacenter.");
        var nomadRegUserOpt = new Option<string?>("--nomad-registry-username", "Docker registry username (empty string to remove).");
        var nomadRegPassOpt = new Option<string?>("--nomad-registry-password", "Docker registry password or token (empty string to remove).");
        var nomadRegisterOpt = new Option<bool?>("--nomad-register-services", "Enable (true) or disable (false) service registration.");
        var nomadProviderOpt = new Option<string?>("--nomad-service-provider", "Service provider: nomad | consul.");
        var nomadHealthPathOpt = new Option<string?>("--nomad-health-check-path", "HTTP path for service health checks.");
        var deployDashboardOpt = new Option<bool?>("--deploy-dashboard", "Enable (true) or disable (false) the Aspire Dashboard.");
        var dashboardTagOpt = new Option<string?>("--dashboard-tag", "Image tag for the Aspire Dashboard.");
        var dashboardPortOpt = new Option<int?>("--dashboard-port", "Host port for the dashboard UI.");
        var dashboardOtlpPortOpt = new Option<int?>("--dashboard-otlp-port", "Host port for the OTLP gRPC endpoint.");
        var dashboardTokenOpt = new Option<string?>("--dashboard-token", "Browser auth token (empty string to remove).");
        var deployDaprOpt = new Option<bool?>("--deploy-dapr", "Enable (true) or disable (false) Dapr sidecar injection.");
        var daprVersionOpt = new Option<string?>("--dapr-version", "Dapr sidecar image tag.");
        var daprComponentsOpt = new Option<string?>("--dapr-components-path", "Path to Dapr components directory.");
        var daprConfigPathOpt = new Option<string?>("--dapr-config-path", "Path to Dapr config file.");
        var daprLogLevelOpt = new Option<string?>("--dapr-log-level", "Dapr sidecar log level.");

        var cmd = new Command("set", "Update settings on an existing environment.") { nameArg };
        foreach (var o in new Option[] { GlobalOptions.ProjectPath, registryOpt, tagOpt,
            containerOsOpt, containerArchOpt,
            nomadAddrOpt, nomadTokOpt, nomadNsOpt, nomadDcOpt,
            nomadRegUserOpt, nomadRegPassOpt,
            nomadRegisterOpt, nomadProviderOpt, nomadHealthPathOpt,
            deployDashboardOpt, dashboardTagOpt, dashboardPortOpt, dashboardOtlpPortOpt, dashboardTokenOpt,
            deployDaprOpt, daprVersionOpt, daprComponentsOpt, daprConfigPathOpt, daprLogLevelOpt })
        {
	        cmd.AddOption(o);
        }

        cmd.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(nameArg);
            var directory = Resolve(ctx.ParseResult.GetValueForOption(GlobalOptions.ProjectPath)!);
            var stateManager = BuildStateManager();
            var stateFile = await stateManager.LoadFileAsync(directory);

            if (!stateFile.Environments.TryGetValue(name, out var config))
            {
                AnsiConsole.MarkupLine($"[red]✗[/] Environment '{name}' not found."); ctx.ExitCode = 1; return;
            }

            var changed = new List<string>();
            var pr = ctx.ParseResult;

            void SetOpt<T>(Option<T?> opt, Action<T> apply) where T : class
            {
                var v = pr.GetValueForOption(opt);
                if (v is not null) { apply(v); changed.Add(opt.Name); }
            }

            SetOpt(registryOpt, v => config.ContainerRegistry = v);
            SetOpt(tagOpt, v => config.ImageTag = v);
            SetOpt(containerOsOpt, v => config.ContainerOs = v);
            SetOpt(containerArchOpt, v => config.ContainerArch = v);

            // Dapr
            var deployDapr = pr.GetValueForOption(deployDaprOpt);
            if (deployDapr.HasValue) { config.DeployDapr = deployDapr.Value; changed.Add("deploy-dapr"); }
            
            var daprVer = pr.GetValueForOption(daprVersionOpt);
            if (daprVer is not null) { config.Dapr.DaprVersion = daprVer; changed.Add("dapr-version"); }
            
            var daprComp = pr.GetValueForOption(daprComponentsOpt);
            if (daprComp is not null) { config.Dapr.ComponentsPath = string.IsNullOrEmpty(daprComp) ? null : daprComp; changed.Add("dapr-components-path"); }
            
            var daprCfgPath = pr.GetValueForOption(daprConfigPathOpt);
            if (daprCfgPath is not null) { config.Dapr.ConfigPath = string.IsNullOrEmpty(daprCfgPath) ? null : daprCfgPath; changed.Add("dapr-config-path"); }
            
            var daprLog = pr.GetValueForOption(daprLogLevelOpt);
            if (daprLog is not null) { config.Dapr.LogLevel = daprLog; changed.Add("dapr-log-level"); }
            
            var deployDb = pr.GetValueForOption(deployDashboardOpt);
            if (deployDb.HasValue) { config.DeployDashboard = deployDb.Value; changed.Add("deploy-dashboard"); }
            
            var dbTag = pr.GetValueForOption(dashboardTagOpt);
            if (dbTag is not null) { config.Dashboard.ImageTag = dbTag; changed.Add("dashboard-tag"); }
            
            var dbPort = pr.GetValueForOption(dashboardPortOpt);
            if (dbPort.HasValue) { config.Dashboard.Port = dbPort.Value; changed.Add("dashboard-port"); }
            
            var dbOtlp = pr.GetValueForOption(dashboardOtlpPortOpt);
            if (dbOtlp.HasValue) { config.Dashboard.OtlpPort = dbOtlp.Value; changed.Add("dashboard-otlp-port"); }
            
            var dbToken = pr.GetValueForOption(dashboardTokenOpt);
            if (dbToken is not null)
            {
                config.Dashboard.BrowserToken = string.IsNullOrEmpty(dbToken) ? null : dbToken;
                changed.Add("dashboard-token");
            }

            // Patch nomad sub-config
            if (config.Target == DeploymentTargetType.Nomad &&
                config.TargetConfig.TryGetValue("nomad", out var rawNomad) &&
                rawNomad is JsonElement nomadEl)
            {
                var nc = nomadEl.Deserialize<NomadTargetConfig>()!;
                SetOpt(nomadAddrOpt, v => nc.Address = v);
                SetOpt(nomadTokOpt, v => nc.Token = v);
                SetOpt(nomadNsOpt, v => nc.Namespace = v);
                SetOpt(nomadDcOpt, v => nc.Datacenters = [v]);
                var regUser = ctx.ParseResult.GetValueForOption(nomadRegUserOpt);
                if (regUser is not null) { nc.RegistryUsername = string.IsNullOrEmpty(regUser) ? null : regUser; changed.Add("nomad-registry-username"); }
                
                var regPass = ctx.ParseResult.GetValueForOption(nomadRegPassOpt);
                if (regPass is not null) { nc.RegistryPassword = string.IsNullOrEmpty(regPass) ? null : regPass; changed.Add("nomad-registry-password"); }
                
                var register = ctx.ParseResult.GetValueForOption(nomadRegisterOpt);
                if (register.HasValue) { nc.RegisterServices = register.Value; changed.Add("nomad-register-services"); }
                
                var provider = ctx.ParseResult.GetValueForOption(nomadProviderOpt);
                if (provider is not null) { nc.ServiceProvider = provider; changed.Add("nomad-service-provider"); }
                
                var healthPath = ctx.ParseResult.GetValueForOption(nomadHealthPathOpt);
                if (healthPath is not null) { nc.HealthCheckPath = healthPath; changed.Add("nomad-health-check-path"); }
                
                if (changed.Any(c => c.StartsWith("nomad")))
                {
	                config.TargetConfig["nomad"] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(nc));
                }
            }

            if (changed.Count == 0) { AnsiConsole.MarkupLine("[yellow]No changes specified.[/]"); return; }

            await stateManager.SaveFileAsync(stateFile, directory);
            AnsiConsole.MarkupLine($"[green]✓[/] [bold cyan]{name}[/] updated: {string.Join(", ", changed)}");
        });
        return cmd;
    }

    // ── remove ────────────────────────────────────────────────────────────────

    private static Command RemoveCommand()
    {
        var nameArg = new Argument<string>("name", "Environment to remove.");
        var forceOpt = new Option<bool>("--force", "Skip confirmation prompt.");
        var cmd = new Command("remove", "Remove an environment.") { nameArg };
        cmd.AddOption(GlobalOptions.ProjectPath);
        cmd.AddOption(forceOpt);
        cmd.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(nameArg);
            var force = ctx.ParseResult.GetValueForOption(forceOpt);
            var directory = Resolve(ctx.ParseResult.GetValueForOption(GlobalOptions.ProjectPath)!);
            var stateManager = BuildStateManager();
            var stateFile = await stateManager.LoadFileAsync(directory);

            if (!stateFile.Environments.ContainsKey(name))
            {
	            AnsiConsole.MarkupLine($"[red]✗[/] '{name}' not found."); 
	            ctx.ExitCode = 1; 
	            return;
            }

            if (stateFile.Environments.Count == 1)
            {
	            AnsiConsole.MarkupLine("[red]✗[/] Cannot remove the only environment."); 
	            ctx.ExitCode = 1; 
	            return; 
            }

            if (stateFile.ActiveEnvironment == name)
            {
	            AnsiConsole.MarkupLine("[red]✗[/] Cannot remove the active environment. Switch first."); 
	            ctx.ExitCode = 1; 
	            return;
            }

            if (!force && !AnsiConsole.Confirm($"Remove '[bold]{name}[/]'?", defaultValue: false))
            {
	            return;
            }

            stateFile.Environments.Remove(name);
            await stateManager.SaveFileAsync(stateFile, directory);
            AnsiConsole.MarkupLine($"[green]✓[/] Environment [bold]{name}[/] removed.");
        });
        return cmd;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal static Dictionary<string, object?> BuildTargetConfig(
        DeploymentTargetType target,
        string? nomadAddr, string? nomadToken, string? nomadNs, string? nomadDc,
        string? k8SContext, string? k8SPrefix, string? k8SDomain,
        string? composeProject, string? composeRestart)
    {
        return target switch
        {
            DeploymentTargetType.Nomad => Serialize("nomad", new NomadTargetConfig
            {
                Address = nomadAddr ?? "http://localhost:4646",
                Token = string.IsNullOrWhiteSpace(nomadToken) ? null : nomadToken,
                Namespace = nomadNs ?? "default",
                Datacenters = [nomadDc ?? "dc1"]
            }),
            DeploymentTargetType.Kubernetes => Serialize("kubernetes", new KubernetesTargetConfig
            {
                Context = string.IsNullOrWhiteSpace(k8SContext) ? null : k8SContext,
                NamespacePrefix = k8SPrefix ?? "deployify",
                IngressBaseDomain = string.IsNullOrWhiteSpace(k8SDomain) ? null : k8SDomain,
                GenerateIngress = !string.IsNullOrWhiteSpace(k8SDomain)
            }),
            DeploymentTargetType.Compose => Serialize("compose", new ComposeTargetConfig
            {
                ProjectName = composeProject ?? "deployify",
                RestartPolicy = composeRestart ?? "unless-stopped",
                GenerateProfiles = true,
                GenerateEnvFile = true,
                GenerateMergedFile = true
            }),
            DeploymentTargetType.AwsEcs => Serialize("ecs", new EcsTargetConfig()),
            DeploymentTargetType.AzureContainerApps => Serialize("aca", new AcaTargetConfig()),
            DeploymentTargetType.NomadConsul => Serialize("nomadconsul", new NomadConsulTargetConfig()),
            _ => []
        };
    }

    private static Dictionary<string, object?> Serialize(string key, object cfg) => new Dictionary<string, object?> { [key] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(cfg)) };

    private static string GetDisplayAddress(EnvironmentConfig cfg)
    {
        var key = cfg.Target switch
        {
            DeploymentTargetType.Nomad => "nomad",
            DeploymentTargetType.Kubernetes => "kubernetes",
            DeploymentTargetType.Compose => "compose",
            DeploymentTargetType.AwsEcs => "ecs",
            DeploymentTargetType.AzureContainerApps => "aca",
            DeploymentTargetType.NomadConsul => "nomadconsul",
            _ => cfg.Target.ToString().ToLower()
        };
        if (!cfg.TargetConfig.TryGetValue(key, out var raw) || raw is not JsonElement el)
        {
	        return "[dim]-[/]";
        }

        return cfg.Target switch
        {
            DeploymentTargetType.Nomad => el.TryGetProperty("address", out var a) ? a.GetString() ?? "-" : "-",
            DeploymentTargetType.Kubernetes => el.TryGetProperty("context", out var c) ? c.GetString() ?? "(current)" : "(current)",
            DeploymentTargetType.Compose => el.TryGetProperty("projectName", out var p) ? p.GetString() ?? "-" : "-",
            DeploymentTargetType.AwsEcs => el.TryGetProperty("region", out var r) ? r.GetString() ?? "-" : "-",
            DeploymentTargetType.AzureContainerApps => el.TryGetProperty("location", out var l) ? l.GetString() ?? "-" : "-",
            DeploymentTargetType.NomadConsul => el.TryGetProperty("nomadAddress", out var na) ? na.GetString() ?? "-" : "-",
            _ => "-"
        };
    }

    private static IStateManager BuildStateManager() =>
        new ServiceCollection()
            .AddSingleton<IStateManager, StateManager>()
            .BuildServiceProvider()
            .GetRequiredService<IStateManager>();

    private static string Resolve(string p) =>
        File.Exists(p) ? Path.GetDirectoryName(Path.GetFullPath(p))! : Path.GetFullPath(p);
}
