using System;
using System.Collections.Generic;
using System.IO;
using Deployify.Commands;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Shouldly;
using Spectre.Console;
using Xunit;

namespace Deployify.Integration.Tests;

/// <summary>The old and new values <c>diff</c> prints below its table.</summary>
[Collection(nameof(ConsoleOutput))]
public sealed class DiffValuesOutputTests : IDisposable
{
    private readonly IAnsiConsole _original = AnsiConsole.Console;
    private readonly StringWriter _out = new();

    public DiffValuesOutputTests()
    {
        AnsiConsole.Console = PlainConsole.Create(_out, width: 300);
    }

    public void Dispose() => AnsiConsole.Console = _original;

    private static readonly Dictionary<string, SensitiveValue> NoSecrets = [];

    [Fact]
    public void ASingleValue_PrintsOldAndNewOnOneLine()
    {
        DiffCommand.PrintValues(
            [new DiffEntry { ServiceName = "api", Kind = DiffKind.Changed, Field = "instance_count", OldValue = "1", NewValue = "2" }],
            NoSecrets);

        _out.ToString().ShouldContain("api instance_count: 1 → 2");
    }

    [Fact]
    public void OnlyOneSide_PrintsJustThatValue()
    {
        DiffCommand.PrintValues(
        [
            new DiffEntry { ServiceName = "web", Kind = DiffKind.Changed, Field = "files", NewValue = "web/deployment.yaml" },
            new DiffEntry { ServiceName = "old", Kind = DiffKind.Removed, OldValue = "worker" }
        ], NoSecrets);

        var text = _out.ToString();
        text.ShouldContain("web files: web/deployment.yaml");
        text.ShouldContain("old: worker");
        text.ShouldNotContain("→");
    }

    [Fact]
    public void ARenderedDiff_PrintsEveryLineUnderItsService()
    {
        DiffCommand.PrintValues(
        [
            new DiffEntry
            {
                ServiceName = "worker", Kind = DiffKind.Changed, Field = "manifest",
                NewValue = "--- live\n+++ merged\n@@ -1,2 +1,2 @@\n-        image: worker:1\n+        image: worker:2\n         ports: [8080]"
            }
        ], NoSecrets);

        var lines = _out.ToString().Replace("\r\n", "\n").Split('\n');
        lines.ShouldContain("worker manifest");
        lines.ShouldContain("  -        image: worker:1");
        lines.ShouldContain("  +        image: worker:2");
        lines.ShouldContain("           ports: [8080]");
    }

    [Fact]
    public void Secrets_AreMasked()
    {
        DiffCommand.PrintValues(
        [
            new DiffEntry { ServiceName = "api", Kind = DiffKind.Changed, Field = "env.DB", OldValue = "pw=old-secret-1", NewValue = "pw=new-secret-2" }
        ], new Dictionary<string, SensitiveValue>
        {
            ["a"] = new("old-secret-1"),
            ["b"] = new("new-secret-2")
        });

        var text = _out.ToString();
        text.ShouldNotContain("secret-");
        text.ShouldContain($"api env.DB: pw={SensitiveValue.Mask} → pw={SensitiveValue.Mask}");
    }

    private static readonly DiffEntry[] Changes =
    [
        new() { ServiceName = "api", Kind = DiffKind.Changed, Field = "instance_count", OldValue = "1", NewValue = "2" },
        new() { ServiceName = "web", Kind = DiffKind.Added }
    ];

    [Fact]
    public void Text_ShowsTheTableTheValuesAndTheCounts()
    {
        DiffCommand.PrintText(Changes, NoSecrets, summary: false);

        var text = _out.ToString();
        text.ShouldContain("~ api");
        text.ShouldContain("+ web");
        text.ShouldContain("api instance_count: 1 → 2");
        text.ShouldContain("Diff: +1 ~1 -0");
    }

    [Fact]
    public void Summary_LeavesTheValuesOut_ButKeepsTheTableAndTheCounts()
    {
        DiffCommand.PrintText(Changes, NoSecrets, summary: true);

        var text = _out.ToString();
        text.ShouldContain("~ api");
        text.ShouldContain("instance_count");
        text.ShouldNotContain("1 → 2");
        text.ShouldContain("Diff: +1 ~1 -0");
    }

    [Fact]
    public void SummaryOption_IsAccepted()
        => DeployifyCli.CreateRootCommand().Parse(["diff", "--summary", "--output", "json"]).Errors.ShouldBeEmpty();

    [Fact]
    public void EntriesWithoutValues_PrintNothing()
    {
        DiffCommand.PrintValues([new DiffEntry { ServiceName = "api", Kind = DiffKind.Added }], NoSecrets);

        _out.ToString().ShouldBeEmpty();
    }
}
