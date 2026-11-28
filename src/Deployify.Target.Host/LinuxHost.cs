using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Processes;
using Deployify.Core.ServiceLogs;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Host;

/// <summary>
/// A Linux host with systemd. Every action sends deployify-host.sh to bash on standard input,
/// through <c>ssh host bash -s</c> or, with no host, to bash on this machine; packages go over
/// with <c>scp</c>. Nothing is installed on the host besides the services themselves.
/// </summary>
internal sealed class LinuxHost(HostTargetConfig config, string? host, ILogger logger, IProcessRunner processes) : IHostDriver
{
    public const string ScriptFileName = "deployify-host.sh";
    private const string ResultPrefix = "##deployify ";

    public static string Script { get; } = HostScripts.Load("Deployify.Target.Host.deployify-host.sh");

    public string Where => host ?? "this machine";

    private string Root => config.EffectiveRoot;

    public async Task<string?> UnavailableReasonAsync(CancellationToken ct)
    {
        if (host is null)
        {
            return OperatingSystem.IsLinux()
                ? null
                : "host.os is Linux, but Deployify isn't running on Linux: list the machines to deploy to in host.hosts.";
        }

        // ssh -V prints its version on stderr and exits 0.
        return await processes.IsAvailableAsync("ssh", ["-V"], TimeSpan.FromSeconds(30), ct)
            ? null
            : "ssh was not found: install the OpenSSH client to deploy to Linux hosts.";
    }

    public async Task<IReadOnlyList<string>> CheckAsync(CancellationToken ct)
    {
        var info = await RunAsync("ping", new Dictionary<string, string> { ["dotnetroot"] = config.DotnetRoot ?? "" }, sensitive: false, ct);
        var problems = new List<string>();
        var user = One(info, "user") ?? "the deploy user";
        if (One(info, "os") is { } os && os != "Linux")
        {
            problems.Add($"it runs {os}, not Linux (set host.os to Windows for Windows hosts)");
        }

        if (One(info, "systemd") != "yes")
        {
            problems.Add("systemd (systemctl) was not found");
        }

        if (One(info, "sudo") != "yes")
        {
            problems.Add($"{user} can't run 'sudo -n systemctl' (add a sudoers rule for systemctl and journalctl, or connect as root)");
        }

        if (One(info, "root") != "yes")
        {
            problems.Add($"{user} can't write to {Root} (create it and make {user} its owner)");
        }

        if (One(info, "tar") != "yes")
        {
            problems.Add("tar was not found");
        }

        if (!config.SelfContained && One(info, "dotnet") != "yes")
        {
            problems.Add("no .NET runtime was found, which framework-dependent apps need (install one, set host.dotnetRoot, or set host.selfContained to true)");
        }

        if (One(info, "curl") != "yes")
        {
            logger.LogWarning("{Host}: curl is not installed, so the health gate only checks that services keep running.", Where);
        }

        return problems;
    }

    public async Task<IReadOnlyList<LiveService>> ListAsync(CancellationToken ct)
    {
        var result = await RunAsync("list", [], sensitive: false, ct);
        return result.TryGetValue("service", out var markers) ? [.. markers.Select(ParseMarker).OfType<LiveService>()] : [];
    }

    internal static LiveService? ParseMarker(string marker)
    {
        var fields = marker.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Contains('='))
            .Select(l => (Key: l[..l.IndexOf('=')], Value: l[(l.IndexOf('=') + 1)..]))
            .GroupBy(f => f.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        string? Get(string key) => fields.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

        return Get("service") is { } service && Get("name") is { } name && Get("unit") is { } unit
            ? new LiveService(service, name, unit, Get("environment"), Get("fingerprint"), Get("release"), Get("installed") == "yes")
            : null;
    }

