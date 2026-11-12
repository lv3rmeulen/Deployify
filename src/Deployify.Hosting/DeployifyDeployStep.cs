using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Aspire.Hosting;
using Aspire.Hosting.Pipelines;
using Deployify.Core.Deployments;
using Deployify.Core.Models;
using Deployify.Core.Rollouts;
using Deployify.Targets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deployify.Hosting;

/// <summary>The pipeline step <c>aspire deploy</c> runs for a <see cref="DeployifyEnvironmentResource"/>.</summary>
internal static class DeployifyDeployStep
{
    /// <summary>Aspire's step that writes the app model as aspire-manifest.json.</summary>
    public const string PublishManifestStep = "publish-manifest";

    public const string ManifestFileName = "aspire-manifest.json";

    public static async Task RunAsync(PipelineStepContext context, DeployifyEnvironmentResource resource)
    {
        var ct = context.CancellationToken;
        var configuration = context.Services.GetRequiredService<IConfiguration>();
        var pipelineOptions = context.Services.GetRequiredService<IOptions<PipelineOptions>>().Value;
        var options = Resolve(resource.Options, configuration.GetSection("Deployify"));
        var reporter = new PipelineDeploymentReporter(context.ReportingStep);

        // Deployify gets its own container; its logs go through the AppHost's logger factory,
        // which shows them under this step.
        var collection = new ServiceCollection()
            .AddSingleton(context.Services.GetRequiredService<ILoggerFactory>())
            .AddSingleton(typeof(ILogger<>), typeof(Logger<>))
            .AddDeployify();
        resource.ConfigureServices?.Invoke(collection);
        await using var services = collection.BuildServiceProvider();

        await using var session = await DeploymentSessionResolver.ResolveAsync(services, new DeploymentSessionRequest
        {
            ProjectPath = options.ProjectPath ?? resource.AppHostDirectory,
            Environment = options.Environment,
            Target = options.Target,
            LockFor = "aspire-deploy",
            ManifestPath = GetManifestPath(pipelineOptions.OutputPath, resource.AppHostDirectory),
            Reporter = reporter
        }, ct);

        if (options.ImageTag is { Length: > 0 } tag)
        {
            session.Config.ImageTag = tag;
        }

        reporter.Info($"Environment: {session.EnvName}  Target: {session.Target.DisplayName} ({session.DisplayAddress})");

        var manifest = await session.LoadManifestAsync(ct)
            ?? throw new InvalidOperationException("Deployify could not load the Aspire manifest; see the errors above.");

        var interaction = context.Services.GetService<IInteractionService>();
        var autoApprove = options.AutoApprove || pipelineOptions.SkipConfirmation;
        var result = await new DeploymentRunner(session).RunAsync(manifest, new DeploymentRunOptions
        {
            Publish = options.Publish,
            HealthGate = options.HealthGate,
            Strict = options.Strict,
            Rollout = new RolloutOptions
            {
                AutoRollback = options.AutoRollback,
                HealthTimeout = options.HealthTimeout ?? new RolloutOptions().HealthTimeout
            },
            Approve = autoApprove ? null : (plan, token) => ApproveAsync(interaction, session, plan, token)
        }, ct);

        context.Summary.Add($"Deployify ({session.EnvName})", result.Summary);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Deployify deploy to '{session.EnvName}' failed: {result.Summary}");
        }

        await context.ReportingStep.CompleteAsync(
            $"Deployed {session.EnvName} to {session.Target.DisplayName}: {result.Summary}",
            result.Outcome == DeploymentRunOutcome.Declined ? CompletionState.CompletedWithWarning : CompletionState.Completed,
            ct);
    }

    /// <summary>
    /// Where Aspire's publish-manifest step writes the manifest: the output path when it names a
    /// .json file, otherwise aspire-manifest.json inside it.
    /// </summary>
    internal static string GetManifestPath(string? outputPath, string appHostDirectory)
    {
        var output = string.IsNullOrEmpty(outputPath) ? DefaultOutputPath(appHostDirectory) : outputPath;
        return output.EndsWith(".json", StringComparison.Ordinal) ? output : Path.Combine(output, ManifestFileName);
    }

    /// <summary>The folder Aspire uses when no --output-path is given.</summary>
    internal static string DefaultOutputPath(string appHostDirectory) => Path.Combine(appHostDirectory, "aspire-output");

    private static async Task<bool> ApproveAsync(
        IInteractionService? interaction, DeploymentSession session, PlanResult plan, CancellationToken ct)
    {
        if (interaction is not { IsAvailable: true })
        {
            throw new InvalidOperationException(
                $"Deploying to '{session.EnvName}' needs confirmation, but 'aspire deploy' can't ask here. " +
                "Set Deployify:AutoApprove=true (environment variable Deployify__AutoApprove=true) or " +
                "AutoApprove in AddDeployify to deploy without asking.");
        }

        var result = await interaction.PromptConfirmationAsync(
            $"Deploy to {session.EnvName}?",
            $"Deployify will deploy to {session.Target.DisplayName} ({session.DisplayAddress}). " +
            $"Plan: {DeploymentRunner.Describe(plan)}.",
            new MessageBoxInteractionOptions { PrimaryButtonText = "Deploy", ShowSecondaryButton = true },
            ct);
        return !result.Canceled && result.Data;
    }

    /// <summary>Code settings, overridden by whatever configuration under Deployify: sets.</summary>
    internal static DeployifyDeployOptions Resolve(DeployifyDeployOptions code, IConfiguration section)
    {
        return new DeployifyDeployOptions
        {
            Environment = Text(section["Environment"]) ?? code.Environment,
            ProjectPath = Text(section["ProjectPath"]) ?? code.ProjectPath,
            Target = Text(section["Target"]) is { } target
                ? DeploymentTargetType.TryParse(target, out var t)
                    ? t
                    : throw new InvalidOperationException(
                        $"Deployify:Target '{target}' is not a valid target ID. Built in: " +
                        $"{string.Join(", ", DeploymentTargetType.BuiltIn)}; plugin targets use the ID their plugin declares.")
                : code.Target,
            Publish = Bool(section, "Publish") ?? code.Publish,
            ImageTag = Text(section["ImageTag"]) ?? code.ImageTag,
            HealthGate = Bool(section, "HealthGate") ?? code.HealthGate,
            AutoRollback = Bool(section, "AutoRollback") ?? code.AutoRollback,
            HealthTimeout = Text(section["HealthTimeoutSeconds"]) is { } seconds
                ? int.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s > 0
                    ? TimeSpan.FromSeconds(s)
                    : throw new InvalidOperationException($"Deployify:HealthTimeoutSeconds '{seconds}' is not a positive number of seconds.")
                : code.HealthTimeout,
            AutoApprove = Bool(section, "AutoApprove") ?? code.AutoApprove,
            Strict = Bool(section, "Strict") ?? code.Strict
        };

        static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        static bool? Bool(IConfiguration section, string key)
            => Text(section[key]) is not { } value
                ? null
                : bool.TryParse(value, out var b)
                    ? b
                    : throw new InvalidOperationException($"Deployify:{key} '{value}' is not true or false.");
    }
}
