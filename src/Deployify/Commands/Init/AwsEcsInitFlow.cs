using System.Collections.Generic;
using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Target.AwsEcs;
using Spectre.Console;
using static Deployify.Commands.Init.TargetInitFlows;

namespace Deployify.Commands.Init;

/// <summary>AWS ECS (Fargate).</summary>
internal sealed class AwsEcsInitFlow : ITargetInitFlow
{
    private readonly Option<string> _region = new("--ecs-region")
    {
        Description = "AWS region.",
        DefaultValueFactory = _ => "us-east-1"
    };

    private readonly Option<string> _cluster = new("--ecs-cluster")
    {
        Description = "ECS cluster name.",
        DefaultValueFactory = _ => "deployify"
    };

    private readonly Option<string?> _profile = new("--ecs-profile")
    {
        Description = "AWS CLI profile name."
    };

    private readonly Option<string?> _subnets = new("--ecs-subnets")
    {
        Description = "Comma-separated subnet IDs for Fargate tasks."
    };

    private readonly Option<string?> _securityGroups = new("--ecs-security-groups")
    {
        Description = "Comma-separated security group IDs."
    };

    private readonly Option<string?> _executionRole = new("--ecs-execution-role")
    {
        Description = "ECS task execution role ARN."
    };

    public IReadOnlyList<DeploymentTargetType> Targets { get; } = [DeploymentTargetType.AwsEcs];

    public IEnumerable<Option> Options => [_region, _cluster, _profile, _subnets, _securityGroups, _executionRole];

    public Dictionary<string, object?> FromOptions(DeploymentTargetType target, ParseResult parseResult, InitContext context)
        => Config(
            parseResult.GetValue(_region), parseResult.GetValue(_cluster), parseResult.GetValue(_profile),
            parseResult.GetValue(_subnets), parseResult.GetValue(_securityGroups), parseResult.GetValue(_executionRole));

    public async Task<Dictionary<string, object?>> PromptAsync(DeploymentTargetType target, InitContext context, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[dim]AWS ECS settings:[/]");
        var region = await ConsolePrompts.AskAsync("AWS region:", "us-east-1", ct);
        var cluster = await ConsolePrompts.AskAsync("ECS cluster name:", "deployify", ct);
        var profile = await ConsolePrompts.AskAsync("AWS profile [dim](blank = default)[/]:", string.Empty, ct);
        var subnets = await ConsolePrompts.AskAsync("Subnet IDs [dim](comma-separated, required for Fargate)[/]:", string.Empty, ct);
        var securityGroups = await ConsolePrompts.AskAsync("Security group IDs [dim](comma-separated)[/]:", string.Empty, ct);
        var executionRole = await ConsolePrompts.AskAsync("Execution role ARN [dim](blank = auto-detect)[/]:", string.Empty, ct);
        return Config(region, cluster, profile, subnets, securityGroups, executionRole);
    }

    private static Dictionary<string, object?> Config(
        string? region, string? cluster, string? profile, string? subnets, string? securityGroups, string? executionRole)
        => new()
        {
            ["ecs"] = new EcsTargetConfig
            {
                Region = OrDefault(region, "us-east-1"),
                ClusterName = OrDefault(cluster, "deployify"),
                AwsProfile = OrNull(profile),
                SubnetIds = SplitList(subnets),
                SecurityGroupIds = SplitList(securityGroups),
                ExecutionRoleArn = OrNull(executionRole)
            }
        };
}
