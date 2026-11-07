using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace Deployify.Core.Expressions;

/// <summary>Supplies values for <c>parameter.v0</c> inputs.</summary>
public interface IParameterSource
{
    /// <summary>Returns the value for <paramref name="input"/> of <paramref name="parameter"/>, or null to defer.</summary>
    string? GetValue(string parameter, ParameterInput input);
}

/// <summary>
/// Explicit values keyed by parameter name (<c>"pg-password"</c>) or, for an input other than
/// <c>value</c>, by <c>"parameter.input"</c>. Keys are case-insensitive.
/// </summary>
public sealed class DictionaryParameterSource(IReadOnlyDictionary<string, string> values) : IParameterSource
{
    private readonly Dictionary<string, string> _values = new(values, StringComparer.OrdinalIgnoreCase);

    public string? GetValue(string parameter, ParameterInput input)
    {
        if (_values.TryGetValue($"{parameter}.{input.Name}", out var v))
        {
            return v;
        }

        return input.Name == "value" && _values.TryGetValue(parameter, out v) ? v : null;
    }
}

/// <summary>
/// Reads parameters from the environment using Aspire's configuration convention,
/// <c>Parameters__{name}</c>. A dash in the name may also be written as an underscore,
/// since most shells can't export a variable with a dash in it.
/// </summary>
public sealed class EnvironmentParameterSource(Func<string, string?>? getVariable = null) : IParameterSource
{
    private readonly Func<string, string?> _get = getVariable ?? Environment.GetEnvironmentVariable;

    public string? GetValue(string parameter, ParameterInput input)
    {
        foreach (var name in VariableNames(parameter, input.Name))
        {
            var value = _get(name);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>The variable names that are checked for a parameter, in order.</summary>
    public static IEnumerable<string> VariableNames(string parameter, string input = "value")
    {
        var key = input == "value" ? parameter : $"{parameter}__{input}";
        yield return $"Parameters__{key}";
        if (key.Contains('-'))
        {
            yield return $"Parameters__{key.Replace('-', '_')}";
        }
    }
}

/// <summary>Uses <c>default.value</c> from the manifest.</summary>
public sealed class DefaultValueParameterSource : IParameterSource
{
    public string? GetValue(string parameter, ParameterInput input) => input.DefaultValue;
}

/// <summary>
/// Generates a value for inputs with <c>default.generate</c>. Values are kept in
/// <paramref name="store"/> under <c>"parameter.input"</c>, so a caller that persists the
/// store (e.g. in deployify-state.json or a secret store) gets the same password on every
/// deploy instead of rotating it and locking the app out of its existing data.
/// </summary>
public sealed class GeneratedParameterSource(
    IDictionary<string, string> store, Action<string, string>? onGenerated = null) : IParameterSource
{
    public GeneratedParameterSource()
        : this(new Dictionary<string, string>(StringComparer.Ordinal))
    {
    }

    /// <summary>All generated (or previously stored) values.</summary>
    public IDictionary<string, string> Store { get; } = store;

    public string? GetValue(string parameter, ParameterInput input)
    {
        if (input.Generate is null)
        {
            return null;
        }

        var key = $"{parameter}.{input.Name}";
        if (!Store.TryGetValue(key, out var value))
        {
            value = PasswordGenerator.Generate(input.Generate);
            Store[key] = value;
            onGenerated?.Invoke(key, value);
        }

        return value;
    }
}

/// <summary>Asks each source in turn and returns the first value supplied.</summary>
public sealed class CompositeParameterSource(params IParameterSource[] sources) : IParameterSource
{
    public string? GetValue(string parameter, ParameterInput input)
        => sources.Select(s => s.GetValue(parameter, input)).FirstOrDefault(v => v is not null);
}

public static class ParameterSources
{
    /// <summary>
    /// The usual lookup order: explicit values, then <c>Parameters__*</c> environment variables,
    /// then the manifest's <c>default.value</c>, then a generated value kept in
    /// <paramref name="generatedStore"/>.
    /// </summary>
    public static IParameterSource Default(
        IReadOnlyDictionary<string, string>? values = null,
        IDictionary<string, string>? generatedStore = null,
        Func<string, string?>? getVariable = null,
        Action<string, string>? onGenerated = null)
    {
        var sources = new List<IParameterSource>();
        if (values is { Count: > 0 })
        {
            sources.Add(new DictionaryParameterSource(values));
        }

        sources.Add(new EnvironmentParameterSource(getVariable));
        sources.Add(new DefaultValueParameterSource());
        sources.Add(new GeneratedParameterSource(
            generatedStore ?? new Dictionary<string, string>(StringComparer.Ordinal), onGenerated));
        return new CompositeParameterSource([.. sources]);
    }
}

/// <summary>Generates random values that satisfy a <see cref="GenerateOptions"/>.</summary>
public static class PasswordGenerator
{
    public const string LowerChars = "abcdefghijklmnopqrstuvwxyz";
    public const string UpperChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    public const string NumericChars = "0123456789";

    /// <summary>
    /// Special characters that are safe in connection strings, URLs and shell/YAML values:
    /// no <c>; = ' " @ / \ ? # % { } $</c> or whitespace.
    /// </summary>
    public const string SpecialChars = "-_.~!*()+";

    public static string Generate(GenerateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var classes = new (bool Enabled, int Min, string Chars, string Name)[]
        {
            (options.Lower, options.MinLower, LowerChars, "lower"),
            (options.Upper, options.MinUpper, UpperChars, "upper"),
            (options.Numeric, options.MinNumeric, NumericChars, "numeric"),
            (options.Special, options.MinSpecial, SpecialChars, "special")
        };

        foreach (var c in classes)
        {
            if (c.Min < 0)
            {
                throw new ArgumentException($"min{c.Name} cannot be negative.", nameof(options));
            }

            if (!c.Enabled && c.Min > 0)
            {
                throw new ArgumentException($"min{c.Name} is {c.Min} but {c.Name} characters are disabled.", nameof(options));
            }
        }

        var pool = string.Concat(classes.Where(c => c.Enabled).Select(c => c.Chars));
        if (pool.Length == 0)
        {
            throw new ArgumentException("At least one character class must be enabled.", nameof(options));
        }

        var chars = new List<char>();
        foreach (var c in classes.Where(c => c.Enabled))
        {
            for (var i = 0; i < c.Min; i++)
            {
                chars.Add(c.Chars[RandomNumberGenerator.GetInt32(c.Chars.Length)]);
            }
        }

        var length = Math.Max(options.MinLength, chars.Count);
        while (chars.Count < length)
        {
            chars.Add(pool[RandomNumberGenerator.GetInt32(pool.Length)]);
        }

        var result = chars.ToArray();
        RandomNumberGenerator.Shuffle(result.AsSpan());
        return new string(result);
    }
}