    public async Task<bool> ApplyAsync(HostRelease release, CancellationToken ct)
    {
        var service = release.Service;
        var args = new Dictionary<string, string>
        {
            ["name"] = service.Name,
            ["unit"] = service.UnitName,
            ["release"] = release.Release
        };

        var prepared = await RunAsync("prepare", args, sensitive: false, ct);
        string archive;
        if (One(prepared, "upload") == "yes")
        {
            var local = await PackageArchive.EnsureAsync(release, ct);
            archive = host is null ? local : One(prepared, "incoming") ?? throw new InvalidOperationException($"{Where} gave no upload path.");
            if (host is not null)
            {
                await UploadAsync(local, archive, ct);
            }
        }
        else
        {
            archive = "";
        }

        args["archive"] = archive;
        args["service"] = service.Service;
        args["environment"] = service.Environment ?? "";
        args["fingerprint"] = release.Fingerprint;
        args["entry"] = release.EntryPoint;
        args["runtime"] = config.EffectiveRuntime;
        args["keep"] = service.KeepReleases.ToString(CultureInfo.InvariantCulture);
        args["unitfile"] = SystemdUnit.Render(service, Root);
        args["env"] = SystemdUnit.RenderEnvironment(release.AllVariables());

        var result = await RunAsync("apply", args, sensitive: true, ct);
        return One(result, "changed") != "no";
    }

    public async Task<bool> ActivateAsync(ServiceRef service, string release, CancellationToken ct)
    {
        var result = await RunAsync("activate", new Dictionary<string, string>
        {
            ["name"] = service.Name, ["unit"] = service.UnitName, ["release"] = release
        }, sensitive: false, ct);
        return One(result, "changed") != "no";
    }

    public async Task<HealthObservation> HealthAsync(HostService service, string release, CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(config.HealthPath) ? "/" : config.HealthPath.Trim();
        var result = await RunAsync("health", new Dictionary<string, string>
        {
            ["name"] = service.Name,
            ["unit"] = service.UnitName,
            ["service"] = service.Service,
            ["release"] = release,
            ["port"] = service.Ports.Count > 0 ? service.Ports[0].ToString(CultureInfo.InvariantCulture) : "",
            ["healthhost"] = HealthHost(config.ListenAddress),
            ["path"] = path.StartsWith('/') ? path : "/" + path,
            ["startup"] = Math.Max(0, config.StartupSeconds).ToString(CultureInfo.InvariantCulture)
        }, sensitive: false, ct);

        var detail = One(result, "detail");
        return One(result, "state") switch
        {
            "healthy" => HealthObservation.Healthy(detail),
            "failed" => HealthObservation.Failed(detail ?? "unhealthy"),
            _ => HealthObservation.Progressing(detail)
        };
    }

    /// <summary>Where the health gate sends its request: the listen address, or loopback when it listens everywhere.</summary>
    internal static string HealthHost(string? listenAddress)
        => string.IsNullOrWhiteSpace(listenAddress) || listenAddress.Trim() is "*" or "+" or "0.0.0.0" or "localhost" or "[::]"
            ? "127.0.0.1"
            : listenAddress.Trim();

    public async Task<IReadOnlyList<string>> DeleteAsync(ServiceRef service, CancellationToken ct)
    {
        var result = await RunAsync("delete", new Dictionary<string, string> { ["name"] = service.Name, ["unit"] = service.UnitName },
            sensitive: false, ct);
        return result.TryGetValue("removed", out var removed) ? removed : [];
    }

