// The per-run context moved to Deployify.Core so the Aspire integration shares it;
// the commands keep calling it by its old name.
global using ResolvedContext = Deployify.Core.Deployments.DeploymentSession;
