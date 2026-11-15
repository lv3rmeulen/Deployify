using System.Collections.Generic;
using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Target.AzureContainerApps;
using Spectre.Console;
using static Deployify.Commands.Init.TargetInitFlows;

namespace Deployify.Commands.Init;

/// <summary>Azure Container Apps.</summary>
internal sealed class AzureContainerAppsInitFlow : ITargetInitFlow
{
    private readonly Option<string?> _subscription = new("--aca-subscription")
    {
        Description = "Azure subscription ID."
    };

    private readonly Option<string> _resourceGroup = new("--aca-resource-group")
    {
        Description = "Azure resource group.",
        DefaultValueFactory = _ => "deployify-rg"
    };

    private readonly Option<string> _location = new("--aca-location")
    {
        Description = "Azure region.",
        DefaultValueFactory = _ => "eastus"
    };

    private readonly Option<string> _environment = new("--aca-environment")
    {
        Description = "Container Apps environment name.",
        DefaultValueFactory = _ => "deployify-env"
    };

    private readonly Option<string?> _acr = new("--aca-acr")
    {
        Description = "Azure Container Registry login server."
    };

    public IReadOnlyList<DeploymentTargetType> Targets { get; } = [DeploymentTargetType.AzureContainerApps];

    public IEnumerable<Option> Options => [_subscription, _resourceGroup, _location, _environment, _acr];

    public Dictionary<string, object?> FromOptions(DeploymentTargetType target, ParseResult parseResult, InitContext context)
        => Config(
            parseResult.GetValue(_subscription), parseResult.GetValue(_resourceGroup), parseResult.GetValue(_location),
            parseResult.GetValue(_environment), parseResult.GetValue(_acr));

    public async Task<Dictionary<string, object?>> PromptAsync(DeploymentTargetType target, InitContext context, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[dim]Azure Container Apps settings:[/]");
        var subscription = await ConsolePrompts.AskAsync("Subscription ID:", string.Empty, ct);
        var resourceGroup = await ConsolePrompts.AskAsync("Resource group:", "deployify-rg", ct);
        var location = await ConsolePrompts.AskAsync("Location:", "eastus", ct);
        var environment = await ConsolePrompts.AskAsync("Container Apps environment name:", "deployify-env", ct);
        var acr = await ConsolePrompts.AskAsync("ACR login server [dim](blank to skip)[/]:", string.Empty, ct);
        return Config(subscription, resourceGroup, location, environment, acr);
    }

    private static Dictionary<string, object?> Config(
        string? subscription, string? resourceGroup, string? location, string? environment, string? acr)
        => new()
        {
            ["aca"] = new AcaTargetConfig
            {
                SubscriptionId = OrDefault(subscription, string.Empty),
                ResourceGroup = OrDefault(resourceGroup, "deployify-rg"),
                Location = OrDefault(location, "eastus"),
                EnvironmentName = OrDefault(environment, "deployify-env"),
                AcrLoginServer = OrNull(acr)
            }
        };
}
