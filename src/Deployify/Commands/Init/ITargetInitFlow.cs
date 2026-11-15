using System.Collections.Generic;
using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;

namespace Deployify.Commands.Init;

/// <summary>
/// How <c>deployify init</c> sets up one target (or a family of targets that share settings):
/// its command-line flags, its interactive questions, and the target config both produce.
/// </summary>
/// <remarks>
/// Adding a built-in target means adding one of these to <see cref="TargetInitFlows"/>;
/// <see cref="InitCommand"/> itself only handles the settings every target shares.
/// </remarks>
internal interface ITargetInitFlow
{
    /// <summary>The targets this flow configures.</summary>
    IReadOnlyList<DeploymentTargetType> Targets { get; }

    /// <summary>
    /// The flow's flags, in help order. An option shared with another flow is the same
    /// instance; <see cref="TargetInitFlows.Options"/> lists it once.
    /// </summary>
    IEnumerable<Option> Options { get; }

    /// <summary>Flags that hold secrets, rejected as literals unless --allow-plaintext-secrets is set.</summary>
    IEnumerable<SecretFlag> SecretFlags => [];

    /// <summary>The target config (key → section) from flags, for <c>--non-interactive</c>.</summary>
    Dictionary<string, object?> FromOptions(DeploymentTargetType target, ParseResult parseResult, InitContext context);

    /// <summary>The target config (key → section) from interactive prompts.</summary>
    Task<Dictionary<string, object?>> PromptAsync(DeploymentTargetType target, InitContext context, CancellationToken ct);
}

/// <summary>What a flow needs to know about the environment being set up.</summary>
/// <param name="EnvironmentName">Name of the environment, used for default names.</param>
/// <param name="Directory">Directory of deployify-state.json.</param>
internal sealed record InitContext(string EnvironmentName, string Directory);

/// <summary>A flag that holds a secret, with the variable to suggest instead.</summary>
/// <param name="ReadWhenOmitted">Whether the target reads <paramref name="EnvVar"/> on its own when the flag is left out.</param>
internal readonly record struct SecretFlag(Option<string?> Flag, string EnvVar, bool ReadWhenOmitted);
