// The per-run context moved to Deployify.Core so the Aspire integration shares it;
// the commands keep calling it by its old name.
global using ResolvedContext = Deployify.Core.Deployments.DeploymentSession;

// Capability lookup and async queries on IDeploymentTarget, used throughout the commands.
global using Deployify.Core.Interfaces;
