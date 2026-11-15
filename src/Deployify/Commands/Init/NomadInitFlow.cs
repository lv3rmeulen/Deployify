using System.Collections.Generic;
using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Target.Nomad;
using Spectre.Console;
using static Deployify.Commands.Init.TargetInitFlows;

namespace Deployify.Commands.Init;

/// <summary>HashiCorp Nomad.</summary>
internal sealed class NomadInitFlow : ITargetInitFlow
{
    private readonly Option<string> _address = new("--nomad-address")
    {
        Description = "Nomad cluster address.",
        DefaultValueFactory = _ => "http://localhost:4646"
    };

    private readonly Option<string?> _token = new("--nomad-token")
    {
        Description = "Nomad ACL token as env:NAME. Default: NOMAD_TOKEN at run time."
    };

    private readonly Option<string> _namespace = new("--nomad-namespace")
    {
        Description = "Nomad namespace.",
        DefaultValueFactory = _ => "default"
    };

    private readonly Option<string> _datacenter = new("--nomad-datacenter")
    {
        Description = "Nomad datacenter.",
        DefaultValueFactory = _ => "dc1"
    };

    private readonly Option<NomadDeploymentMode> _mode = new("--nomad-mode")
    {
        Description = "Nomad deployment mode: Docker (default) | RawExec.",
        DefaultValueFactory = _ => NomadDeploymentMode.Docker
    };

    private readonly Option<string?> _registryUser = new("--nomad-registry-username")
    {
        Description = "Docker registry username for private image pulls."
    };

    private readonly Option<string?> _registryPassword = new("--nomad-registry-password")
    {
        Description = "Docker registry password or access token as env:NAME. Default: DEPLOYIFY_REGISTRY_PASSWORD at run time."
    };

    private readonly Option<bool> _registerServices = new("--nomad-register-services")
    {
        Description = "Register services in Nomad/Consul service catalog.",
        DefaultValueFactory = _ => true
    };

    private readonly Option<string> _serviceProvider = new("--nomad-service-provider")
    {
        Description = "Service registration provider: nomad (default) | consul.",
        DefaultValueFactory = _ => "nomad"
    };

    private readonly Option<string> _healthCheckPath = new("--nomad-health-check-path")
    {
        Description = "HTTP path for service health checks.",
        DefaultValueFactory = _ => "/health"
    };

    public IReadOnlyList<DeploymentTargetType> Targets { get; } = [DeploymentTargetType.Nomad];

    public IEnumerable<Option> Options =>
    [
        _address, _token, _namespace, _datacenter, _mode, _registryUser, _registryPassword,
        _registerServices, _serviceProvider, _healthCheckPath
    ];

    public IEnumerable<SecretFlag> SecretFlags =>
    [
        new(_token, "NOMAD_TOKEN", ReadWhenOmitted: true),
        new(_registryPassword, "DEPLOYIFY_REGISTRY_PASSWORD", ReadWhenOmitted: true)
    ];

    public Dictionary<string, object?> FromOptions(DeploymentTargetType target, ParseResult parseResult, InitContext context)
        => Config(new NomadTargetConfig
        {
            Address = parseResult.GetValue(_address)!,
            Token = OrNull(parseResult.GetValue(_token)),
            Namespace = parseResult.GetValue(_namespace)!,
            Datacenters = [parseResult.GetValue(_datacenter)!],
            DeploymentMode = parseResult.GetValue(_mode),
            RegistryUsername = OrNull(parseResult.GetValue(_registryUser)),
            RegistryPassword = OrNull(parseResult.GetValue(_registryPassword)),
            RegisterServices = parseResult.GetValue(_registerServices),
            ServiceProvider = parseResult.GetValue(_serviceProvider)!,
            HealthCheckPath = parseResult.GetValue(_healthCheckPath)!
        });

    public async Task<Dictionary<string, object?>> PromptAsync(DeploymentTargetType target, InitContext context, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[dim]Nomad settings:[/]");
        var config = new NomadTargetConfig
        {
            Address = await ConsolePrompts.AskAsync("Nomad address:", "http://localhost:4646", ct),
            Namespace = await ConsolePrompts.AskAsync("Namespace:", "default", ct),
            Datacenters = [await ConsolePrompts.AskAsync("Datacenter:", "dc1", ct)],
            Token = OrNull(await SecretPrompts.AskSourceAsync("Nomad ACL token", "NOMAD_TOKEN", context.Directory, ct)),
            DeploymentMode = await ConsolePrompts.PromptAsync(
                new SelectionPrompt<NomadDeploymentMode>()
                    .Title("Deployment mode:")
                    .AddChoices(NomadDeploymentMode.Docker, NomadDeploymentMode.RawExec), ct)
        };

        if (await ConsolePrompts.ConfirmAsync("Authenticate with a private Docker registry?", defaultValue: false, ct))
        {
            config.RegistryUsername = OrNull(await ConsolePrompts.AskAsync<string>("Registry username:", ct));
            config.RegistryPassword = OrNull(await SecretPrompts.AskSourceAsync(
                "registry password", "DEPLOYIFY_REGISTRY_PASSWORD", context.Directory, ct));
        }

        config.RegisterServices = await ConsolePrompts.ConfirmAsync("Register services in service catalog?", defaultValue: true, ct);
        config.ServiceProvider = "nomad";
        config.HealthCheckPath = "/health";
        if (config.RegisterServices)
        {
            config.ServiceProvider = await ConsolePrompts.PromptAsync(
                new SelectionPrompt<string>()
                    .Title("Service provider:")
                    .AddChoices("nomad", "consul"), ct);
            config.HealthCheckPath = await ConsolePrompts.AskAsync("Health check path:", "/health", ct);
        }

        return Config(config);
    }

    private static Dictionary<string, object?> Config(NomadTargetConfig nomad) => new() { ["nomad"] = nomad };
}
