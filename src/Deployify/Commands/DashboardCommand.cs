using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Core.ResourceSources;
using Deployify.ResourceServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// Runs a standalone Aspire dashboard resource service for a deployed environment, so the Aspire
/// dashboard shows its services (with their instances, console logs and start/stop/restart) the
/// way it does for <c>aspire run</c>. Optionally starts the dashboard container pointed at it.
/// </summary>
public static class DashboardCommand
{
    /// <summary>Read when --api-key is left out; a random key is generated when it isn't set either.</summary>
    public const string ApiKeyVariable = "DEPLOYIFY_RESOURCE_SERVICE_API_KEY";

    private const string DashboardContainerPort = "18888";
    private const string DashboardOtlpContainerPort = "18889";

    public static Command Build()
    {
        var port = new Option<int>("--port")
        {
            Description = $"Port the resource service listens on (default: {ResourceServerOptions.DefaultPort}).",
            DefaultValueFactory = _ => ResourceServerOptions.DefaultPort
        };
        var bind = new Option<string>("--bind")
        {
            Description = "Address to listen on: an IP address, localhost, or 0.0.0.0 for every interface (default: 127.0.0.1).",
            DefaultValueFactory = _ => "127.0.0.1"
        };
        var apiKey = new Option<string?>("--api-key")
        {
            Description = $"API key the dashboard must send, as env:NAME. Default: ${ApiKeyVariable}, or a random key printed at start."
        };
        var unsecured = new Option<bool>("--unsecured")
        {
            Description = "Accept every caller without an API key. Only allowed when listening on a loopback address."
        };
        var pollInterval = new Option<double>("--poll-interval")
        {
            Description = "Seconds between reads of the target (default: 3).",
            DefaultValueFactory = _ => 3
        };
        var hideEnv = new Option<bool>("--hide-env-values")
        {
            Description = "Show environment variable names without their values. Secret values are never shown."
        };
        var launch = new Option<bool>("--launch")
        {
            Description = "Also start the Aspire dashboard container (Docker or Podman) pointed at the resource service."
        };
        var dashboardPort = new Option<int?>("--dashboard-port")
        {
            Description = "With --launch: host port for the dashboard UI (default: dashboard.port, 18888)."
        };

        var cmd = new Command("dashboard",
            "Serve the environment's deployed resources to the Aspire dashboard (a standalone resource service).");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.Environment);
        cmd.Options.Add(GlobalOptions.Target);
        cmd.Options.Add(GlobalOptions.AllowUnverified);
        cmd.Options.Add(port);
        cmd.Options.Add(bind);
        cmd.Options.Add(apiKey);
        cmd.Options.Add(unsecured);
        cmd.Options.Add(pollInterval);
        cmd.Options.Add(hideEnv);
        cmd.Options.Add(launch);
        cmd.Options.Add(dashboardPort);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var bindAddress = parseResult.GetValue(bind)!;
            var isLoopback = IsLoopback(bindAddress);
            var noKey = parseResult.GetValue(unsecured);
            if (noKey && !isLoopback)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[red]✗[/] --unsecured is only allowed on a loopback address; '{bindAddress}' lets other machines connect.");
                return 1;
            }

            string? key = null;
            if (!noKey)
            {
                var configured = parseResult.GetValue(apiKey);
                if (Secrets.IsLiteral(configured))
                {
                    AnsiConsole.MarkupLine("[red]✗[/] --api-key takes an env:NAME reference, so the key stays out of shell history.");
                    return 1;
                }

                key = Secrets.Resolve(configured, ApiKeyVariable);
                if (configured is not null && key is null)
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {configured} is not set.");
                    return 1;
                }

                key ??= ResourceServerOptions.GenerateApiKey();
            }

            ResolvedContext resolved;
            try
            {
                resolved = await DependencyInjection.ResolveAsync(
                    parseResult.GetValue(GlobalOptions.ProjectPath)!,
                    parseResult.GetValue(GlobalOptions.Environment),
                    parseResult.GetValue(GlobalOptions.Target), ct,
                    allowUnverifiedPlugins: parseResult.GetValue(GlobalOptions.AllowUnverified));
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {ex.Message}");
                return 1;
            }

            await using var session = resolved;
            var (services, config, envName, _, target, _) = resolved;
            if (target is not IResourceSourceProvider provider)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[red]✗[/] The {target.DisplayName} target can't be watched yet. Supported: Kubernetes, Nomad.");
                return 1;
            }

            var (reachable, error) = await target.PingAsync(config, ct);
            if (!reachable)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]![/] {target.DisplayName} is not reachable ({error}). Serving anyway; resources appear once it is.");
            }

            var loggers = services.GetRequiredService<ILoggerFactory>();
            var options = new ResourceSourceOptions
            {
                PollInterval = TimeSpan.FromSeconds(Math.Max(0.5, parseResult.GetValue(pollInterval))),
                ShowEnvironmentValues = !parseResult.GetValue(hideEnv)
            };
            await using var source = provider.CreateResourceSource(config, options, loggers);

            RunningResourceServer server;
            try
            {
                server = await ResourceServer.ResourceServer.StartAsync(source, new ResourceServerOptions
                {
                    BindAddress = bindAddress,
                    Port = parseResult.GetValue(port),
                    ApiKey = key,
                    ApplicationName = $"{envName} ({target.DisplayName})"
                }, loggers, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] Could not start the resource service: {ex.Message}");
                return 1;
            }

            await using (server)
            {
                AnsiConsole.MarkupLineInterpolated($"[bold]Deployify resource service[/] — [cyan]{envName}[/] / [cyan]{source.DisplayName}[/]");
                PrintConnectionInfo(server.Url, key);

                string? container = null;
                if (parseResult.GetValue(launch))
                {
                    container = await LaunchDashboardAsync(config, envName, server.Url, key,
                        parseResult.GetValue(dashboardPort) ?? config.Dashboard.Port, ct);
                    if (container is null)
                    {
                        return 1;
                    }
                }

                AnsiConsole.MarkupLine("[dim]Press Ctrl+C to stop.[/]");
                await server.WaitForShutdownAsync(ct);

                if (container is not null)
                {
                    await StopDashboardAsync(config, container);
                }
            }

            AnsiConsole.MarkupLine("[dim]Resource service stopped.[/]");
            return 0;
        });

        return cmd;
    }

    private static void PrintConnectionInfo(Uri url, string? key)
    {
        var table = new Table().HideHeaders().AddColumns("", "");
        table.AddRow("Resource service", Markup.Escape(url.ToString()));
        table.AddRow("Auth", key is null ? "[yellow]none (unsecured)[/]" : "API key");
        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine("To point a standalone Aspire dashboard at it, set:");
        AnsiConsole.MarkupLineInterpolated($"  [grey]Dashboard__ResourceServiceClient__Url=[/]{url}");
        if (key is null)
        {
            AnsiConsole.MarkupLine("  [grey]Dashboard__ResourceServiceClient__AuthMode=[/]Unsecured");
        }
        else
        {
            AnsiConsole.MarkupLine("  [grey]Dashboard__ResourceServiceClient__AuthMode=[/]ApiKey");
            AnsiConsole.MarkupLineInterpolated($"  [grey]Dashboard__ResourceServiceClient__ApiKey=[/]{key}");
        }

        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Starts the dashboard container pointed at the resource service; returns its name, or null
    /// when it couldn't be started. The API key and browser token reach the container through the
    /// environment of the docker process, never its command line.
    /// </summary>
    private static async Task<string?> LaunchDashboardAsync(
        EnvironmentConfig config, string envName, Uri serverUrl, string? key, int uiPort, CancellationToken ct)
    {
        var runtime = config.ContainerRuntime == ContainerRuntime.Podman ? "podman" : "docker";
        var name = $"deployify-dashboard-{envName}";
        var image = $"{AspireDashboard.Image}:{config.Dashboard.ImageTag}";
        var otlpPort = config.Dashboard.OtlpPort;
        var ui = uiPort.ToString(CultureInfo.InvariantCulture);
        var otlp = otlpPort.ToString(CultureInfo.InvariantCulture);

        var args = new List<string> { "run", "--rm", "-d", "--name", name };
        string resourceServiceUrl;
        if (OperatingSystem.IsLinux())
        {
            // Host networking: the container reaches the service on the host's loopback address.
            args.AddRange(["--network", "host",
                "-e", $"ASPNETCORE_URLS=http://localhost:{ui}",
                "-e", $"ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL=http://localhost:{otlp}",
                "-e", $"DOTNET_DASHBOARD_OTLP_ENDPOINT_URL=http://localhost:{otlp}"]);
            resourceServiceUrl = serverUrl.ToString();
        }
        else
        {
            // Docker/Podman Desktop: publish the ports, reach the host through its gateway name.
            args.AddRange(["-p", $"127.0.0.1:{ui}:{DashboardContainerPort}", "-p", $"127.0.0.1:{otlp}:{DashboardOtlpContainerPort}"]);
            var gateway = runtime == "podman" ? "host.containers.internal" : "host.docker.internal";
            resourceServiceUrl = new UriBuilder(serverUrl) { Host = gateway }.Uri.ToString();
        }

        var env = new Dictionary<string, string?>
        {
            ["Dashboard__ResourceServiceClient__Url"] = resourceServiceUrl,
            ["Dashboard__ResourceServiceClient__AuthMode"] = key is null ? "Unsecured" : "ApiKey",
            ["Dashboard__Otlp__AuthMode"] = "Unsecured"
        };
        if (key is not null)
        {
            env["Dashboard__ResourceServiceClient__ApiKey"] = key;
        }

        var browserToken = Secrets.Resolve(config.Dashboard.BrowserToken);
        if (browserToken is not null)
        {
            env["Dashboard__Frontend__AuthMode"] = "BrowserToken";
            env["Dashboard__Frontend__BrowserToken"] = browserToken;
        }
        else
        {
            env["Dashboard__Frontend__AuthMode"] = "Unsecured";
        }

        foreach (var variable in env.Keys)
        {
            // "-e NAME" without a value copies NAME from the docker process's own environment.
            args.AddRange(["-e", variable]);
        }

        args.Add(image);

        ProcessResult result;
        try
        {
            result = await ProcessRunner.Default.RunAsync(new ProcessSpec(runtime, args) { Env = env, Sensitive = true }, ct);
        }
        catch (ToolNotFoundException)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]✗[/] --launch needs {runtime} on PATH.");
            return null;
        }

        if (!result.Ok)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]✗[/] Could not start the dashboard container: {result.ErrorOrOutput.Trim()}");
            return null;
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[green]✓[/] Dashboard ({image}) at [link]http://localhost:{ui}[/] — send OTLP to http://localhost:{otlp}");
        if (browserToken is null)
        {
            AnsiConsole.MarkupLine("[dim]  The UI has no login (dashboard.browserToken is not set); it only listens on localhost.[/]");
        }

        return name;
    }

    private static async Task StopDashboardAsync(EnvironmentConfig config, string container)
    {
        var runtime = config.ContainerRuntime == ContainerRuntime.Podman ? "podman" : "docker";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await ProcessRunner.Default.RunAsync(new ProcessSpec(runtime, ["stop", container]), timeout.Token);
        }
        catch (Exception ex) when (ex is ToolNotFoundException or OperationCanceledException or TimeoutException)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]![/] Could not stop container {container}; stop it with '{runtime} stop {container}'.");
        }
    }

    internal static bool IsLoopback(string bindAddress)
        => bindAddress == "localhost" || (IPAddress.TryParse(bindAddress, out var ip) && IPAddress.IsLoopback(ip));
}
