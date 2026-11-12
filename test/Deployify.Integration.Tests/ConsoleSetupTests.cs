using System.IO;
using Shouldly;
using Spectre.Console;
using Xunit;

namespace Deployify.Integration.Tests;

/// <summary>
/// The CLI's output with no terminal, as on CI runners. A console that writes to a
/// <see cref="StringWriter"/> has no terminal either, so these tests don't depend on how
/// they're run.
/// </summary>
public sealed class ConsoleSetupTests
{
    private static (IAnsiConsole Console, StringWriter Output) NoTerminal()
    {
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(output)
        });
        return (console, output);
    }

    [Fact]
    public void WithoutATerminal_OutputIsWritten()
    {
        var (console, output) = NoTerminal();

        ConsoleSetup.Configure(console, _ => null);
        console.MarkupLine("[green]✓[/] Applied 3 services.");
        console.WriteLine("plain line");

        output.ToString().ShouldBe("✓ Applied 3 services.\nplain line\n", StringCompareShould.IgnoreLineEndings);
    }

    [Fact]
    public void WithoutATerminal_LongLinesAreNotBrokenAt80Columns()
    {
        var (console, output) = NoTerminal();
        var message = new string('x', 150);

        ConsoleSetup.Configure(console, _ => null);
        console.WriteLine(message);

        console.Profile.Width.ShouldBe(ConsoleSetup.DefaultWidth);
        output.ToString().TrimEnd().ShouldBe(message);
    }

    [Theory]
    [InlineData("100", 100)]
    [InlineData("0", ConsoleSetup.DefaultWidth)]
    [InlineData("wide", ConsoleSetup.DefaultWidth)]
    public void WithoutATerminal_ColumnsSetsTheWidth(string columns, int expected)
    {
        var (console, _) = NoTerminal();

        ConsoleSetup.Configure(console, name => name == "COLUMNS" ? columns : null);

        console.Profile.Width.ShouldBe(expected);
    }

    [Fact]
    public void AConsoleThatHasAWidth_IsLeftAlone()
    {
        var (console, _) = NoTerminal();
        console.Profile.Width = 132;

        ConsoleSetup.Configure(console, _ => "100");

        console.Profile.Width.ShouldBe(132);
    }
}
