using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Deployify.Core.Services;

public class Publisher(ILogger<Publisher> logger) : IPublisher
{
    public async Task PublishAsync(AspireManifest manifest, EnvironmentConfig config,
        string appHostDirectory, CancellationToken ct = default)
    {
        var runtime = await ResolveRuntimeAsync(config.ContainerRuntime, ct);

        // Pre-flight: fail fast with a clear message before attempting to publish.
        // dotnet publish -t:PublishContainer talks to the daemon via a named pipe and
        // gives an opaque IOException if it isn't running.
        if (!await IsRuntimeReachableAsync(runtime, ct))
        {
            var hint = runtime == ContainerRuntime.Docker
                ? "Start Docker Desktop and try again, or set containerRuntime: Podman in your environment config."
                : "Ensure Podman is running and try again.";
            throw new InvalidOperationException(
                $"{runtime} daemon is not reachable. {hint}");
        }

        var runtimeArg = runtime == ContainerRuntime.Podman
            ? " -p:LocalRegistry=podman"
            : string.Empty;

        // Pass the daemon socket explicitly so Microsoft.NET.Build.Containers finds it
        // without probing, which avoids the opaque "pipe has been ended" error.
        var socketArg = GetContainerDaemonSocketArg(runtime);

        foreach (var (name, resource) in manifest.Resources)
        {
            if (!resource.Type.StartsWith("project.v") || resource.Path is null)
            {
                continue;
            }

            var projectPath = ResolveProjectPath(appHostDirectory, resource.Path!);
            var tag = config.ServiceOverrides.TryGetValue(name, out var ov) && ov.ImageTag is not null
                ? ov.ImageTag : config.ImageTag;
            // ContainerRegistry must be set explicitly for the SDK to actually push —
            // ContainerImageName alone only loads the image into the local Docker daemon.
            //
            // config.ContainerRegistry holds the full prefix the user configured, e.g.:
            //   "lukvermeulen/test"           → Docker Hub, repository "lukvermeulen/test/{name}"
            //   "myregistry.azurecr.io"       → registry "myregistry.azurecr.io", repository "{name}"
            //   "myregistry.azurecr.io/myorg" → registry "myregistry.azurecr.io", repository "myorg/{name}"
            string registryArg;
            string repository;
            if (config.ContainerRegistry is null)
            {
                registryArg = string.Empty;
                repository = name;
            }
            else
            {
                var firstSegment = config.ContainerRegistry.Split('/')[0];
                var looksLikeHostname = firstSegment.Contains('.') || firstSegment.Contains(':');
                if (looksLikeHostname)
                {
                    registryArg = $" -p:ContainerRegistry={firstSegment}";
                    var rest = config.ContainerRegistry[firstSegment.Length..].TrimStart('/');
                    repository = string.IsNullOrEmpty(rest) ? name : $"{rest}/{name}";
                }
                else
                {
                    // Docker Hub: no hostname, push to docker.io implicitly
                    registryArg = " -p:ContainerRegistry=docker.io";
                    repository = $"{config.ContainerRegistry.TrimEnd('/')}/{name}";
                }
            }

            logger.LogInformation("Publishing '{Name}' → {Repository}:{Tag} (runtime: {Runtime})...",
                name, repository, tag, runtime);
            (await ProcessRunner.Default.RunAsync(new ProcessSpec("dotnet",
                    BuildPublishArgs(projectPath, config.ContainerOs, config.ContainerArch, repository, tag,
                        registryArg, runtimeArg, socketArg)) { Log = logger }, ct))
                .EnsureSuccess("dotnet publish");
        }
    }

    /// <summary>
    /// The <c>dotnet publish</c> arguments, one list item per argument, so a project path with
    /// spaces or a repository with unusual characters can't split into extra arguments.
    /// </summary>
    internal static IReadOnlyList<string> BuildPublishArgs(
        string projectPath, string os, string arch, string repository, string tag, params string[] properties)
        =>
        [
            "publish", projectPath, "-t:PublishContainer", "--os", os, "--arch", arch,
            $"-p:ContainerRepository={repository}", $"-p:ContainerImageTag={tag}",
            .. properties.Select(p => p.Trim()).Where(p => p.Length > 0)
        ];

