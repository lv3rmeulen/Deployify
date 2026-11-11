using System.Collections.Generic;

namespace Deployify.Target.Iis;

/// <summary>
/// IIS settings stored under targetConfig["iis"] in deployify-state.json.
/// Each Aspire project becomes an IIS site with its own application pool; the published app
/// goes into a release folder under <see cref="SitesRoot"/>, and the site is switched to it.
/// </summary>
public sealed class IisTargetConfig
{
    /// <summary>
    /// The Windows server running IIS. Leave empty to deploy to the machine Deployify runs on
    /// (a build agent on the web server itself).
    /// </summary>
    public string? Host { get; set; }

    /// <summary>How to reach <see cref="Host"/>: PowerShell remoting over WinRM or over SSH.</summary>
    public IisTransport Transport { get; set; } = IisTransport.WinRm;

    /// <summary>WinRM or SSH port. Null uses the default (5985, 5986 with <see cref="UseSsl"/>, or 22).</summary>
    public int? Port { get; set; }

    /// <summary>WinRM: connect over HTTPS (port 5986).</summary>
    public bool UseSsl { get; set; }

    /// <summary>
    /// User to connect as. For WinRM, leave empty to use the current Windows identity (Kerberos);
    /// for SSH, defaults to the current user.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// WinRM password. Use an <c>env:NAME</c> reference; defaults to the <c>IIS_PASSWORD</c>
    /// environment variable. SSH uses key authentication instead (<see cref="KeyFilePath"/>).
    /// </summary>
    public string? Password { get; set; }

    /// <summary>SSH: private key file. Defaults to the SSH client's own key lookup.</summary>
    public string? KeyFilePath { get; set; }

    /// <summary>
    /// PowerShell executable Deployify runs locally to drive the deployment. Defaults to
    /// <c>pwsh</c>, falling back to Windows PowerShell (<c>powershell</c>) on Windows.
    /// </summary>
    public string? PowerShell { get; set; }

    /// <summary>Folder on the server that holds one folder per service, with its releases.</summary>
    public string SitesRoot { get; set; } = @"C:\inetpub\deployify";

    /// <summary>Prefix for site and application pool names. Defaults to "{environment}-".</summary>
    public string? SiteNamePrefix { get; set; }

    /// <summary>
    /// Release folders kept per service, the current one included. <c>deployify rollback</c> can
    /// only go back to a release that is still on the server, so at least 2 are kept.
    /// </summary>
    public int KeepReleases { get; set; } = 3;

    /// <summary>
    /// Path the health gate requests on each site, from the server itself. Any response below 500
    /// counts as healthy; the ASP.NET Core Module's "app failed to start" pages count as failed.
    /// </summary>
    public string HealthPath { get; set; } = "/";

    /// <summary>Runtime identifier passed to <c>dotnet publish -r</c>.</summary>
    public string Runtime { get; set; } = "win-x64";

    /// <summary>
    /// Publish self-contained apps. Off by default: the server's ASP.NET Core Hosting Bundle
    /// supplies the runtime.
    /// </summary>
    public bool SelfContained { get; set; }

    /// <summary>Base port for services whose Aspire bindings carry no port.</summary>
    public int BasePort { get; set; } = 8080;

    /// <summary>Host name per service, added to its IIS bindings as a host header.</summary>
    public Dictionary<string, string> HostNames { get; set; } = [];

    /// <summary>
    /// Thumbprint of a certificate in the server's machine store, used for https bindings.
    /// Without one, https bindings are left out and services are reached over http.
    /// </summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>Machine certificate store holding <see cref="CertificateThumbprint"/>.</summary>
    public string CertificateStore { get; set; } = "My";

    /// <summary>
    /// Application pool identity: ApplicationPoolIdentity, NetworkService, LocalService or LocalSystem.
    /// </summary>
    public string AppPoolIdentity { get; set; } = "ApplicationPoolIdentity";

    /// <summary>
    /// Start application pools when IIS starts and never stop them for being idle, so background
    /// work (hosted services, consumers) keeps running. Turn off to let IIS start apps on the first request.
    /// </summary>
    public bool AlwaysRunning { get; set; } = true;

    /// <summary>
    /// Where to reach resources IIS can't host (Redis, databases and other containers), keyed by
    /// resource name. References to them resolve to this host instead of the resource name.
    /// </summary>
    public Dictionary<string, string> ExternalHosts { get; set; } = [];
}

public enum IisTransport
{
    /// <summary>PowerShell remoting over WinRM (Windows PowerShell's default).</summary>
    WinRm,

    /// <summary>PowerShell remoting over SSH (PowerShell 7 on both ends; works from Linux runners).</summary>
    Ssh
}
