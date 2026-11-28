using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Deployify.Target.Host;

/// <summary>
/// Host target settings stored under targetConfig["host"] in deployify-state.json. Each Aspire
/// project is published to a folder and runs as an operating-system service on one or more
/// machines: a systemd unit on Linux, a Windows service on Windows. Every release gets its own
/// folder under <see cref="Root"/>; a deploy switches the service to the new folder and
/// restarts it, and a rollback switches it back.
/// </summary>
public sealed class HostTargetConfig
{
    /// <summary>
    /// The machines to deploy to, as host names or SSH config aliases (<c>user@</c> is allowed).
    /// Every service runs on every host; a deploy goes host by host and stops at the first one
    /// where the new release isn't healthy. Leave empty to deploy to the machine Deployify runs on.
    /// </summary>
    public List<string> Hosts { get; set; } = [];

    /// <summary>
    /// Operating system of the hosts. Defaults to the OS Deployify runs on when
    /// <see cref="Hosts"/> is empty, and to Linux otherwise.
    /// </summary>
    public HostOs? Os { get; set; }

    /// <summary>
    /// Windows hosts only: PowerShell remoting over WinRM or over SSH. Linux hosts are always
    /// reached with <c>ssh</c>.
    /// </summary>
    public HostTransport Transport { get; set; } = HostTransport.WinRm;

    /// <summary>SSH or WinRM port. Null uses the default (22, or 5985/5986 for WinRM).</summary>
    public int? Port { get; set; }

    /// <summary>WinRM: connect over HTTPS (port 5986).</summary>
    public bool UseSsl { get; set; }

    /// <summary>
    /// User to connect as. On Linux the services also run as this user, so the deploy user
    /// owns the release folders and the services' secrets; it needs passwordless sudo for
    /// <c>systemctl</c> and <c>journalctl</c> unless it is root. Defaults to the SSH client's
    /// own choice (the current user, or what ~/.ssh/config says).
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Windows over WinRM: the password for <see cref="UserName"/>. Use an <c>env:NAME</c>
    /// reference; defaults to the <c>HOST_PASSWORD</c> environment variable. SSH uses key
    /// authentication instead (<see cref="KeyFilePath"/>).
    /// </summary>
    public string? Password { get; set; }

    /// <summary>SSH: private key file. Defaults to the SSH client's own key lookup.</summary>
    public string? KeyFilePath { get; set; }

    /// <summary>
    /// Windows hosts: PowerShell executable Deployify runs locally to drive the deployment.
    /// Defaults to <c>pwsh</c>, falling back to Windows PowerShell (<c>powershell</c>) on Windows.
    /// </summary>
    public string? PowerShell { get; set; }

    /// <summary>
    /// Folder on each host that holds one folder per service, with its releases. Defaults to
    /// <c>/opt/deployify</c> on Linux and <c>C:\deployify</c> on Windows. On Linux the deploy
    /// user must be able to create it, or it must exist and belong to that user.
    /// </summary>
    public string? Root { get; set; }

    /// <summary>
    /// Prefix for service names. Defaults to "{environment}-". The systemd unit or Windows service
    /// is called <c>deployify-{prefix}{service}</c>.
    /// </summary>
    public string? NamePrefix { get; set; }

    /// <summary>
    /// Windows hosts: the account services run as. Defaults to <c>NT AUTHORITY\LocalService</c>;
    /// <c>NT AUTHORITY\NetworkService</c>, <c>LocalSystem</c>, a group managed service account
    /// (ending in <c>$</c>) or a user with <see cref="ServiceAccountPassword"/> also work.
    /// </summary>
    public string? ServiceAccount { get; set; }

    /// <summary>
    /// Windows hosts: password for a user <see cref="ServiceAccount"/>. Use an <c>env:NAME</c>
    /// reference; defaults to the <c>HOST_SERVICE_PASSWORD</c> environment variable.
    /// </summary>
    public string? ServiceAccountPassword { get; set; }

    /// <summary>
    /// Release folders kept per service, the current one included. <c>deployify rollback</c> can
    /// only go back to a release that is still on the host, so at least 2 are kept.
    /// </summary>
    public int KeepReleases { get; set; } = 3;

