using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Integration.Tests;

/// <summary>
/// System.CommandLine 2.0 runs a command by parsing first and then invoking the parse
/// result. This keeps the tests' "root.InvokeAsync(args)" calls short.
/// </summary>
internal static class CommandInvocation
{
    public static Task<int> InvokeAsync(this Command command, string[] args, CancellationToken ct = default)
        => command.Parse(args).InvokeAsync(cancellationToken: ct);

    public static Task<int> InvokeAsync(this Command command, string commandLine, CancellationToken ct = default)
        => command.Parse(commandLine).InvokeAsync(cancellationToken: ct);
}
