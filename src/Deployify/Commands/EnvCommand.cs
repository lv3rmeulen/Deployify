using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Deployify.Target.AwsEcs;
using Deployify.Target.AzureContainerApps;
using Deployify.Target.Compose;
using Deployify.Target.GitOps;
using Deployify.Target.Helm;
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

            if (!stateFile.Environments.ContainsKey(name))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] Environment '[bold]{name}[/]' not found. Available: {string.Join(", ", stateFile.Environments.Keys)}");
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

    private static Command AddCommand()
    {
        var nameArg = new Argument<string>("name")
        {
            Description = "New environment name."
        };
        var targetOpt = new Option<DeploymentTargetType>("--target")
        {
            Description = TargetOption.Description,
            Required = true,
            CustomParser = TargetOption.Parse
        };
        var activateOpt = new Option<bool>("--activate")
        {
            Description = "Set as active after adding."
        };
        var registryOpt = new Option<string?>("--container-registry")
        {
            Description = "Container registry."
        };
        var tagOpt = new Option<string?>("--image-tag")
        {
            Description = "Default image tag."
        };
        var containerOsOpt = new Option<string?>("--container-os")
        {
            Description = "Target OS for container images (e.g. linux)."
        };
        var containerArchOpt = new Option<string?>("--container-arch")
        {
            Description = "Target CPU architecture: x64 | arm64 | arm."
        };
        // Nomad
        var nomadAddrOpt = new Option<string?>("--nomad-address")
        {
            Description = "Nomad address."
        };
        var nomadNsOpt = new Option<string?>("--nomad-namespace")
        {
            Description = "Nomad namespace."
        };
        var nomadDcOpt = new Option<string?>("--nomad-datacenter")
        {
            Description = "Nomad datacenter."
        };
        var nomadTokOpt = new Option<string?>("--nomad-token")
        {
            Description = "Nomad ACL token."
        };
        // Kubernetes
        var kubernetesCtxOpt = new Option<string?>("--kubernetes-context")
        {
            Description = "kubectl context."
        };
        var kubernetesPrefOpt = new Option<string?>("--kubernetes-namespace-prefix")
        {
            Description = "Namespace prefix."
        };
        var kubernetesDomOpt = new Option<string?>("--kubernetes-ingress-domain")
        {
            Description = "Ingress base domain."
        };
        // Compose
        var cmpProjOpt = new Option<string?>("--compose-project")
        {
            Description = "Docker Compose project name."
        };
        var cmpRestOpt = new Option<string?>("--compose-restart")
        {
            Description = "Restart policy."
        };

        var cmd = new Command("add", "Add a new environment.") { nameArg };
        Option[] addOptions =
        [
            GlobalOptions.ProjectPath, GlobalOptions.NonInteractive,
            targetOpt, activateOpt, registryOpt, tagOpt, containerOsOpt, containerArchOpt,
            nomadAddrOpt, nomadNsOpt, nomadDcOpt, nomadTokOpt,
            kubernetesCtxOpt, kubernetesPrefOpt, kubernetesDomOpt,
            cmpProjOpt, cmpRestOpt
        ];
        foreach (var o in addOptions)
        {
            cmd.Options.Add(o);
        }

        cmd.SetAction(async (parseResult, ct) =>
        {
            var name = parseResult.GetRequiredValue(nameArg);
            var nonInteractive = parseResult.GetValue(GlobalOptions.NonInteractive);
            var directory = Resolve(parseResult.GetValue(GlobalOptions.ProjectPath)!);
            var activate = parseResult.GetValue(activateOpt);
            var stateManager = BuildStateManager();

            DeployifyStateFile stateFile;
            try
            {
                stateFile = await stateManager.LoadFileAsync(directory, ct);
            }
            catch (FileNotFoundException)
            {
                stateFile = new DeployifyStateFile { Environments = [] };
            }

            if (stateFile.Environments.ContainsKey(name))
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]![/] '{name}' already exists. Use [cyan]deployify env set[/] to update.");
                return 1;
            }

            EnvironmentConfig config;
            if (nonInteractive)
            {
                var pr = parseResult;
                var target = pr.GetValue(targetOpt);
                config = new EnvironmentConfig
                {
                    Target = target,
                    ContainerRegistry = pr.GetValue(registryOpt),
                    ImageTag = pr.GetValue(tagOpt) ?? "latest",
                    TargetConfig = BuildTargetConfig(target,
                        pr.GetValue(nomadAddrOpt),
                        pr.GetValue(nomadTokOpt),
                        pr.GetValue(nomadNsOpt),
                        pr.GetValue(nomadDcOpt),
                        pr.GetValue(kubernetesCtxOpt),
                        pr.GetValue(kubernetesPrefOpt),
                        pr.GetValue(kubernetesDomOpt),
                        pr.GetValue(cmpProjOpt),
                        pr.GetValue(cmpRestOpt))
                };
            }
            else
            {
                config = await InitCommand.PromptEnvironmentConfigAsync(name, directory, ct);
            }

            stateFile.Environments[name] = config;
            if (activate || stateFile.Environments.Count == 1)
            {
                stateFile.ActiveEnvironment = name;
            }

            await stateManager.SaveFileAsync(stateFile, directory, ct);
            await SecretsWarning.PrintAsync(stateFile, directory, ct);
            AnsiConsole.MarkupLineInterpolated($"[green]✓[/] Environment [bold cyan]{name}[/] added ({config.Target}).");
            if (activate)
            {
                AnsiConsole.MarkupLineInterpolated($"  Now active: [bold cyan]{name}[/]");
            }

            return 0;
        });
        return cmd;
    }

    // ── set ───────────────────────────────────────────────────────────────────

    private static Command SetCommand()
    {
        var nameArg = new Argument<string>("name")
        {
            Description = "Environment to update."
        };
        var registryOpt = new Option<string?>("--container-registry")
        {
            Description = "Update container registry."
        };
        var tagOpt = new Option<string?>("--image-tag")
        {
            Description = "Update default image tag."
        };
        var containerOsOpt = new Option<string?>("--container-os")
        {
            Description = "Target OS for container images (e.g. linux)."
        };
        var containerArchOpt = new Option<string?>("--container-arch")
        {
            Description = "Target CPU architecture: x64 | arm64 | arm."
        };
        var nomadAddrOpt = new Option<string?>("--nomad-address")
        {
            Description = "Update Nomad address."
        };
        var nomadTokOpt = new Option<string?>("--nomad-token")
        {
            Description = "Update Nomad token."
        };
        var nomadNsOpt = new Option<string?>("--nomad-namespace")
        {
            Description = "Update Nomad namespace."
        };
        var nomadDcOpt = new Option<string?>("--nomad-datacenter")
        {
            Description = "Update Nomad datacenter."
        };
        var nomadRegUserOpt = new Option<string?>("--nomad-registry-username")
        {
            Description = "Docker registry username (empty string to remove)."
        };
        var nomadRegPassOpt = new Option<string?>("--nomad-registry-password")
        {
            Description = "Docker registry password or token (empty string to remove)."
        };
        var nomadRegisterOpt = new Option<bool?>("--nomad-register-services")
        {
            Description = "Enable (true) or disable (false) service registration."
        };
        var nomadProviderOpt = new Option<string?>("--nomad-service-provider")
        {
            Description = "Service provider: nomad | consul."
        };
        var nomadHealthPathOpt = new Option<string?>("--nomad-health-check-path")
        {
            Description = "HTTP path for service health checks."
        };
        var deployDashboardOpt = new Option<bool?>("--deploy-dashboard")
        {
            Description = "Enable (true) or disable (false) the Aspire Dashboard."
        };
        var dashboardTagOpt = new Option<string?>("--dashboard-tag")
        {
            Description = "Image tag for the Aspire Dashboard."
        };
        var dashboardPortOpt = new Option<int?>("--dashboard-port")
        {
            Description = "Host port for the dashboard UI."
        };
        var dashboardOtlpPortOpt = new Option<int?>("--dashboard-otlp-port")
        {
            Description = "Host port for the OTLP gRPC endpoint."
        };
        var dashboardTokenOpt = new Option<string?>("--dashboard-token")
        {
            Description = "Browser auth token (empty string to remove)."
        };
        var deployDaprOpt = new Option<bool?>("--deploy-dapr")
        {
            Description = "Enable (true) or disable (false) Dapr sidecar injection."
        };
        var daprVersionOpt = new Option<string?>("--dapr-version")
        {
            Description = "Dapr sidecar image tag."
        };
        var daprComponentsOpt = new Option<string?>("--dapr-components-path")
        {
            Description = "Path to Dapr components directory."
        };
        var daprConfigPathOpt = new Option<string?>("--dapr-config-path")
        {
            Description = "Path to Dapr config file."
        };
        var daprLogLevelOpt = new Option<string?>("--dapr-log-level")
        {
            Description = "Dapr sidecar log level."
        };

        var cmd = new Command("set", "Update settings on an existing environment.") { nameArg };
        foreach (var o in new Option[] { GlobalOptions.ProjectPath, registryOpt, tagOpt,
            containerOsOpt, containerArchOpt,
            nomadAddrOpt, nomadTokOpt, nomadNsOpt, nomadDcOpt,
            nomadRegUserOpt, nomadRegPassOpt,
            nomadRegisterOpt, nomadProviderOpt, nomadHealthPathOpt,
            deployDashboardOpt, dashboardTagOpt, dashboardPortOpt, dashboardOtlpPortOpt, dashboardTokenOpt,
            deployDaprOpt, daprVersionOpt, daprComponentsOpt, daprConfigPathOpt, daprLogLevelOpt })
        {
            cmd.Options.Add(o);
        }

        cmd.SetAction(async (parseResult, ct) =>
        {
            var name = parseResult.GetRequiredValue(nameArg);
            var directory = Resolve(parseResult.GetValue(GlobalOptions.ProjectPath)!);
            var stateManager = BuildStateManager();
            var stateFile = await stateManager.LoadFileAsync(directory, ct);

            if (!stateFile.Environments.TryGetValue(name, out var config))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] Environment '{name}' not found.");
                return 1;
            }

            var changed = new List<string>();
            var pr = parseResult;

            void SetOpt<T>(Option<T?> opt, Action<T> apply) where T : class
            {
                var v = pr.GetValue(opt);
                if (v is not null)
                {
                    apply(v);
                    changed.Add(opt.Name);
                }
            }

            SetOpt(registryOpt, v => config.ContainerRegistry = v);
            SetOpt(tagOpt, v => config.ImageTag = v);
            SetOpt(containerOsOpt, v => config.ContainerOs = v);
            SetOpt(containerArchOpt, v => config.ContainerArch = v);

            // Dapr
            var deployDapr = pr.GetValue(deployDaprOpt);
            if (deployDapr.HasValue)
            {
                config.DeployDapr = deployDapr.Value;
                changed.Add("deploy-dapr");
            }

            var daprVer = pr.GetValue(daprVersionOpt);
            if (daprVer is not null)
            {
                config.Dapr.DaprVersion = daprVer;
                changed.Add("dapr-version");
            }

            var daprComp = pr.GetValue(daprComponentsOpt);
            if (daprComp is not null)
            {
                config.Dapr.ComponentsPath = string.IsNullOrEmpty(daprComp) ? null : daprComp;
                changed.Add("dapr-components-path");
            }

            var daprCfgPath = pr.GetValue(daprConfigPathOpt);
            if (daprCfgPath is not null)
            {
                config.Dapr.ConfigPath = string.IsNullOrEmpty(daprCfgPath) ? null : daprCfgPath;
                changed.Add("dapr-config-path");
            }

            var daprLog = pr.GetValue(daprLogLevelOpt);
            if (daprLog is not null)
            {
                config.Dapr.LogLevel = daprLog;
                changed.Add("dapr-log-level");
            }

            var deployDb = pr.GetValue(deployDashboardOpt);
            if (deployDb.HasValue)
            {
                config.DeployDashboard = deployDb.Value;
                changed.Add("deploy-dashboard");
            }

            var dbTag = pr.GetValue(dashboardTagOpt);
            if (dbTag is not null)
            {
                config.Dashboard.ImageTag = dbTag;
                changed.Add("dashboard-tag");
            }

            var dbPort = pr.GetValue(dashboardPortOpt);
            if (dbPort.HasValue)
            {
                config.Dashboard.Port = dbPort.Value;
                changed.Add("dashboard-port");
            }

            var dbOtlp = pr.GetValue(dashboardOtlpPortOpt);
            if (dbOtlp.HasValue)
            {
                config.Dashboard.OtlpPort = dbOtlp.Value;
                changed.Add("dashboard-otlp-port");
            }

            var dbToken = pr.GetValue(dashboardTokenOpt);
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
                // Same options init uses, so stri"Docker" deserialize.
                var nc = nomadEl.Deserialize<NomadTargetConfig>(TargetConfigJsonOptions.Default)!;
                SetOpt(nomadAddrOpt, v => nc.Address = v);
                SetOpt(nomadTokOpt, v => nc.Token = v);
                SetOpt(nomadNsOpt, v => nc.Namespace = v);
                SetOpt(nomadDcOpt, v => nc.Datacenters = [v]);
                var regUser = parseResult.GetValue(nomadRegUserOpt);
                if (regUser is not null)
                {
                    nc.RegistryUsername = string.IsNullOrEmpty(regUser) ? null : regUser;
                    changed.Add("nomad-registry-username");
                }

                var regPass = parseResult.GetValue(nomadRegPassOpt);
                if (regPass is not null)
                {
                    nc.RegistryPassword = string.IsNullOrEmpty(regPass) ? null : regPass;
                    changed.Add("nomad-registry-password");
                }

                var register = parseResult.GetValue(nomadRegisterOpt);
                if (register.HasValue)
                {
                    nc.RegisterServices = register.Value;
                    changed.Add("nomad-register-services");
                }

                var provider = parseResult.GetValue(nomadProviderOpt);
                if (provider is not null)
                {
                    nc.ServiceProvider = provider;
                    changed.Add("nomad-service-provider");
                }

                var healthPath = parseResult.GetValue(nomadHealthPathOpt);
                if (healthPath is not null)
                {
                    nc.HealthCheckPath = healthPath;
                    changed.Add("nomad-health-check-path");
                }

                if (changed.Any(c => c.StartsWith("nomad")))
                {
                    config.TargetConfig["nomad"] = JsonSerializer.SerializeToElement(nc, TargetConfigJsonOptions.Default);
                }
            }

            if (changed.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No changes specified.[/]");
                return 0;
            }

            await stateManager.SaveFileAsync(stateFile, directory, ct);
            await SecretsWarning.PrintAsync(stateFile, directory, ct);
            AnsiConsole.MarkupLineInterpolated($"[green]✓[/] [bold cyan]{name}[/] updated: {string.Join(", ", changed)}");

            return 0;
        });
        return cmd;
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

    internal static Dictionary<string, object?> BuildTargetConfig(
        DeploymentTargetType target,
        string? nomadAddr, string? nomadToken, string? nomadNs, string? nomadDc,
        string? kubernetesContext, string? kubernetesPrefix, string? kubernetesDomain,
        string? composeProject, string? composeRestart)
    {
        return target.Value switch
        {
            DeploymentTargetType.Names.Nomad => Serialize("nomad", new NomadTargetConfig
            {
                Address = nomadAddr ?? "http://localhost:4646",
                Token = string.IsNullOrWhiteSpace(nomadToken) ? null : nomadToken,
                Namespace = nomadNs ?? "default",
                Datacenters = [nomadDc ?? "dc1"]
            }),
            DeploymentTargetType.Names.Kubernetes => Serialize("kubernetes", new KubernetesTargetConfig
            {
                Context = string.IsNullOrWhiteSpace(kubernetesContext) ? null : kubernetesContext,
                NamespacePrefix = kubernetesPrefix ?? "deployify",
                IngressBaseDomain = string.IsNullOrWhiteSpace(kubernetesDomain) ? null : kubernetesDomain,
                GenerateIngress = !string.IsNullOrWhiteSpace(kubernetesDomain)
            }),
            DeploymentTargetType.Names.Compose => Serialize("compose", new ComposeTargetConfig
            {
                ProjectName = composeProject ?? "deployify",
                RestartPolicy = composeRestart ?? "unless-stopped",
                GenerateProfiles = true,
                GenerateEnvFile = true,
                GenerateMergedFile = true
            }),
            DeploymentTargetType.Names.AwsEcs => Serialize("ecs", new EcsTargetConfig()),
            DeploymentTargetType.Names.AzureContainerApps => Serialize("aca", new AcaTargetConfig()),
            DeploymentTargetType.Names.NomadConsul => Serialize("nomadconsul", new NomadConsulTargetConfig()),
            // Helm deploys the Kubernetes target's manifests, so it reads the same settings.
            DeploymentTargetType.Names.Helm => new Dictionary<string, object?>
            {
                ["kubernetes"] = JsonSerializer.SerializeToElement(new KubernetesTargetConfig
                {
                    Context = string.IsNullOrWhiteSpace(kubernetesContext) ? null : kubernetesContext,
                    NamespacePrefix = kubernetesPrefix ?? "deployify",
                    IngressBaseDomain = string.IsNullOrWhiteSpace(kubernetesDomain) ? null : kubernetesDomain,
                    GenerateIngress = !string.IsNullOrWhiteSpace(kubernetesDomain)
                }, TargetConfigJsonOptions.Default),
                ["helm"] = JsonSerializer.SerializeToElement(new HelmTargetConfig(), TargetConfigJsonOptions.Default)
            },
            // The GitOps targets render the Kubernetes target's manifests; set gitops.repoUrl before applying.
            DeploymentTargetType.Names.ArgoCd or DeploymentTargetType.Names.Flux => new Dictionary<string, object?>
            {
                ["kubernetes"] = JsonSerializer.SerializeToElement(new KubernetesTargetConfig
                {
                    Context = string.IsNullOrWhiteSpace(kubernetesContext) ? null : kubernetesContext,
                    NamespacePrefix = kubernetesPrefix ?? "deployify",
                    IngressBaseDomain = string.IsNullOrWhiteSpace(kubernetesDomain) ? null : kubernetesDomain,
                    GenerateIngress = !string.IsNullOrWhiteSpace(kubernetesDomain)
                }, TargetConfigJsonOptions.Default),
                ["gitops"] = JsonSerializer.SerializeToElement(new GitOpsTargetConfig(), TargetConfigJsonOptions.Default),
                [target == DeploymentTargetType.ArgoCd ? "argocd" : "flux"] = target == DeploymentTargetType.ArgoCd
                    ? JsonSerializer.SerializeToElement(new ArgoCdTargetConfig(), TargetConfigJsonOptions.Default)
                    : JsonSerializer.SerializeToElement(new FluxTargetConfig(), TargetConfigJsonOptions.Default)
            },
            // OVHcloud deploys the Kubernetes target's manifests; set ovhcloud.projectId and clusterId.
            DeploymentTargetType.Names.OvhCloud => new Dictionary<string, object?>
            {
                ["kubernetes"] = JsonSerializer.SerializeToElement(new KubernetesTargetConfig
                {
                    NamespacePrefix = kubernetesPrefix ?? "deployify",
                    IngressBaseDomain = string.IsNullOrWhiteSpace(kubernetesDomain) ? null : kubernetesDomain,
                    GenerateIngress = !string.IsNullOrWhiteSpace(kubernetesDomain)
                }, TargetConfigJsonOptions.Default),
                ["ovhcloud"] = JsonSerializer.SerializeToElement(new OvhCloudTargetConfig { Endpoint = "ovh-eu" }, TargetConfigJsonOptions.Default)
            },
            _ => []
        };
    }

    private static Dictionary<string, object?> Serialize(string key, object cfg)
        => new() { [key] = JsonSerializer.SerializeToElement(cfg, TargetConfigJsonOptions.Default) };

    private static string GetDisplayAddress(EnvironmentConfig cfg)
    {
        var key = cfg.Target.Value switch
        {
            DeploymentTargetType.Names.Nomad => "nomad",
            DeploymentTargetType.Names.Kubernetes or DeploymentTargetType.Names.Helm => "kubernetes",
            DeploymentTargetType.Names.Compose => "compose",
            DeploymentTargetType.Names.AwsEcs => "ecs",
            DeploymentTargetType.Names.AzureContainerApps => "aca",
            DeploymentTargetType.Names.NomadConsul => "nomadconsul",
            DeploymentTargetType.Names.ArgoCd or DeploymentTargetType.Names.Flux => "gitops",
            DeploymentTargetType.Names.OvhCloud => "ovhcloud",
            _ => cfg.Target.ToString().ToLower()
        };
        if (!cfg.TargetConfig.TryGetValue(key, out var raw) || raw is not JsonElement el)
        {
            return "[dim]-[/]";
        }

        return cfg.Target.Value switch
        {
            DeploymentTargetType.Names.Nomad => el.TryGetProperty("address", out var a) ? a.GetString() ?? "-" : "-",
            DeploymentTargetType.Names.Kubernetes or DeploymentTargetType.Names.Helm => el.TryGetProperty("context", out var c) ? c.GetString() ?? "(current)" : "(current)",
            DeploymentTargetType.Names.Compose => el.TryGetProperty("projectName", out var p) ? p.GetString() ?? "-" : "-",
            DeploymentTargetType.Names.AwsEcs => el.TryGetProperty("region", out var r) ? r.GetString() ?? "-" : "-",
            DeploymentTargetType.Names.AzureContainerApps => el.TryGetProperty("location", out var l) ? l.GetString() ?? "-" : "-",
            DeploymentTargetType.Names.NomadConsul => el.TryGetProperty("nomadAddress", out var na) ? na.GetString() ?? "-" : "-",
            DeploymentTargetType.Names.ArgoCd or DeploymentTargetType.Names.Flux => el.TryGetProperty("repoUrl", out var g) ? g.GetString() ?? "-" : "-",
            DeploymentTargetType.Names.OvhCloud => el.TryGetProperty("clusterId", out var k) || el.TryGetProperty("ClusterId", out k) ? k.GetString() ?? "-" : "-",
            _ => "-"
        };
    }

    private static IStateManager BuildStateManager()
        => new ServiceCollection()
            .AddSingleton<IStateManager, StateManager>()
            .BuildServiceProvider()
            .GetRequiredService<IStateManager>();

    private static string Resolve(string p)
        => File.Exists(p) ? Path.GetDirectoryName(Path.GetFullPath(p))! : Path.GetFullPath(p);
}