    /// <summary>
    /// Path the health gate requests on each service with an http endpoint, from the host itself.
    /// Any response below 500 counts as healthy. A service without an http endpoint is healthy
    /// once it has kept running for <see cref="StartupSeconds"/>.
    /// </summary>
    public string HealthPath { get; set; } = "/";

    /// <summary>
    /// How long a service without an http endpoint must keep running after a restart before
    /// it counts as healthy.
    /// </summary>
    public int StartupSeconds { get; set; } = 10;

    /// <summary>
    /// With several hosts: how long to wait for a service to become healthy on one host before
    /// the deploy moves on to the next. A service that isn't healthy by then stops the deploy.
    /// </summary>
    public int HostHealthTimeoutSeconds { get; set; } = 120;

    /// <summary>Runtime identifier passed to <c>dotnet publish -r</c>. Defaults to linux-x64 or win-x64.</summary>
    public string? Runtime { get; set; }

    /// <summary>
    /// Publish self-contained apps, so the hosts don't need a .NET runtime. On by default; turn
    /// it off to publish smaller framework-dependent apps for hosts with .NET installed.
    /// </summary>
    public bool SelfContained { get; set; } = true;

    /// <summary>
    /// Linux, framework-dependent apps: where .NET is installed on the hosts, when it isn't in a
    /// location the app finds by itself. Set as <c>DOTNET_ROOT</c> for every service.
    /// </summary>
    public string? DotnetRoot { get; set; }

    /// <summary>Base port for services whose Aspire bindings carry no port.</summary>
    public int BasePort { get; set; } = 8080;

    /// <summary>
    /// Address services listen on. <c>*</c> (the default) listens on every interface, so the
    /// services are reachable from other machines; <c>localhost</c> keeps them on the host, for
    /// a reverse proxy you run yourself.
    /// </summary>
    public string ListenAddress { get; set; } = "*";

    /// <summary>
    /// Where to reach resources the host target can't run (Redis, databases and other containers),
    /// keyed by resource name. References to them resolve to this host instead of the resource name.
    /// </summary>
    public Dictionary<string, string> ExternalHosts { get; set; } = [];

    /// <summary>The OS the hosts run: <see cref="Os"/>, or the default for <see cref="Hosts"/>.</summary>
    [JsonIgnore]
    public HostOs EffectiveOs
        => Os ?? (Hosts.Count == 0 && OperatingSystem.IsWindows() ? HostOs.Windows : HostOs.Linux);

    /// <summary><see cref="Root"/>, or the OS's default.</summary>
    [JsonIgnore]
    public string EffectiveRoot
        => string.IsNullOrWhiteSpace(Root)
            ? EffectiveOs == HostOs.Windows ? @"C:\deployify" : "/opt/deployify"
            : Root.TrimEnd('/', '\\') is { Length: > 0 } trimmed ? trimmed : Root;

    /// <summary><see cref="Runtime"/>, or the OS's default.</summary>
    [JsonIgnore]
    public string EffectiveRuntime
        => string.IsNullOrWhiteSpace(Runtime) ? EffectiveOs == HostOs.Windows ? "win-x64" : "linux-x64" : Runtime;

    /// <summary><see cref="ServiceAccount"/>, or LocalService.</summary>
    [JsonIgnore]
    public string EffectiveServiceAccount
        => string.IsNullOrWhiteSpace(ServiceAccount) ? @"NT AUTHORITY\LocalService" : ServiceAccount;

    /// <summary>The hosts to deploy to; <c>null</c> stands for this machine.</summary>
    [JsonIgnore]
    public IReadOnlyList<string?> Targets
        => Hosts.Count == 0 ? [null] : [.. Hosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => (string?)h.Trim())];
}

public enum HostOs
{
    /// <summary>systemd units, reached over SSH.</summary>
    Linux,

    /// <summary>Windows services, reached over PowerShell remoting.</summary>
    Windows
}

public enum HostTransport
{
    /// <summary>PowerShell remoting over WinRM (Windows PowerShell's default).</summary>
    WinRm,

    /// <summary>PowerShell remoting over SSH (PowerShell 7 on both ends; works from Linux runners).</summary>
    Ssh
}
