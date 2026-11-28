using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Core.ServiceLogs;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Host;

/// <summary>
/// A Windows host. Deployify runs deployify-host.ps1 locally, which opens a PowerShell remoting
/// session to the host (WinRM or SSH) or, with no host, works on this machine, and manages the
/// services through the Windows service manager.
/// </summary>
internal sealed class WindowsHost(HostTargetConfig config, string? host, ILogger logger, IProcessRunner processes) : IHostDriver
{
    public const string ScriptFileName = "deployify-host.ps1";
    private const string ResultPrefix = "##deployify ";

    public static string Script { get; } = HostScripts.Load("Deployify.Target.Host.deployify-host.ps1");

    private string? _shell;

    /// <summary>The script to run instead of the embedded one; for tests, which replace the Windows-only commands.</summary>
    internal string? ScriptPath { get; init; }

    /// <summary>How often following asks the host for new lines; replaceable in tests.</summary>
    internal TimeSpan LogPollInterval { get; init; } = TimeSpan.FromSeconds(3);

    public string Where => host ?? "this machine";

    public async Task<string?> UnavailableReasonAsync(CancellationToken ct)
    {
        if (host is null && !OperatingSystem.IsWindows())
        {
            return "host.os is Windows: list the machines to deploy to in host.hosts to deploy to them from this machine.";
        }

        return await ResolveShellAsync(ct) is null
            ? "PowerShell was not found: install PowerShell 7 (pwsh) or set host.powerShell."
            : null;
    }

    public async Task<IReadOnlyList<string>> CheckAsync(CancellationToken ct)
    {
        var info = await RunAsync("Ping", new JsonObject(), ct);
        var problems = new List<string>();
        if (!Flag(info, "windows"))
        {
            return ["it doesn't run Windows (set host.os to Linux for Linux hosts)"];
        }

        if (!Flag(info, "admin"))
        {
            problems.Add("the account is not an administrator, which installing Windows services requires");
        }

        return problems;
    }

    public async Task<IReadOnlyList<LiveService>> ListAsync(CancellationToken ct)
        => ParseList(await RunAsync("List", new JsonObject(), ct));

    internal static IReadOnlyList<LiveService> ParseList(JsonElement result)
    {
        if (!result.TryGetProperty("services", out var services))
        {
            return [];
        }

        // ConvertTo-Json writes a one-item array as the item itself in Windows PowerShell 5.1.
        var items = services.ValueKind switch
        {
            JsonValueKind.Array => services.EnumerateArray().ToList(),
            JsonValueKind.Object => [services],
            _ => []
        };

        return
        [
            .. items
                .Where(i => Str(i, "service") is not null && Str(i, "name") is not null && Str(i, "unitName") is not null)
                .Select(i => new LiveService(
                    Str(i, "service")!, Str(i, "name")!, Str(i, "unitName")!, Str(i, "environment"), Str(i, "fingerprint"),
                    Str(i, "release"), Flag(i, "installed")))
        ];
    }

