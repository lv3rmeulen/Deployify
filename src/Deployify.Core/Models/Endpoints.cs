using System;
using System.Collections.Generic;
using System.Linq;
using Deployify.Core.Expressions;

namespace Deployify.Core.Models;

/// <summary>Builds container image references without double-tagging.</summary>
public static class ImageReference
{
    /// <summary>
    /// True when <paramref name="image"/> already carries a tag or digest.
    /// Only the last path segment is inspected, so a registry port such as
    /// <c>localhost:5000/api</c> is not mistaken for a tag.
    /// </summary>
    public static bool HasTag(string image)
    {
        if (image.Contains('@'))
        {
            return true;
        }

        var lastSegment = image[(image.LastIndexOf('/') + 1)..];
        return lastSegment.Contains(':');
    }

    /// <summary>
    /// Returns <c>image:tag</c>, or the image unchanged when it already has a tag
    /// (e.g. <c>redis:7.4</c> or <c>mcr.microsoft.com/dotnet/aspire-dashboard:9.0</c>).
    /// </summary>
    public static string Format(string image, string? tag)
        => HasTag(image) || string.IsNullOrEmpty(tag) ? image : $"{image}:{tag}";
}

/// <summary>
/// Assigns a concrete container port to every binding of every deployable resource.
/// Aspire leaves the port out for most project bindings and fills it in at run time;
/// a deployment needs a fixed value. Ports declared in the manifest are kept, and the
/// rest are allocated from <c>basePort</c> upwards, skipping any already in use.
/// </summary>
public static class ServicePorts
{
    public static IReadOnlyDictionary<(string Resource, string Binding), int> Assign(
        AspireManifest manifest, int basePort = 8080)
    {
        var result = new Dictionary<(string, string), int>();
        var used = new HashSet<int>();

        // Pass 1: keep every explicit port so allocation never collides with one.
        foreach (var (name, resource) in manifest.Resources)
        {
            if (resource.Bindings is null)
            {
                continue;
            }

            foreach (var (label, binding) in resource.Bindings)
            {
                if (binding.ContainerPort is { } p)
                {
                    result[(name, label)] = p;
                    used.Add(p);
                }
            }
        }

        // Pass 2: allocate the rest. https sits next to http on the same service.
        var next = basePort;

        int Allocate()
        {
            while (used.Contains(next))
            {
                next++;
            }

            used.Add(next);
            return next++;
        }

        foreach (var (name, resource) in manifest.Resources)
        {
            if (resource.Bindings is null)
            {
                continue;
            }

            foreach (var (label, _) in resource.Bindings.OrderBy(b => b.Key == "http" ? 0 : 1))
            {
                if (result.ContainsKey((name, label)))
                {
                    continue;
                }

                if (label == "https" && result.TryGetValue((name, "http"), out var http) && !used.Contains(http + 1))
                {
                    result[(name, label)] = http + 1;
                    used.Add(http + 1);
                }
                else
                {
                    result[(name, label)] = Allocate();
                }
            }
        }

        return result;
    }
}

/// <summary>
/// Describes how one service reaches another on a particular target. Each target
/// supplies its own: host networking uses <c>localhost</c>, Kubernetes and Compose use
/// the service name, Consul Connect uses the local upstream port, and so on.
/// </summary>
public sealed class EndpointStrategy
{
    /// <summary>Host name used to reach <c>resource</c>. Defaults to the resource name.</summary>
    public Func<string, string> Host { get; init; } = name => name;

    /// <summary>
    /// Port used to reach <c>(resource, binding)</c> given its container port.
    /// Defaults to the container port.
    /// </summary>
    public Func<string, string, int, int> Port { get; init; } = (_, _, containerPort) => containerPort;

    /// <summary>
    /// Optional override for full URLs, e.g. Azure Container Apps where internal ingress
    /// is plain <c>http://app-name</c> with no port. Return null to use host + port.
    /// </summary>
    public Func<string, string, string, int, string?>? Url { get; init; }
}

/// <summary>
/// Resolves Aspire placeholders such as <c>{api.bindings.http.url}</c>,
/// <c>{api.bindings.http.targetPort}</c> or <c>{cache.connectionString}</c> into
/// concrete strings for one deployment target.
///
/// This is the string-in, string-out facade the target generators use. The work is done by
/// <see cref="ExpressionResolver"/>; use <see cref="Expressions"/> directly to get structured
/// values (secret tracking, parameter parts) and <see cref="Diagnostics"/>.
///
/// Anything that can't be resolved, such as a <c>parameter.v0</c> input when no
/// <see cref="IParameterSource"/> is given, is left as-is and reported through
/// <see cref="Unresolved"/>.
/// </summary>
public sealed class PlaceholderResolver(
    AspireManifest manifest,
    IReadOnlyDictionary<(string Resource, string Binding), int> ports,
    EndpointStrategy strategy,
    IParameterSource? parameters = null)
{
    private ExpressionResolver? _expressions;

    /// <inheritdoc cref="ExpressionResolver.ProjectHttpsAsHttp"/>
    public bool ProjectHttpsAsHttp { get; init; } = true;

    /// <summary>The underlying resolver. Created on first use so init-only options apply to it.</summary>
    public ExpressionResolver Expressions
        => _expressions ??= new ExpressionResolver(manifest, ports, strategy, parameters)
        {
            ProjectHttpsAsHttp = ProjectHttpsAsHttp
        };

    /// <summary>Placeholders seen so far that could not be resolved.</summary>
    public IReadOnlyCollection<string> Unresolved => Expressions.Unresolved;

    /// <summary>Why each unresolved placeholder could not be resolved.</summary>
    public IReadOnlyList<ExpressionDiagnostic> Diagnostics => Expressions.Diagnostics;

    public string Resolve(string value) => Expressions.Resolve(value).ToString();

    /// <summary>
    /// Resolves a resource's env and splits it into plain and secret values. Override values
    /// (from serviceOverrides) are taken literally and win over the manifest.
    /// </summary>
    public ResolvedEnvironment ResolveEnvironment(
        IReadOnlyDictionary<string, string>? env,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var result = new ResolvedEnvironment();
        if (env is not null)
        {
            foreach (var (k, v) in env)
            {
                result.Set(k, Expressions.Resolve(v));
            }
        }

        if (overrides is not null)
        {
            foreach (var (k, v) in overrides)
            {
                result.SetPlain(k, v);
            }
        }

        return result;
    }

    /// <summary>Resolves every value of an env dictionary into a new dictionary.</summary>
    public Dictionary<string, string> ResolveAll(IReadOnlyDictionary<string, string>? env)
    {
        var result = new Dictionary<string, string>();
        if (env is null)
        {
            return result;
        }

        foreach (var (k, v) in env)
        {
            result[k] = Resolve(v);
        }

        return result;
    }
}
