using System;
using System.Collections.Generic;

namespace Deployify.Core.Expressions;

public enum ExpressionDiagnosticKind
{
    /// <summary>The reference names a resource that isn't in the manifest.</summary>
    UnknownResource,

    /// <summary>The resource exists but the property path isn't supported (e.g. <c>{api.foo}</c>).</summary>
    UnsupportedProperty,

    /// <summary>The property is valid but has no value here (no port assigned, no parameter value, …).</summary>
    MissingValue,

    /// <summary>The reference eventually refers back to itself.</summary>
    Cycle,

    /// <summary>References nest deeper than <see cref="ExpressionResolver.MaxDepth"/>.</summary>
    DepthExceeded
}

/// <summary>Why a reference could not be resolved.</summary>
/// <param name="Placeholder">The reference that failed, e.g. <c>{pg-password.inputs.value}</c>.</param>
/// <param name="Chain">The references being resolved when it failed, outermost first.</param>
public sealed record ExpressionDiagnostic(
    string Placeholder,
    ExpressionDiagnosticKind Kind,
    string Message,
    IReadOnlyList<string> Chain)
{
    public override string ToString()
        => Chain.Count == 0 ? $"{Placeholder}: {Message}" : $"{Placeholder}: {Message} (via {string.Join(" -> ", Chain)})";
}

/// <summary>
/// Thrown by an <see cref="IReferenceHandler"/> when a reference can't be resolved, and by
/// <see cref="ExpressionResolver"/> in <see cref="ExpressionResolver.Strict"/> mode.
/// </summary>
public sealed class ExpressionException : Exception
{
    public ExpressionException(ExpressionDiagnosticKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public ExpressionException(ExpressionDiagnostic diagnostic)
        : base(diagnostic.ToString())
    {
        Kind = diagnostic.Kind;
        Diagnostic = diagnostic;
    }

    public ExpressionDiagnosticKind Kind { get; }

    /// <summary>Set when thrown by the resolver in strict mode.</summary>
    public ExpressionDiagnostic? Diagnostic { get; }
}
