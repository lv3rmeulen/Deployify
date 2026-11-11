using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Iis;

/// <summary>
/// Runs deployify-iis.ps1 with a local PowerShell, which does the IIS work on this machine or
/// on <see cref="IisTargetConfig.Host"/> through PowerShell remoting. The request goes in on
/// standard input (it can hold secrets and the WinRM password); the script prints one
/// <c>##deployify {json}</c> line with the result.
/// </summary>
internal sealed class IisRemote(IisTargetConfig iis, ILogger logger, IProcessRunner processes)
{
    public const string ScriptFileName = "deployify-iis.ps1";
    private const string ResultPrefix = "##deployify ";

    /// <summary>The script, as embedded in this assembly.</summary>
    public static string Script { get; } = LoadScript();

    private string? _shell;

    /// <summary>Where the work happens, for messages.</summary>
    public string Where => string.IsNullOrWhiteSpace(iis.Host) ? "this machine" : iis.Host;

    /// <summary>Why the target can't be used from here, or null when it can.</summary>
    public async Task<string?> UnavailableReasonAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(iis.Host) && !OperatingSystem.IsWindows())
        {
            return "IIS runs on Windows: set iis.host to the server to deploy to it from this machine.";
        }

        return await ResolveShellAsync(ct) is null
            ? "PowerShell was not found: install PowerShell 7 (pwsh) or set iis.powerShell."
            : null;
    }

    public async Task<JsonElement> RunAsync(string action, JsonObject request, bool sensitive, CancellationToken ct)
    {
        var shell = await ResolveShellAsync(ct)
                    ?? throw new InvalidOperationException("PowerShell was not found: install PowerShell 7 (pwsh) or set iis.powerShell.");

        request["connection"] = new JsonObject
        {
            ["host"] = string.IsNullOrWhiteSpace(iis.Host) ? null : iis.Host,
            ["transport"] = iis.Transport.ToString(),
            ["port"] = iis.Port,
            ["useSsl"] = iis.UseSsl,
            ["userName"] = iis.UserName,
            ["password"] = Secrets.Resolve(iis.Password, "IIS_PASSWORD"),
            ["keyFilePath"] = iis.KeyFilePath
        };
        request["sitesRoot"] = iis.SitesRoot;

        var result = await processes.RunAsync(new ProcessSpec(shell, ShellArgs(ScriptPath(), action))
        {
            Stdin = request.ToJsonString(),
            Log = logger,
            LogTag = "iis",
            Sensitive = sensitive,
            Timeout = TimeSpan.FromMinutes(20)
        }, ct);

        if (!result.Ok)
        {
            throw new InvalidOperationException($"IIS {action} on {Where} failed: {result.ErrorOrOutput.Trim()}");
        }

        return ParseResult(result.Stdout)
               ?? throw new InvalidOperationException($"IIS {action} on {Where} returned no result: {result.Output.Trim()}");
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

        if (!string.IsNullOrWhiteSpace(iis.PowerShell))
        {
            return _shell = iis.PowerShell;
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

    /// <summary>
    /// The embedded script, written once per version to the temp folder, so every command runs
    /// the script this build of Deployify ships, whatever is in the output folder.
    /// </summary>
    private static string ScriptPath()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Script)))[..12];
        var dir = Path.Combine(Path.GetTempPath(), "deployify");
        var path = Path.Combine(dir, $"deployify-iis-{hash}.ps1");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(dir);
            var temp = path + "." + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, Script, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }

        return path;
    }

    private static string LoadScript()
    {
        using var stream = typeof(IisRemote).Assembly.GetManifestResourceStream("Deployify.Target.Iis.deployify-iis.ps1")
                           ?? throw new InvalidOperationException("deployify-iis.ps1 is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
