using System.IO;
using Shouldly;
using Spectre.Console;
using Xunit;

namespace Deployify.Integration.Tests;

/// <summary>
/// The CLI's output with no terminal, as on CI runners. The tests use a console whose output
/// reports what Spectre.Console 0.57.2 reports there, width -1, rather than asking the process:
/// run from a terminal, Spectre would read the terminal's real width even for a console that
/// writes to a <see cref="StringWriter"/>.
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
            Out = new NoTerminalOutput(output)
        });
        return (console, output);
    }

    /// <summary>Output that isn't a terminal and has no width, as Spectre sees stdout on a CI runner.</summary>
    private sealed class NoTerminalOutput(TextWriter writer) : IAnsiConsoleOutput
    {
        public TextWriter Writer => writer;

        public bool IsTerminal => false;

        public int Width => -1;

        public int Height => -1;

        public void SetEncoding(System.Text.Encoding encoding)
        {
        }
    }

    [Fact]
    public void WithoutATerminal_SpectreWritesNothingUnconfigured()
    {
        // The bug ConsoleSetup works around. If this starts failing, Spectre fixed it itself.
        var (console, output) = NoTerminal();

        console.WriteLine("plain line");

        output.ToString().ShouldBeEmpty();
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
