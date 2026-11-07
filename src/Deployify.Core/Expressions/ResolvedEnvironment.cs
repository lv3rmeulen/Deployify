using System;
using System.Collections.Generic;
using System.Linq;

namespace Deployify.Core.Expressions;

/// <summary>
/// A service's environment variables after resolution, split by sensitivity. Plain values
/// can go anywhere (a ConfigMap, a job file, a task definition); secret values contain a
/// secret parameter and each target stores them in its own secret mechanism instead.
/// </summary>
public sealed class ResolvedEnvironment
{
    /// <summary>Values that contain no secret, fully rendered.</summary>
    public Dictionary<string, string> Plain { get; } = new(StringComparer.Ordinal);

    /// <summary>Values that contain at least one secret parameter.</summary>
    public Dictionary<string, ResolvedValue> Secrets { get; } = new(StringComparer.Ordinal);

    /// <summary>Adds a resolved value, sorting it into <see cref="Plain"/> or <see cref="Secrets"/>.</summary>
    public void Set(string key, ResolvedValue value)
    {
        if (value.IsSecret)
        {
            Secrets[key] = value;
            Plain.Remove(key);
        }
        else
        {
            Plain[key] = value.ToString();
            Secrets.Remove(key);
        }
    }

    /// <summary>Adds a literal value (e.g. a serviceOverrides entry). It replaces any secret of the same name.</summary>
    public void SetPlain(string key, string value)
    {
        Plain[key] = value;
        Secrets.Remove(key);
    }

    /// <summary>All variables with secret values masked, for explain output and logs.</summary>
    public Dictionary<string, string> ForDisplay(string mask = "****") => ForDisplay(Plain, Secrets, mask);

    /// <summary>Merges plain and secret variables, masking the secrets.</summary>
    public static Dictionary<string, string> ForDisplay(
        IReadOnlyDictionary<string, string> plain,
        IReadOnlyDictionary<string, ResolvedValue> secrets,
        string mask = "****")
    {
        var result = new Dictionary<string, string>(plain, StringComparer.Ordinal);
        foreach (var (key, value) in secrets)
        {
            result[key] = value.ToDisplayString(mask);
        }

        return result;
    }

    /// <summary>
    /// A name usable as an environment variable for a parameter, following Aspire's
    /// configuration convention: <c>Parameters__pg_password</c>, or
    /// <c>Parameters__p__region</c> for a non-default input.
    /// </summary>
    public static string VariableName(ParameterPart parameter)
    {
        static string Clean(string s) => new([.. s.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_')]);
        return parameter.Input == "value"
            ? $"Parameters__{Clean(parameter.Parameter)}"
            : $"Parameters__{Clean(parameter.Parameter)}__{Clean(parameter.Input)}";
    }
}
