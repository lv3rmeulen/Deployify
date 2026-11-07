using System;
using System.Collections.Generic;
using Deployify.Core.Models;

namespace Deployify.Core.Expressions;

/// <summary>Connects an <see cref="EnvironmentConfig"/> to the parameter sources the generators use.</summary>
public static class ParameterResolution
{
    /// <summary>
    /// The parameter source for <paramref name="config"/>. Uses the one the CLI attached; otherwise
    /// creates an in-memory source once and keeps it on the config, so plan, apply and explain in
    /// the same run all resolve a generated password to the same value.
    /// </summary>
    public static IParameterSource For(EnvironmentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.ParameterSource ??= ParameterSources.Default(ExplicitValues(config));
    }

    /// <summary>
    /// <see cref="EnvironmentConfig.Parameters"/> with <c>env:NAME</c> references resolved.
    /// Entries whose variable isn't set are left out, so the next source gets a chance.
    /// </summary>
    public static Dictionary<string, string> ExplicitValues(EnvironmentConfig config)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, configured) in config.Parameters)
        {
            if (Secrets.Resolve(configured) is { } value)
            {
                result[name] = value;
            }
        }

        return result;
    }
}
