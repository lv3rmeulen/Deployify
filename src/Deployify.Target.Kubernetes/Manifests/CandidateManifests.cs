using System.IO;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace Deployify.Target.Kubernetes.Manifests;

/// <summary>
/// The objects of a canary or blue-green candidate, made from the service's own generated objects
/// so the candidate runs exactly what a plain apply would run. Each is edited as a YAML tree,
/// which keeps every value and its quoting as generated.
/// </summary>
internal static class CandidateManifests
{
    /// <summary>Label on every candidate object: the service it is a candidate of.</summary>
    public const string CandidateOfLabel = "deployify/candidate-of";

    /// <summary>Label telling canary pods apart from the service's own pods.</summary>
    public const string TrackLabel = "deployify/track";

    /// <summary>
    /// The Deployment <paramref name="deploymentYaml"/>, renamed to <paramref name="name"/>.
    /// A canary keeps the <c>app</c> label, so the service's Service sends it a share of the traffic,
    /// and adds <see cref="TrackLabel"/> to its selector. A preview gets <c>app: {name}</c>, so it gets
    /// no traffic until the Service's selector is switched to it.
    /// </summary>
    public static string Deployment(
        string deploymentYaml, string service, string name, bool preview, int replicas,
        string? configMapName, string originalConfigMapName)
    {
        var (stream, root) = Load(deploymentYaml);
        var metadata = Map(root, "metadata");
        metadata.Children[new YamlScalarNode("name")] = new YamlScalarNode(name);
        Labels(metadata, service);

        var spec = Map(root, "spec");
        spec.Children[new YamlScalarNode("replicas")] = new YamlScalarNode(replicas.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var matchLabels = Map(Map(spec, "selector"), "matchLabels");
        var template = Map(spec, "template");
        var podLabels = Map(Map(template, "metadata"), "labels");

        if (preview)
        {
            matchLabels.Children[new YamlScalarNode("app")] = new YamlScalarNode(name);
            podLabels.Children[new YamlScalarNode("app")] = new YamlScalarNode(name);
        }
        else
        {
            matchLabels.Children[new YamlScalarNode(TrackLabel)] = new YamlScalarNode("canary");
            podLabels.Children[new YamlScalarNode(TrackLabel)] = new YamlScalarNode("canary");
        }

        podLabels.Children[new YamlScalarNode(CandidateOfLabel)] = new YamlScalarNode(service);

        if (configMapName is not null && Child(Map(template, "spec"), "containers") is YamlSequenceNode containers)
        {
            foreach (var container in containers.Children.OfType<YamlMappingNode>())
            {
                if (Child(container, "envFrom") is not YamlSequenceNode envFrom)
                {
                    continue;
                }

                foreach (var source in envFrom.Children.OfType<YamlMappingNode>())
                {
                    if (Child(source, "configMapRef") is YamlMappingNode reference &&
                        Child(reference, "name") is YamlScalarNode { Value: { } current } && current == originalConfigMapName)
                    {
                        reference.Children[new YamlScalarNode("name")] = new YamlScalarNode(configMapName);
                    }
                }
            }
        }

        return Save(stream);
    }

    /// <summary>The service's ConfigMap under <paramref name="name"/>, so the candidate reads the new values and the running pods keep theirs.</summary>
    public static string ConfigMap(string configMapYaml, string service, string name)
    {
        var (stream, root) = Load(configMapYaml);
        var metadata = Map(root, "metadata");
        metadata.Children[new YamlScalarNode("name")] = new YamlScalarNode(name);
        Labels(metadata, service);
        return Save(stream);
    }

    /// <summary>The service's Service under <paramref name="name"/>, selecting the preview's pods.</summary>
    public static string PreviewService(string serviceYaml, string service, string name)
    {
        var (stream, root) = Load(serviceYaml);
        var metadata = Map(root, "metadata");
        metadata.Children[new YamlScalarNode("name")] = new YamlScalarNode(name);
        Labels(metadata, service);
        Map(Map(root, "spec"), "selector").Children[new YamlScalarNode("app")] = new YamlScalarNode(name);
        return Save(stream);
    }

    /// <summary>A copy of <paramref name="yaml"/> with <c>metadata.name</c> set to <paramref name="name"/>.</summary>
    public static string Renamed(string yaml, string name)
    {
        var (stream, root) = Load(yaml);
        Map(root, "metadata").Children[new YamlScalarNode("name")] = new YamlScalarNode(name);
        return Save(stream);
    }

    /// <summary>A Deployment without <c>spec.replicas</c>, for a controller (Flagger) that owns the count.</summary>
    public static string WithoutReplicas(string deploymentYaml)
    {
        var (stream, root) = Load(deploymentYaml);
        Map(root, "spec").Children.Remove(new YamlScalarNode("replicas"));
        return Save(stream);
    }

    private static void Labels(YamlMappingNode metadata, string service)
        => Map(metadata, "labels").Children[new YamlScalarNode(CandidateOfLabel)] = new YamlScalarNode(service);

    private static (YamlStream Stream, YamlMappingNode Root) Load(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (stream, (YamlMappingNode)stream.Documents[0].RootNode);
    }

    private static string Save(YamlStream stream)
    {
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        // YamlStream ends a document with "...": harmless, but noisy in logs.
        var text = writer.ToString().ReplaceLineEndings("\n");
        return text.EndsWith("...\n", System.StringComparison.Ordinal) ? text[..^4] : text;
    }

    private static YamlNode? Child(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var child) ? child : null;

    private static YamlMappingNode Map(YamlMappingNode node, string key)
    {
        if (Child(node, key) is YamlMappingNode existing)
        {
            return existing;
        }

        var created = new YamlMappingNode();
        node.Children[new YamlScalarNode(key)] = created;
        return created;
    }
}