    public async Task<bool> ApplyAsync(HostRelease release, CancellationToken ct)
    {
        var s = release.Service;
        var request = new JsonObject
        {
            ["service"] = new JsonObject
            {
                ["service"] = s.Service,
                ["environment"] = s.Environment,
                ["name"] = s.Name,
                ["unitName"] = s.UnitName,
                ["fingerprint"] = release.Fingerprint,
                ["release"] = release.Release,
                ["entryPoint"] = release.EntryPoint,
                ["keepReleases"] = s.KeepReleases
            },
            ["variables"] = new JsonObject(release.AllVariables()
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
            ["packagePath"] = System.IO.Path.GetFullPath(release.PackageDirectory)
        };
        AddAccount(request);

        var result = await RunAsync("Apply", request, ct);
        return !(result.TryGetProperty("changed", out var c) && c.ValueKind == JsonValueKind.False);
    }

    public async Task<bool> ActivateAsync(ServiceRef service, string release, CancellationToken ct)
    {
        var request = new JsonObject { ["name"] = service.Name, ["unitName"] = service.UnitName, ["release"] = release };
        AddAccount(request);
        var result = await RunAsync("Activate", request, ct);
        return !(result.TryGetProperty("changed", out var c) && c.ValueKind == JsonValueKind.False);
    }

    private void AddAccount(JsonObject request)
    {
        request["account"] = config.EffectiveServiceAccount;
        request["accountPassword"] = Secrets.Resolve(config.ServiceAccountPassword, "HOST_SERVICE_PASSWORD");
    }

    public async Task<HealthObservation> HealthAsync(HostService service, string release, CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(config.HealthPath) ? "/" : config.HealthPath.Trim();
        var request = new JsonObject
        {
            ["name"] = service.Name,
            ["unitName"] = service.UnitName,
            ["release"] = release,
            ["port"] = service.Ports.Count > 0 ? service.Ports[0] : null,
            ["healthHost"] = LinuxHost.HealthHost(config.ListenAddress),
            ["path"] = path.StartsWith('/') ? path : "/" + path,
            ["startup"] = Math.Max(0, config.StartupSeconds)
        };

        var result = await RunAsync("Health", request, ct);
        var detail = Str(result, "detail");
        return Str(result, "state") switch
        {
            "healthy" => HealthObservation.Healthy(detail),
            "failed" => HealthObservation.Failed(detail ?? "unhealthy"),
            _ => HealthObservation.Progressing(detail)
        };
    }

    public async Task<IReadOnlyList<string>> DeleteAsync(ServiceRef service, CancellationToken ct)
    {
        var result = await RunAsync("Delete", new JsonObject { ["name"] = service.Name, ["unitName"] = service.UnitName }, ct);
        if (!result.TryGetProperty("removed", out var removed))
        {
            return [];
        }

        // Windows PowerShell 5.1 writes a one-item array as the item itself.
        return removed.ValueKind switch
        {
            JsonValueKind.Array => [.. removed.EnumerateArray().Select(i => i.GetString()).OfType<string>()],
            JsonValueKind.String => [removed.GetString()!],
            _ => []
        };
    }

    /// <summary>
    /// The app's Application log entries and the service manager's entries about the service.
    /// Following asks the host again every few seconds for what is new since the last answer.
    /// </summary>
    public async IAsyncEnumerable<ServiceLogLine> LogsAsync(
        ServiceRef service, ServiceLogQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        var request = new JsonObject
        {
            ["name"] = service.Name,
            ["unitName"] = service.UnitName,
            ["tail"] = query.Tail,
            ["since"] = query.Since?.UtcDateTime.ToString("o"),
            ["follow"] = query.Follow
        };

        var instance = config.Hosts.Count > 1 ? host : null;
        var result = await RunAsync("Logs", request, ct);
        foreach (var line in ParseLogs(result, instance))
        {
            if (LogLines.IsSince(line, query.Since))
            {
                yield return line;
            }
        }

        while (query.Follow)
        {
            await Task.Delay(LogPollInterval, ct);
            request["cursor"] = result.TryGetProperty("cursor", out var cursor) ? JsonNode.Parse(cursor.GetRawText()) : null;
            request["tail"] = null;
            result = await RunAsync("Logs", request, ct);
            foreach (var line in ParseLogs(result, instance))
            {
                yield return line;
            }
        }
    }

    internal static IReadOnlyList<ServiceLogLine> ParseLogs(JsonElement result, string? host)
    {
        if (!result.TryGetProperty("lines", out var lines))
        {
            return [];
        }

        var items = lines.ValueKind switch
        {
            JsonValueKind.Array => [.. lines.EnumerateArray()],
            JsonValueKind.Object => [lines],
            _ => new List<JsonElement>()
        };

        return
        [
            .. items.Select(item =>
            {
                DateTimeOffset? time = Str(item, "time") is { } t && LogLines.TryParseTimestamp(t, out var ts) ? ts : null;
                var source = Str(item, "instance");
                return new ServiceLogLine
                {
                    Text = Str(item, "text") ?? "",
                    Instance = (host, source) switch
                    {
                        (null, null) => null,
                        (null, _) => source,
                        (_, null) => host,
                        _ => $"{host}/{source}"
                    },
                    Timestamp = time,
                    IsError = Flag(item, "error")
                };
            })
        ];
    }

    // ── Running the script ───────────────────────────────────────────────────

    private async Task<JsonElement> RunAsync(string action, JsonObject request, CancellationToken ct)
    {
        var shell = await ResolveShellAsync(ct)
                    ?? throw new InvalidOperationException("PowerShell was not found: install PowerShell 7 (pwsh) or set host.powerShell.");

        request["connection"] = new JsonObject
        {
            ["host"] = host,
            ["transport"] = config.Transport.ToString(),
            ["port"] = config.Port,
            ["useSsl"] = config.UseSsl,
            ["userName"] = config.UserName,
            ["password"] = Secrets.Resolve(config.Password, "HOST_PASSWORD"),
            ["keyFilePath"] = config.KeyFilePath
        };
        request["root"] = config.EffectiveRoot;

        var result = await processes.RunAsync(new ProcessSpec(shell, ShellArgs(ScriptPath ?? HostScripts.TempCopy(ScriptFileName, Script), action))
        {
            Stdin = request.ToJsonString(),
            Log = logger,
            LogTag = "host",
            Sensitive = true, // the request carries the connection password either way
            Timeout = TimeSpan.FromMinutes(20)
        }, ct);

        if (!result.Ok)
        {
            throw new InvalidOperationException($"{action} on {Where} failed: {result.ErrorOrOutput.Trim()}");
        }

        return ParseResult(result.Stdout)
               ?? throw new InvalidOperationException($"{action} on {Where} returned no result: {result.Output.Trim()}");
    }

    internal static IReadOnlyList<string> ShellArgs(string scriptPath, string action)
        => ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-Action", action];

    internal static JsonElement? ParseResult(string stdout)
    {
        var line = stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith(ResultPrefix, StringComparison.Ordinal));
        return line is null ? null : JsonDocument.Parse(line[ResultPrefix.Length..]).RootElement.Clone();
    }

    private async Task<string?> ResolveShellAsync(CancellationToken ct)
    {
        if (_shell is not null)
        {
            return _shell;
        }

        if (!string.IsNullOrWhiteSpace(config.PowerShell))
        {
            return _shell = config.PowerShell;
        }

        string[] probe = ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "exit 0"];
        if (await processes.IsAvailableAsync("pwsh", probe, TimeSpan.FromSeconds(30), ct))
        {
            return _shell = "pwsh";
        }

        if (OperatingSystem.IsWindows() && await processes.IsAvailableAsync("powershell", probe, TimeSpan.FromSeconds(30), ct))
        {
            return _shell = "powershell";
        }

        return null;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