    /// <summary>
    /// The unit's journal, read with <c>journalctl -o json</c>, so every line has its time and
    /// priority. Following keeps one <c>journalctl -f</c> running until cancelled.
    /// </summary>
    public async IAsyncEnumerable<ServiceLogLine> LogsAsync(
        ServiceRef service, ServiceLogQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        var args = new Dictionary<string, string>
        {
            ["name"] = service.Name,
            ["unit"] = service.UnitName,
            ["tail"] = query.Tail?.ToString(CultureInfo.InvariantCulture) ?? "",
            ["since"] = query.Since?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "",
            ["follow"] = query.Follow ? "yes" : "no"
        };

        var instance = config.Hosts.Count > 1 ? host : null;
        var errors = new StringBuilder();
        await foreach (var line in processes.StreamAsync(Spec("logs", args, sensitive: true, timeout: null), ct))
        {
            if (line.IsStdErr)
            {
                errors.AppendLine(line.Text);
                continue;
            }

            if (ParseJournalLine(line.Text, instance) is { } parsed)
            {
                yield return parsed;
            }
        }

        if (errors.Length > 0 && !ct.IsCancellationRequested)
        {
            var message = errors.ToString().Trim();
            if (message.Contains("sudo", StringComparison.OrdinalIgnoreCase) || message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("ssh", StringComparison.OrdinalIgnoreCase) || message.Contains("No journal files", StringComparison.Ordinal))
            {
                throw new ServiceLogsException($"Reading the journal on {Where} failed: {message}");
            }

            logger.LogDebug("journalctl on {Host}: {Message}", Where, message);
        }
    }

    /// <summary>One <c>journalctl -o json</c> line: MESSAGE (text, or bytes for binary data), its time and priority.</summary>
    internal static ServiceLogLine? ParseJournalLine(string json, string? instance)
    {
        if (string.IsNullOrWhiteSpace(json) || json[0] != '{')
        {
            return null;
        }

        JsonElement e;
        try
        {
            e = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            return null;
        }

        var text = e.TryGetProperty("MESSAGE", out var m)
            ? m.ValueKind switch
            {
                JsonValueKind.String => m.GetString() ?? "",
                JsonValueKind.Array => Encoding.UTF8.GetString([.. m.EnumerateArray().Where(b => b.ValueKind == JsonValueKind.Number).Select(b => (byte)b.GetInt32())]),
                _ => ""
            }
            : "";

        DateTimeOffset? time = e.TryGetProperty("__REALTIME_TIMESTAMP", out var t) && t.ValueKind == JsonValueKind.String &&
                               long.TryParse(t.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros)
            ? DateTimeOffset.FromUnixTimeMilliseconds(micros / 1000)
            : null;
        var priority = e.TryGetProperty("PRIORITY", out var p) && p.ValueKind == JsonValueKind.String &&
                       int.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)
            ? level
            : 6;

