using System.Collections.Generic;
using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Target.NomadConsul;
using Spectre.Console;
using static Deployify.Commands.Init.TargetInitFlows;

namespace Deployify.Commands.Init;

/// <summary>Nomad with a Consul Connect service mesh.</summary>
internal sealed class NomadConsulInitFlow : ITargetInitFlow
{
    private readonly Option<string> _nomadAddress = new("--nc-nomad-address")
    {
        Description = "Nomad address (Consul mesh target).",
        DefaultValueFactory = _ => "http://localhost:4646"
    };

    private readonly Option<string?> _nomadToken = new("--nc-nomad-token")
    {
        Description = "Nomad ACL token as env:NAME (Consul mesh target). Default: NOMAD_TOKEN at run time."
    };

    private readonly Option<string> _nomadNamespace = new("--nc-nomad-namespace")
    {
        Description = "Nomad namespace (Consul mesh target).",
        DefaultValueFactory = _ => "default"
    };

    private readonly Option<string> _datacenter = new("--nc-datacenter")
    {
        Description = "Nomad/Consul datacenter.",
        DefaultValueFactory = _ => "dc1"
    };

    private readonly Option<string> _consulAddress = new("--nc-consul-address")
    {
        Description = "Consul address.",
        DefaultValueFactory = _ => "http://localhost:8500"
    };

    private readonly Option<string?> _consulToken = new("--nc-consul-token")
    {
        Description = "Consul ACL token as env:NAME. Default: CONSUL_HTTP_TOKEN at run time."
    };

    private readonly Option<bool> _ingressGateway = new("--nc-ingress-gateway")
    {
        Description = "Enable Consul Ingress Gateway for HTTP services.",
        DefaultValueFactory = _ => false
    };

    public IReadOnlyList<DeploymentTargetType> Targets { get; } = [DeploymentTargetType.NomadConsul];

    public IEnumerable<Option> Options =>
        [_nomadAddress, _nomadToken, _nomadNamespace, _datacenter, _consulAddress, _consulToken, _ingressGateway];

    public IEnumerable<SecretFlag> SecretFlags =>
    [
        new(_nomadToken, "NOMAD_TOKEN", ReadWhenOmitted: true),
        new(_consulToken, "CONSUL_HTTP_TOKEN", ReadWhenOmitted: true)
    ];

    public Dictionary<string, object?> FromOptions(DeploymentTargetType target, ParseResult parseResult, InitContext context)
        => Config(
            parseResult.GetValue(_nomadAddress), parseResult.GetValue(_nomadToken), parseResult.GetValue(_nomadNamespace),
            parseResult.GetValue(_datacenter), parseResult.GetValue(_consulAddress), parseResult.GetValue(_consulToken),
            parseResult.GetValue(_ingressGateway));

    public async Task<Dictionary<string, object?>> PromptAsync(DeploymentTargetType target, InitContext context, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[dim]Nomad + Consul Connect settings:[/]");
        var nomadAddress = await ConsolePrompts.AskAsync("Nomad address:", "http://localhost:4646", ct);
        var nomadNamespace = await ConsolePrompts.AskAsync("Nomad namespace:", "default", ct);
        var datacenter = await ConsolePrompts.AskAsync("Datacenter:", "dc1", ct);
        var nomadToken = await SecretPrompts.AskSourceAsync("Nomad ACL token", "NOMAD_TOKEN", context.Directory, ct);
        var consulAddress = await ConsolePrompts.AskAsync("Consul address:", "http://localhost:8500", ct);
        var consulToken = await SecretPrompts.AskSourceAsync("Consul ACL token", "CONSUL_HTTP_TOKEN", context.Directory, ct);
        var ingressGateway = await ConsolePrompts.ConfirmAsync("Enable Ingress Gateway for HTTP services?", defaultValue: false, ct);
        return Config(nomadAddress, nomadToken, nomadNamespace, datacenter, consulAddress, consulToken, ingressGateway);
    }

    private static Dictionary<string, object?> Config(
        string? nomadAddress, string? nomadToken, string? nomadNamespace, string? datacenter,
        string? consulAddress, string? consulToken, bool ingressGateway)
        => new()
        {
            ["nomadconsul"] = new NomadConsulTargetConfig
            {
                NomadAddress = OrDefault(nomadAddress, "http://localhost:4646"),
                NomadToken = OrNull(nomadToken),
                NomadNamespace = OrDefault(nomadNamespace, "default"),
                Datacenters = [OrDefault(datacenter, "dc1")],
                ConsulAddress = OrDefault(consulAddress, "http://localhost:8500"),
                ConsulToken = OrNull(consulToken),
                EnableIngressGateway = ingressGateway
            }
        };
}
