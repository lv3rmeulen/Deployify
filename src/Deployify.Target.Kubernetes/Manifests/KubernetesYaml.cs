using System.IO;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace Deployify.Target.Kubernetes.Manifests;

/// <summary>Reads what Deployify needs back out of a written manifest.</summary>
internal static class KubernetesYaml
{
    /// <summary>kind, apiVersion, metadata.name and metadata.namespace of a manifest.</summary>
    public static (string Kind, string ApiVersion, string Name, string? Namespace) ReadMetadata(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = stream.Documents.FirstOrDefault()?.RootNode as YamlMappingNode;
        var metadata = Child(root, "metadata") as YamlMappingNode;
        var ns = Scalar(metadata, "namespace");
        return (Scalar(root, "kind") ?? "", Scalar(root, "apiVersion") ?? "", Scalar(metadata, "name") ?? "",
            string.IsNullOrEmpty(ns) ? null : ns);

        static YamlNode? Child(YamlMappingNode? node, string key)
            => node is not null && node.Children.TryGetValue(new YamlScalarNode(key), out var child) ? child : null;

        static string? Scalar(YamlMappingNode? node, string key) => (Child(node, key) as YamlScalarNode)?.Value;
    }
}
