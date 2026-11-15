using System.Collections.Generic;
using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Target.Iis;
using Spectre.Console;
using static Deployify.Commands.Init.TargetInitFlows;

namespace Deployify.Commands.Init;

/// <summary>IIS on a Windows server, reached over PowerShell remoting.</summary>
internal sealed class IisInitFlow : ITargetInitFlow
{
    private const string DefaultSitesRoot = @"C:\inetpub\deployify";

    private readonly Option<string?> _host = new("--iis-host")
    {
        Description = "Windows server running IIS. Default: this machine."
    };

    private readonly Option<IisTransport> _transport = new("--iis-transport")
    {
        Description = "How to reach --iis-host: WinRm | Ssh.",
        DefaultValueFactory = _ => IisTransport.WinRm
    };

    private readonly Option<string?> _user = new("--iis-user")
    {
        Description = "User to connect as. WinRM reads the password from IIS_PASSWORD."
    };

    private readonly Option<string> _sitesRoot = new("--iis-sites-root")
    {
        Description = "Folder on the server for the sites and their releases.",
        DefaultValueFactory = _ => DefaultSitesRoot
    };

    public IReadOnlyList<DeploymentTargetType> Targets { get; } = [DeploymentTargetType.Iis];

    public IEnumerable<Option> Options => [_host, _transport, _user, _sitesRoot];

    public Dictionary<string, object?> FromOptions(DeploymentTargetType target, ParseResult parseResult, InitContext context)
        => Config(parseResult.GetValue(_host), parseResult.GetValue(_transport), parseResult.GetValue(_user), parseResult.GetValue(_sitesRoot));

    public async Task<Dictionary<string, object?>> PromptAsync(DeploymentTargetType target, InitContext context, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[dim]IIS settings:[/]");
        var host = await ConsolePrompts.AskAsync("IIS server [dim](blank = this machine)[/]:", string.Empty, ct);
        var transport = IisTransport.WinRm;
        string? user = null;
        if (!string.IsNullOrWhiteSpace(host))
        {
            transport = await ConsolePrompts.PromptAsync(
                new SelectionPrompt<IisTransport>()
                    .Title("Connect with PowerShell remoting over:")
                    .AddChoices(IisTransport.WinRm, IisTransport.Ssh), ct);
            user = await ConsolePrompts.AskAsync(
                "User [dim](blank = current Windows identity / SSH user)[/]:", string.Empty, ct);
        }

        var sitesRoot = await ConsolePrompts.AskAsync("Sites folder on the server:", DefaultSitesRoot, ct);
        AnsiConsole.MarkupLine("[dim]Projects are published for win-x64 with 'deployify publish'; the server needs IIS and the " +
                               "ASP.NET Core Hosting Bundle. A WinRM password is read from IIS_PASSWORD.[/]");
        return Config(host, transport, user, sitesRoot);
    }

    private static Dictionary<string, object?> Config(string? host, IisTransport transport, string? user, string? sitesRoot)
        => new()
        {
            ["iis"] = new IisTargetConfig
            {
                Host = OrNull(host),
                Transport = transport,
                UserName = OrNull(user),
                SitesRoot = OrDefault(sitesRoot, DefaultSitesRoot)
            }
        };
}
