using System;
using System.Collections.Generic;
using System.Globalization;
using Deployify.Core.Models;
using Deployify.Target.Nomad.Api;

namespace Deployify.Target.Nomad;

/// <summary>The Dapr sidecar task that runs next to a service's own task (Nomad and Nomad + Consul).</summary>
public static class DaprSidecar
{
    /// <summary>
    /// A prestart sidecar task running daprd. With <paramref name="hostNetwork"/> it shares the
    /// host's network; otherwise it shares the group's network namespace and reaches the app on localhost.
    /// </summary>
    public static NomadTask Task(
        string jobName, string? appId, int? appPort, int httpPort, int grpcPort, string version,
        DaprConfig dapr, string driver, bool hostNetwork)
    {
        var args = new List<string>
        {
            "./daprd",
            "--app-id", appId ?? jobName,
            "--dapr-http-port", httpPort.ToString(CultureInfo.InvariantCulture),
            "--dapr-grpc-port", grpcPort.ToString(CultureInfo.InvariantCulture),
            "--log-level", dapr.LogLevel
        };
        if (appPort.HasValue)
        {
            args.AddRange(["--app-port", appPort.Value.ToString(CultureInfo.InvariantCulture)]);
        }

        if (dapr.ComponentsPath is not null)
        {
            args.AddRange(["--resources-path", dapr.ComponentsPath]);
        }

        if (dapr.ConfigPath is not null)
        {
            args.AddRange(["--config", dapr.ConfigPath]);
        }

        var config = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["image"] = $"{DaprConstants.SidecarImage}:{version}"
        };
        if (hostNetwork)
        {
            config["network_mode"] = "host";
        }

        config["args"] = args;

        return new NomadTask
        {
            Name = $"{jobName}-dapr",
            Driver = driver,
            Config = config,
            Resources = new NomadResources { Cpu = 50, MemoryMB = 64 },
            Lifecycle = new NomadTaskLifecycle { Hook = "prestart", Sidecar = true }
        };
    }
}
