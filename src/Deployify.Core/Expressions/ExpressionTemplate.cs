using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Deployify.Core.Expressions;

/// <summary>One piece of a parsed <see cref="ExpressionTemplate"/>.</summary>
public abstract record TemplateSegment;

/// <summary>Text that is copied through unchanged.</summary>
public sealed record LiteralSegment(string Text) : TemplateSegment;

/// <summary>
/// A <c>{resource.property[.more]}</c> reference, e.g. <c>{pg.bindings.tcp.host}</c>,
/// <c>{db.connectionString}</c> or <c>{pg-password.inputs.value}</c>.
/// </summary>
public sealed record ReferenceSegment(string Resource, IReadOnlyList<string> Path) : TemplateSegment
{
    /// <summary>The reference without braces: <c>pg.bindings.tcp.host</c>.</summary>
    public string Expression => $"{Resource}.{string.Join('.', Path)}";

    /// <summary>The reference as it appears in the manifest: <c>{pg.bindings.tcp.host}</c>.</summary>
    public string Placeholder => "{" + Expression + "}";

    /// <summary>The first path element, which selects the handler (<c>bindings</c>, <c>connectionString</c>, …).</summary>
    public string Property => Path[0];

    public bool Equals(ReferenceSegment? other)
        => other is not null && Resource == other.Resource && Path.SequenceEqual(other.Path);

    public override int GetHashCode() => Expression.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Placeholder;
}

/// <summary>
/// A manifest value split into literal text and <c>{resource.path}</c> references.
/// Parsing is lenient on purpose: braces that don't form a reference (JSON in an env
/// value, a Go template, a lone <c>{</c>) are kept as literal text rather than rejected.
///
/// To write a reference-shaped value literally, double its braces: <c>{{api.value}}</c>
/// becomes the text <c>{api.value}</c> and is not resolved. Only a doubled reference is
/// unescaped. Other <c>{{</c> / <c>}}</c> pairs are left alone, because Aspire writes literal
/// braces into the manifest as-is (<c>ReferenceExpression.ValueExpression</c> undoes its own
/// escaping), so a general rule would corrupt Go templates or JSON that ends in <c>}}</c>.
/// </summary>
public sealed class ExpressionTemplate
{
    private const string ReferenceBody = @"[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*";

    // An escaped reference ({{res.path}}) is tried first so its inner braces aren't matched.
    private static readonly Regex s_reference = new(
        $@"\{{\{{(?<escaped>{ReferenceBody})\}}\}}|\{{(?<res>[A-Za-z0-9_-]+)\.(?<path>[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*)\}}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private ExpressionTemplate(string source, IReadOnlyList<TemplateSegment> segments)
    {
        Source = source;
        Segments = segments;
    }

    /// <summary>The original text.</summary>
    public string Source { get; }

    public IReadOnlyList<TemplateSegment> Segments { get; }

    public IEnumerable<ReferenceSegment> References => Segments.OfType<ReferenceSegment>();

    public bool HasReferences => Segments.Any(s => s is ReferenceSegment);

    public static ExpressionTemplate Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var segments = new List<TemplateSegment>();
        if (!source.Contains('{'))
        {
            if (source.Length > 0)
            {
                segments.Add(new LiteralSegment(source));
            }

            return new ExpressionTemplate(source, segments);
        }

        var position = 0;
        foreach (Match m in s_reference.Matches(source))
        {
            if (m.Index > position)
            {
                segments.Add(new LiteralSegment(source[position..m.Index]));
            }

            if (m.Groups["escaped"].Success)
            {
                segments.Add(new LiteralSegment("{" + m.Groups["escaped"].Value + "}"));
            }
            else
            {
                segments.Add(new ReferenceSegment(m.Groups["res"].Value, m.Groups["path"].Value.Split('.')));
            }

            position = m.Index + m.Length;
        }

        if (position < source.Length)
        {
            segments.Add(new LiteralSegment(source[position..]));
        }

        return new ExpressionTemplate(source, MergeLiterals(segments));
    }

    private static List<TemplateSegment> MergeLiterals(List<TemplateSegment> segments)
    {
        var merged = new List<TemplateSegment>(segments.Count);
        foreach (var segment in segments)
        {
            if (segment is LiteralSegment literal && merged.Count > 0 && merged[^1] is LiteralSegment previous)
            {
                merged[^1] = new LiteralSegment(previous.Text + literal.Text);
            }
            else
            {
                merged.Add(segment);
            }
        }

        return merged;
    }

    public override string ToString() => Source;
}
