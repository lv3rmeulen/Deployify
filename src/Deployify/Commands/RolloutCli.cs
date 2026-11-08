using System;
using System.CommandLine;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Rollouts;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// Options and console output for health-gated rollouts, shared by <c>apply</c> and <c>ci</c>.
/// Targets that implement <see cref="IRolloutTarget"/> deploy through <see cref="RolloutEngine"/>;
/// the others keep the plain apply.
/// </summary>
internal static class RolloutCli
{
    public static readonly Option<bool> NoHealthGate = new("--no-health-gate")
    {
        Description = "Deploy without waiting for services to become healthy and without automatic rollback.",
        DefaultValueFactory = _ => false
    };

    public static readonly Option<bool> NoRollback = new("--no-rollback")
    {
        Description = "Wait for health, but leave a failed deployment in place instead of rolling it back.",
        DefaultValueFactory = _ => false
    };

    public static readonly Option<int> HealthTimeout = new("--health-timeout")
    {
        Description = "Seconds each service may take to become healthy before the rollout fails (default: 600).",
        DefaultValueFactory = _ => 600
    };

    public static readonly Option<int> RollbackTimeout = new("--rollback-timeout")
    {
        Description = "Seconds the whole rollback may take (default: 900).",
        DefaultValueFactory = _ => 900
    };

    public static void AddTo(Command cmd)
    {
        cmd.Options.Add(NoHealthGate);
        cmd.Options.Add(NoRollback);
        cmd.Options.Add(HealthTimeout);
        cmd.Options.Add(RollbackTimeout);
    }

    /// <summary>
    /// The rollout capability of <paramref name="target"/>, or null when it has none or the
    /// health gate is switched off. Says so on the console when the target can't do it.
    /// </summary>
    public static IRolloutTarget? Resolve(ParseResult parseResult, IDeploymentTarget target)
    {
        if (parseResult.GetValue(NoHealthGate))
        {
            return null;
        }

        if (target is IRolloutTarget rollout)
        {
            return rollout;
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[dim]{target.DisplayName} has no health-gated rollout yet: deploying without health checks or automatic rollback.[/]");
        return null;
    }

    public static RolloutOptions ReadOptions(ParseResult parseResult) => new()
    {
        HealthTimeout = TimeSpan.FromSeconds(Math.Max(1, parseResult.GetValue(HealthTimeout))),
        RollbackTimeout = TimeSpan.FromSeconds(Math.Max(1, parseResult.GetValue(RollbackTimeout))),
        AutoRollback = !parseResult.GetValue(NoRollback)
    };

    public static async Task<RolloutResult> RunAsync(
        ResolvedContext resolved, IRolloutTarget rollout, AspireManifest manifest, string outputDir,
        RolloutOptions options, CancellationToken ct)
    {
        var request = new RolloutRequest
        {
            Target = resolved.Target,
            Rollout = rollout,
            Manifest = manifest,
            Config = resolved.Config,
            EnvironmentName = resolved.EnvName,
            OutputDirectory = outputDir,
            StateDirectory = resolved.Directory,
            Options = options,
            OnProgress = Print
        };

        RolloutResult result;
        try
        {
            result = await new RolloutEngine().RunAsync(request, ct);
        }
        catch (InvalidOperationException ex)
        {
            // Raised before anything changed, e.g. another rollout holds the lock.
            AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {ex.Message}");
            return new RolloutResult { Outcome = RolloutOutcome.Failed, Reason = ex.Message };
        }

        PrintSummary(result, resolved.EnvName);
        return result;
    }

    internal static void Print(RolloutProgress p)
    {
        var who = p.Service is null ? "" : $"{p.Service}: ";
        switch (p.Kind)
        {
            case RolloutProgressKind.Success:
                AnsiConsole.MarkupLineInterpolated($"  [green]✓[/] {who}{p.Message}");
                break;
            case RolloutProgressKind.Warning:
                AnsiConsole.MarkupLineInterpolated($"  [yellow]![/] {who}{p.Message}");
                break;
            case RolloutProgressKind.Error:
                AnsiConsole.MarkupLineInterpolated($"  [red]✗[/] {who}{p.Message}");
                break;
            default:
                if (p.Service is null)
                {
                    AnsiConsole.MarkupLineInterpolated($"[bold]{p.Message}[/]");
                }
                else
                {
                    AnsiConsole.MarkupLineInterpolated($"  [dim]{who}{p.Message}[/]");
                }

                break;
        }
    }

    private static void PrintSummary(RolloutResult result, string envName)
    {
        AnsiConsole.WriteLine();
        switch (result.Outcome)
        {
            case RolloutOutcome.Succeeded:
                AnsiConsole.MarkupLineInterpolated(
                    $"[green]Rollout complete.[/] {result.Services.Count} service(s) healthy in [cyan]{envName}[/].");
                break;
            case RolloutOutcome.RolledBack:
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]Rollout failed and was rolled back.[/] {result.Reason ?? ""}");
                break;
            case RolloutOutcome.RollbackFailed:
                var stuck = string.Join(", ", result.Services
                    .Where(s => s.State == ServiceRolloutState.RollbackFailed).Select(s => s.Name));
                AnsiConsole.MarkupLineInterpolated(
                    $"[red]Rollout failed and the rollback did not complete.[/] Check these services by hand: {stuck}");
                AnsiConsole.MarkupLineInterpolated($"[dim]Cause: {result.Reason ?? ""}[/]");
                break;
            default:
                AnsiConsole.MarkupLineInterpolated($"[red]Rollout failed.[/] {result.Reason ?? ""}");
                break;
        }

        if (!string.IsNullOrEmpty(result.JournalPath))
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]Journal: {result.JournalPath}[/]");
        }
    }
}
