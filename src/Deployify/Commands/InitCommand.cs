using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Deployify.Target.AzureContainerApps;
using Deployify.Target.AwsEcs;
using Deployify.Target.Compose;
using Deployify.Target.Kubernetes;
using Deployify.Target.Nomad;
using Deployify.Target.NomadConsul;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Deployify.Commands;

public static class InitCommand
{
    public static Command Build()
    {
        var cmd = new Command("init", "Initialise deployify in an Aspire AppHost project.");
        cmd.AddOption(GlobalOptions.ProjectPath);
        cmd.AddOption(GlobalOptions.NonInteractive);

        // Common flags
        var envNameOpt = new Option<string>("--env-name", () => "local", "Environment name.");
        var targetOpt = new Option<DeploymentTargetType?>("--target", "Deployment target.");
        var registryOpt = new Option<string?>("--container-registry", "Container registry URL.");
        var tagOpt = new Option<string>("--image-tag", () => "latest", "Default image tag.");
        var outPathOpt = new Option<string>("--output-path", () => "deployify-output", "Output folder for artefacts.");
        var runtimeOpt = new Option<ContainerRuntime>("--container-runtime", () => ContainerRuntime.Docker, "Container runtime for publishing: docker (default) | podman.");
        var containerOsOpt = new Option<string>("--container-os", () => "linux", "Target OS for container images (default: linux).");
        var containerArchOpt = new Option<string>("--container-arch", () => "x64", "Target CPU architecture for container images: x64 (default) | arm64 | arm.");

        // Dapr
        var daprOpt = new Option<bool>("--deploy-dapr", () => false, "Inject a Dapr sidecar into every service.");
        var daprVersionOpt = new Option<string>("--dapr-version", () => "latest", "Dapr sidecar image tag.");
        var daprHttpPortOpt = new Option<int>("--dapr-http-port", () => 3500, "Base Dapr HTTP port.");
        var daprGrpcPortOpt = new Option<int>("--dapr-grpc-port", () => 50001, "Base Dapr gRPC port.");
        var daprComponentsOpt = new Option<string?>("--dapr-components-path", "Path to Dapr components directory.");
        var daprConfigOpt = new Option<string?>("--dapr-config-path", "Path to Dapr config file.");
        var daprLogLevelOpt = new Option<string>("--dapr-log-level", () => "info", "Dapr sidecar log level.");

        // Dashboard
        var dashboardOpt = new Option<bool>("--deploy-dashboard", () => false, "Inject the Aspire Dashboard container into every deployment.");
        var dashboardTagOpt = new Option<string>("--dashboard-tag", () => "latest", "Image tag for mcr.microsoft.com/dotnet/aspire-dashboard.");
        var dashboardPortOpt = new Option<int>("--dashboard-port", () => 18888, "Host port for the dashboard UI.");
        var dashboardOtlpPortOpt = new Option<int>("--dashboard-otlp-port", () => 18889, "Host port for the OTLP gRPC endpoint.");
        var dashboardTokenOpt = new Option<string?>("--dashboard-token", "Browser auth token (omit for unauthenticated).");

        // Nomad-specific
        var nomadAddrOpt = new Option<string>("--nomad-address", () => "http://localhost:4646", "Nomad cluster address.");
        var nomadTokenOpt = new Option<string?>("--nomad-token", "Nomad ACL token.");
        var nomadNsOpt = new Option<string>("--nomad-namespace", () => "default", "Nomad namespace.");
        var nomadModeOpt = new Option<NomadDeploymentMode>("--nomad-mode", () => NomadDeploymentMode.Docker, "Nomad deployment mode: Docker (default) | RawExec.");
        var nomadRegUserOpt = new Option<string?>("--nomad-registry-username", "Docker registry username for private image pulls.");
        var nomadRegPassOpt = new Option<string?>("--nomad-registry-password", "Docker registry password or access token.");
        var nomadRegisterOpt = new Option<bool>("--nomad-register-services", () => true, "Register services in Nomad/Consul service catalog.");
        var nomadProviderOpt = new Option<string>("--nomad-service-provider", () => "nomad", "Service registration provider: nomad (default) | consul.");
        var nomadHealthPathOpt = new Option<string>("--nomad-health-check-path", () => "/health", "HTTP path for service health checks.");
        var nomadDcOpt = new Option<string>("--nomad-datacenter", () => "dc1", "Nomad datacenter.");

        // Kubernetes-specific
        var k8SContextOpt = new Option<string?>("--k8s-context", "kubectl context name.");
        var k8SPrefixOpt = new Option<string>("--k8s-namespace-prefix", () => "deployify", "Kubernetes namespace prefix.");
        var k8SDomainOpt = new Option<string?>("--k8s-ingress-domain", "Ingress base domain.");

        // Compose-specific
        var composeProjectOpt = new Option<string>("--compose-project", () => "deployify", "Docker Compose project name.");
        var composeRestartOpt = new Option<string>("--compose-restart", () => "unless-stopped", "Restart policy.");

        // ECS-specific
        var ecsRegionOpt = new Option<string>("--ecs-region", () => "us-east-1", "AWS region.");
        var ecsClusterOpt = new Option<string>("--ecs-cluster", () => "deployify", "ECS cluster name.");
        var ecsProfileOpt = new Option<string?>("--ecs-profile", "AWS CLI profile name.");
        var ecsSubnetsOpt = new Option<string?>("--ecs-subnets", "Comma-separated subnet IDs for Fargate tasks.");
        var ecsSecGroupsOpt = new Option<string?>("--ecs-security-groups", "Comma-separated security group IDs.");
        var ecsExecRoleOpt = new Option<string?>("--ecs-execution-role", "ECS task execution role ARN.");

        // ACA-specific
        var acaSubOpt = new Option<string?>("--aca-subscription", "Azure subscription ID.");
        var acaRgOpt = new Option<string>("--aca-resource-group", () => "deployify-rg", "Azure resource group.");
        var acaLocationOpt = new Option<string>("--aca-location", () => "eastus", "Azure region.");
        var acaEnvOpt = new Option<string>("--aca-environment", () => "deployify-env", "Container Apps environment name.");
        var acaAcrOpt = new Option<string?>("--aca-acr", "Azure Container Registry login server.");

        // NomadConsul-specific
        var ncNomadAddrOpt = new Option<string>("--nc-nomad-address", () => "http://localhost:4646", "Nomad address (Consul mesh target).");
        var ncNomadTokenOpt = new Option<string?>("--nc-nomad-token", "Nomad ACL token (Consul mesh target).");
        var ncNomadNsOpt = new Option<string>("--nc-nomad-namespace", () => "default", "Nomad namespace (Consul mesh target).");
        var ncNomadDcOpt = new Option<string>("--nc-datacenter", () => "dc1", "Nomad/Consul datacenter.");
        var ncConsulAddrOpt = new Option<string>("--nc-consul-address", () => "http://localhost:8500", "Consul address. ");
        var ncConsulTokenOpt = new Option<string?>("--nc-consul-token", "Consul ACL token.");
        var ncIngressOpt = new Option<bool>("--nc-ingress-gateway", () => false, "Enable Consul Ingress Gateway for HTTP services.");

        cmd.AddOption(envNameOpt); cmd.AddOption(targetOpt); cmd.AddOption(registryOpt);
        cmd.AddOption(tagOpt); cmd.AddOption(outPathOpt); cmd.AddOption(runtimeOpt);
        cmd.AddOption(containerOsOpt); cmd.AddOption(containerArchOpt);
        cmd.AddOption(daprOpt); cmd.AddOption(daprVersionOpt); cmd.AddOption(daprHttpPortOpt);
        cmd.AddOption(daprGrpcPortOpt); cmd.AddOption(daprComponentsOpt); cmd.AddOption(daprConfigOpt);
        cmd.AddOption(daprLogLevelOpt);
        cmd.AddOption(dashboardOpt); cmd.AddOption(dashboardTagOpt); cmd.AddOption(dashboardPortOpt);
        cmd.AddOption(dashboardOtlpPortOpt); cmd.AddOption(dashboardTokenOpt);
        cmd.AddOption(nomadAddrOpt); cmd.AddOption(nomadTokenOpt); cmd.AddOption(nomadNsOpt); cmd.AddOption(nomadDcOpt); cmd.AddOption(nomadModeOpt);
        cmd.AddOption(nomadRegUserOpt); cmd.AddOption(nomadRegPassOpt);
        cmd.AddOption(nomadRegisterOpt); cmd.AddOption(nomadProviderOpt); cmd.AddOption(nomadHealthPathOpt);
        cmd.AddOption(k8SContextOpt); cmd.AddOption(k8SPrefixOpt); cmd.AddOption(k8SDomainOpt);
        cmd.AddOption(composeProjectOpt); cmd.AddOption(composeRestartOpt);
        cmd.AddOption(ecsRegionOpt); cmd.AddOption(ecsClusterOpt); cmd.AddOption(ecsProfileOpt);
        cmd.AddOption(ecsSubnetsOpt); cmd.AddOption(ecsSecGroupsOpt); cmd.AddOption(ecsExecRoleOpt);
        cmd.AddOption(acaSubOpt); cmd.AddOption(acaRgOpt); cmd.AddOption(acaLocationOpt);
        cmd.AddOption(acaEnvOpt); cmd.AddOption(acaAcrOpt);
        cmd.AddOption(ncNomadAddrOpt); cmd.AddOption(ncNomadTokenOpt); cmd.AddOption(ncNomadNsOpt);
        cmd.AddOption(ncNomadDcOpt); cmd.AddOption(ncConsulAddrOpt); cmd.AddOption(ncConsulTokenOpt);
        cmd.AddOption(ncIngressOpt);

        cmd.SetHandler(async ctx =>
        {
            var projectPath = ctx.ParseResult.GetValueForOption(GlobalOptions.ProjectPath)!;
            var nonInteractive = ctx.ParseResult.GetValueForOption(GlobalOptions.NonInteractive);
            var directory = ResolveDirectory(projectPath);

            var sp = new ServiceCollection()
                .AddSingleton<IStateManager, StateManager>()
                .BuildServiceProvider();
            var stateManager = sp.GetRequiredService<IStateManager>();

            DeployifyStateFile stateFile;

            if (nonInteractive)
            {
                var pr = ctx.ParseResult;
                var target = pr.GetValueForOption(targetOpt) ?? DeploymentTargetType.Nomad;
                var name = pr.GetValueForOption(envNameOpt)!;
                var config = new EnvironmentConfig
                {
                    Target = target,
                    ContainerRegistry = pr.GetValueForOption(registryOpt),
                    ImageTag = pr.GetValueForOption(tagOpt)!,
                    OutputPath = pr.GetValueForOption(outPathOpt)!,
                    AppHostPath = directory,
                    ContainerRuntime = pr.GetValueForOption(runtimeOpt),
                    ContainerOs = pr.GetValueForOption(containerOsOpt)!,
                    ContainerArch = pr.GetValueForOption(containerArchOpt)!,
                    DeployDapr = pr.GetValueForOption(daprOpt),
                    Dapr = new DaprConfig
                    {
                        DaprVersion = pr.GetValueForOption(daprVersionOpt)!,
                        DaprHttpPort = pr.GetValueForOption(daprHttpPortOpt),
                        DaprGrpcPort = pr.GetValueForOption(daprGrpcPortOpt),
                        ComponentsPath = pr.GetValueForOption(daprComponentsOpt),
                        ConfigPath = pr.GetValueForOption(daprConfigOpt),
                        LogLevel = pr.GetValueForOption(daprLogLevelOpt)!
                    },
                    DeployDashboard = pr.GetValueForOption(dashboardOpt),
                    Dashboard = new DashboardConfig
                    {
                        ImageTag = pr.GetValueForOption(dashboardTagOpt)!,
                        Port = pr.GetValueForOption(dashboardPortOpt),
                        OtlpPort = pr.GetValueForOption(dashboardOtlpPortOpt),
                        BrowserToken = pr.GetValueForOption(dashboardTokenOpt)
                    },
                    TargetConfig = BuildTargetConfig(target,
                        pr.GetValueForOption(nomadAddrOpt)!,
                        pr.GetValueForOption(nomadTokenOpt),
                        pr.GetValueForOption(nomadNsOpt)!,
                        pr.GetValueForOption(nomadDcOpt)!,
                        pr.GetValueForOption(nomadModeOpt),
                        pr.GetValueForOption(nomadRegUserOpt),
                        pr.GetValueForOption(nomadRegPassOpt),
                        pr.GetValueForOption(nomadRegisterOpt),
                        pr.GetValueForOption(nomadProviderOpt)!,
                        pr.GetValueForOption(nomadHealthPathOpt)!,
                        pr.GetValueForOption(k8SContextOpt),
                        pr.GetValueForOption(k8SPrefixOpt)!,
                        pr.GetValueForOption(k8SDomainOpt),
                        pr.GetValueForOption(composeProjectOpt)!,
                        pr.GetValueForOption(composeRestartOpt)!,
                        pr.GetValueForOption(ecsRegionOpt)!,
                        pr.GetValueForOption(ecsClusterOpt)!,
                        pr.GetValueForOption(ecsProfileOpt),
                        pr.GetValueForOption(ecsSubnetsOpt),
                        pr.GetValueForOption(ecsSecGroupsOpt),
                        pr.GetValueForOption(ecsExecRoleOpt),
                        pr.GetValueForOption(acaSubOpt),
                        pr.GetValueForOption(acaRgOpt)!,
                        pr.GetValueForOption(acaLocationOpt)!,
                        pr.GetValueForOption(acaEnvOpt)!,
                        pr.GetValueForOption(acaAcrOpt),
                        pr.GetValueForOption(ncNomadAddrOpt)!,
                        pr.GetValueForOption(ncNomadTokenOpt),
                        pr.GetValueForOption(ncNomadNsOpt)!,
                        pr.GetValueForOption(ncNomadDcOpt)!,
                        pr.GetValueForOption(ncConsulAddrOpt)!,
                        pr.GetValueForOption(ncConsulTokenOpt),
                        pr.GetValueForOption(ncIngressOpt))
                };
                stateFile = new DeployifyStateFile
                {
                    ActiveEnvironment = name,
                    Environments = { [name] = config }
                };
            }
            else
            {
                stateFile = await PromptAsync(directory, stateManager);
            }

            await stateManager.SaveFileAsync(stateFile, directory);

            AnsiConsole.MarkupLine($"\n[green]✓[/] Deployify initialised → [bold]{directory}/deployify-state.json[/]");
            AnsiConsole.MarkupLine($"  Active environment : [cyan]{stateFile.ActiveEnvironment}[/]");
            AnsiConsole.MarkupLine($"  Environments: {string.Join(", ", stateFile.Environments.Keys.Select(k => $"[cyan]{k}[/]"))}");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Next steps:");
            AnsiConsole.MarkupLine("  [cyan]deployify plan --target <nomad|kubernetes|compose|awsecs|azurecontainerapps>[/]");
            AnsiConsole.MarkupLine("  [cyan]deployify apply --target <nomad|kubernetes|compose|awsecs|azurecontainerapps>[/]");
        });

        return cmd;
    }

