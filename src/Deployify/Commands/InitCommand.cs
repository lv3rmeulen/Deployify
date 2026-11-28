using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Commands.Init;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Services;
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
            Description = "Browser auth token as env:NAME (omit for unauthenticated)."
        };

        var flows = new TargetInitFlows();

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
        foreach (var option in flows.Options)
        {
            cmd.Options.Add(option);
        }

        cmd.Options.Add(PlaintextSecretFlags.AllowOption);

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
                SecretFlag[] secretFlags =
                [
                    new(dashboardTokenOpt, "DEPLOYIFY_DASHBOARD_TOKEN", ReadWhenOmitted: false),
                    .. flows.SecretFlags
                ];
                if (!PlaintextSecretFlags.Check(pr, [.. secretFlags.Select(f => (f.Flag, f.EnvVar, f.ReadWhenOmitted))]))
                {
                    return 1;
                }

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
                    TargetConfig = flows.Find(target)?.FromOptions(target, pr, new InitContext(name, directory)) ?? []
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
            // The environment just set up is the active one and carries its target, so the next
            // commands need neither --env nor --target, and this list never goes stale.
            var targetNote = stateFile.ActiveEnvironment is { } activeName && stateFile.Environments.TryGetValue(activeName, out var activeEnv)
                ? $" ({activeEnv.Target})"
                : "";
            AnsiConsole.MarkupLineInterpolated($"Next steps, for [cyan]{stateFile.ActiveEnvironment}[/]{targetNote}:");
            AnsiConsole.MarkupLine("  [cyan]deployify publish[/]   [dim]build and push the images (or packages)[/]");
            AnsiConsole.MarkupLine("  [cyan]deployify plan[/]      [dim]preview what apply would change[/]");
            AnsiConsole.MarkupLine("  [cyan]deployify apply[/]     [dim]deploy[/]");
            return 0;
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
                .AddChoices(TargetInitFlows.Choices)
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
            if (await ConsolePrompts.ConfirmAsync("Require a browser token to open the dashboard?", defaultValue: false, ct))
            {
                // An empty value means "no login", so the default variable is stored as a reference.
                dashboardConfig.BrowserToken = await SecretPrompts.AskSourceAsync(
                    "dashboard browser token", "DEPLOYIFY_DASHBOARD_TOKEN", directory, ct, storeDefaultReference: true);
            }
        }

        // Plugin targets have no flow; their settings are added to the state file by hand.
        var flow = new TargetInitFlows().Find(target);
        var targetConfig = flow is null
            ? []
            : await flow.PromptAsync(target, new InitContext(envName, directory), ct);

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
            TargetConfig = targetConfig
        };
    }

    internal static string ResolveDirectory(string p)
        => File.Exists(p) ? Path.GetDirectoryName(Path.GetFullPath(p))! : Path.GetFullPath(p);
}
