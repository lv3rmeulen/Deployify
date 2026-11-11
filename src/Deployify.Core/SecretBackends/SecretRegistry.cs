using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;

namespace Deployify.Core.SecretBackends;

/// <summary>
/// Remembers every secret value resolved during this run, so files Deployify writes itself
/// (the state file, artifacts) can be checked for them before they're saved.
/// </summary>
public sealed class SecretRegistry
{
    private readonly ConcurrentDictionary<string, SensitiveValue> _values = new(StringComparer.Ordinal);

    public void Record(string id, string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _values[id] = new SensitiveValue(value);
        }
    }

    public IReadOnlyDictionary<string, SensitiveValue> Snapshot() => _values.ToDictionary(StringComparer.Ordinal);

    public int Count => _values.Count;
}

/// <summary>Passes lookups through to another source and records the secret values it returns.</summary>
public sealed class RecordingParameterSource(IParameterSource inner, SecretRegistry registry) : IParameterSource
{
    public async ValueTask<string?> GetValueAsync(string parameter, ParameterInput input, CancellationToken ct = default)
    {
        var value = await inner.GetValueAsync(parameter, input, ct);
        if (value is not null && input.Secret)
        {
            registry.Record(input.Name == "value" ? parameter : $"{parameter}.{input.Name}", value);
        }

        return value;
    }
}
