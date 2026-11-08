using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deployify.Core.SecretBackends;

/// <summary>
/// A secret value held in memory. It can't leak by accident: <see cref="ToString"/> returns a
/// mask, and serializing it to JSON throws, so a secret that ends up on a state or output model
/// fails the build or the run instead of being written to disk. Call <see cref="Reveal"/> at the
/// one point where the value goes to its backend.
/// </summary>
[JsonConverter(typeof(SensitiveValueJsonConverter))]
public sealed class SensitiveValue : IEquatable<SensitiveValue>
{
    public const string Mask = "****";

    private readonly string _value;

    public SensitiveValue(string value) => _value = value ?? throw new ArgumentNullException(nameof(value));

    public int Length => _value.Length;

    /// <summary>The plain value. Only call this when handing the value to a secret backend.</summary>
    public string Reveal() => _value;

    public override string ToString() => Mask;

    /// <summary>Compares in constant time, so the comparison doesn't leak how much of a value matched.</summary>
    public bool Equals(SensitiveValue? other)
        => other is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(_value), Encoding.UTF8.GetBytes(other._value));

    public override bool Equals(object? obj) => obj is SensitiveValue other && Equals(other);

    // Hash codes don't need to be secret-safe for dictionary use, but avoid exposing the value.
    public override int GetHashCode() => _value.Length;
}

/// <summary>Refuses to serialize or deserialize a <see cref="SensitiveValue"/>.</summary>
public sealed class SensitiveValueJsonConverter : JsonConverter<SensitiveValue>
{
    public override SensitiveValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new SecretLeakException("A secret value can't be read from JSON. Store a reference instead.");

    public override void Write(Utf8JsonWriter writer, SensitiveValue value, JsonSerializerOptions options)
        => throw new SecretLeakException("A secret value can't be written to JSON. Store a reference instead.");
}