    /// <summary>
    /// Returns the explicit ContainerDaemonSocket MSBuild property for the active runtime,
    /// or an empty string when the default socket location is used.
    /// Passing this explicitly prevents the "pipe has been ended" IOException that occurs
    /// when Microsoft.NET.Build.Containers probes for the socket and fails silently.
    /// </summary>
    private static string GetContainerDaemonSocketArg(ContainerRuntime runtime)
    {
        if (runtime == ContainerRuntime.Podman)
        {
            // Find the Podman socket and pass it explicitly
            foreach (var path in PodmanSocketCandidates())
            {
                if (File.Exists(path))
                {
                    return $" -p:ContainerDaemonSocket=unix://{path}";
                }
            }

            // Fall back to DOCKER_HOST if set
            var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
            if (!string.IsNullOrEmpty(dockerHost))
            {
                return $" -p:ContainerDaemonSocket={dockerHost}";
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // On Windows, Docker Desktop uses a named pipe.
            // Microsoft.NET.Build.Containers expects the npipe URI format.
            return " -p:ContainerDaemonSocket=npipe:////./pipe/docker_engine";
        }
        else
        {
            // On Linux/macOS use the Unix socket
            var sock = Environment.GetEnvironmentVariable("DOCKER_HOST");
            if (!string.IsNullOrEmpty(sock))
            {
                return $" -p:ContainerDaemonSocket={sock}";
            }

            var defaultSock = "/var/run/docker.sock";
            if (File.Exists(defaultSock))
            {
                return $" -p:ContainerDaemonSocket=unix://{defaultSock}";
            }
        }

        return string.Empty;
    }

    private static async Task<bool> IsRuntimeReachableAsync(ContainerRuntime runtime, CancellationToken ct)
        => runtime == ContainerRuntime.Podman
            ? await IsPodmanReachableAsync(ct)
            : await IsDockerReachableAsync(ct);

    /// <summary>
    /// Resolves which container runtime to actually use.
    /// When set to Docker (the default), probes for Docker first, then falls back to Podman.
    /// </summary>
    private async Task<ContainerRuntime> ResolveRuntimeAsync(ContainerRuntime configured, CancellationToken ct)
    {
        if (configured == ContainerRuntime.Podman)
        {
            return ContainerRuntime.Podman;
        }

        if (await IsDockerReachableAsync(ct))
        {
            return ContainerRuntime.Docker;
        }

        if (await IsPodmanReachableAsync(ct))
        {
            logger.LogWarning(
                "Docker is not reachable. Podman was found on PATH — switching to Podman automatically. " +
                "Set containerRuntime: Podman in your environment config to silence this warning.");
            return ContainerRuntime.Podman;
        }

        logger.LogWarning(
            "Neither Docker nor Podman appear to be running. " +
            "The publish step may fail. Ensure a container runtime is available.");
        return ContainerRuntime.Docker;
    }

    /// <summary>
    /// Resolves a resource path from the Aspire manifest to an absolute path.
    /// Aspire emits resource paths relative to the solution/repo root. We walk up from
    /// the AppHost directory until we find an ancestor where the path resolves to a file.
    /// </summary>
    private string ResolveProjectPath(string appHostDirectory, string resourcePath)
    {
        // Aspire emits resource paths relative to the AppHost project directory.
        // Path.GetFullPath correctly resolves ../ segments.
        var normalized = resourcePath.Replace('/', Path.DirectorySeparatorChar)
                                     .Replace('\\', Path.DirectorySeparatorChar);

        var resolved = Path.GetFullPath(Path.Combine(appHostDirectory, normalized));
        if (File.Exists(resolved))
        {
            logger.LogInformation("Resolved '{Resource}' → {Path}", resourcePath, resolved);
            return resolved;
        }

        // Aspire sometimes generates one extra ../ depending on how dotnet run was invoked.
        // If the direct resolution doesn't exist, strip the leading ..\ and try again.
        var stripped = StripLeadingParent(normalized);
        if (stripped != normalized)
        {
            var viaStripped = Path.GetFullPath(Path.Combine(appHostDirectory, stripped));
            if (File.Exists(viaStripped))
            {
                logger.LogInformation("Resolved '{Resource}' → {Path} (stripped extra ../)", resourcePath, viaStripped);
                return viaStripped;
            }
        }

        logger.LogWarning(
            "Could not resolve '{ResourcePath}' from '{AppHostDir}'. Falling back to: {Fallback}",
            resourcePath, appHostDirectory, resolved);
        return resolved;
    }

    /// <summary>Removes the first ..\ or ../ segment from a normalized path.</summary>
    private static string StripLeadingParent(string path)
    {
        var parent = ".." + Path.DirectorySeparatorChar;
        return path.StartsWith(parent, StringComparison.Ordinal)
            ? path.Substring(parent.Length)
            : path;
    }

    private static async Task<bool> IsDockerReachableAsync(CancellationToken ct)
    {
        return await ProcessRunner.Default.IsAvailableAsync("docker", ["info"], TimeSpan.FromSeconds(3), ct);
    }

    private static async Task<bool> IsPodmanReachableAsync(CancellationToken ct)
    {
        TrySetPodmanSocketAsDockerHost();
        return await ProcessRunner.Default.IsAvailableAsync("podman", ["info"], TimeSpan.FromSeconds(3), ct);
    }

    private static void TrySetPodmanSocketAsDockerHost()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST")))
        {
            return;
        }

        foreach (var path in PodmanSocketCandidates())
        {
            if (File.Exists(path))
            {
                Environment.SetEnvironmentVariable("DOCKER_HOST", $"unix://{path}");
                return;
            }
        }
    }

    private static IEnumerable<string> PodmanSocketCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var uid = GetUid();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (uid.HasValue)
            {
                yield return $"/run/user/{uid}/podman/podman.sock";
            }

            yield return Path.Combine(home, ".local/share/containers/podman/machine/podman.sock");
            yield return "/run/podman/podman.sock";
            yield return "/var/run/podman/podman.sock";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return Path.Combine(home, ".local/share/containers/podman/machine/qemu/podman.sock");
            yield return Path.Combine(home, ".local/share/containers/podman/machine/podman.sock");
        }
    }

    /// <summary>
    /// The current user id, read straight from libc rather than by running <c>id -u</c>,
    /// so finding the rootless Podman socket needs no blocking child process.
    /// </summary>
    private static int? GetUid()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return null;
        }

        try
        {
            return (int)NativeMethods.getuid();
        }
        catch
        {
            return null;
        }
    }

    private static class NativeMethods
    {
        [DllImport("libc")]
        internal static extern uint getuid();
    }
}
