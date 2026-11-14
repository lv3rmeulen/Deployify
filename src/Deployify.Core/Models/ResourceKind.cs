using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Deployify.Core.Models;

/// <summary>What Deployify does with a resource from the Aspire manifest.</summary>
public enum ResourceKind
{
    /// <summary>Becomes a running service on the target (projects and containers with an image).</summary>
    Workload,

    /// <summary>
    /// Only supplies values to other resources: parameters, values, and child resources such as
    /// a database whose connection string points at its server. Nothing to deploy by design.
    /// </summary>
    Input,

    /// <summary>Folded into another resource before deploying (Dapr sidecars and components).</summary>
    Folded,

    /// <summary>A resource type Deployify knows but can't deploy (executables, Bicep, containers with nothing to run).</summary>
    Unsupported,

    /// <summary>A resource type, or a version of one, this Deployify doesn't know.</summary>
    Unknown
}

/// <summary>A resource's <see cref="ResourceKind"/>, and for Unsupported and Unknown resources, why.</summary>
public sealed record ResourceClassification(ResourceKind Kind, string? Reason = null)
{
    /// <summary>True when Deployify won't deploy the resource although it may matter to the app.</summary>
    public bool IsGap => Kind is ResourceKind.Unsupported or ResourceKind.Unknown;
}

/// <summary>
/// The Aspire manifest resource types Deployify knows, and how each is handled. Every place that
/// asks "is this a workload?" or "is this a project?" goes through here, so a type is either
/// handled on purpose or reported, never skipped by accident.
/// </summary>
public static partial class ResourceTypes
{
    public const string ProjectV0 = "project.v0";
    public const string ProjectV1 = "project.v1";
    public const string ContainerV0 = "container.v0";
    public const string ContainerV1 = "container.v1";
    public const string Dockerfile = "dockerfile.v0";
    public const string Executable = "executable.v0";
    public const string Parameter = "parameter.v0";
    public const string Value = "value.v0";
    public const string AnnotatedString = "annotated.string";
    public const string Dapr = "dapr.v0";
    public const string DaprComponent = "dapr.component.v0";

    /// <summary>True for <c>project.v0</c> and <c>project.v1</c>.</summary>
    public static bool IsProject(string type) => type is ProjectV0 or ProjectV1;

    /// <summary>True for <c>container.v0</c> and <c>container.v1</c>, with or without an image.</summary>
    public static bool IsContainer(string type) => type is ContainerV0 or ContainerV1;

    /// <summary>
    /// True when the resource becomes a running service: a project, a Dockerfile build, or a
    /// container with an image.
    /// </summary>
    public static bool IsWorkload(AspireResource resource)
        => IsProject(resource.Type) || resource.DockerfileBuild is not null ||
           (IsContainer(resource.Type) && !string.IsNullOrEmpty(resource.Image));

    /// <summary>
    /// Classifies a resource on its own. A resource with a <c>parent</c> whose type Deployify
    /// doesn't know is classified by <see cref="Classify(string, AspireManifest)"/>, which can see
    /// the parent.
    /// </summary>
    public static ResourceClassification Classify(AspireResource resource)
    {
        var type = resource.Type;

        if (IsWorkload(resource))
        {
            return new(ResourceKind.Workload);
        }

        if (type is Parameter or Value or AnnotatedString || type.EndsWith(".database.v0", StringComparison.Ordinal))
        {
            return new(ResourceKind.Input);
        }

        if (type is Dapr or DaprComponent)
        {
            return new(ResourceKind.Folded);
        }

        if (IsContainer(type) || type == Dockerfile)
        {
            // A Dockerfile build with its context and Dockerfile is a workload (above); this one lacks them.
            return new(ResourceKind.Unsupported,
                "has neither an image nor a Dockerfile build with a context and a Dockerfile, so there is " +
                "nothing to run. Give it an image, or build it with AddDockerfile or WithDockerfile");
        }

        if (type == Executable)
        {
            return new(ResourceKind.Unsupported,
                "runs a local executable; Deployify deploys projects and containers only. " +
                "Call PublishAsDockerFile() on it and deploy the image as a container, or deploy it another way");
        }

        if (type.StartsWith("azure.", StringComparison.Ordinal))
        {
            return new(ResourceKind.Unsupported,
                "is Azure infrastructure, which Deployify doesn't provision. Create it yourself and pass " +
                "its connection details in as parameters");
        }

        if (type.StartsWith("aws.", StringComparison.Ordinal))
        {
            return new(ResourceKind.Unsupported,
                "is AWS infrastructure, which Deployify doesn't provision. Create it yourself and pass " +
                "its connection details in as parameters");
        }

        if (string.IsNullOrEmpty(type))
        {
            return new(ResourceKind.Unknown, "has no type");
        }

        var family = VersionSuffix().Replace(type, "");
        if (family is "project" or "container" or "dockerfile" or "executable" or "parameter" or "value")
        {
            return new(ResourceKind.Unknown,
                $"uses manifest format '{type}', which is newer than this version of Deployify understands. Update Deployify");
        }

        return new(ResourceKind.Unknown,
            $"has resource type '{type}', which this version of Deployify doesn't know. It may come from an " +
            "Aspire integration or a newer Aspire");
    }

    /// <summary>
    /// Classifies resource <paramref name="name"/> of <paramref name="manifest"/>. Beyond
    /// <see cref="Classify(AspireResource)"/>, a resource of an unknown type that is the child of a
    /// workload and only carries a connection string or value is an input: Aspire integrations
    /// emit such children for databases, queues and the like.
    /// </summary>
    public static ResourceClassification Classify(string name, AspireManifest manifest)
    {
        var resource = manifest.Resources[name];
        var own = Classify(resource);
        if (own.Kind != ResourceKind.Unknown || resource.Parent is null ||
            !manifest.Resources.TryGetValue(resource.Parent, out var parent) || !IsWorkload(parent) ||
            resource.Image is not null || resource.Path is not null || resource.Bindings is { Count: > 0 })
        {
            return own;
        }

        return new(ResourceKind.Input);
    }

    [GeneratedRegex(@"\.v\d+$")]
    private static partial Regex VersionSuffix();
}
