using System;
using Deployify.Core.Models;

namespace Deployify.Hosting;

/// <summary>
/// How <c>aspire deploy</c> runs Deployify. Each setting can also come from configuration under
/// <c>Deployify:</c> (for example the <c>Deployify__Environment</c> environment variable in CI),
/// which wins over what is set in code.
/// </summary>
public sealed class DeployifyDeployOptions
{
    /// <summary>
    /// The Deployify environment to deploy (key <c>Deployify:Environment</c>). Null deploys the
    /// active environment of deployify-state.json, as <c>deployify apply</c> does.
    /// </summary>
    public string? Environment { get; set; }

    /// <summary>
    /// Where to start looking for deployify-state.json (key <c>Deployify:ProjectPath</c>). It is
    /// searched from here upwards. Defaults to the AppHost directory.
    /// </summary>
    public string? ProjectPath { get; set; }

    /// <summary>Overrides the environment's configured target (key <c>Deployify:Target</c>).</summary>
    public DeploymentTargetType? Target { get; set; }

    /// <summary>
    /// Build and push project images with Deployify's own publisher before deploying
    /// (key <c>Deployify:Publish</c>). Turn off when images are built elsewhere.
    /// </summary>
    public bool Publish { get; set; } = true;

    /// <summary>Overrides the image tag for this deploy (key <c>Deployify:ImageTag</c>).</summary>
    public string? ImageTag { get; set; }

    /// <summary>
    /// Use a health-gated rollout with automatic rollback when the target supports one
    /// (key <c>Deployify:HealthGate</c>), like <c>deployify apply</c>.
    /// </summary>
    public bool HealthGate { get; set; } = true;

    /// <summary>Roll back automatically when a gated rollout fails (key <c>Deployify:AutoRollback</c>).</summary>
    public bool AutoRollback { get; set; } = true;

    /// <summary>How long a gated rollout waits for services to become healthy (key <c>Deployify:HealthTimeoutSeconds</c>).</summary>
    public TimeSpan? HealthTimeout { get; set; }

    /// <summary>
    /// Deploy without asking for confirmation (key <c>Deployify:AutoApprove</c>). Without it,
    /// <c>aspire deploy</c> asks once the plan is known, and fails when it can't ask
    /// (non-interactive runs), the same way <c>deployify ci</c> needs <c>--auto-approve</c>.
    /// </summary>
    public bool AutoApprove { get; set; }

    /// <summary>
    /// Fail when the manifest has resources Deployify won't deploy, even if nothing depends on
    /// them (key <c>Deployify:Strict</c>), like <c>deployify apply --strict</c>. Resources a deployed
    /// service depends on always stop the deploy; list the ones you handle elsewhere under
    /// <c>ignoreResources</c> in deployify-state.json.
    /// </summary>
    public bool Strict { get; set; }
}
