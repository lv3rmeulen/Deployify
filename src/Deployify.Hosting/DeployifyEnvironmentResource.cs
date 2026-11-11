using Aspire.Hosting.ApplicationModel;

namespace Deployify.Hosting;

/// <summary>
/// A Deployify deployment in the app model. It only takes part in <c>aspire deploy</c>, where it
/// adds one pipeline step; it never runs and is left out of the published manifest.
/// </summary>
public sealed class DeployifyEnvironmentResource : Resource
{
    public DeployifyEnvironmentResource(string name, string appHostDirectory, DeployifyDeployOptions options)
        : base(name)
    {
        AppHostDirectory = appHostDirectory;
        Options = options;
    }

    /// <summary>The AppHost project directory.</summary>
    public string AppHostDirectory { get; }

    public DeployifyDeployOptions Options { get; }

    /// <summary>Changes Deployify's own services before they are built. A seam for tests.</summary>
    internal System.Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? ConfigureServices { get; set; }

    /// <summary>The pipeline step that deploys this resource, e.g. <c>deployify-deploy</c>.</summary>
    public string StepName => $"{Name}-deploy";
}
