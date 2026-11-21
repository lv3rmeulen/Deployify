using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.ServiceLogs;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// Prints a deployed service's console output, the same way on every target: the target reads
/// it from its platform (<see cref="IServiceLogSource"/>). stdout gets the log lines only;
/// everything else goes to stderr, so the output can be piped.
/// </summary>
public static class LogsCommand
{
    public static Command Build()
    {
        var service = new Argument<string>("service")
        {
            Description = "The service, by its name in the AppHost."
        };
        var follow = new Option<bool>("--follow", "-f")
        {
            Description = "Keep printing new lines as they are written, until Ctrl+C."
        };
        var tail = new Option<string>("--tail", "-n")
        {
            Description = $"How many recent lines to start with, per instance, or 'all' (default: {ServiceLogQuery.DefaultTail}).",
            DefaultValueFactory = _ => ServiceLogQuery.DefaultTail.ToString(CultureInfo.InvariantCulture)
        };
        var since = new Option<string?>("--since")
        {
            Description = "Only lines written since then: a duration such as 30s, 10m, 2h or 1d, or a time such as 2026-05-01T10:00:00Z."
        };
        var timestamps = new Option<bool>("--timestamps")
        {
            Description = "Start each line with the time the platform recorded for it (UTC), where it records one."
        };
        var noPrefix = new Option<bool>("--no-prefix")
        {
            Description = "Don't start each line with the instance (pod, allocation, container, …) it came from."
        };
        var output = new Option<OutputFormat>("--output", "-o")
        {
            Description = "Output format: text | json. With json, every line is a JSON object on a line of its own " +
                          "(JSON Lines): service, instance, timestamp, stream and text.",
            DefaultValueFactory = _ => OutputFormat.Text
        };

        var cmd = new Command("logs", "Show a deployed service's console output (stdout and stderr), on any target.");
        cmd.Arguments.Add(service);
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.Environment);
        cmd.Options.Add(GlobalOptions.Target);
        cmd.Options.Add(GlobalOptions.AllowUnverified);
        cmd.Options.Add(follow);
        cmd.Options.Add(tail);
        cmd.Options.Add(since);
        cmd.Options.Add(timestamps);
        cmd.Options.Add(noPrefix);
        cmd.Options.Add(output);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var invocation = parseResult.InvocationConfiguration;
            using var routing = ConsoleRouting.ReserveStdout(invocation.Error);
            var reporter = ConsoleDeploymentReporter.Instance;

            if (!TryParseTail(parseResult.GetValue(tail)!, out var tailLines))
            {
                reporter.Error($"--tail takes a number of lines (0 or more) or 'all', not '{parseResult.GetValue(tail)}'.");
                return 1;
            }

            DateTimeOffset? sinceTime = null;
            if (parseResult.GetValue(since) is { } sinceText)
            {
                if (!LogLines.TryParseSince(sinceText, DateTimeOffset.UtcNow, out var parsed))
                {
                    reporter.Error($"--since takes a duration (30s, 10m, 2h, 1d) or a time (2026-05-01T10:00:00Z), not '{sinceText}'.");
                    return 1;
                }

                sinceTime = parsed;
            }

            var query = new ServiceLogQuery
            {
                Service = parseResult.GetValue(service)!,
                Tail = tailLines,
                Since = sinceTime,
                Follow = parseResult.GetValue(follow)
            };

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
                reporter.Error(ex.Message);
                return 1;
            }

            await using var session = resolved;
            var target = resolved.Target;
            IServiceLogSource? source;
            try
            {
                source = target.GetCapability<IServiceLogSource>();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                reporter.Error(ex.Message);
                return 1;
            }

            if (source is null)
            {
                reporter.Error($"The {target.DisplayName} target can't read logs: its plugin doesn't offer them.");
                return 1;
            }

            if (query.Follow)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[dim]Following {query.Service} on {target.DisplayName} ({resolved.EnvName}). Press Ctrl+C to stop.[/]");
            }

            var writer = new LogWriter(
                invocation.Output, query.Service, parseResult.GetValue(output) == OutputFormat.Json,
                parseResult.GetValue(timestamps), !parseResult.GetValue(noPrefix),
                color: ReferenceEquals(invocation.Output, Console.Out) && UseColor());
            try
            {
                await foreach (var line in source.ReadLogsAsync(resolved.Config, query, ct))
                {
                    await writer.WriteAsync(line);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Ctrl+C is how following ends.
                await invocation.Output.FlushAsync(System.Threading.CancellationToken.None);
                return 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await invocation.Output.FlushAsync(System.Threading.CancellationToken.None);
                reporter.Error($"Could not read the logs of '{query.Service}': {ex.Message}");
                return 1;
            }

            await invocation.Output.FlushAsync(ct);
            if (writer.Count == 0 && !query.Follow)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[dim]No log lines for {query.Service} on {target.DisplayName}{(query.Since is null ? "" : " in that time")}.[/]");
            }

            return 0;
        });

        return cmd;
    }

    internal static bool TryParseTail(string text, out int? tail)
    {
        tail = null;
        if (string.Equals(text.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            tail = n;
            return true;
        }

        return false;
    }

    private static bool UseColor()
        => !Console.IsOutputRedirected &&
           string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")) &&
           AnsiConsole.Profile.Capabilities.Ansi;

    /// <summary>Writes lines as text (with an optional time and instance) or as JSON Lines.</summary>
    internal sealed class LogWriter(TextWriter output, string service, bool json, bool timestamps, bool prefix, bool color)
    {
        private static readonly string[] Palette = ["36", "33", "35", "32", "34", "96", "93", "95", "92", "94"];
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        private readonly Dictionary<string, string> _colors = new(StringComparer.Ordinal);

        public int Count { get; private set; }

        public async Task WriteAsync(ServiceLogLine line)
        {
            Count++;
            if (json)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new JsonLogLine(
                    service, line.Instance, line.Timestamp?.ToUniversalTime(), line.IsError ? "stderr" : "stdout", line.Text), JsonOptions));
                return;
            }

            if (timestamps && line.Timestamp is { } t)
            {
                await output.WriteAsync(t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                await output.WriteAsync(' ');
            }

            if (prefix && line.Instance is { } instance)
            {
                await output.WriteAsync(color ? $"\e[{ColorOf(instance)}m[{instance}]\e[0m " : $"[{instance}] ");
            }

            await output.WriteLineAsync(line.Text);
        }

        private string ColorOf(string instance)
        {
            if (!_colors.TryGetValue(instance, out var code))
            {
                code = Palette[_colors.Count % Palette.Length];
                _colors[instance] = code;
            }

            return code;
        }
    }

    /// <summary>One line of <c>logs --output json</c>.</summary>
    internal sealed record JsonLogLine(string Service, string? Instance, DateTimeOffset? Timestamp, string Stream, string Text);
}