        return new ServiceLogLine { Text = text.TrimEnd('\r', '\n'), Timestamp = time, Instance = instance, IsError = priority <= 3 };
    }

    // ── Running the script ───────────────────────────────────────────────────

    private async Task<Dictionary<string, List<string>>> RunAsync(
        string action, Dictionary<string, string> args, bool sensitive, CancellationToken ct)
    {
        var result = await processes.RunAsync(Spec(action, args, sensitive, TimeSpan.FromMinutes(10)), ct);
        if (!result.Ok)
        {
            var message = result.ErrorOrOutput.Trim();
            throw new InvalidOperationException(
                $"{action} on {Where} failed: {(message.Length > 0 ? message : $"exit code {result.ExitCode}")}");
        }

        return ParseResult(result.Stdout);
    }

    private ProcessSpec Spec(string action, Dictionary<string, string> args, bool sensitive, TimeSpan? timeout)
    {
        var program = Program(action, args, Root);
        return host is null
            ? new ProcessSpec("bash", ["-s"]) { Stdin = program, Log = logger, LogTag = "host", Sensitive = sensitive, Timeout = timeout }
            : new ProcessSpec("ssh", [.. SshArgs(config), host, "bash -s"])
            {
                Stdin = program, Log = logger, LogTag = "host", Sensitive = sensitive, Timeout = timeout
            };
    }

    internal static List<string> SshArgs(HostTargetConfig config)
    {
        List<string> args = ["-o", "BatchMode=yes", "-o", "ConnectTimeout=20"];
        if (config.Port is { } port)
        {
            args.AddRange(["-p", port.ToString(CultureInfo.InvariantCulture)]);
        }

        if (!string.IsNullOrWhiteSpace(config.KeyFilePath))
        {
            args.AddRange(["-i", config.KeyFilePath]);
        }

        if (!string.IsNullOrWhiteSpace(config.UserName))
        {
            args.AddRange(["-l", config.UserName]);
        }

        return args;
    }

    /// <summary>
    /// Copies the archive with scp. The local side is passed as a file name in its own folder:
    /// scp would read a Windows drive letter (C:) as a host name.
    /// </summary>
    private async Task UploadAsync(string localArchive, string remotePath, CancellationToken ct)
    {
        List<string> args = ["-q", "-o", "BatchMode=yes", "-o", "ConnectTimeout=20"];
        if (config.Port is { } port)
        {
            args.AddRange(["-P", port.ToString(CultureInfo.InvariantCulture)]);
        }

        if (!string.IsNullOrWhiteSpace(config.KeyFilePath))
        {
            args.AddRange(["-i", config.KeyFilePath]);
        }

        if (!string.IsNullOrWhiteSpace(config.UserName))
        {
            args.AddRange(["-o", "User=" + config.UserName]);
        }

        args.AddRange([Path.GetFileName(localArchive), $"{host}:{remotePath}"]);
        logger.LogInformation("Uploading {Archive} to {Host}", Path.GetFileName(localArchive), Where);
        (await processes.RunAsync(new ProcessSpec("scp", args)
            {
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(localArchive)), Log = logger, LogTag = "scp",
                Timeout = TimeSpan.FromMinutes(30)
            }, ct))
            .EnsureSuccess($"scp to {Where}");
    }

    /// <summary>
    /// The request followed by the script. Each value travels base64-encoded, so no quoting can
    /// break it; the trailing 'x' keeps a value's final newlines, which $( ) would drop.
    /// </summary>
    internal static string Program(string action, IReadOnlyDictionary<string, string> args, string root)
    {
        var sb = new StringBuilder();
        sb.Append("DEPLOYIFY_ACTION=").Append(action).Append('\n');
        foreach (var (key, value) in args.Append(KeyValuePair.Create("root", root)))
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            sb.Append("arg_").Append(key).Append("=\"$(printf '%s' '").Append(b64).Append("' | base64 -d; printf x)\"; ")
                .Append("arg_").Append(key).Append("=\"${arg_").Append(key).Append("%x}\"\n");
        }

        sb.Append(Script.ReplaceLineEndings("\n"));
        return sb.ToString();
    }

    internal static Dictionary<string, List<string>> ParseResult(string stdout)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(ResultPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line[ResultPrefix.Length..].Split(' ', 2);
            var value = parts.Length > 1 && parts[1].Length > 0 ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])) : "";
            if (!result.TryGetValue(parts[0], out var list))
            {
                result[parts[0]] = list = [];
            }

            list.Add(value);
        }

        return result;
    }

    private static string? One(Dictionary<string, List<string>> result, string key)
        => result.TryGetValue(key, out var values) && values.Count > 0 ? values[^1] : null;
}

/// <summary>The package as a .tar.gz, for copying to Linux hosts.</summary>
internal static class PackageArchive
{
    public static async Task<string> EnsureAsync(HostRelease release, CancellationToken ct)
    {
        var path = release.ArchivePath;
        if (File.Exists(path))
        {
            return path;
        }

        foreach (var old in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "package-*.tar.gz"))
        {
            File.Delete(old); // an earlier build's
        }

        var temp = path + "." + Guid.NewGuid().ToString("N");
        await using (var file = File.Create(temp))
        await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        {
            await TarFile.CreateFromDirectoryAsync(release.PackageDirectory, gzip, includeBaseDirectory: false, ct);
        }

        File.Move(temp, path, overwrite: true);
        return path;
    }
}
