using System;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Deployify.Hosting;

namespace Aspire.Hosting;

public static class DeployifyBuilderExtensions
{
    /// <summary>
    /// Deploys this app with Deployify when you run <c>aspire deploy</c>. Adds one pipeline step
    /// (<c>{name}-deploy</c>, required by <c>deploy</c>) that reads the manifest Aspire publishes,
    /// then plans, publishes images, and applies to the environment in deployify-state.json,
    /// exactly as <c>deployify apply</c> would. Run <c>deployify init</c> first.
    /// </summary>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">Resource name; also names the step.</param>
    /// <param name="configure">Settings; configuration under <c>Deployify:</c> overrides them per run.</param>
    public static IResourceBuilder<DeployifyEnvironmentResource> AddDeployify(
        this IDistributedApplicationBuilder builder,
        string name = "deployify",
        Action<DeployifyDeployOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var options = new DeployifyDeployOptions();
        configure?.Invoke(options);
        var resource = new DeployifyEnvironmentResource(name, builder.AppHostDirectory, options);

        // Nothing to run locally: keep it off the dashboard.
        if (builder.ExecutionContext.IsRunMode)
        {
            return builder.CreateResourceBuilder(resource);
        }

        // Aspire only writes the manifest when it has an output path. Pin the folder it would
        // use anyway, so the manifest lands where this step looks for it.
        if (string.IsNullOrEmpty(builder.Configuration["Pipeline:OutputPath"]))
        {
            builder.Configuration["Pipeline:OutputPath"] = DeployifyDeployStep.DefaultOutputPath(builder.AppHostDirectory);
        }

        return builder.AddResource(resource)
            .ExcludeFromManifest()
            .WithPipelineStepFactory(_ => new PipelineStep
            {
                Name = resource.StepName,
                Description = "Deploys the app with Deployify: plan, publish images, apply.",
                Action = context => DeployifyDeployStep.RunAsync(context, resource),
                DependsOnSteps = [DeployifyDeployStep.PublishManifestStep],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                Resource = resource
            });
    }
}
