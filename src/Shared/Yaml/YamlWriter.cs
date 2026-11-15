using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;
using YamlDotNet.Serialization.NamingConventions;

namespace Deployify.Yaml;

// Compiled into each target that writes YAML (Kubernetes, Compose), so the plugin SDK and
// Deployify.Core don't take a dependency on YamlDotNet.

/// <summary>
/// Writes typed models as YAML. Nulls are left out, strings that YAML 1.1 or 1.2 would read as
/// something else (<c>true</c>, <c>yes</c>, <c>8080</c>, an empty string) are quoted, and
/// <see cref="Quoted"/> values are always double-quoted (or a <c>|-</c> block when they span lines).
/// </summary>
internal sealed partial class YamlWriter
{
    /// <summary>camelCase property names, as Kubernetes uses.</summary>
    public static YamlWriter CamelCase { get; } = new(CamelCaseNamingConvention.Instance);

    /// <summary>snake_case property names, as Docker Compose uses.</summary>
    public static YamlWriter SnakeCase { get; } = new(UnderscoredNamingConvention.Instance);

    private readonly ISerializer _serializer;

    private YamlWriter(INamingConvention naming) => _serializer = new SerializerBuilder()
        .WithNamingConvention(naming)
        // Not OmitEmptyCollections: it treats strings as collections and drops "" values. The
        // models leave optional lists null instead.
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .WithQuotingNecessaryStrings(quoteYaml1_1Strings: true)
        .WithTypeConverter(new QuotedConverter())
        .WithEventEmitter(next => new AmbiguousStringEmitter(next))
        .DisableAliases()
        .Build();

    /// <summary>Two-space indent, sequences indented under their key, no line folding, LF line endings.</summary>
    private static EmitterSettings Settings => new(
        bestIndent: 2, bestWidth: int.MaxValue, isCanonical: false, maxSimpleKeyLength: 1024,
        skipAnchorName: false, indentSequences: true, newLine: "\n");

    public string Serialize(object manifest)
    {
        using var writer = new StringWriter { NewLine = "\n" };
        _serializer.Serialize(new Emitter(writer, Settings), manifest);
        // A null mapping value comes out as "key: "; drop the trailing space. (Block scalars
        // never end a line with a space: the emitter quotes text that would need one.)
        return TrailingSpaceAfterKey().Replace(writer.ToString(), ":\n");
    }

