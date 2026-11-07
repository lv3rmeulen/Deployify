using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Deployify.Core.Expressions;

/// <summary>One piece of a <see cref="ResolvedValue"/>.</summary>
public abstract record ValuePart;

/// <summary>Plain text: literals from the template and resolved binding properties.</summary>
public sealed record TextPart(string Text) : ValuePart;

/// <summary>
/// The value of a <c>parameter.v0</c> input. Kept separate from <see cref="TextPart"/> so a
/// target can emit a native reference instead of inlining it, e.g. <c>${PG_PASSWORD}</c> in
/// Compose or a <c>secretKeyRef</c> in Kubernetes.
/// </summary>
public sealed record ParameterPart(string Parameter, string Input, string Value, bool IsSecret) : ValuePart
{
    // Keep generated passwords out of logs and test output.
    public override string ToString()
        => $"ParameterPart {{ Parameter = {Parameter}, Input = {Input}, IsSecret = {IsSecret} }}";
}

/// <summary>A reference that could not be resolved. Rendered as the original placeholder.</summary>
public sealed record UnresolvedPart(string Placeholder) : ValuePart;

/// <summary>
/// The result of resolving an expression. It keeps the structure of the value (text,
/// parameters, unresolved references) so a target can decide how each part is written out,
/// and it remembers whether a secret went into it.
/// </summary>
public sealed class ResolvedValue
{
    public static readonly ResolvedValue Empty = new([]);

    private ResolvedValue(IReadOnlyList<ValuePart> parts) => Parts = parts;

    public IReadOnlyList<ValuePart> Parts { get; }

    /// <summary>True when any part of the value came from a secret parameter.</summary>
    public bool IsSecret => Parts.Any(p => p is ParameterPart { IsSecret: true });

    /// <summary>True when every reference in the value was resolved.</summary>
    public bool IsFullyResolved => !Parts.Any(p => p is UnresolvedPart);

    /// <summary>The parameter values that went into this value, in order.</summary>
    public IEnumerable<ParameterPart> Parameters => Parts.OfType<ParameterPart>();

    /// <summary>The placeholders that could not be resolved, in order.</summary>
    public IEnumerable<string> UnresolvedPlaceholders => Parts.OfType<UnresolvedPart>().Select(p => p.Placeholder);

    public static ResolvedValue Text(string text)
        => string.IsNullOrEmpty(text) ? Empty : new([new TextPart(text)]);

    public static ResolvedValue Of(ValuePart part) => new([part]);

    /// <summary>Joins values in order, merging neighbouring text so the parts stay minimal.</summary>
    public static ResolvedValue Concat(IEnumerable<ResolvedValue> values)
    {
        var parts = new List<ValuePart>();
        foreach (var part in values.SelectMany(v => v.Parts))
        {
            if (part is TextPart text && parts.Count > 0 && parts[^1] is TextPart previous)
            {
                parts[^1] = new TextPart(previous.Text + text.Text);
            }
            else
            {
                parts.Add(part);
            }
        }

        return parts.Count == 0 ? Empty : new(parts);
    }

    /// <summary>
    /// Writes the value out. By default parameters are inlined and unresolved references
    /// are left as their original placeholder; pass delegates to render either differently.
    /// <paramref name="text"/> escapes literal text, e.g. doubling <c>$</c> for Compose when
    /// parameters are rendered as <c>${VAR}</c>.
    /// </summary>
    public string Render(
        Func<ParameterPart, string>? parameter = null,
        Func<UnresolvedPart, string>? unresolved = null,
        Func<string, string>? text = null)
    {
        var sb = new StringBuilder();
        foreach (var part in Parts)
        {
            sb.Append(part switch
            {
                TextPart t => text is null ? t.Text : text(t.Text),
                ParameterPart p => parameter is null ? p.Value : parameter(p),
                UnresolvedPart u => unresolved is null ? u.Placeholder : unresolved(u),
                _ => throw new InvalidOperationException($"Unknown value part {part.GetType().Name}.")
            });
        }

        return sb.ToString();
    }

    /// <summary>Renders with secret parameters masked, for logs and plan output.</summary>
    public string ToDisplayString(string mask = "****")
        => Render(p => p.IsSecret ? mask : p.Value);

    /// <summary>The fully inlined value (see <see cref="Render"/>).</summary>
    public override string ToString() => Render();
}
