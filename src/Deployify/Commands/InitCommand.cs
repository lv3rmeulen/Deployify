using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Deployify.Target.AwsEcs;
using Deployify.Target.AzureContainerApps;
using Deployify.Target.Compose;
using Deployify.Target.GitOps;
using Deployify.Target.Helm;
using Deployify.Target.Iis;
using Deployify.Target.Kubernetes;
using Deployify.Target.Nomad;
using Deployify.Target.NomadConsul;
using Deployify.Target.OvhCloud;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Deployify.Commands;

public static class InitCommand
{
    public static Command Build()
    {
        var cmd = new Command("init", "Initialize deployify in an Aspire AppHost project.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.NonInteractive);

        // Common flags
        var envNameOpt = new Option<string>("--env-name")
        {
            Description = "Environment name.",
            DefaultValueFactory = _ => "local"
        };
        var targetOpt = new Option<DeploymentTargetType?>("--target")
        {
            Description = TargetOption.Description,
            CustomParser = TargetOption.ParseOptional
        };
        var registryOpt = new Option<string?>("--container-registry")
        {
            Description = "Container registry URL."
        };
        var tagOpt = new Option<string>("--image-tag")
        {
            Description = "Default image tag.",
            DefaultValueFactory = _ => "latest"
        };
        var outPathOpt = new Option<string>("--output-path")
        {
            Description = "Output folder for artifacts.",
            DefaultValueFactory = _ => "deployify-output"
        };
        var runtimeOpt = new Option<ContainerRuntime>("--container-runtime")
        {
            Description = "Container runtime for publishing: docker (default) | podman.",
            DefaultValueFactory = _ => ContainerRuntime.Docker
        };
        var containerOsOpt = new Option<string>("--container-os")
        {
            Description = "Target OS for container images (default: linux).",
            DefaultValueFactory = _ => "linux"
        };
        var containerArchOpt = new Option<string>("--container-arch")
        {
            Description = "Target CPU architecture for container images: x64 (default) | arm64 | arm.",
            DefaultValueFactory = _ => "x64"
        };

        // Dapr
        var daprOpt = new Option<bool>("--deploy-dapr")
        {
            Description = "Inject a Dapr sidecar into every service.",
            DefaultValueFactory = _ => false
        };
        var daprVersionOpt = new Option<string>("--dapr-version")
        {
            Description = "Dapr sidecar image tag.",
            DefaultValueFactory = _ => "latest"
        };
        var daprHttpPortOpt = new Option<int>("--dapr-http-port")
        {
            Description = "Base Dapr HTTP port.",
            DefaultValueFactory = _ => 3500
        };
        var daprGrpcPortOpt = new Option<int>("--dapr-grpc-port")
        {
            Description = "Base Dapr gRPC port.",
            DefaultValueFactory = _ => 50001
        };
        var daprComponentsOpt = new Option<string?>("--dapr-components-path")
        {
            Description = "Path to Dapr components directory."
        };
        var daprConfigOpt = new Option<string?>("--dapr-config-path")
        {
            Description = "Path to Dapr config file."
        };
        var daprLogLevelOpt = new Option<string>("--dapr-log-level")
        {
            Description = "Dapr sidecar log level.",
            DefaultValueFactory = _ => "info"
        };

        // Dashboard
        var dashboardOpt = new Option<bool>("--deploy-dashboard")
        {
            Description = "Inject the Aspire Dashboard container into every deployment.",
            DefaultValueFactory = _ => false
        };
        var dashboardTagOpt = new Option<string>("--dashboard-tag")
        {
            Description = "Image tag for mcr.microsoft.com/dotnet/aspire-dashboard.",
            DefaultValueFactory = _ => "latest"
        };
        var dashboardPortOpt = new Option<int>("--dashboard-port")
        {
            Description = "Host port for the dashboard UI.",
            DefaultValueFactory = _ => 18888
        };
        var dashboardOtlpPortOpt = new Option<int>("--dashboard-otlp-port")
        {
            Description = "Host port for the OTLP gRPC endpoint.",
            DefaultValueFactory = _ => 18889
        };
        var dashboardTokenOpt = new Option<string?>("--dashboard-token")
        {
            Description = "Browser auth token (omit for unauthenticated)."
        };

        // Nomad-specific
        var nomadAddrOpt = new Option<string>("--nomad-address")
        {
            Description = "Nomad cluster address.",
            DefaultValueFactory = _ => "http://localhost:4646"
        };
        var nomadTokenOpt = new Option<string?>("--nomad-token")
        {
            Description = "Nomad ACL token."
        };
        var nomadNsOpt = new Option<string>("--nomad-namespace")
        {
            Description = "Nomad namespace.",
            DefaultValueFactory = _ => "default"
        };
        var nomadModeOpt = new Option<NomadDeploymentMode>("--nomad-mode")
        {
            Description = "Nomad deployment mode: Docker (default) | RawExec.",
            DefaultValueFactory = _ => NomadDeploymentMode.Docker
        };
        var nomadRegUserOpt = new Option<string?>("--nomad-registry-username")
        {
            Description = "Docker registry username for private image pulls."
        };
        var nomadRegPassOpt = new Option<string?>("--nomad-registry-password")
        {
            Description = "Docker registry password or access token."
        };
        var nomadRegisterOpt = new Option<bool>("--nomad-register-services")
        {
            Description = "Register services in Nomad/Consul service catalog.",
            DefaultValueFactory = _ => true
        };
        var nomadProviderOpt = new Option<string>("--nomad-service-provider")
        {
            Description = "Service registration provider: nomad (default) | consul.",
            DefaultValueFactory = _ => "nomad"
        };
        var nomadHealthPathOpt = new Option<string>("--nomad-health-check-path")
        {
            Description = "HTTP path for service health checks.",
            DefaultValueFactory = _ => "/health"
        };
        var nomadDcOpt = new Option<string>("--nomad-datacenter")
        {
            Description = "Nomad datacenter.",
            DefaultValueFactory = _ => "dc1"
        };

        // Kubernetes-specific
        var kubernetesContextOpt = new Option<string?>("--kubernetes-context")
        {
            Description = "kubectl context name."
        };
        var kubernetesPrefixOpt = new Option<string>("--kubernetes-namespace-prefix")
        {
            Description = "Kubernetes namespace prefix.",
            DefaultValueFactory = _ => "deployify"
        };
        var kubernetesDomainOpt = new Option<string?>("--kubernetes-ingress-domain")
        {
            Description = "Ingress base domain."
        };

        // GitOps-specific (Argo CD, Flux)
        var gitopsRepoOpt = new Option<string?>("--gitops-repo")
        {
            Description = "Config repository the GitOps controller watches (https or ssh URL)."
        };
        var gitopsBranchOpt = new Option<string>("--gitops-branch")
        {
            Description = "Branch Deployify commits to.",
            DefaultValueFactory = _ => "main"
        };
        var gitopsPathOpt = new Option<string?>("--gitops-path")
        {
            Description = "Folder in the repository for this environment. Default: deployify/{env}."
        };

        // OVHcloud-specific (Managed Kubernetes)
        var ovhEndpointOpt = new Option<string>("--ovh-endpoint")
        {
            Description = "OVHcloud API endpoint: ovh-eu | ovh-ca | ovh-us.",
            DefaultValueFactory = _ => "ovh-eu"
        };
        var ovhProjectOpt = new Option<string?>("--ovh-project")
        {
            Description = "OVHcloud Public Cloud project ID. Default: OVH_CLOUD_PROJECT_SERVICE at run time."
        };
        var ovhClusterOpt = new Option<string?>("--ovh-cluster")
        {
            Description = "OVHcloud Managed Kubernetes cluster ID."
        };

        // IIS-specific
        var iisHostOpt = new Option<string?>("--iis-host")
        {
            Description = "Windows server running IIS. Default: this machine."
        };
        var iisTransportOpt = new Option<IisTransport>("--iis-transport")
        {
            Description = "How to reach --iis-host: WinRm | Ssh.",
            DefaultValueFactory = _ => IisTransport.WinRm
        };
        var iisUserOpt = new Option<string?>("--iis-user")
        {
            Description = "User to connect as. WinRM reads the password from IIS_PASSWORD."
        };
        var iisSitesRootOpt = new Option<string>("--iis-sites-root")
        {
            Description = "Folder on the server for the sites and their releases.",
            DefaultValueFactory = _ => @"C:\inetpub\deployify"
        };

        // Compose-specific
        var composeProjectOpt = new Option<string>("--compose-project")
        {
            Description = "Docker Compose project name.",
            DefaultValueFactory = _ => "deployify"
        };
        var composeRestartOpt = new Option<string>("--compose-restart")
        {
            Description = "Restart policy.",
            DefaultValueFactory = _ => "unless-stopped"
        };

        // ECS-specific
        var ecsRegionOpt = new Option<string>("--ecs-region")
        {
            Description = "AWS region.",
            DefaultValueFactory = _ => "us-east-1"
        };
        var ecsClusterOpt = new Option<string>("--ecs-cluster")
        {
            Description = "ECS cluster name.",
            DefaultValueFactory = _ => "deployify"
        };
        var ecsProfileOpt = new Option<string?>("--ecs-profile")
        {
            Description = "AWS CLI profile name."
        };
        var ecsSubnetsOpt = new Option<string?>("--ecs-subnets")
        {
            Description = "Comma-separated subnet IDs for Fargate tasks."
        };
        var ecsSecGroupsOpt = new Option<string?>("--ecs-security-groups")
        {
            Description = "Comma-separated security group IDs."
        };
        var ecsExecRoleOpt = new Option<string?>("--ecs-execution-role")
        {
            Description = "ECS task execution role ARN."
        };

        // ACA-specific
        var acaSubOpt = new Option<string?>("--aca-subscription")
        {
            Description = "Azure subscription ID."
        };
        var acaRgOpt = new Option<string>("--aca-resource-group")
        {
            Description = "Azure resource group.",
            DefaultValueFactory = _ => "deployify-rg"
        };
        var acaLocationOpt = new Option<string>("--aca-location")
        {
            Description = "Azure region.",
            DefaultValueFactory = _ => "eastus"
        };
        var acaEnvOpt = new Option<string>("--aca-environment")
        {
            Description = "Container Apps environment name.",
            DefaultValueFactory = _ => "deployify-env"
        };
        var acaAcrOpt = new Option<string?>("--aca-acr")
        {
            Description = "Azure Container Registry login server."
        };

        // NomadConsul-specific
        var ncNomadAddrOpt = new Option<string>("--nc-nomad-address")
        {
            Description = "Nomad address (Consul mesh target).",
            DefaultValueFactory = _ => "http://localhost:4646"
        };
        var ncNomadTokenOpt = new Option<string?>("--nc-nomad-token")
        {
            Description = "Nomad ACL token (Consul mesh target)."
        };
        var ncNomadNsOpt = new Option<string>("--nc-nomad-namespace")
        {
            Description = "Nomad namespace (Consul mesh target).",
            DefaultValueFactory = _ => "default"
        };
        var ncNomadDcOpt = new Option<string>("--nc-datacenter")
        {
            Description = "Nomad/Consul datacenter.",
            DefaultValueFactory = _ => "dc1"
        };
        var ncConsulAddrOpt = new Option<string>("--nc-consul-address")
        {
            Description = "Consul address.",
            DefaultValueFactory = _ => "http://localhost:8500"
        };
        var ncConsulTokenOpt = new Option<string?>("--nc-consul-token")
        {
            Description = "Consul ACL token."
        };
        var ncIngressOpt = new Option<bool>("--nc-ingress-gateway")
        {
            Description = "Enable Consul Ingress Gateway for HTTP services.",
            DefaultValueFactory = _ => false
        };

        cmd.Options.Add(envNameOpt);
        cmd.Options.Add(targetOpt);
        cmd.Options.Add(registryOpt);
        cmd.Options.Add(tagOpt);
        cmd.Options.Add(outPathOpt);
        cmd.Options.Add(runtimeOpt);
        cmd.Options.Add(containerOsOpt);
        cmd.Options.Add(containerArchOpt);
        cmd.Options.Add(daprOpt);
        cmd.Options.Add(daprVersionOpt);
        cmd.Options.Add(daprHttpPortOpt);
        cmd.Options.Add(daprGrpcPortOpt);
        cmd.Options.Add(daprComponentsOpt);
        cmd.Options.Add(daprConfigOpt);
        cmd.Options.Add(daprLogLevelOpt);
        cmd.Options.Add(dashboardOpt);
        cmd.Options.Add(dashboardTagOpt);
        cmd.Options.Add(dashboardPortOpt);
        cmd.Options.Add(dashboardOtlpPortOpt);
        cmd.Options.Add(dashboardTokenOpt);
        cmd.Options.Add(nomadAddrOpt);
        cmd.Options.Add(nomadTokenOpt);
        cmd.Options.Add(nomadNsOpt);
        cmd.Options.Add(nomadDcOpt);
        cmd.Options.Add(nomadModeOpt);
        cmd.Options.Add(nomadRegUserOpt);
        cmd.Options.Add(nomadRegPassOpt);
        cmd.Options.Add(nomadRegisterOpt);
        cmd.Options.Add(nomadProviderOpt);
        cmd.Options.Add(nomadHealthPathOpt);
        cmd.Options.Add(kubernetesContextOpt);
        cmd.Options.Add(kubernetesPrefixOpt);
        cmd.Options.Add(kubernetesDomainOpt);
        cmd.Options.Add(gitopsRepoOpt);
        cmd.Options.Add(gitopsBranchOpt);
        cmd.Options.Add(gitopsPathOpt);
        cmd.Options.Add(ovhEndpointOpt);
        cmd.Options.Add(ovhProjectOpt);
        cmd.Options.Add(ovhClusterOpt);
        cmd.Options.Add(iisHostOpt);
        cmd.Options.Add(iisTransportOpt);
        cmd.Options.Add(iisUserOpt);
        cmd.Options.Add(iisSitesRootOpt);
        cmd.Options.Add(composeProjectOpt);
        cmd.Options.Add(composeRestartOpt);
        cmd.Options.Add(ecsRegionOpt);
        cmd.Options.Add(ecsClusterOpt);
        cmd.Options.Add(ecsProfileOpt);
        cmd.Options.Add(ecsSubnetsOpt);
        cmd.Options.Add(ecsSecGroupsOpt);
        cmd.Options.Add(ecsExecRoleOpt);
        cmd.Options.Add(acaSubOpt);
        cmd.Options.Add(acaRgOpt);
        cmd.Options.Add(acaLocationOpt);
        cmd.Options.Add(acaEnvOpt);
        cmd.Options.Add(acaAcrOpt);
        cmd.Options.Add(ncNomadAddrOpt);
        cmd.Options.Add(ncNomadTokenOpt);
        cmd.Options.Add(ncNomadNsOpt);
        cmd.Options.Add(ncNomadDcOpt);
        cmd.Options.Add(ncConsulAddrOpt);
        cmd.Options.Add(ncConsulTokenOpt);
        cmd.Options.Add(ncIngressOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var projectPath = parseResult.GetValue(GlobalOptions.ProjectPath)!;
            var nonInteractive = parseResult.GetValue(GlobalOptions.NonInteractive);
            var directory = ResolveDirectory(projectPath);

            var sp = new ServiceCollection()
                .AddSingleton<IStateManager, StateManager>()
                .BuildServiceProvider();
            var stateManager = sp.GetRequiredService<IStateManager>();

            DeployifyStateFile stateFile;

            if (nonInteractive)
            {
                var pr = parseResult;
                var target = pr.GetValue(targetOpt) ?? DeploymentTargetType.Nomad;
                var name = pr.GetValue(envNameOpt)!;
                var config = new EnvironmentConfig
                {
                    Target = target,
                    ContainerRegistry = pr.GetValue(registryOpt),
                    ImageTag = pr.GetValue(tagOpt)!,
                    OutputPath = pr.GetValue(outPathOpt)!,
                    AppHostPath = directory,
                    ContainerRuntime = pr.GetValue(runtimeOpt),
                    ContainerOs = pr.GetValue(containerOsOpt)!,
                    ContainerArch = pr.GetValue(containerArchOpt)!,
                    DeployDapr = pr.GetValue(daprOpt),
                    Dapr = new DaprConfig
                    {
                        DaprVersion = pr.GetValue(daprVersionOpt)!,
                        DaprHttpPort = pr.GetValue(daprHttpPortOpt),
                        DaprGrpcPort = pr.GetValue(daprGrpcPortOpt),
                        ComponentsPath = pr.GetValue(daprComponentsOpt),
                        ConfigPath = pr.GetValue(daprConfigOpt),
                        LogLevel = pr.GetValue(daprLogLevelOpt)!
                    },
                    DeployDashboard = pr.GetValue(dashboardOpt),
                    Dashboard = new DashboardConfig
                    {
                        ImageTag = pr.GetValue(dashboardTagOpt)!,
                        Port = pr.GetValue(dashboardPortOpt),
                        OtlpPort = pr.GetValue(dashboardOtlpPortOpt),
                        BrowserToken = pr.GetValue(dashboardTokenOpt)
                    },
                    TargetConfig = BuildTargetConfig(target,
                        pr.GetValue(nomadAddrOpt)!,
                        pr.GetValue(nomadTokenOpt),
                        pr.GetValue(nomadNsOpt)!,
                        pr.GetValue(nomadDcOpt)!,
                        pr.GetValue(nomadModeOpt),
                        pr.GetValue(nomadRegUserOpt),
                        pr.GetValue(nomadRegPassOpt),
                        pr.GetValue(nomadRegisterOpt),
                        pr.GetValue(nomadProviderOpt)!,
                        pr.GetValue(nomadHealthPathOpt)!,
                        pr.GetValue(kubernetesContextOpt),
                        pr.GetValue(kubernetesPrefixOpt)!,
                        pr.GetValue(kubernetesDomainOpt),
                        pr.GetValue(composeProjectOpt)!,
                        pr.GetValue(composeRestartOpt)!,
                        pr.GetValue(ecsRegionOpt)!,
                        pr.GetValue(ecsClusterOpt)!,
                        pr.GetValue(ecsProfileOpt),
                        pr.GetValue(ecsSubnetsOpt),
                        pr.GetValue(ecsSecGroupsOpt),
                        pr.GetValue(ecsExecRoleOpt),
                        pr.GetValue(acaSubOpt),
                        pr.GetValue(acaRgOpt)!,
                        pr.GetValue(acaLocationOpt)!,
                        pr.GetValue(acaEnvOpt)!,
                        pr.GetValue(acaAcrOpt),
                        pr.GetValue(ncNomadAddrOpt)!,
                        pr.GetValue(ncNomadTokenOpt),
                        pr.GetValue(ncNomadNsOpt)!,
                        pr.GetValue(ncNomadDcOpt)!,
                        pr.GetValue(ncConsulAddrOpt)!,
                        pr.GetValue(ncConsulTokenOpt),
                        pr.GetValue(ncIngressOpt),
                        pr.GetValue(gitopsRepoOpt),
                        pr.GetValue(gitopsBranchOpt)!,
                        pr.GetValue(gitopsPathOpt),
                        pr.GetValue(ovhEndpointOpt)!,
                        pr.GetValue(ovhProjectOpt),
                        pr.GetValue(ovhClusterOpt),
                        pr.GetValue(iisHostOpt),
                        pr.GetValue(iisTransportOpt),
                        pr.GetValue(iisUserOpt),
                        pr.GetValue(iisSitesRootOpt)!)
                };
                // Add to (or replace) this one environment; keep any others already defined.
                stateFile = stateManager.Exists(directory)
                    ? await stateManager.LoadFileAsync(directory, ct)
                    : new DeployifyStateFile { Environments = [] };
                if (stateFile.Environments.ContainsKey(name))
                {
                    AnsiConsole.MarkupLineInterpolated($"[yellow]![/] Replacing existing environment [cyan]{name}[/].");
                }

                stateFile.Environments[name] = config;
                stateFile.ActiveEnvironment = name;
            }
            else
            {
                stateFile = await PromptAsync(directory, stateManager, ct);
            }

            await stateManager.SaveFileAsync(stateFile, directory, ct);
            await SecretsWarning.PrintAsync(stateFile, directory, ct);

            AnsiConsole.MarkupLineInterpolated($"\n[green]✓[/] Deployify initialized → [bold]{directory}/deployify-state.json[/]");
            AnsiConsole.MarkupLineInterpolated($"  Active environment : [cyan]{stateFile.ActiveEnvironment}[/]");
            AnsiConsole.MarkupLine(
                $"  Environments: {string.Join(", ", stateFile.Environments.Keys.Select(k => $"[cyan]{Markup.Escape(k)}[/]"))}");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Next steps:");
            AnsiConsole.MarkupLine("  [cyan]deployify plan --target <nomad|kubernetes|compose|awsecs|azurecontainerapps|ovhcloud|iis>[/]");
            AnsiConsole.MarkupLine("  [cyan]deployify apply --target <nomad|kubernetes|compose|awsecs|azurecontainerapps|ovhcloud|iis>[/]");
        });

        return cmd;
    }

