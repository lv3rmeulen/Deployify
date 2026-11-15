using System.Collections.Generic;
using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Target.Compose;
using Spectre.Console;
using static Deployify.Commands.Init.TargetInitFlows;

namespace Deployify.Commands.Init;

/// <summary>Docker Compose.</summary>
internal sealed class ComposeInitFlow : ITargetInitFlow
{
    private readonly Option<string> _project = new("--compose-project")
    {
        Description = "Docker Compose project name.",
        DefaultValueFactory = _ => "deployify"
    };

    private readonly Option<string> _restart = new("--compose-restart")
    {
        Description = "Restart policy.",
        DefaultValueFactory = _ => "unless-stopped"
    };

    public IReadOnlyList<DeploymentTargetType> Targets { get; } = [DeploymentTargetType.Compose];

    public IEnumerable<Option> Options => [_project, _restart];

    public Dictionary<string, object?> FromOptions(DeploymentTargetType target, ParseResult parseResult, InitContext context)
        => Config(parseResult.GetValue(_project)!, parseResult.GetValue(_restart));

    public async Task<Dictionary<string, object?>> PromptAsync(DeploymentTargetType target, InitContext context, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[dim]Docker Compose settings:[/]");
        var project = await ConsolePrompts.AskAsync("Project name:", context.EnvironmentName, ct);
        var restart = await ConsolePrompts.AskAsync("Restart policy:", "unless-stopped", ct);
        return Config(project, restart);
    }

    private static Dictionary<string, object?> Config(string project, string? restart)
        => new()
        {
            ["compose"] = new ComposeTargetConfig
            {
                ProjectName = project,
                RestartPolicy = OrDefault(restart, "unless-stopped"),
                GenerateProfiles = true,
                GenerateEnvFile = true,
                GenerateMergedFile = true
            }
        };
}