    // ── Interactive prompts ───────────────────────────────────────────────────

    private static async Task<DeployifyStateFile> PromptAsync(
        string directory, IStateManager stateManager)
    {
        AnsiConsole.Write(new FigletText("Deployify").Color(Color.Cyan1));

        var stateFile = stateManager.Exists(directory)
            ? await stateManager.LoadFileAsync(directory)
            : new DeployifyStateFile { Environments = [] };

        var addAnother = true;
        var isFirst = stateFile.Environments.Count == 0;

        while (addAnother)
        {
            var name = AnsiConsole.Ask<string>(isFirst
                ? "Name for first environment [dim](e.g. local)[/]:"
                : "Name for next environment:");

            stateFile.Environments[name] = PromptEnvironmentConfig(name, directory);
            if (isFirst) { stateFile.ActiveEnvironment = name; isFirst = false; }

            addAnother = AnsiConsole.Confirm("Add another environment?", defaultValue: false);
        }

        if (stateFile.Environments.Count > 1)
        {
	        stateFile.ActiveEnvironment = AnsiConsole.Prompt(
		        new SelectionPrompt<string>()
			        .Title("Which environment should be [bold]active[/] by default?")
			        .AddChoices(stateFile.Environments.Keys));
        }

        return stateFile;
    }

