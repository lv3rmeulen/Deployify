using System;
using System.Collections.Generic;
using System.Linq;
using Deployify.Core.Expressions;

namespace Deployify.Core.SecretBackends;

/// <summary>Names a secret parameter consistently across backends.</summary>
public static class SecretKeys
{
    /// <summary>
    /// The parameter's id: <c>pg-password</c>, or <c>pg-password.region</c> for an input other
    /// than <c>value</c>. Also the key under which secrets are stored in each backend, which
    /// accepts it as-is: Kubernetes Secret keys, Vault KV keys and SSM path segments all allow
    /// letters, digits, <c>-</c>, <c>_</c> and <c>.</c>.
    /// </summary>
    public static string Id(ParameterPart parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var id = parameter.Input == "value" ? parameter.Parameter : $"{parameter.Parameter}.{parameter.Input}";
        return Sanitize(id);
    }

    /// <summary>The key a generated value is kept under: <c>parameter.input</c>.</summary>
    public static string GeneratedKey(ParameterPart parameter) => $"{parameter.Parameter}.{parameter.Input}";

    /// <summary>Replaces anything other than letters, digits, <c>-</c>, <c>_</c> and <c>.</c> with <c>_</c>.</summary>
    public static string Sanitize(string key)
        => new([.. key.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')]);

    /// <summary>The distinct secret parameters used by a set of values, in first-use order.</summary>
    public static IReadOnlyList<ParameterPart> SecretParameters(IEnumerable<ResolvedValue> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ParameterPart>();
        foreach (var p in values.SelectMany(v => v.Parameters).Where(p => p.IsSecret))
        {
            if (seen.Add(Id(p)))
            {
                result.Add(p);
            }
        }

        return result;
    }

    /// <summary>The secret parameter values used by a set of values, keyed by <see cref="Id"/>.</summary>
    public static IReadOnlyDictionary<string, SensitiveValue> SecretValues(IEnumerable<ResolvedValue> values)
        => SecretParameters(values).ToDictionary(Id, p => new SensitiveValue(p.Value), StringComparer.Ordinal);

    /// <summary>The value is exactly one secret parameter, with nothing around it.</summary>
    public static bool IsSingleSecret(ResolvedValue value, out ParameterPart parameter)
    {
        if (value.Parts is [ParameterPart { IsSecret: true } p])
        {
            parameter = p;
            return true;
        }

        parameter = null!;
        return false;
    }
}
