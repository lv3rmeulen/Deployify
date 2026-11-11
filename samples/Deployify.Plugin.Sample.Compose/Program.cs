using Deployify.Plugin.Sdk;
using Deployify.Target.Compose;

// The whole plugin. The target class is the one Deployify ships built in; a third-party plugin
// has its own class implementing IDeploymentTarget (and optionally IRolloutTarget and
// IResourceLifecycle) and is otherwise the same.
return await DeployifyPlugin.RunAsync(plugin => plugin
    .Named("compose-plugin")
    .AddTarget<ComposeDeploymentTarget>("compose-plugin", "Docker Compose (plugin)", target =>
    {
        // Reads the same settings as the built-in target: TargetConfig.compose.
        target.ConfigKey = "compose";
        target.WithConfigSchema<ComposeTargetConfig>();
    }));
