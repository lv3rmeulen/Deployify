using System.CommandLine.Parsing;
using Deployify.Core.Models;

namespace Deployify.Commands;

/// <summary>
/// Parses <c>--target</c> values. Built-in names are matched case-insensitively; any other valid
/// ID is accepted as a plugin target and checked once the target factory is built.
/// </summary>
internal static class TargetOption
{
    public const string Description =
        "Deployment target: nomad | kubernetes | helm | argocd | flux | compose | awsecs | azurecontainerapps | nomadconsul, " +
        "or the ID of a plugin target.";

    public static DeploymentTargetType? ParseOptional(ArgumentResult result)
        => result.Tokens.Count == 0 ? null : Parse(result);

    public static DeploymentTargetType Parse(ArgumentResult result)
    {
        var text = result.Tokens.Count == 0 ? string.Empty : result.Tokens[^1].Value;
        if (DeploymentTargetType.TryParse(text, out var target))
        {
            return target;
        }

        result.AddError(
            $"'{text}' is not a valid target. Use a built-in target ({string.Join(", ", DeploymentTargetType.BuiltIn).ToLowerInvariant()}) " +
            "or a plugin target ID (letters, digits, '-', '_', '.').");
        return default;
    }
}
