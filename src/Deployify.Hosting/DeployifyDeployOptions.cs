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

    /// <summary>
    /// Overrides the image tag for this deploy (key <c>Deployify:ImageTag</c>). Without it, a
    /// deploy that publishes images replaces a configured tag of "latest" (or no tag) with the
    /// commit SHA, like <c>deployify ci</c>, so a rollback can return to the previous image.
    /// </summary>
    public string? ImageTag { get; set; }

    /// <summary>
    /// Publish with "latest" (or no tag) when no commit SHA is available, or when
    /// <see cref="ImageTag"/> asks for "latest" (key <c>Deployify:AllowMutableTag</c>), like
    /// <c>deployify ci --allow-mutable-tag</c>. Off: such a deploy stops, because a rollback
    /// couldn't restore the previous image.
    /// </summary>
    public bool AllowMutableTag { get; set; }

    /// <summary>
    /// Stop instead of deploying a built image by tag when no published digest is recorded for
    /// it (key <c>Deployify:RequireDigest</c>), like <c>deployify ci --require-digest</c>.
    /// Checked when this deploy doesn't publish (<see cref="Publish"/> off): the images must have
    /// been published to this environment, or promoted to it with <c>deployify promote</c>.
    /// </summary>
    public bool RequireDigest { get; set; }

    /// <summary>
    /// Stop unless every built image is pinned by digest and has a cosign signature the
    /// environment's <c>signing</c> settings trust (key <c>Deployify:RequireSignature</c>), like
    /// <c>deployify ci --require-signature</c>. Images this deploy publishes are checked after
    /// publishing; otherwise before planning. Needs cosign.
    /// </summary>
    public bool RequireSignature { get; set; }

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

    /// <summary>
    /// Start a plugin target that deployify-plugins.lock.json has no hash for, with a warning
    /// each time (key <c>Deployify:AllowUnverifiedPlugins</c>), like <c>deployify apply
    /// --allow-unverified</c>. Off: such a plugin stops the deploy. A plugin whose files don't
    /// match its locked hash stops it either way.
    /// </summary>
    public bool AllowUnverifiedPlugins { get; set; }
}