    internal static EnvironmentConfig PromptEnvironmentConfig(string envName, string directory)
    {
        AnsiConsole.MarkupLine($"\n[bold]Configuring: [cyan]{envName}[/][/]");

        var target = AnsiConsole.Prompt(
            new SelectionPrompt<DeploymentTargetType>()
                .Title("Deployment target:")
                .AddChoices(
                    DeploymentTargetType.Nomad,
                    DeploymentTargetType.Kubernetes,
                    DeploymentTargetType.Compose,
                    DeploymentTargetType.AwsEcs,
                    DeploymentTargetType.AzureContainerApps,
                    DeploymentTargetType.NomadConsul)
                .HighlightStyle(new Style(foreground: Color.Cyan1)));

        var registry = AnsiConsole.Ask("Container registry [dim](blank to skip)[/]:", string.Empty);
        var tag = AnsiConsole.Ask("Default image tag:", "latest");
        var outPath = AnsiConsole.Ask("Output path:", "deployify-output");
        var runtime = AnsiConsole.Prompt(
            new SelectionPrompt<ContainerRuntime>()
                .Title("Container runtime for publishing:")
                .AddChoices(ContainerRuntime.Docker, ContainerRuntime.Podman)
                .HighlightStyle(new Style(foreground: Color.Cyan1)));

        var containerArch = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Target CPU architecture for container images:")
                .AddChoices("x64", "arm64", "arm")
                .HighlightStyle(new Style(foreground: Color.Cyan1)));

