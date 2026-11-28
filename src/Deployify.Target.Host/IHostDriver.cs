using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.ServiceLogs;

namespace Deployify.Target.Host;

/// <summary>
/// Talks to one host: systemd over SSH (<see cref="LinuxHost"/>) or the Windows service manager
/// over PowerShell remoting (<see cref="WindowsHost"/>). The target decides what to deploy; the
/// driver only carries it out on its machine.
/// </summary>
internal interface IHostDriver
{
    /// <summary>The host, for messages: its name, or "this machine".</summary>
    string Where { get; }

    /// <summary>Why this host can't be reached from here (a missing client, the wrong OS), or null when it can.</summary>
    Task<string?> UnavailableReasonAsync(CancellationToken ct);

    /// <summary>Connects and checks the host has what deploying needs. Returns the problems found, empty when none.</summary>
    Task<IReadOnlyList<string>> CheckAsync(CancellationToken ct);

    /// <summary>The services with Deployify's marker under the root, for every environment.</summary>
    Task<IReadOnlyList<LiveService>> ListAsync(CancellationToken ct);

    /// <summary>Copies the release to the host unless it is there already, and switches the service to it. True when anything changed.</summary>
    Task<bool> ApplyAsync(HostRelease release, CancellationToken ct);

    /// <summary>Switches the service back to a release still on the host. True when anything changed.</summary>
    Task<bool> ActivateAsync(ServiceRef service, string release, CancellationToken ct);

    /// <summary>One look at whether the service runs <paramref name="release"/> and answers.</summary>
    Task<HealthObservation> HealthAsync(HostService service, string release, CancellationToken ct);

    /// <summary>Stops and removes the service and its folder. Returns a line per thing removed.</summary>
    Task<IReadOnlyList<string>> DeleteAsync(ServiceRef service, CancellationToken ct);

    /// <summary>The service's output on this host.</summary>
    IAsyncEnumerable<ServiceLogLine> LogsAsync(ServiceRef service, ServiceLogQuery query, CancellationToken ct);
}

/// <summary>A service as the hosts know it: Deployify's name, its folder name and its unit or Windows service name.</summary>
internal sealed record ServiceRef(string Service, string Name, string UnitName, string? Environment)
{
    public static ServiceRef Of(HostService s) => new(s.Service, s.Name, s.UnitName, s.Environment);
}

/// <summary>What a host's marker says about one service.</summary>
internal sealed record LiveService(
    string Service, string Name, string UnitName, string? Environment, string? Fingerprint, string? Release, bool Installed)
{
    public ServiceRef Ref => new(Service, Name, UnitName, Environment);
}

/// <summary>Everything a host needs to run one release of a service.</summary>
internal sealed record HostRelease(
    HostService Service,
    string Fingerprint,
    string Release,
    string EntryPoint,
    string PackageDirectory,
    string PackageHash,
    IReadOnlyDictionary<string, string> Secrets)
{
    /// <summary>Every variable, secrets included.</summary>
    public IEnumerable<KeyValuePair<string, string>> AllVariables()
    {
        foreach (var kv in Service.Env)
        {
            if (!Secrets.ContainsKey(kv.Key))
            {
                yield return kv;
            }
        }

        foreach (var kv in Secrets)
        {
            yield return kv;
        }
    }

    /// <summary>The package as a .tar.gz next to the package folder, made once per build.</summary>
    public string ArchivePath => Path.Combine(Path.GetDirectoryName(PackageDirectory)!, $"package-{PackageHash[..12]}.tar.gz");
}
