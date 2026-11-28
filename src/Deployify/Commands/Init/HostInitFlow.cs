using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Target.Host;
using Spectre.Console;
using static Deployify.Commands.Init.TargetInitFlows;

namespace Deployify.Commands.Init;

/// <summary>Plain machines: systemd units on Linux over SSH, Windows services over PowerShell remoting.</summary>
internal sealed class HostInitFlow : ITargetInitFlow
{
    private readonly Option<string?> _hosts = new("--host-hosts")
    {
        Description = "Comma-separated machines to deploy to (host names or SSH aliases). Default: this machine."
    };

    private readonly Option<HostOs?> _os = new("--host-os")
    {
        Description = "Operating system of the hosts: Linux | Windows. Default: Linux, or this machine's OS without --host-hosts."
    };

    private readonly Option<HostTransport> _transport = new("--host-transport")
    {
        Description = "Windows hosts: how to reach them, WinRm | Ssh.",
        DefaultValueFactory = _ => HostTransport.WinRm
    };

    private readonly Option<string?> _user = new("--host-user")
    {
        Description = "User to connect as; on Linux the services run as this user too. WinRM reads the password from HOST_PASSWORD."
    };

    private readonly Option<string?> _root = new("--host-root")
    {
        Description = @"Folder on the hosts for the services and their releases. Default: /opt/deployify or C:\deployify."
    };

    public IReadOnlyList<DeploymentTargetType> Targets { get; } = [DeploymentTargetType.Host];

    public IEnumerable<Option> Options => [_hosts, _os, _transport, _user, _root];

    public Dictionary<string, object?> FromOptions(DeploymentTargetType target, ParseResult parseResult, InitContext context)
        => Config(SplitList(parseResult.GetValue(_hosts)), parseResult.GetValue(_os), parseResult.GetValue(_transport),
            parseResult.GetValue(_user), parseResult.GetValue(_root));

    public async Task<Dictionary<string, object?>> PromptAsync(DeploymentTargetType target, InitContext context, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[dim]Host settings:[/]");
        var hosts = SplitList(await ConsolePrompts.AskAsync("Hosts, comma-separated [dim](blank = this machine)[/]:", string.Empty, ct));
        var os = hosts.Count == 0
            ? OperatingSystem.IsWindows() ? HostOs.Windows : HostOs.Linux
            : await ConsolePrompts.PromptAsync(
                new SelectionPrompt<HostOs>().Title("The hosts run:").AddChoices(HostOs.Linux, HostOs.Windows), ct);

        var transport = HostTransport.WinRm;
        string? user = null;
        if (hosts.Count > 0)
        {
            if (os == HostOs.Windows)
            {
                transport = await ConsolePrompts.PromptAsync(
                    new SelectionPrompt<HostTransport>()
                        .Title("Connect with PowerShell remoting over:")
                        .AddChoices(HostTransport.WinRm, HostTransport.Ssh), ct);
            }

            user = await ConsolePrompts.AskAsync(
                os == HostOs.Windows
                    ? "User [dim](blank = current Windows identity / SSH user)[/]:"
                    : "SSH user, which the services also run as [dim](blank = your SSH default)[/]:",
                string.Empty, ct);
        }

        var root = await ConsolePrompts.AskAsync("Folder on the hosts:", os == HostOs.Windows ? @"C:\deployify" : "/opt/deployify", ct);
        AnsiConsole.MarkupLine(os == HostOs.Windows
            ? "[dim]Projects are published self-contained for win-x64 with 'deployify publish' and run as Windows services, so " +
              "each must use Microsoft.Extensions.Hosting.WindowsServices. A WinRM password is read from HOST_PASSWORD.[/]"
            : "[dim]Projects are published self-contained for linux-x64 with 'deployify publish' and run as systemd units. The " +
              "SSH user needs passwordless sudo for systemctl and journalctl, and must own the folder on the hosts.[/]");
        return Config(hosts, hosts.Count == 0 ? null : os, transport, user, root);
    }

    private static Dictionary<string, object?> Config(List<string> hosts, HostOs? os, HostTransport transport, string? user, string? root)
    {
        var config = new HostTargetConfig
        {
            Hosts = hosts,
            Os = os,
            Transport = transport,
            UserName = OrNull(user)
        };

        // Keep the OS default unless a different folder was given.
        config.Root = string.IsNullOrWhiteSpace(root) || root == config.EffectiveRoot ? null : root;
        return new Dictionary<string, object?> { ["host"] = config };
    }
}
