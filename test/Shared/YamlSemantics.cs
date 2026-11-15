using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Deployify.Tests.Shared;

/// <summary>
/// Turns YAML into a canonical JSON form that only keeps its meaning: mapping keys sorted,
/// plain scalars typed the way YAML 1.2's core schema reads them (so <c>8080</c> is a number and
/// <c>"8080"</c> a string), quoting and layout dropped. Two documents that Kubernetes would read
/// the same way give the same text.
/// </summary>
internal static partial class YamlSemantics
{
    public static string Normalize(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (var document in stream.Documents)
            {
                Write(writer, document.RootNode);
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                writer.WriteStartObject();
                foreach (var (key, value) in mapping.Children
                             .Select(c => (Key: ((YamlScalarNode)c.Key).Value ?? string.Empty, c.Value))
                             .OrderBy(c => c.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    Write(writer, value);
                }

                writer.WriteEndObject();
                break;

            case YamlSequenceNode sequence:
                writer.WriteStartArray();
                foreach (var item in sequence.Children)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;

            case YamlScalarNode scalar:
                WriteScalar(writer, scalar);
                break;

            default:
                throw new InvalidOperationException($"Unexpected YAML node {node.NodeType}.");
        }
    }

    private static void WriteScalar(Utf8JsonWriter writer, YamlScalarNode scalar)
    {
        var value = scalar.Value ?? string.Empty;
        if (scalar.Style != ScalarStyle.Plain)
        {
            writer.WriteStringValue(value);
            return;
        }

        if (value is "" or "~" or "null" or "Null" or "NULL")
        {
            writer.WriteNullValue();
        }
        else if (value is "true" or "True" or "TRUE")
        {
            writer.WriteBooleanValue(true);
        }
        else if (value is "false" or "False" or "FALSE")
        {
            writer.WriteBooleanValue(false);
        }
        else if (IntPattern().IsMatch(value) || FloatPattern().IsMatch(value))
        {
            // Tagged so "1" (string) and 1 (number) stay apart without losing precision.
            writer.WriteStringValue("#number:" + value);
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }

    [GeneratedRegex(@"^([-+]?[0-9]+|0o[0-7]+|0x[0-9a-fA-F]+)$")]
    private static partial Regex IntPattern();

    [GeneratedRegex(@"^([-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?|[-+]?\.(inf|Inf|INF)|\.(nan|NaN|NAN))$")]
    private static partial Regex FloatPattern();
}