    // ── Interactive prompts ───────────────────────────────────────────────────

    private static async Task<DeployifyStateFile> PromptAsync(
        string directory, IStateManager stateManager, CancellationToken ct)
    {
        AnsiConsole.Write(new FigletText("Deployify").Color(Color.Cyan1));

        var stateFile = stateManager.Exists(directory)
            ? await stateManager.LoadFileAsync(directory, ct)
            : new DeployifyStateFile { Environments = [] };

        var addAnother = true;
        var isFirst = stateFile.Environments.Count == 0;

        while (addAnother)
        {
            var name = await ConsolePrompts.AskAsync<string>(isFirst
                ? "Name for first environment [dim](e.g. local)[/]:"
                : "Name for next environment:", ct);

            stateFile.Environments[name] = await PromptEnvironmentConfigAsync(name, directory, ct);
            if (isFirst)
            {
                stateFile.ActiveEnvironment = name;
                isFirst = false;
            }

            addAnother = await ConsolePrompts.ConfirmAsync("Add another environment?", defaultValue: false, ct);
        }

        if (stateFile.Environments.Count > 1)
        {
            stateFile.ActiveEnvironment = await ConsolePrompts.PromptAsync(
                new SelectionPrompt<string>()
                    .Title("Which environment should be [bold]active[/] by default?")
                    .UseConverter(Markup.Escape) // choices are rendered as markup
                    .AddChoices(stateFile.Environments.Keys), ct);
        }

        return stateFile;
    }

