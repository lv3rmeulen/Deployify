using Deployify.Plugin.Sdk;
using Deployify.Target.DigitalOcean;

return await DeployifyPlugin.RunAsync(plugin => plugin
    .Named("digitalocean")
    .AddTarget<DoDeploymentTarget>(DoDeploymentTarget.Id, "DigitalOcean App Platform", target =>
    {
        target.ConfigKey = DoTargetConfig.Key;
        target.WithConfigSchema<DoTargetConfig>();
    }));
