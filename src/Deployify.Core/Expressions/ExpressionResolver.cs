using System;
using System.Collections.Generic;
using System.Linq;
using Deployify.Core.Models;

namespace Deployify.Core.Expressions;

/// <summary>
/// Resolves the <c>{resource.path}</c> expressions in an Aspire manifest: binding properties,
/// connection strings, <c>value.v0</c> values and <c>parameter.v0</c> inputs. Expressions can
/// nest to any depth (a connection string that refers to a parameter that refers to an input).
///
/// The resolver does the shared work: parsing, recursion, caching, cycle detection, secret
/// tracking and diagnostics. What a property means is decided by one
/// <see cref="IReferenceHandler"/> per property name, and everything target-specific (how one
/// service addresses another) comes from the <see cref="EndpointStrategy"/>.
///
/// A reference that can't be resolved is kept as its placeholder and reported in
/// <see cref="Diagnostics"/>, unless <see cref="Strict"/> is set, in which case it throws.
/// One resolver is meant to be used for one generation run against one target.
/// </summary>
public sealed class ExpressionResolver
{
    private readonly Dictionary<string, IReferenceHandler> _handlers;
    private readonly Dictionary<ReferenceSegment, ResolvedValue> _cache = [];
    private readonly List<ReferenceSegment> _stack = [];
    private readonly List<ExpressionDiagnostic> _diagnostics = [];
    private readonly HashSet<(string, ExpressionDiagnosticKind)> _reported = [];
    private readonly HashSet<string> _unresolved = new(StringComparer.Ordinal);

    public ExpressionResolver(
        AspireManifest manifest,
        IReadOnlyDictionary<(string Resource, string Binding), int> ports,
        EndpointStrategy strategy,
        IParameterSource? parameters = null,
        IEnumerable<IReferenceHandler>? additionalHandlers = null)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        Ports = ports ?? throw new ArgumentNullException(nameof(ports));
        Strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        Parameters = parameters;

