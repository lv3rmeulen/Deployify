using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

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
/// concrete values for one deployment target. Placeholders can appear anywhere inside
/// a value and can nest (a connection string that refers to bindings). Anything that
/// can't be resolved, such as a <c>parameter.v0</c> input, is left as-is and reported
/// through <see cref="Unresolved"/>.
/// </summary>
public sealed class PlaceholderResolver(
    AspireManifest manifest,
    IReadOnlyDictionary<(string Resource, string Binding), int> ports,
    EndpointStrategy strategy)
{
    private static readonly Regex s_placeholder =
        new(@"\{(?<res>[A-Za-z0-9_-]+)\.(?<path>[A-Za-z0-9_.-]+)\}", RegexOptions.Compiled);

    private readonly HashSet<string> _unresolved = new(StringComparer.Ordinal);

    /// <summary>Placeholders seen so far that could not be resolved.</summary>
    public IReadOnlyCollection<string> Unresolved => _unresolved;

    /// <summary>
    /// When true (the default), the https binding of a project resource resolves to its
    /// http endpoint. Deployed project containers only listen on HTTP_PORTS; no TLS
    /// certificate is configured, so nothing answers on the https port. Aspire's service
    /// discovery prefers an https entry when one exists, so leaving it pointing at a dead
    /// port breaks every call between services.
    /// </summary>
    public bool ProjectHttpsAsHttp { get; init; } = true;

    public string Resolve(string value) => Resolve(value, depth: 0);

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

    private string Resolve(string value, int depth)
    {
        if (depth > 8 || !value.Contains('{'))
        {
            return value;
        }

        return s_placeholder.Replace(value, m =>
        {
            var resolved = ResolveOne(m.Groups["res"].Value, m.Groups["path"].Value, depth);
            if (resolved is null)
            {
                _unresolved.Add(m.Value);
            }

            return resolved ?? m.Value;
        });
    }

    private string? ResolveOne(string resource, string path, int depth)
    {
        if (!manifest.Resources.TryGetValue(resource, out var res))
        {
            return null;
        }

        var parts = path.Split('.');

        // {res.connectionString}
        if (parts is ["connectionString"])
        {
            return res.ConnectionString is null ? null : Resolve(res.ConnectionString, depth + 1);
        }

        // {res.value} on value.v0 resources
        if (parts is ["value"] && res.ExtensionData is not null &&
            res.ExtensionData.TryGetValue("value", out var v) && v.ValueKind == JsonValueKind.String)
        {
            return Resolve(v.GetString()!, depth + 1);
        }

        // {res.bindings.<binding>.<property>}
        if (parts is ["bindings", var bindingName, var property] &&
            res.Bindings is not null && res.Bindings.TryGetValue(bindingName, out var binding))
        {
            if (ProjectHttpsAsHttp && property != "targetPort" &&
                binding.Scheme == "https" && res.Type.StartsWith("project.", StringComparison.Ordinal) &&
                res.Bindings.FirstOrDefault(b => b.Value.Scheme == "http") is { Key: not null } http)
            {
                bindingName = http.Key;
                binding = http.Value;
            }

            if (!ports.TryGetValue((resource, bindingName), out var containerPort))
            {
                return null;
            }

            var scheme = binding.EffectiveScheme;
            return property switch
            {
                "targetPort" => containerPort.ToString(),
                "port" => strategy.Port(resource, bindingName, containerPort).ToString(),
                "host" => strategy.Host(resource),
                "scheme" => scheme,
                "url" => strategy.Url?.Invoke(resource, bindingName, scheme, containerPort)
                         ?? $"{scheme}://{strategy.Host(resource)}:{strategy.Port(resource, bindingName, containerPort)}",
                _ => null
            };
        }

        return null;
    }
}
