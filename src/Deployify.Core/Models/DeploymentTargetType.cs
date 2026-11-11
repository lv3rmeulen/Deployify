using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Deployify.Core.Models;

/// <summary>
/// Identifies a deployment target: one of the built-in targets (the static members) or a
/// target a plugin provides (<c>example</c>, …). Compared case-insensitively, so
/// <c>nomad</c>, <c>Nomad</c> and <c>NOMAD</c> are the same target. Built-in IDs are always
/// written in their canonical spelling, which keeps existing state files unchanged.
/// </summary>
[JsonConverter(typeof(DeploymentTargetTypeJsonConverter))]
public readonly partial struct DeploymentTargetType : IEquatable<DeploymentTargetType>
{
    public static readonly DeploymentTargetType Nomad = new(Names.Nomad);
    public static readonly DeploymentTargetType Kubernetes = new(Names.Kubernetes);
    public static readonly DeploymentTargetType Compose = new(Names.Compose);
    public static readonly DeploymentTargetType AwsEcs = new(Names.AwsEcs);
    public static readonly DeploymentTargetType AzureContainerApps = new(Names.AzureContainerApps);
    public static readonly DeploymentTargetType NomadConsul = new(Names.NomadConsul);

    /// <summary>Kubernetes, with each service installed as a Helm release.</summary>
    public static readonly DeploymentTargetType Helm = new(Names.Helm);

    /// <summary>Kubernetes through GitOps: manifests committed to a repository, synced by Argo CD.</summary>
    public static readonly DeploymentTargetType ArgoCd = new(Names.ArgoCd);

    /// <summary>Kubernetes through GitOps: manifests committed to a repository, applied by Flux.</summary>
    public static readonly DeploymentTargetType Flux = new(Names.Flux);

    /// <summary>OVHcloud Managed Kubernetes: the Kubernetes target, with the cluster reached through the OVHcloud API.</summary>
    public static readonly DeploymentTargetType OvhCloud = new(Names.OvhCloud);

    /// <summary>IIS on Windows Server: each project published to a folder and hosted as an IIS site.</summary>
    public static readonly DeploymentTargetType Iis = new(Names.Iis);

    /// <summary>The targets that ship with Deployify, in the order they are offered.</summary>
    public static IReadOnlyList<DeploymentTargetType> BuiltIn { get; } =
        [Nomad, Kubernetes, Compose, AwsEcs, AzureContainerApps, NomadConsul, Helm, ArgoCd, Flux, OvhCloud, Iis];

    /// <summary>
    /// The built-in IDs as constants, for <c>switch</c> over <see cref="Value"/> and for attributes.
    /// </summary>
    public static class Names
    {
        public const string Nomad = "Nomad";
        public const string Kubernetes = "Kubernetes";
        public const string Compose = "Compose";
        public const string AwsEcs = "AwsEcs";
        public const string AzureContainerApps = "AzureContainerApps";
        public const string NomadConsul = "NomadConsul";
        public const string Helm = "Helm";
        public const string ArgoCd = "ArgoCd";
        public const string Flux = "Flux";
        public const string OvhCloud = "OvhCloud";
        public const string Iis = "Iis";
    }

    private readonly string? _value;

    private DeploymentTargetType(string value) => _value = value;

    /// <summary>The ID as written in configuration. Never null; the default value is <c>Nomad</c>.</summary>
    public string Value => _value ?? Nomad._value!;

    /// <summary>True for a target that ships with Deployify, false for one a plugin provides.</summary>
    public bool IsBuiltIn
    {
        get
        {
            foreach (var builtIn in BuiltIn)
            {
                if (Equals(builtIn))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Parses a target ID. Built-in names map to their canonical spelling; anything else that
    /// looks like an ID (letters, digits, '-', '_', '.', starting with a letter) is a plugin target.
    /// </summary>
    public static DeploymentTargetType Parse(string value)
        => TryParse(value, out var target)
            ? target
            : throw new FormatException(
                $"'{value}' is not a valid target ID: use letters, digits, '-', '_' or '.', starting with a letter.");

    public static bool TryParse(string? value, out DeploymentTargetType target)
    {
        target = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        foreach (var builtIn in BuiltIn)
        {
            if (string.Equals(builtIn.Value, value, StringComparison.OrdinalIgnoreCase))
            {
                target = builtIn;
                return true;
            }
        }

        if (!IdPattern().IsMatch(value))
        {
            return false;
        }

        target = new DeploymentTargetType(value);
        return true;
    }

    public bool Equals(DeploymentTargetType other)
        => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is DeploymentTargetType other && Equals(other);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    public override string ToString() => Value;

    public static bool operator ==(DeploymentTargetType left, DeploymentTargetType right) => left.Equals(right);

    public static bool operator !=(DeploymentTargetType left, DeploymentTargetType right) => !left.Equals(right);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex IdPattern();
}

/// <summary>Reads and writes a <see cref="DeploymentTargetType"/> as its ID string.</summary>
public sealed class DeploymentTargetTypeJsonConverter : JsonConverter<DeploymentTargetType>
{
    public override DeploymentTargetType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A deployment target must be a string.");
        }

        var text = reader.GetString();
        return DeploymentTargetType.TryParse(text, out var target)
            ? target
            : throw new JsonException($"'{text}' is not a valid deployment target ID.");
    }

    public override void Write(Utf8JsonWriter writer, DeploymentTargetType value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);

    public override DeploymentTargetType ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DeploymentTargetType.Parse(reader.GetString()!);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, DeploymentTargetType value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.Value);
}
