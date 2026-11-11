using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deployify.Plugins;

/// <summary>
/// Supplies plugin targets to the target factory. Looks the target ID up in the
/// <see cref="PluginCatalog"/> and hands out one <see cref="RemoteDeploymentTarget"/> per ID;
/// targets of the same executable share one process. Nothing starts until a target is used.
/// </summary>
public sealed class PluginTargetSource : IDeploymentTargetSource, IAsyncDisposable
{
    private readonly Func<DeploymentTargetType, string?, PluginLaunchSpec?> _locate;
    private readonly ILoggerFactory _loggers;
    private readonly PluginHostOptions _options;
    private readonly ConcurrentDictionary<string, PluginHost> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(DeploymentTargetType Target, string? Project), RemoteDeploymentTarget> _targets = new();

    public PluginTargetSource(PluginCatalog catalog, ILoggerFactory? loggers = null, PluginHostOptions? options = null)
        : this((target, project) => Locate(catalog, target, project), loggers, options)
    {
    }

    /// <summary>
    /// Finds the plugin for a target and attaches what the project's lock file requires of it.
    /// A locked target that isn't installed is an error, not a missing target.
    /// </summary>
    public static PluginLaunchSpec? Locate(PluginCatalog catalog, DeploymentTargetType target, string? projectDirectory)
    {
        var locked = projectDirectory is null ? null : PluginLockFile.Load(projectDirectory)?.Find(target.Value);
        if (catalog.Find(target, projectDirectory) is not { } found)
        {
            return locked is null
                ? null
                : throw new PluginException(
                    $"{PluginLockFile.FileName} locks target '{target}' to plugin {locked.Plugin} {locked.Version}, " +
                    $"but no '{Plugin.Contracts.PluginProtocol.ExecutablePrefix}{target}' executable is installed.");
        }

        return new PluginLaunchSpec { ExecutablePath = found.ExecutablePath, Source = found.Source, Lock = locked };
    }

    /// <param name="locate">
    /// Where the plugin for a target ID is, given the project directory when known, or null when there is none.
    /// </param>
    public PluginTargetSource(
        Func<DeploymentTargetType, string?, PluginLaunchSpec?> locate, ILoggerFactory? loggers = null, PluginHostOptions? options = null)
    {
        _locate = locate;
        _loggers = loggers ?? NullLoggerFactory.Instance;
        _options = options ?? new PluginHostOptions();
    }

    public IDeploymentTarget? Find(DeploymentTargetType targetType, string? projectDirectory)
    {
        if (targetType.IsBuiltIn)
        {
            return null;
        }

        var key = (targetType, projectDirectory is null ? null : System.IO.Path.GetFullPath(projectDirectory));
        if (_targets.TryGetValue(key, out var existing))
        {
            return existing;
        }

        // Not cached when missing: a plugin installed later in the same process is still found.
        if (_locate(key.Item1, key.Item2) is not { } spec)
        {
            return null;
        }

        var host = _hosts.GetOrAdd(spec.ExecutablePath, _ => new PluginHost(spec, _loggers, _options));
        return _targets.GetOrAdd(key, _ => new RemoteDeploymentTarget(host, targetType));
    }

    /// <summary>Stops every plugin process this source started.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.Values)
        {
            await host.DisposeAsync();
        }

        _hosts.Clear();
    }
}
