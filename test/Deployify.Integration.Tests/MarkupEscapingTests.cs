using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using Deployify.Commands;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Shouldly;
using Spectre.Console;
using Xunit;

namespace Deployify.Integration.Tests;

/// <summary>Swaps the global AnsiConsole for a writer, so these tests mustn't run alongside each other.</summary>
[CollectionDefinition(nameof(ConsoleOutput), DisableParallelization = true)]
public sealed class ConsoleOutput;

/// <summary>
/// Names, paths and messages can contain '[' and ']' (an environment called "dev[1]", an
/// IPv6 host, a Windows folder). Printed as markup without escaping, Spectre throws or eats
/// them; these check they come out literally.
/// </summary>
[Collection(nameof(ConsoleOutput))]
public sealed class MarkupEscapingTests : IDisposable
{
    private readonly IAnsiConsole _original = AnsiConsole.Console;
    private readonly StringWriter _out = new();

    public MarkupEscapingTests()
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(_out), Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors
        });
        console.Profile.Width = 200; // a redirected writer reports no width, which wraps everything away
        AnsiConsole.Console = console;
    }

    public void Dispose() => AnsiConsole.Console = _original;

    private static RootCommand Root() => new("t") { EnvCommand.Build() };

    private static async Task WriteStateAsync(string dir)
        => await new StateManager().SaveFileAsync(new DeployifyStateFile
        {
            ActiveEnvironment = "dev[1]",
            Environments =
            {
                ["dev[1]"] = Harness.NomadConfig("http://[::1]:4646"),
                ["prod[eu]"] = Harness.NomadConfig("http://nomad.prod:4646")
            }
        }, dir, TestContext.Current.CancellationToken);

    [Fact]
    public async Task EnvList_PrintsBracketedNamesLiterally()
    {
        using var dir = new TempDir();
        await WriteStateAsync(dir.Path);

        var code = await Root().InvokeAsync(["env", "list", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        code.ShouldBe(0);
        _out.ToString().ShouldContain("dev[1]");
        _out.ToString().ShouldContain("prod[eu]");
    }

    [Fact]
    public async Task EnvUse_PrintsBracketedNamesLiterally()
    {
        using var dir = new TempDir();
        await WriteStateAsync(dir.Path);

        var code = await Root().InvokeAsync(["env", "use", "prod[eu]", "--project-path", dir.Path],
            TestContext.Current.CancellationToken);

        code.ShouldBe(0);
        _out.ToString().ShouldContain("dev[1]");
        _out.ToString().ShouldContain("prod[eu]");
    }

    [Fact]
    public void PlanOutput_ListsNamesAndReasonsLiterally()
    {
        var plan = new PlanResult
        {
            ToCreate = ["api[v2]"],
            Unknown = [new PlanUnknown("web", "kubectl diff failed: [error] boom")]
        };

        PlanOutput.PrintList("green", "+", "to create", plan.ToCreate);
        PlanOutput.PrintUnknown(plan);
        AnsiConsole.MarkupLine(PlanOutput.Summary(plan));

        var text = _out.ToString();
        text.ShouldContain("+ 1 to create: api[v2]");
        text.ShouldContain("kubectl diff failed: [error] boom");
        text.ShouldContain("Plan: 1 to create, 0 to update, 0 to delete, 1 could not be checked.");
    }

    [Fact]
    public void PlanSummary_OmitsUnknownWhenThereIsNone()
        => PlanOutput.Summary(new PlanResult { ToUpdate = ["api"] })
            .ShouldBe("Plan: [green]0 to create[/], [yellow]1 to update[/], [red]0 to delete[/].");
}