    /// <summary>
    /// Serializes <paramref name="manifest"/> with the node at each path replaced by raw text
    /// (a Helm template action, say). Paths are keys separated by dots, with <c>[n]</c> for a
    /// sequence item; every path must exist, so a change to the models can't silently leave a
    /// value in place. <paramref name="transform"/> runs on the YAML before the raw text goes
    /// in, so it can escape the rest of the document without touching the replacements.
    /// </summary>
    /// <param name="replacements">
    /// Path → the text that takes the node's place, given the column its key starts at. Text
    /// starting with a newline goes on the next line, with no space after the colon.
    /// </param>
    public string SerializeWithReplacements(
        object manifest,
        IReadOnlyList<(string Path, Func<int, string> Replacement)> replacements,
        Func<string, string>? transform = null)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(Serialize(manifest)));
        var root = (YamlMappingNode)stream.Documents.Single().RootNode;

        var markers = new List<(string Marker, Func<int, string> Replacement)>();
        foreach (var (path, replacement) in replacements)
        {
            var marker = $"__deployify_replace_{markers.Count}__";
            Replace(root, path, new YamlScalarNode(marker) { Style = ScalarStyle.Plain });
            markers.Add((marker, replacement));
        }

        using var writer = new StringWriter { NewLine = "\n" };
        stream.Save(new Emitter(writer, Settings), assignAnchors: false);
        var text = writer.ToString();
        if (text.EndsWith("...\n", StringComparison.Ordinal))
        {
            text = text[..^4]; // the document end marker YamlStream adds
        }

        text = transform?.Invoke(text) ?? text;
        foreach (var (marker, replacement) in markers)
        {
            var at = text.IndexOf(marker, StringComparison.Ordinal);
            var lineStart = text.LastIndexOf('\n', at) + 1;
            var column = text[lineStart..at].TakeWhile(c => c == ' ').Count();
            var value = replacement(column);
            var start = value.StartsWith('\n') && text[at - 1] == ' ' ? at - 1 : at;
            text = string.Concat(text.AsSpan(0, start), value, text.AsSpan(at + marker.Length));
        }

        return text;
    }

    private static void Replace(YamlMappingNode root, string path, YamlNode value)
    {
        YamlNode parent = root;
        var segments = path.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            var (key, index) = ParseSegment(segments[i]);
            var mapping = parent as YamlMappingNode
                ?? throw new InvalidOperationException($"'{path}': '{key}' is not under a mapping.");
            var keyNode = new YamlScalarNode(key);
            if (!mapping.Children.TryGetValue(keyNode, out var child))
            {
                throw new InvalidOperationException($"'{path}': no '{key}' in the manifest.");
            }

            var last = i == segments.Length - 1;
            if (index is null)
            {
                if (last)
                {
                    mapping.Children[keyNode] = value;
                    return;
                }

                parent = child;
                continue;
            }

            var sequence = child as YamlSequenceNode
                ?? throw new InvalidOperationException($"'{path}': '{key}' is not a sequence.");
            if (index >= sequence.Children.Count)
            {
                throw new InvalidOperationException($"'{path}': '{key}' has no item {index}.");
            }

            if (last)
            {
                sequence.Children[index.Value] = value;
                return;
            }

            parent = sequence.Children[index.Value];
        }
    }

    private static (string Key, int? Index) ParseSegment(string segment)
    {
        var bracket = segment.IndexOf('[');
        return bracket < 0
            ? (segment, null)
            : (segment[..bracket], int.Parse(segment[(bracket + 1)..^1], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Quotes a string that a YAML 1.1 or 1.2 parser could read as something else. Kubernetes
    /// tools parse YAML 1.1 style (<c>yes</c> and <c>off</c> are bools, <c>1:20</c> is a number)
    /// and YamlDotNet's own check misses some of those (<c>1e3</c>, an empty string).
    /// </summary>
    private sealed class AmbiguousStringEmitter(IEventEmitter next) : ChainedEventEmitter(next)
    {
        public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
        {
            if (eventInfo.Source.Type == typeof(string)
                && eventInfo.Style is ScalarStyle.Any or ScalarStyle.Plain
                && IsAmbiguous(eventInfo.Source.Value as string))
            {
                eventInfo.Style = ScalarStyle.DoubleQuoted;
            }

            base.Emit(eventInfo, emitter);
        }
    }

    /// <summary>True when a plain scalar with this text wouldn't be read back as that string.</summary>
    internal static bool IsAmbiguous(string? text)
        => string.IsNullOrEmpty(text) || text.Trim() != text || NonStringScalar().IsMatch(text);

    // null, YAML 1.1 bools, ints (decimal, octal, hex, binary, sexagesimal), floats, .inf/.nan, dates.
    [System.Text.RegularExpressions.GeneratedRegex(@": \n")]
    private static partial System.Text.RegularExpressions.Regex TrailingSpaceAfterKey();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^(~|null|Null|NULL" +
        @"|y|Y|yes|Yes|YES|n|N|no|No|NO|true|True|TRUE|false|False|FALSE|on|On|ON|off|Off|OFF" +
        @"|[-+]?[0-9][0-9_]*|0o[0-7_]+|[-+]?0x[0-9a-fA-F_]+|[-+]?0b[01_]+|[-+]?[0-9][0-9_]*(:[0-5]?[0-9])+(\.[0-9_]*)?" +
        @"|[-+]?([0-9][0-9_]*)?\.[0-9_]*([eE][-+]?[0-9]+)?|[-+]?[0-9][0-9_]*[eE][-+]?[0-9]+" +
        @"|[-+]?\.(inf|Inf|INF)|\.(nan|NaN|NAN)" +
        @"|[0-9]{4}-[0-9]{1,2}-[0-9]{1,2}([Tt ].*)?)$")]
    private static partial System.Text.RegularExpressions.Regex NonStringScalar();

    private sealed class QuotedConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(Quoted);

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
            => throw new NotSupportedException("Manifests are only written.");

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            var text = ((Quoted)value!).Value;
            // |- keeps multi-line text (Vault templates, Key Vault object lists) readable; the
            // emitter falls back to a quoted scalar when a block can't hold the text exactly.
            var style = text.Contains('\n') ? ScalarStyle.Literal : ScalarStyle.DoubleQuoted;
            emitter.Emit(new Scalar(null, null, text, style, isPlainImplicit: true, isQuotedImplicit: true));
        }
    }
}

/// <summary>
/// A string that is always written quoted: values that come from the user or the app (env
/// values, image references, annotations), so YAML never reads one as a number, bool or null.
/// </summary>
internal sealed record Quoted(string Value)
{
    public static implicit operator Quoted(string value) => new(value);

    public static Quoted? OrNull(string? value) => value is null ? null : new(value);

    public override string ToString() => Value;
}