    internal static async Task<EnvironmentConfig> PromptEnvironmentConfigAsync(
        string envName, string directory, CancellationToken ct)
    {
        AnsiConsole.MarkupLineInterpolated($"\n[bold]Configuring: [cyan]{envName}[/][/]");

        var target = await ConsolePrompts.PromptAsync(
            new SelectionPrompt<DeploymentTargetType>()
                .Title("Deployment target:")
                .AddChoices(
                    DeploymentTargetType.Nomad,
                    DeploymentTargetType.Kubernetes,
                    DeploymentTargetType.Compose,
                    DeploymentTargetType.AwsEcs,
                    DeploymentTargetType.AzureContainerApps,
                    DeploymentTargetType.NomadConsul,
                    DeploymentTargetType.Helm,
                    DeploymentTargetType.ArgoCd,
                    DeploymentTargetType.Flux,
                    DeploymentTargetType.OvhCloud,
                    DeploymentTargetType.Iis)
                .HighlightStyle(new Style(foreground: Color.Cyan1)), ct);

        var registry = await ConsolePrompts.AskAsync("Container registry [dim](blank to skip)[/]:", string.Empty, ct);
        var tag = await ConsolePrompts.AskAsync("Default image tag:", "latest", ct);
        var outPath = await ConsolePrompts.AskAsync("Output path:", "deployify-output", ct);
        var runtime = await ConsolePrompts.PromptAsync(
            new SelectionPrompt<ContainerRuntime>()
                .Title("Container runtime for publishing:")
                .AddChoices(ContainerRuntime.Docker, ContainerRuntime.Podman)
                .HighlightStyle(new Style(foreground: Color.Cyan1)), ct);

        var containerArch = await ConsolePrompts.PromptAsync(
            new SelectionPrompt<string>()
                .Title("Target CPU architecture for container images:")
                .AddChoices("x64", "arm64", "arm")
                .HighlightStyle(new Style(foreground: Color.Cyan1)), ct);

        var deployDapr = await ConsolePrompts.ConfirmAsync("Inject Dapr sidecars into all services?", defaultValue: false, ct);
        var daprConfig = new DaprConfig();
        if (deployDapr)
        {
            daprConfig.DaprHttpPort = await ConsolePrompts.AskAsync("Base Dapr HTTP port:", 3500, ct);
            daprConfig.DaprGrpcPort = await ConsolePrompts.AskAsync("Base Dapr gRPC port:", 50001, ct);
            var components = await ConsolePrompts.AskAsync("Dapr components path [dim](blank to skip)[/]:", string.Empty, ct);
            if (!string.IsNullOrWhiteSpace(components))
            {
                daprConfig.ComponentsPath = components;
            }
        }

        var deployDashboard = await ConsolePrompts.ConfirmAsync("Deploy Aspire Dashboard alongside services?", defaultValue: false, ct);
        var dashboardConfig = new DashboardConfig();
        if (deployDashboard)
        {
            dashboardConfig.Port = await ConsolePrompts.AskAsync("Dashboard UI port:", 18888, ct);
            dashboardConfig.OtlpPort = await ConsolePrompts.AskAsync("Dashboard OTLP port:", 18889, ct);
            var token = await ConsolePrompts.AskSecretAsync("Browser auth token [dim](blank for none)[/]:", ct);
            if (!string.IsNullOrWhiteSpace(token))
            {
                dashboardConfig.BrowserToken = token;
            }
        }

        string nomadAddr = string.Empty, nomadToken = string.Empty, nomadNs = string.Empty, nomadDc = string.Empty;
        var nomadMode = NomadDeploymentMode.Docker;
        string? nomadRegUser = null, nomadRegPass = null;
        var nomadRegisterServices = true;
        var nomadServiceProvider = "nomad";
        var nomadHealthPath = "/health";
        string kubernetesContext = string.Empty, kubernetesPrefix = string.Empty, kubernetesDomain = string.Empty;
        string composeProject = string.Empty, composeRestart = string.Empty;
        string ecsRegion = string.Empty, ecsCluster = string.Empty;
        string? ecsProfile = null, ecsSubnets = null, ecsSecGroups = null, ecsExecRole = null;
        string? acaSub = null, acaRg = string.Empty, acaLocation = string.Empty, acaEnv = string.Empty, acaAcr = null;
        string ncNomadAddr = string.Empty, ncNomadNs = string.Empty, ncNomadDc = string.Empty;
        string? ncNomadToken = null, ncConsulToken = null;
        string ncConsulAddr = string.Empty;
        bool ncIngress = false;
        string? gitopsRepo = null, gitopsPath = null;
        var gitopsBranch = "main";
        var ovhEndpoint = "ovh-eu";
        string? ovhProject = null, ovhCluster = null;
        string? iisHost = null, iisUser = null;
        var iisTransport = IisTransport.WinRm;
        var iisSitesRoot = @"C:\inetpub\deployify";

        switch (target.Value)
        {
            case DeploymentTargetType.Names.Nomad:
                AnsiConsole.MarkupLine("[dim]Nomad settings:[/]");
                nomadAddr = await ConsolePrompts.AskAsync("Nomad address:", "http://localhost:4646", ct);
                nomadNs = await ConsolePrompts.AskAsync("Namespace:", "default", ct);
                nomadDc = await ConsolePrompts.AskAsync("Datacenter:", "dc1", ct);
                nomadToken = await ConsolePrompts.AskSecretAsync("ACL token [dim](blank to skip, or env:VAR_NAME)[/]:", ct);
                nomadMode = await ConsolePrompts.PromptAsync(
                    new SelectionPrompt<NomadDeploymentMode>()
                        .Title("Deployment mode:")
                        .AddChoices(NomadDeploymentMode.Docker, NomadDeploymentMode.RawExec), ct);
                if (await ConsolePrompts.ConfirmAsync("Authenticate with a private Docker registry?", defaultValue: false, ct))
                {
                    nomadRegUser = await ConsolePrompts.AskAsync<string>("Registry username:", ct);
                    nomadRegPass = await ConsolePrompts.AskSecretAsync("Registry password / token [dim](or env:VAR_NAME)[/]:", ct);
                }

                nomadRegisterServices = await ConsolePrompts.ConfirmAsync("Register services in service catalog?", defaultValue: true, ct);
                if (nomadRegisterServices)
                {
                    nomadServiceProvider = await ConsolePrompts.PromptAsync(
                        new SelectionPrompt<string>()
                            .Title("Service provider:")
                            .AddChoices("nomad", "consul"), ct);
                    nomadHealthPath = await ConsolePrompts.AskAsync("Health check path:", "/health", ct);
                }

                break;

            case DeploymentTargetType.Names.Kubernetes:
            case DeploymentTargetType.Names.Helm:
            case DeploymentTargetType.Names.ArgoCd:
            case DeploymentTargetType.Names.Flux:
                AnsiConsole.MarkupLine("[dim]Kubernetes settings:[/]");
                kubernetesContext = await ConsolePrompts.AskAsync("kubectl context [dim](blank = current)[/]:", string.Empty, ct);
                kubernetesPrefix = await ConsolePrompts.AskAsync("Namespace prefix:", envName, ct);
                kubernetesDomain = await ConsolePrompts.AskAsync("Ingress base domain [dim](blank to skip)[/]:", string.Empty, ct);
                if (target == DeploymentTargetType.ArgoCd || target == DeploymentTargetType.Flux)
                {
                    AnsiConsole.MarkupLine("[dim]GitOps settings:[/]");
                    gitopsRepo = await ConsolePrompts.AskAsync("Config repository URL:", string.Empty, ct);
                    gitopsBranch = await ConsolePrompts.AskAsync("Branch:", "main", ct);
                    gitopsPath = await ConsolePrompts.AskAsync("Folder in the repository:", $"deployify/{envName}", ct);
                }

                break;

            case DeploymentTargetType.Names.OvhCloud:
                AnsiConsole.MarkupLine("[dim]OVHcloud Managed Kubernetes settings:[/]");
                ovhEndpoint = await ConsolePrompts.PromptAsync(
                    new SelectionPrompt<string>()
                        .Title("API endpoint:")
                        .AddChoices("ovh-eu", "ovh-ca", "ovh-us"), ct);
                ovhProject = await ConsolePrompts.AskAsync(
                    "Public Cloud project ID [dim](blank = OVH_CLOUD_PROJECT_SERVICE)[/]:", string.Empty, ct);
                ovhCluster = await ConsolePrompts.AskAsync<string>("Managed Kubernetes cluster ID:", ct);
                kubernetesPrefix = await ConsolePrompts.AskAsync("Namespace prefix:", envName, ct);
                kubernetesDomain = await ConsolePrompts.AskAsync("Ingress base domain [dim](blank to skip)[/]:", string.Empty, ct);
                AnsiConsole.MarkupLine("[dim]API credentials are read from OVH_APPLICATION_KEY, OVH_APPLICATION_SECRET and " +
                                       "OVH_CONSUMER_KEY, or OVH_CLIENT_ID and OVH_CLIENT_SECRET.[/]");
                break;

            case DeploymentTargetType.Names.Iis:
                AnsiConsole.MarkupLine("[dim]IIS settings:[/]");
                iisHost = await ConsolePrompts.AskAsync("IIS server [dim](blank = this machine)[/]:", string.Empty, ct);
                if (!string.IsNullOrWhiteSpace(iisHost))
                {
                    iisTransport = await ConsolePrompts.PromptAsync(
                        new SelectionPrompt<IisTransport>()
                            .Title("Connect with PowerShell remoting over:")
                            .AddChoices(IisTransport.WinRm, IisTransport.Ssh), ct);
                    iisUser = await ConsolePrompts.AskAsync(
                        "User [dim](blank = current Windows identity / SSH user)[/]:", string.Empty, ct);
                }

                iisSitesRoot = await ConsolePrompts.AskAsync("Sites folder on the server:", iisSitesRoot, ct);
                AnsiConsole.MarkupLine("[dim]Projects are published for win-x64 with 'deployify publish'; the server needs IIS and the " +
                                       "ASP.NET Core Hosting Bundle. A WinRM password is read from IIS_PASSWORD.[/]");
                break;

            case DeploymentTargetType.Names.Compose:
                AnsiConsole.MarkupLine("[dim]Docker Compose settings:[/]");
                composeProject = await ConsolePrompts.AskAsync("Project name:", envName, ct);
                composeRestart = await ConsolePrompts.AskAsync("Restart policy:", "unless-stopped", ct);
                break;

            case DeploymentTargetType.Names.AwsEcs:
                AnsiConsole.MarkupLine("[dim]AWS ECS settings:[/]");
                ecsRegion = await ConsolePrompts.AskAsync("AWS region:", "us-east-1", ct);
                ecsCluster = await ConsolePrompts.AskAsync("ECS cluster name:", "deployify", ct);
                ecsProfile = await ConsolePrompts.AskAsync("AWS profile [dim](blank = default)[/]:", string.Empty, ct);
                ecsSubnets = await ConsolePrompts.AskAsync("Subnet IDs [dim](comma-separated, required for Fargate)[/]:", string.Empty, ct);
                ecsSecGroups = await ConsolePrompts.AskAsync("Security group IDs [dim](comma-separated)[/]:", string.Empty, ct);
                ecsExecRole = await ConsolePrompts.AskAsync("Execution role ARN [dim](blank = auto-detect)[/]:", string.Empty, ct);
                break;

            case DeploymentTargetType.Names.AzureContainerApps:
                AnsiConsole.MarkupLine("[dim]Azure Container Apps settings:[/]");
                acaSub = await ConsolePrompts.AskAsync("Subscription ID:", string.Empty, ct);
                acaRg = await ConsolePrompts.AskAsync("Resource group:", "deployify-rg", ct);
                acaLocation = await ConsolePrompts.AskAsync("Location:", "eastus", ct);
                acaEnv = await ConsolePrompts.AskAsync("Container Apps environment name:", "deployify-env", ct);
                acaAcr = await ConsolePrompts.AskAsync("ACR login server [dim](blank to skip)[/]:", string.Empty, ct);
                break;

            case DeploymentTargetType.Names.NomadConsul:
                AnsiConsole.MarkupLine("[dim]Nomad + Consul Connect settings:[/]");
                ncNomadAddr = await ConsolePrompts.AskAsync("Nomad address:", "http://localhost:4646", ct);
                ncNomadNs = await ConsolePrompts.AskAsync("Nomad namespace:", "default", ct);
                ncNomadDc = await ConsolePrompts.AskAsync("Datacenter:", "dc1", ct);
                ncNomadToken = await ConsolePrompts.AskSecretAsync("Nomad ACL token [dim](blank to skip, or env:VAR_NAME)[/]:", ct);
                ncConsulAddr = await ConsolePrompts.AskAsync("Consul address:", "http://localhost:8500", ct);
                ncConsulToken = await ConsolePrompts.AskSecretAsync("Consul ACL token [dim](blank to skip, or env:VAR_NAME)[/]:", ct);
                ncIngress = await ConsolePrompts.ConfirmAsync("Enable Ingress Gateway for HTTP services?", defaultValue: false, ct);
                break;
        }

        return new EnvironmentConfig
        {
            Target = target,
            ContainerRegistry = string.IsNullOrWhiteSpace(registry) ? null : registry,
            ImageTag = tag,
            OutputPath = outPath,
            AppHostPath = directory,
            ContainerRuntime = runtime,
            ContainerOs = "linux",
            ContainerArch = containerArch,
            DeployDapr = deployDapr,
            Dapr = daprConfig,
            DeployDashboard = deployDashboard,
            Dashboard = dashboardConfig,
            TargetConfig = BuildTargetConfig(target, nomadAddr, nomadToken, nomadNs, nomadDc,
                nomadMode, nomadRegUser, nomadRegPass,
                nomadRegisterServices, nomadServiceProvider, nomadHealthPath,
                kubernetesContext, kubernetesPrefix, kubernetesDomain, composeProject, composeRestart,
                ecsRegion, ecsCluster, ecsProfile, ecsSubnets, ecsSecGroups, ecsExecRole,
                acaSub, acaRg, acaLocation, acaEnv, acaAcr,
                ncNomadAddr, ncNomadToken, ncNomadNs, ncNomadDc,
                ncConsulAddr, ncConsulToken, ncIngress,
                gitopsRepo, gitopsBranch, gitopsPath,
                ovhEndpoint, ovhProject, ovhCluster,
                iisHost, iisTransport, iisUser, iisSitesRoot)
        };
    }

