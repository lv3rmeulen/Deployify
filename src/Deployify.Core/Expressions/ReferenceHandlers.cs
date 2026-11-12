using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;

namespace Deployify.Core.Expressions;

/// <summary>What a handler gets to work with when resolving one reference.</summary>
public sealed class ReferenceContext(
    ExpressionResolver resolver, ReferenceSegment reference, AspireResource resource, CancellationToken cancellationToken = default)
{
    public ExpressionResolver Resolver { get; } = resolver;

    public ReferenceSegment Reference { get; } = reference;

    public string ResourceName => Reference.Resource;

    public AspireResource Resource { get; } = resource;

    /// <summary>The path after the property name: <c>["tcp", "host"]</c> for <c>{pg.bindings.tcp.host}</c>.</summary>
    public IReadOnlyList<string> Arguments => [.. Reference.Path.Skip(1)];

    /// <summary>Cancels the resolution this reference is part of.</summary>
    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>Resolves a nested template (with cycle detection and caching).</summary>
    public ValueTask<ResolvedValue> ResolveAsync(string template) => Resolver.ResolveAsync(template, CancellationToken);

    /// <summary>Throws unless the reference has exactly <paramref name="count"/> arguments.</summary>
    public void RequireArguments(int count, string usage)
    {
        if (Arguments.Count != count)
        {
            throw new ExpressionException(ExpressionDiagnosticKind.UnsupportedProperty,
                $"Expected {{{ResourceName}.{usage}}}.");
        }
    }
}

/// <summary>Resolves one property kind, selected by the first path element of a reference.</summary>
public interface IReferenceHandler
{
    /// <summary>The property this handles: <c>bindings</c>, <c>connectionString</c>, <c>value</c>, …</summary>
    string Property { get; }

    /// <summary>
    /// Resolves the reference, or throws <see cref="ExpressionException"/> to report why it can't.
    /// </summary>
    ValueTask<ResolvedValue> ResolveAsync(ReferenceContext context);
}

public static class ReferenceHandlers
{
    public static IReadOnlyList<IReferenceHandler> BuiltIn { get; } =
    [
        new ConnectionStringHandler(),
        new ValueHandler(),
        new InputsHandler(),
        new BindingsHandler()
    ];
}

/// <summary><c>{db.connectionString}</c>: the resource's connection string, itself resolved.</summary>
public sealed class ConnectionStringHandler : IReferenceHandler
{
    public string Property => "connectionString";

    public ValueTask<ResolvedValue> ResolveAsync(ReferenceContext context)
    {
        context.RequireArguments(0, Property);
        return context.Resource.ConnectionString is { } cs
            ? context.ResolveAsync(cs)
            : throw new ExpressionException(ExpressionDiagnosticKind.MissingValue,
                $"'{context.ResourceName}' has no connectionString.");
    }
}

/// <summary><c>{name.value}</c> on <c>value.v0</c> and <c>parameter.v0</c> resources.</summary>
public sealed class ValueHandler : IReferenceHandler
{
    public string Property => "value";

    public ValueTask<ResolvedValue> ResolveAsync(ReferenceContext context)
    {
        context.RequireArguments(0, Property);
        if (context.Resource.ExtensionData is not null &&
            context.Resource.ExtensionData.TryGetValue("value", out var v) &&
            v.ValueKind == JsonValueKind.String)
        {
            return context.ResolveAsync(v.GetString()!);
        }

        // A parameter without an explicit value expression still has a "value" input.
        if (ParameterInput.ReadAll(context.Resource).ContainsKey("value"))
        {
            return context.Resolver.ResolveReferenceAsync(
                new ReferenceSegment(context.ResourceName, ["inputs", "value"]), context.CancellationToken);
        }

        throw new ExpressionException(ExpressionDiagnosticKind.MissingValue,
            $"'{context.ResourceName}' has no value.");
    }
}

/// <summary><c>{name.inputs.value}</c>: a parameter input, supplied by the <see cref="IParameterSource"/>.</summary>
public sealed class InputsHandler : IReferenceHandler
{
    public string Property => "inputs";

    public async ValueTask<ResolvedValue> ResolveAsync(ReferenceContext context)
    {
        context.RequireArguments(1, "inputs.<input>");
        var inputName = context.Arguments[0];

        if (!ParameterInput.ReadAll(context.Resource).TryGetValue(inputName, out var input))
        {
            throw new ExpressionException(ExpressionDiagnosticKind.MissingValue,
                $"'{context.ResourceName}' declares no input named '{inputName}'.");
        }

        var value = context.Resolver.Parameters is { } parameters
            ? await parameters.GetValueAsync(context.ResourceName, input, context.CancellationToken)
            : null;
        if (value is null)
        {
            var variable = EnvironmentParameterSource.VariableNames(context.ResourceName, inputName).First();
            throw new ExpressionException(ExpressionDiagnosticKind.MissingValue,
                $"No value supplied for parameter '{context.ResourceName}'. Set {variable} or add a serviceOverrides env entry.");
        }

        return ResolvedValue.Of(new ParameterPart(context.ResourceName, inputName, value, input.Secret));
    }
}

/// <summary>
/// <c>{api.bindings.http.url|host|port|targetPort|scheme}</c>. Addresses come from the
/// target's <see cref="EndpointStrategy"/> and the assigned container ports.
/// </summary>
public sealed class BindingsHandler : IReferenceHandler
{
    public string Property => "bindings";

    public ValueTask<ResolvedValue> ResolveAsync(ReferenceContext context)
        => ValueTask.FromResult(Resolve(context));

    private static ResolvedValue Resolve(ReferenceContext context)
    {
        context.RequireArguments(2, "bindings.<binding>.<url|host|port|targetPort|scheme>");
        var resolver = context.Resolver;
        var resourceName = context.ResourceName;
        var resource = context.Resource;
        var bindingName = context.Arguments[0];
        var property = context.Arguments[1];

        if (resource.Bindings is null || !resource.Bindings.TryGetValue(bindingName, out var binding))
        {
            throw new ExpressionException(ExpressionDiagnosticKind.MissingValue,
                $"'{resourceName}' has no binding named '{bindingName}'.");
        }

        if (resolver.ProjectHttpsAsHttp && property != "targetPort" &&
            binding.Scheme == "https" && resource.IsProject &&
            resource.Bindings.FirstOrDefault(b => b.Value.Scheme == "http") is { Key: not null } http)
        {
            bindingName = http.Key;
            binding = http.Value;
        }

        if (!resolver.Ports.TryGetValue((resourceName, bindingName), out var containerPort))
        {
            throw new ExpressionException(ExpressionDiagnosticKind.MissingValue,
                $"No port was assigned to '{resourceName}' binding '{bindingName}'.");
        }

        var strategy = resolver.Strategy;
        var scheme = binding.EffectiveScheme;
        var text = property switch
        {
            "targetPort" => containerPort.ToString(),
            "port" => strategy.Port(resourceName, bindingName, containerPort).ToString(),
            "host" => strategy.Host(resourceName),
            "scheme" => scheme,
            "url" => strategy.Url?.Invoke(resourceName, bindingName, scheme, containerPort)
                     ?? $"{scheme}://{strategy.Host(resourceName)}:{strategy.Port(resourceName, bindingName, containerPort)}",
            _ => throw new ExpressionException(ExpressionDiagnosticKind.UnsupportedProperty,
                $"Unknown binding property '{property}'. Supported: url, host, port, targetPort, scheme.")
        };

        return ResolvedValue.Text(text);
    }
}
