using System.Collections.Generic;
using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Target.CloudRun;
using Spectre.Console;
using static Deployify.Commands.Init.TargetInitFlows;

namespace Deployify.Commands.Init;

/// <summary>Google Cloud Run.</summary>
internal sealed class CloudRunInitFlow : ITargetInitFlow
{
    private readonly Option<string?> _project = new("--cloudrun-project")
    {
        Description = "Google Cloud project ID."
    };

    private readonly Option<string> _region = new("--cloudrun-region")
    {
        Description = "Cloud Run region.",
        DefaultValueFactory = _ => "us-central1"
    };

    private readonly Option<string?> _serviceAccount = new("--cloudrun-service-account")
    {
        Description = "Service account the services run as (blank: the default compute service account)."
    };

    public IReadOnlyList<DeploymentTargetType> Targets { get; } = [DeploymentTargetType.CloudRun];

    public IEnumerable<Option> Options => [_project, _region, _serviceAccount];

    public Dictionary<string, object?> FromOptions(DeploymentTargetType target, ParseResult parseResult, InitContext context)
        => Config(parseResult.GetValue(_project), parseResult.GetValue(_region), parseResult.GetValue(_serviceAccount));

    public async Task<Dictionary<string, object?>> PromptAsync(DeploymentTargetType target, InitContext context, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[dim]Google Cloud Run settings:[/]");
        var project = await ConsolePrompts.AskAsync("Project ID:", string.Empty, ct);
        var region = await ConsolePrompts.AskAsync("Region:", "us-central1", ct);
        var serviceAccount = await ConsolePrompts.AskAsync("Service account [dim](blank for the default)[/]:", string.Empty, ct);
        return Config(project, region, serviceAccount);
    }

    private static Dictionary<string, object?> Config(string? project, string? region, string? serviceAccount)
        => new()
        {
            ["cloudrun"] = new CloudRunTargetConfig
            {
                ProjectId = OrDefault(project, string.Empty),
                Region = OrDefault(region, "us-central1"),
                ServiceAccount = OrNull(serviceAccount)
            }
        };
}
