using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;

namespace Deployify.Core.Interfaces;

/// <summary>
/// Implemented by a target whose optional capabilities (<see cref="IRolloutTarget"/>,
/// <see cref="IResourceLifecycle"/>) are only known at run time, such as a target served by a
/// plugin. A type check can't answer for those, so callers ask
/// <see cref="DeploymentTargetExtensions.GetCapability{T}"/> instead.
/// </summary>
public interface ICapabilityProvider
{
    /// <summary>The object that provides <paramref name="capability"/>, or null when this target lacks it.</summary>
    object? GetCapability(Type capability);
}

public static class DeploymentTargetExtensions
{
    /// <summary>
    /// The target's implementation of an optional capability, or null when it has none.
    /// Use this rather than <c>target is IRolloutTarget</c>: a plugin target implements every
    /// interface in-process but only supports what its plugin declared.
    /// </summary>
    public static T? GetCapability<T>(this IDeploymentTarget target) where T : class
        => target is ICapabilityProvider provider
            ? provider.GetCapability(typeof(T)) as T
            : target as T;

    /// <summary>True when the target supports capability <typeparamref name="T"/>.</summary>
    public static bool Supports<T>(this IDeploymentTarget target) where T : class
        => target.GetCapability<T>() is not null;
}

/// <summary>
/// Supplies targets that aren't registered up front, such as targets served by plugins, which
/// <see cref="IDeploymentTargetFactory"/> asks for an ID it doesn't know.
/// </summary>
public interface IDeploymentTargetSource
{
    /// <summary>
    /// The target for <paramref name="targetType"/>, or null when this source has none.
    /// <paramref name="projectDirectory"/> is the directory of deployify-state.json, when known.
    /// </summary>
    IDeploymentTarget? Find(DeploymentTargetType targetType, string? projectDirectory);
}

/// <summary>
/// A target that has to do work before its first use (start a plugin process and learn what it
/// supports). <see cref="Deployments.DeploymentSessionResolver"/> starts it while resolving a run,
/// so a failure shows up there with a clear message.
/// </summary>
public interface IStartableTarget
{
    Task StartAsync(CancellationToken ct = default);
}