    // ── Config builder ────────────────────────────────────────────────────────

    private static Dictionary<string, object?> BuildTargetConfig(
        DeploymentTargetType target,
        string nomadAddr, string? nomadToken, string nomadNs, string nomadDc,
        NomadDeploymentMode nomadMode,
        string? nomadRegUser, string? nomadRegPass,
        bool nomadRegisterServices, string nomadServiceProvider, string nomadHealthPath,
        string? kubernetesContext, string kubernetesPrefix, string? kubernetesDomain,
        string composeProject, string composeRestart,
        string ecsRegion, string ecsCluster,
        string? ecsProfile, string? ecsSubnets, string? ecsSecGroups, string? ecsExecRole,
        string? acaSub, string acaRg, string acaLocation, string acaEnv, string? acaAcr,
        string ncNomadAddr, string? ncNomadToken, string ncNomadNs, string ncNomadDc,
        string ncConsulAddr, string? ncConsulToken, bool ncIngress,
        string? gitopsRepo, string gitopsBranch, string? gitopsPath,
        string ovhEndpoint, string? ovhProject, string? ovhCluster,
        string? iisHost, IisTransport iisTransport, string? iisUser, string iisSitesRoot)
        => target.Value switch
        {
            DeploymentTargetType.Names.Nomad => new Dictionary<string, object?>
            {
                ["nomad"] = new NomadTargetConfig
                {
                    Address = nomadAddr,
                    Token = string.IsNullOrWhiteSpace(nomadToken) ? null : nomadToken,
                    Namespace = nomadNs,
                    Datacenters = [nomadDc],
                    DeploymentMode = nomadMode,
                    RegistryUsername = string.IsNullOrWhiteSpace(nomadRegUser) ? null : nomadRegUser,
                    RegistryPassword = string.IsNullOrWhiteSpace(nomadRegPass) ? null : nomadRegPass,
                    RegisterServices = nomadRegisterServices,
                    ServiceProvider = nomadServiceProvider,
                    HealthCheckPath = nomadHealthPath
                }
            },
            DeploymentTargetType.Names.Kubernetes => new Dictionary<string, object?>
            {
                ["kubernetes"] = new KubernetesTargetConfig
                {
                    Context = string.IsNullOrWhiteSpace(kubernetesContext) ? null : kubernetesContext,
                    NamespacePrefix = kubernetesPrefix,
                    IngressBaseDomain = string.IsNullOrWhiteSpace(kubernetesDomain) ? null : kubernetesDomain,
                    GenerateIngress = !string.IsNullOrWhiteSpace(kubernetesDomain)
                }
            },
            DeploymentTargetType.Names.Compose => new Dictionary<string, object?>
            {
                ["compose"] = new ComposeTargetConfig
                {
                    ProjectName = composeProject,
                    RestartPolicy = string.IsNullOrWhiteSpace(composeRestart) ? "unless-stopped" : composeRestart,
                    GenerateProfiles = true,
                    GenerateEnvFile = true,
                    GenerateMergedFile = true
                }
            },
            DeploymentTargetType.Names.AwsEcs => new Dictionary<string, object?>
            {
                ["ecs"] = new EcsTargetConfig
                {
                    Region = string.IsNullOrWhiteSpace(ecsRegion) ? "us-east-1" : ecsRegion,
                    ClusterName = string.IsNullOrWhiteSpace(ecsCluster) ? "deployify" : ecsCluster,
                    AwsProfile = string.IsNullOrWhiteSpace(ecsProfile) ? null : ecsProfile,
                    SubnetIds = string.IsNullOrWhiteSpace(ecsSubnets)
                        ? []
                        : [.. ecsSubnets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
                    SecurityGroupIds = string.IsNullOrWhiteSpace(ecsSecGroups)
                        ? []
                        : [.. ecsSecGroups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
                    ExecutionRoleArn = string.IsNullOrWhiteSpace(ecsExecRole) ? null : ecsExecRole
                }
            },
            DeploymentTargetType.Names.AzureContainerApps => new Dictionary<string, object?>
            {
                ["aca"] = new AcaTargetConfig
                {
                    SubscriptionId = string.IsNullOrWhiteSpace(acaSub) ? string.Empty : acaSub,
                    ResourceGroup = string.IsNullOrWhiteSpace(acaRg) ? "deployify-rg" : acaRg,
                    Location = string.IsNullOrWhiteSpace(acaLocation) ? "eastus" : acaLocation,
                    EnvironmentName = string.IsNullOrWhiteSpace(acaEnv) ? "deployify-env" : acaEnv,
                    AcrLoginServer = string.IsNullOrWhiteSpace(acaAcr) ? null : acaAcr
                }
            },
            DeploymentTargetType.Names.NomadConsul => new Dictionary<string, object?>
            {
                ["nomadconsul"] = new NomadConsulTargetConfig
                {
                    NomadAddress = string.IsNullOrWhiteSpace(ncNomadAddr) ? "http://localhost:4646" : ncNomadAddr,
                    NomadToken = string.IsNullOrWhiteSpace(ncNomadToken) ? null : ncNomadToken,
                    NomadNamespace = string.IsNullOrWhiteSpace(ncNomadNs) ? "default" : ncNomadNs,
                    Datacenters = [string.IsNullOrWhiteSpace(ncNomadDc) ? "dc1" : ncNomadDc],
                    ConsulAddress = string.IsNullOrWhiteSpace(ncConsulAddr) ? "http://localhost:8500" : ncConsulAddr,
                    ConsulToken = string.IsNullOrWhiteSpace(ncConsulToken) ? null : ncConsulToken,
                    EnableIngressGateway = ncIngress
                }
            },
            // Helm deploys the Kubernetes target's manifests, so it reads the same settings.
            DeploymentTargetType.Names.Helm => new Dictionary<string, object?>
            {
                ["kubernetes"] = new KubernetesTargetConfig
                {
                    Context = string.IsNullOrWhiteSpace(kubernetesContext) ? null : kubernetesContext,
                    NamespacePrefix = kubernetesPrefix,
                    IngressBaseDomain = string.IsNullOrWhiteSpace(kubernetesDomain) ? null : kubernetesDomain,
                    GenerateIngress = !string.IsNullOrWhiteSpace(kubernetesDomain)
                },
                ["helm"] = new HelmTargetConfig()
            },
            // The GitOps targets render the Kubernetes target's manifests and commit them.
            DeploymentTargetType.Names.ArgoCd or DeploymentTargetType.Names.Flux => new Dictionary<string, object?>
            {
                ["kubernetes"] = new KubernetesTargetConfig
                {
                    Context = string.IsNullOrWhiteSpace(kubernetesContext) ? null : kubernetesContext,
                    NamespacePrefix = kubernetesPrefix,
                    IngressBaseDomain = string.IsNullOrWhiteSpace(kubernetesDomain) ? null : kubernetesDomain,
                    GenerateIngress = !string.IsNullOrWhiteSpace(kubernetesDomain)
                },
                ["gitops"] = new GitOpsTargetConfig
                {
                    RepoUrl = gitopsRepo ?? string.Empty,
                    Branch = string.IsNullOrWhiteSpace(gitopsBranch) ? "main" : gitopsBranch,
                    Path = string.IsNullOrWhiteSpace(gitopsPath) ? null : gitopsPath
                },
                [target == DeploymentTargetType.ArgoCd ? "argocd" : "flux"] = target == DeploymentTargetType.ArgoCd
                    ? new ArgoCdTargetConfig()
                    : new FluxTargetConfig()
            },
            // OVHcloud deploys the Kubernetes target's manifests; the kubeconfig comes from the API.
            DeploymentTargetType.Names.OvhCloud => new Dictionary<string, object?>
            {
                ["kubernetes"] = new KubernetesTargetConfig
                {
                    NamespacePrefix = kubernetesPrefix,
                    IngressBaseDomain = string.IsNullOrWhiteSpace(kubernetesDomain) ? null : kubernetesDomain,
                    GenerateIngress = !string.IsNullOrWhiteSpace(kubernetesDomain)
                },
                ["ovhcloud"] = new OvhCloudTargetConfig
                {
                    Endpoint = string.IsNullOrWhiteSpace(ovhEndpoint) ? "ovh-eu" : ovhEndpoint,
                    ProjectId = string.IsNullOrWhiteSpace(ovhProject) ? null : ovhProject,
                    ClusterId = string.IsNullOrWhiteSpace(ovhCluster) ? null : ovhCluster
                }
            },
            DeploymentTargetType.Names.Iis => new Dictionary<string, object?>
            {
                ["iis"] = new IisTargetConfig
                {
                    Host = string.IsNullOrWhiteSpace(iisHost) ? null : iisHost,
                    Transport = iisTransport,
                    UserName = string.IsNullOrWhiteSpace(iisUser) ? null : iisUser,
                    SitesRoot = string.IsNullOrWhiteSpace(iisSitesRoot) ? @"C:\inetpub\deployify" : iisSitesRoot
                }
            },
            _ => []
        };

    private static string ResolveDirectory(string p)
        => File.Exists(p) ? Path.GetDirectoryName(Path.GetFullPath(p))! : Path.GetFullPath(p);
}
