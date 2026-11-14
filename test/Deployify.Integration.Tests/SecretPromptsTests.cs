using System;
using System.Threading.Tasks;
using Deployify.Commands;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace Deployify.Integration.Tests;

/// <summary>
/// Drives the "where should Deployify get the token?" prompt with keystrokes. The first
/// choice is the default variable, then another variable, then plain text.
/// </summary>
[Collection(nameof(ConsoleOutput))]
public sealed class SecretPromptsTests : IDisposable
{
    private readonly IAnsiConsole _original = AnsiConsole.Console;
    private readonly TestConsole _console = new();
    private readonly TempDir _dir = new();

    public SecretPromptsTests()
    {
        _console.Interactive();
        _console.Profile.Width = 200;
        AnsiConsole.Console = _console;
    }

    public void Dispose()
    {
        AnsiConsole.Console = _original;
        _dir.Dispose();
    }

    private Task<string?> AskAsync(bool storeDefaultReference = false)
        => SecretPrompts.AskSourceAsync("Nomad ACL token", "NOMAD_TOKEN", _dir.Path,
            TestContext.Current.CancellationToken, storeDefaultReference);

    private void Choose(int index)
    {
        for (var i = 0; i < index; i++)
        {
            _console.Input.PushKey(ConsoleKey.DownArrow);
        }

        _console.Input.PushKey(ConsoleKey.Enter);
    }

    [Fact]
    public async Task DefaultVariable_StoresNothing()
    {
        Choose(0);

        (await AskAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task DefaultVariable_CanBeStoredAsAReference()
    {
        Choose(0);

        (await AskAsync(storeDefaultReference: true)).ShouldBe("env:NOMAD_TOKEN");
    }

    [Fact]
    public async Task OtherVariable_StoresAReference()
    {
        Choose(1);
        _console.Input.PushTextWithEnter("PROD_NOMAD_TOKEN");

        (await AskAsync()).ShouldBe("env:PROD_NOMAD_TOKEN");
    }

    [Fact]
    public async Task PlainText_IsOnlyStoredAfterConfirming()
    {
        Choose(2);
        _console.Input.PushTextWithEnter("s3cret");
        _console.Input.PushTextWithEnter("y");

        (await AskAsync()).ShouldBe("s3cret");
        _console.Output.ShouldNotContain("s3cret");
    }

    [Fact]
    public async Task PlainText_Declined_GoesBackToTheChoice()
    {
        Choose(2);
        _console.Input.PushTextWithEnter("s3cret");
        _console.Input.PushTextWithEnter("n");
        Choose(0);

        (await AskAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task PlainText_WithAPastedReference_StoresTheReferenceWithoutAsking()
    {
        Choose(2);
        _console.Input.PushTextWithEnter("env:MY_TOKEN");

        (await AskAsync()).ShouldBe("env:MY_TOKEN");
    }
}