        _handlers = new Dictionary<string, IReferenceHandler>(StringComparer.Ordinal);
        foreach (var handler in ReferenceHandlers.BuiltIn.Concat(additionalHandlers ?? []))
        {
            _handlers[handler.Property] = handler; // later registrations override built-ins
        }
    }

    public AspireManifest Manifest { get; }

    public IReadOnlyDictionary<(string Resource, string Binding), int> Ports { get; }

    public EndpointStrategy Strategy { get; }

    /// <summary>Where parameter values come from. When null, parameter inputs stay unresolved.</summary>
    public IParameterSource? Parameters { get; }

    /// <summary>
    /// When true (the default), the https binding of a project resource resolves to its
    /// http endpoint. Deployed project containers only listen on HTTP_PORTS; no TLS
    /// certificate is configured, so nothing answers on the https port. Aspire's service
    /// discovery prefers an https entry when one exists, so leaving it pointing at a dead
    /// port breaks every call between services.
    /// </summary>
    public bool ProjectHttpsAsHttp { get; init; } = true;

    /// <summary>Throw an <see cref="ExpressionException"/> on the first unresolvable reference.</summary>
    public bool Strict { get; init; }

    /// <summary>The deepest chain of nested references followed before giving up.</summary>
    public int MaxDepth { get; init; } = 32;

    /// <summary>Every problem found so far, once per placeholder and kind.</summary>
    public IReadOnlyList<ExpressionDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Every placeholder that could not be resolved so far.</summary>
    public IReadOnlyCollection<string> Unresolved => _unresolved;

    /// <summary>Resolves every reference in <paramref name="template"/>.</summary>
    public ResolvedValue Resolve(string? template)
    {
        if (string.IsNullOrEmpty(template))
        {
            return ResolvedValue.Empty;
        }

        return Resolve(ExpressionTemplate.Parse(template));
    }

    public ResolvedValue Resolve(ExpressionTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return ResolvedValue.Concat(template.Segments.Select(segment => segment switch
        {
            LiteralSegment l => ResolvedValue.Text(l.Text),
            ReferenceSegment r => ResolveReference(r),
            _ => throw new InvalidOperationException($"Unknown segment {segment.GetType().Name}.")
        }));
    }

    /// <summary>Resolves a single reference, e.g. <c>ResolveReference("db", "connectionString")</c>.</summary>
    public ResolvedValue ResolveReference(string resource, params string[] path)
    {
        if (path.Length == 0)
        {
            throw new ArgumentException("A reference needs at least one path element.", nameof(path));
        }

        return ResolveReference(new ReferenceSegment(resource, path));
    }

    public ResolvedValue ResolveReference(ReferenceSegment reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (_cache.TryGetValue(reference, out var cached))
        {
            return cached;
        }

        if (_stack.Contains(reference))
        {
            var cycle = string.Join(" -> ", _stack.SkipWhile(r => !r.Equals(reference)).Append(reference));
            return Fail(reference, ExpressionDiagnosticKind.Cycle, $"Circular reference: {cycle}.", cache: false);
        }

        if (_stack.Count >= MaxDepth)
        {
            return Fail(reference, ExpressionDiagnosticKind.DepthExceeded,
                $"References nest deeper than {MaxDepth} levels.", cache: false);
        }

        if (!Manifest.Resources.TryGetValue(reference.Resource, out var resource))
        {
            return Fail(reference, ExpressionDiagnosticKind.UnknownResource,
                $"No resource named '{reference.Resource}' in the manifest.");
        }

        if (!_handlers.TryGetValue(reference.Property, out var handler))
        {
            return Fail(reference, ExpressionDiagnosticKind.UnsupportedProperty,
                $"'{reference.Property}' is not a supported property. Supported: {string.Join(", ", _handlers.Keys.Order())}.");
        }

        ResolvedValue? value = null;
        ExpressionException? rejected = null;
        _stack.Add(reference);
        try
        {
            value = handler.Resolve(new ReferenceContext(this, reference, resource));
        }
        catch (ExpressionException ex) when (ex.Diagnostic is null)
        {
            // A handler rejected the reference. (Strict-mode exceptions from nested references
            // carry a Diagnostic and propagate unchanged.)
            rejected = ex;
        }
        finally
        {
            _stack.RemoveAt(_stack.Count - 1);
        }

        if (rejected is not null)
        {
            return Fail(reference, rejected.Kind, rejected.Message);
        }

        _cache[reference] = value!;
        return value!;
    }

    /// <summary>Resolves every value of an env dictionary.</summary>
    public IReadOnlyDictionary<string, ResolvedValue> ResolveAll(IReadOnlyDictionary<string, string>? values)
    {
        var result = new Dictionary<string, ResolvedValue>(StringComparer.Ordinal);
        if (values is null)
        {
            return result;
        }

        foreach (var (key, value) in values)
        {
            result[key] = Resolve(value);
        }

        return result;
    }

    /// <summary>
    /// Resolves every expression a deployable resource uses (env, args and connection string)
    /// and returns the diagnostics. Useful as a dry run before generating anything.
    /// </summary>
    public IReadOnlyList<ExpressionDiagnostic> Validate()
    {
        foreach (var (_, resource) in Manifest.Resources.Where(r => r.Value.IsDeployable))
        {
            ResolveAll(resource.Env);
            Resolve(resource.ConnectionString);
            foreach (var arg in ReadArgs(resource))
            {
                Resolve(arg);
            }
        }

        return Diagnostics;
    }

    private static IEnumerable<string> ReadArgs(AspireResource resource)
    {
        if (resource.ExtensionData is null ||
            !resource.ExtensionData.TryGetValue("args", out var args) ||
            args.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var arg in args.EnumerateArray())
        {
            if (arg.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                yield return arg.GetString()!;
            }
        }
    }

    private ResolvedValue Fail(ReferenceSegment reference, ExpressionDiagnosticKind kind, string message, bool cache = true)
    {
        var diagnostic = new ExpressionDiagnostic(
            reference.Placeholder, kind, message, [.. _stack.Select(r => r.Placeholder)]);

        if (_reported.Add((reference.Placeholder, kind)))
        {
            _diagnostics.Add(diagnostic);
        }

        _unresolved.Add(reference.Placeholder);

        if (Strict)
        {
            throw new ExpressionException(diagnostic);
        }

        var value = ResolvedValue.Of(new UnresolvedPart(reference.Placeholder));
        if (cache)
        {
            _cache[reference] = value;
        }

        return value;
    }
}