        var deployDapr = AnsiConsole.Confirm("Inject Dapr sidecars into all services?", defaultValue: false);
        var daprConfig = new DaprConfig();
        if (deployDapr)
        {
            daprConfig.DaprHttpPort = AnsiConsole.Ask("Base Dapr HTTP port:", 3500);
            daprConfig.DaprGrpcPort = AnsiConsole.Ask("Base Dapr gRPC port:", 50001);
            var components = AnsiConsole.Ask("Dapr components path [dim](blank to skip)[/]:", string.Empty);
            if (!string.IsNullOrWhiteSpace(components))
            {
	            daprConfig.ComponentsPath = components;
            }
        }

        var deployDashboard = AnsiConsole.Confirm("Deploy Aspire Dashboard alongside services?", defaultValue: false);
        var dashboardConfig = new DashboardConfig();
        if (deployDashboard)
        {
            dashboardConfig.Port = AnsiConsole.Ask("Dashboard UI port:", 18888);
            dashboardConfig.OtlpPort = AnsiConsole.Ask("Dashboard OTLP port:", 18889);
            var token = AnsiConsole.Ask("Browser auth token [dim](blank for none)[/]:", string.Empty);
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
        string k8SContext = string.Empty, k8SPrefix = string.Empty, k8SDomain = string.Empty;
        string composeProject = string.Empty, composeRestart = string.Empty;
        string ecsRegion = string.Empty, ecsCluster = string.Empty;
        string? ecsProfile = null, ecsSubnets = null, ecsSecGroups = null, ecsExecRole = null;
        string? acaSub = null, acaRg = string.Empty, acaLocation = string.Empty, acaEnv = string.Empty, acaAcr = null;
        string ncNomadAddr = string.Empty, ncNomadNs = string.Empty, ncNomadDc = string.Empty;
        string? ncNomadToken = null, ncConsulToken = null;
        string ncConsulAddr = string.Empty;
        bool ncIngress = false;

        switch (target)
        {
            case DeploymentTargetType.Nomad:
                AnsiConsole.MarkupLine("[dim]Nomad settings:[/]");
                nomadAddr = AnsiConsole.Ask("Nomad address:", "http://localhost:4646");
                nomadNs = AnsiConsole.Ask("Namespace:", "default");
                nomadDc = AnsiConsole.Ask("Datacenter:", "dc1");
                nomadToken = AnsiConsole.Ask("ACL token [dim](blank to skip)[/]:", string.Empty);
                nomadMode = AnsiConsole.Prompt(
                    new SelectionPrompt<NomadDeploymentMode>()
                        .Title("Deployment mode:")
                        .AddChoices(NomadDeploymentMode.Docker, NomadDeploymentMode.RawExec));
                if (AnsiConsole.Confirm("Authenticate with a private Docker registry?", defaultValue: false))
                {
                    nomadRegUser = AnsiConsole.Ask<string>("Registry username:");
                    nomadRegPass = AnsiConsole.Prompt(
                        new TextPrompt<string>("Registry password / token:").Secret());
                }

                nomadRegisterServices = AnsiConsole.Confirm("Register services in service catalog?", defaultValue: true);
                if (nomadRegisterServices)
                {
                    nomadServiceProvider = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Service provider:")
                            .AddChoices("nomad", "consul"));
                    nomadHealthPath = AnsiConsole.Ask("Health check path:", "/health");
                }
                
                break;

            case DeploymentTargetType.Kubernetes:
                AnsiConsole.MarkupLine("[dim]Kubernetes settings:[/]");
                k8SContext = AnsiConsole.Ask("kubectl context [dim](blank = current)[/]:", string.Empty);
                k8SPrefix = AnsiConsole.Ask("Namespace prefix:", envName);
                k8SDomain = AnsiConsole.Ask("Ingress base domain [dim](blank to skip)[/]:", string.Empty);
                break;

            case DeploymentTargetType.Compose:
                AnsiConsole.MarkupLine("[dim]Docker Compose settings:[/]");
                composeProject = AnsiConsole.Ask("Project name:", envName);
                composeRestart = AnsiConsole.Ask("Restart policy:", "unless-stopped");
                break;

            case DeploymentTargetType.AwsEcs:
                AnsiConsole.MarkupLine("[dim]AWS ECS settings:[/]");
                ecsRegion = AnsiConsole.Ask("AWS region:", "us-east-1");
                ecsCluster = AnsiConsole.Ask("ECS cluster name:", "deployify");
                ecsProfile = AnsiConsole.Ask("AWS profile [dim](blank = default)[/]:", string.Empty);
                ecsSubnets = AnsiConsole.Ask("Subnet IDs [dim](comma-separated, required for Fargate)[/]:", string.Empty);
                ecsSecGroups = AnsiConsole.Ask("Security group IDs [dim](comma-separated)[/]:", string.Empty);
                ecsExecRole = AnsiConsole.Ask("Execution role ARN [dim](blank = auto-detect)[/]:", string.Empty);
                break;

            case DeploymentTargetType.AzureContainerApps:
                AnsiConsole.MarkupLine("[dim]Azure Container Apps settings:[/]");
                acaSub = AnsiConsole.Ask("Subscription ID:", string.Empty);
                acaRg = AnsiConsole.Ask("Resource group:", "deployify-rg");
                acaLocation = AnsiConsole.Ask("Location:", "eastus");
                acaEnv = AnsiConsole.Ask("Container Apps environment name:", "deployify-env");
                acaAcr = AnsiConsole.Ask("ACR login server [dim](blank to skip)[/]:", string.Empty);
                break;

            case DeploymentTargetType.NomadConsul:
                AnsiConsole.MarkupLine("[dim]Nomad + Consul Connect settings:[/]");
                ncNomadAddr = AnsiConsole.Ask("Nomad address:", "http://localhost:4646");
                ncNomadNs = AnsiConsole.Ask("Nomad namespace:", "default");
                ncNomadDc = AnsiConsole.Ask("Datacenter:", "dc1");
                ncNomadToken = AnsiConsole.Ask("Nomad ACL token [dim](blank to skip)[/]:", string.Empty);
                ncConsulAddr = AnsiConsole.Ask("Consul address:", "http://localhost:8500");
                ncConsulToken = AnsiConsole.Ask("Consul ACL token [dim](blank to skip)[/]:", string.Empty);
                ncIngress = AnsiConsole.Confirm("Enable Ingress Gateway for HTTP services?", defaultValue: false);
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
                k8SContext, k8SPrefix, k8SDomain, composeProject, composeRestart,
                ecsRegion, ecsCluster, ecsProfile, ecsSubnets, ecsSecGroups, ecsExecRole,
                acaSub, acaRg, acaLocation, acaEnv, acaAcr,
                ncNomadAddr, ncNomadToken, ncNomadNs, ncNomadDc,
                ncConsulAddr, ncConsulToken, ncIngress)
        };
    }

    // ── Config builder ────────────────────────────────────────────────────────

    private static Dictionary<string, object?> BuildTargetConfig(
        DeploymentTargetType target,
        string nomadAddr, string? nomadToken, string nomadNs, string nomadDc,
        NomadDeploymentMode nomadMode,
        string? nomadRegUser, string? nomadRegPass,
        bool nomadRegisterServices, string nomadServiceProvider, string nomadHealthPath,
        string? k8SContext, string k8SPrefix, string? k8SDomain,
        string composeProject, string composeRestart,
        string ecsRegion, string ecsCluster,
        string? ecsProfile, string? ecsSubnets, string? ecsSecGroups, string? ecsExecRole,
        string? acaSub, string acaRg, string acaLocation, string acaEnv, string? acaAcr,
        string ncNomadAddr, string? ncNomadToken, string ncNomadNs, string ncNomadDc,
        string ncConsulAddr, string? ncConsulToken, bool ncIngress)
        => target switch
        {
	        DeploymentTargetType.Nomad => new Dictionary<string, object?>
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
	        DeploymentTargetType.Kubernetes => new Dictionary<string, object?>
	        {
		        ["kubernetes"] = new KubernetesTargetConfig
		        {
			        Context = string.IsNullOrWhiteSpace(k8SContext) ? null : k8SContext,
			        NamespacePrefix = k8SPrefix,
			        IngressBaseDomain = string.IsNullOrWhiteSpace(k8SDomain) ? null : k8SDomain,
			        GenerateIngress = !string.IsNullOrWhiteSpace(k8SDomain)
		        }
	        },
	        DeploymentTargetType.Compose => new Dictionary<string, object?>
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
	        DeploymentTargetType.AwsEcs => new Dictionary<string, object?>
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
	        DeploymentTargetType.AzureContainerApps => new Dictionary<string, object?>
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
	        DeploymentTargetType.NomadConsul => new Dictionary<string, object?>
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
	        _ => []
        };

    private static string ResolveDirectory(string p) =>
        File.Exists(p) ? Path.GetDirectoryName(Path.GetFullPath(p))! : Path.GetFullPath(p);
}
