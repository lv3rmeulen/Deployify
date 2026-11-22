using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Commands;
using Deployify.Commands.Json;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Rollouts;
using Deployify.Core.SecretBackends;
using Deployify.Core.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Shouldly;
using Spectre.Console;
using Xunit;

namespace Deployify.Integration.Tests;

/// <summary>
/// <c>--output json</c> on plan, diff, status, explain, promote and history: stdout holds exactly one
/// document, it matches the published schema, and it says what the text output says.
/// </summary>
/// <remarks>In the console collection: JSON output points the global console at stderr while a command runs.</remarks>
[Collection(nameof(ConsoleOutput))]
public sealed class JsonOutputTests
{
    private static readonly string[] JsonCommands = ["plan", "diff", "drift", "status", "explain", "promote", "history"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Run(int ExitCode, JsonElement Json, string Stdout, string Stderr)
    {
        public JsonElement Result => Json.GetProperty("result");
    }

    /// <summary>Runs the real command tree with stdout and stderr captured, and validates the document.</summary>
    private static async Task<Run> RunJsonAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DeployifyCli.CreateRootCommand()
            .Parse([.. args, "--output", "json"])
            .InvokeAsync(new InvocationConfiguration { Output = stdout, Error = stderr }, Ct);

        // Parsing the whole of stdout proves nothing else was written there.
        using var document = JsonDocument.Parse(stdout.ToString());
        var json = document.RootElement.Clone();
        JsonOutputSchema.Validate(json);

        json.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
        json.GetProperty("command").GetString().ShouldBe(args[0]);
        json.GetProperty("exitCode").GetInt32().ShouldBe(code);
        json.GetProperty("success").GetBoolean().ShouldBe(code == 0);
        (json.GetProperty("error").ValueKind == JsonValueKind.Null).ShouldBe(code == 0);
        return new Run(code, json, stdout.ToString(), stderr.ToString());
    }

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    // ── The option itself ─────────────────────────────────────────────────────

    [Fact]
    public void OutputOption_IsOnExactlyTheCommandsTheSchemaCovers()
    {
        var withOption = DeployifyCli.CreateRootCommand().Subcommands
            .Where(c => c.Options.Contains(GlobalOptions.Output))
            .Select(c => c.Name)
            .Order()
            .ToList();

        withOption.ShouldBe(JsonCommands.Order().ToList());

        var schemaCommands = JsonOutputSchema.Document["properties"]!["command"]!["enum"]!.AsArray()
            .Select(n => n!.GetValue<string>()).Order().ToList();
        schemaCommands.ShouldBe(withOption);
    }

    [Fact]
    public void Schema_AsCheckedHere_RejectsAPropertyItDoesNotList()
    {
        // Guards the guard: a property added to the output but not to the schema must fail these tests.
        var envelope = new JsonRun("diff") { Result = new JsonDiff(true, [new JsonDiffEntry("api", JsonDiffKind.Unchanged, null, null, null, null)]) }
            .ToEnvelope(0);
        var valid = JsonDocument.Parse(JsonOutput.Serialize(envelope)).RootElement;
        JsonOutputSchema.Validate(valid);

        var node = System.Text.Json.Nodes.JsonNode.Parse(valid.GetRawText())!;
        node["result"]!["entries"]![0]!["unlisted"] = "x";
        var extra = JsonDocument.Parse(node.ToJsonString()).RootElement;
        Should.Throw<Exception>(() => JsonOutputSchema.Validate(extra)).Message.ShouldContain("unlisted");
    }

    [Fact]
    public void Option_RejectsAnUnknownFormat()
    {
        var parse = DeployifyCli.CreateRootCommand().Parse(["plan", "--output", "yaml"]);
        parse.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task TextOutput_WritesNothingToTheJsonWriter()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: Ct);
        var stdout = new StringWriter();

        var code = await DeployifyCli.CreateRootCommand()
            .Parse(["status", "--project-path", dir.Path])
            .InvokeAsync(new InvocationConfiguration { Output = stdout, Error = new StringWriter() }, Ct);

        code.ShouldBe(0);
        stdout.ToString().ShouldBeEmpty();
    }

    // ── plan ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_ListsTheChanges_OnStdout_AndThePlanOnStderr()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, address: "http://127.0.0.1:1", ct: Ct);
        await Harness.WriteManifestAsync(dir.Path, ct: Ct);

        var run = await RunJsonAsync("plan", "--project-path", dir.Path);

        run.ExitCode.ShouldBe(0);
        run.Json.GetProperty("environment").GetString().ShouldBe("local");
        run.Json.GetProperty("target").GetProperty("id").GetString().ShouldBe("Nomad");
        run.Json.GetProperty("target").GetProperty("name").GetString().ShouldBe("HashiCorp Nomad");

        // Nomad isn't reachable, so everything counts as new.
        Strings(run.Result.GetProperty("create")).Order().ShouldBe(["api", "redis", "worker"]);
        Strings(run.Result.GetProperty("update")).ShouldBeEmpty();
        Strings(run.Result.GetProperty("delete")).ShouldBeEmpty();
        run.Result.GetProperty("upToDate").GetBoolean().ShouldBeFalse();
        run.Result.GetProperty("blocked").GetBoolean().ShouldBeFalse();
        run.Result.GetProperty("blockedReason").ValueKind.ShouldBe(JsonValueKind.Null);
        run.Result.GetProperty("deletesOnApply").GetBoolean().ShouldBeFalse();

        // The human-readable plan still goes out, on stderr.
        run.Stderr.ShouldContain("Deployify Plan");
        run.Stderr.ShouldContain("3 to create");
    }

    [Fact]
    public async Task Plan_WhenItCannotResolveTheEnvironment_StillWritesADocument()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: Ct);

        var run = await RunJsonAsync("plan", "--project-path", dir.Path, "--env", "nonexistent");

        run.ExitCode.ShouldNotBe(0);
        run.Json.GetProperty("success").GetBoolean().ShouldBeFalse();
        run.Json.GetProperty("error").GetString()!.ShouldContain("nonexistent");
        run.Json.GetProperty("environment").ValueKind.ShouldBe(JsonValueKind.Null);
        run.Json.GetProperty("target").ValueKind.ShouldBe(JsonValueKind.Null);
        run.Result.ValueKind.ShouldBe(JsonValueKind.Null);
        run.Json.GetProperty("diagnostics").EnumerateArray()
            .ShouldContain(d => d.GetProperty("severity").GetString() == "error");
    }

    [Fact]
    public async Task Plan_BlockedByTheManifestAudit_ExitsOne_WithTheFindings()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, address: "http://127.0.0.1:1", ct: Ct);
        var output = Path.Combine(dir.Path, "deployify-output");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), """
            {
              "resources": {
                "api": { "type": "project.v0", "path": "Api/Api.csproj" },
                "storage": { "type": "azure.bicep.v0", "path": "storage.bicep" }
              }
            }
            """, Ct);

        var run = await RunJsonAsync("plan", "--project-path", dir.Path, "--strict");

        run.ExitCode.ShouldBe(1);
        run.Result.GetProperty("blocked").GetBoolean().ShouldBeTrue();
        run.Result.GetProperty("blockedReason").GetString()!.ShouldContain("--strict");
        run.Json.GetProperty("error").GetString().ShouldBe(run.Result.GetProperty("blockedReason").GetString());

        var skipped = run.Result.GetProperty("skipped").EnumerateArray().Single();
        skipped.GetProperty("name").GetString().ShouldBe("storage");
        skipped.GetProperty("type").GetString().ShouldBe("azure.bicep.v0");

        var finding = run.Result.GetProperty("manifestFindings").EnumerateArray().Single();
        finding.GetProperty("resource").GetString().ShouldBe("storage");
        finding.GetProperty("severity").GetString().ShouldBe("warning");
    }

    // ── diff ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Diff_ListsEveryService()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, address: "http://127.0.0.1:1", ct: Ct);
        await Harness.WriteManifestAsync(dir.Path, ct: Ct);

        var run = await RunJsonAsync("diff", "--project-path", dir.Path);

        run.ExitCode.ShouldBe(0);
        run.Result.GetProperty("identical").GetBoolean().ShouldBeFalse();
        var entries = run.Result.GetProperty("entries").EnumerateArray().ToList();
        entries.Select(e => e.GetProperty("service").GetString()).Order().ShouldBe(["api", "redis", "worker"]);
        entries.ShouldAllBe(e => e.GetProperty("kind").GetString() != "unchanged");
    }

    [Fact]
    public void Diff_MapsEveryKind_InTableOrder()
    {
        var result = DiffCommand.ToJson(
        [
            new DiffEntry { ServiceName = "e", Kind = DiffKind.Unknown, Reason = "timed out" },
            new DiffEntry { ServiceName = "d", Kind = DiffKind.Unchanged },
            new DiffEntry { ServiceName = "c", Kind = DiffKind.Changed, Field = "image", OldValue = "api:1", NewValue = "api:2" },
            new DiffEntry { ServiceName = "b", Kind = DiffKind.Removed },
            new DiffEntry { ServiceName = "a", Kind = DiffKind.Added }
        ]);

        var json = JsonDocument.Parse(JsonSerializer.Serialize(result, JsonOutput.SerializerOptions)).RootElement;
        json.GetProperty("identical").GetBoolean().ShouldBeFalse();
        var entries = json.GetProperty("entries").EnumerateArray().ToList();
        entries.Select(e => e.GetProperty("kind").GetString()).ShouldBe(["added", "removed", "changed", "unchanged", "unknown"]);
        entries[2].GetProperty("field").GetString().ShouldBe("image");
        entries[2].GetProperty("oldValue").GetString().ShouldBe("api:1");
        entries[2].GetProperty("newValue").GetString().ShouldBe("api:2");
        entries[0].GetProperty("oldValue").ValueKind.ShouldBe(JsonValueKind.Null);
        entries[4].GetProperty("reason").GetString().ShouldBe("timed out");
    }

    [Fact]
    public void Diff_MasksSecretValues_RawAndBase64()
    {
        var registry = new SecretRegistry();
        registry.Record("db-password", "hunter2-hunter2");
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("hunter2-hunter2"));

        var result = DiffCommand.ToJson(
        [
            new DiffEntry
            {
                ServiceName = "api", Kind = DiffKind.Changed, Field = "manifest",
                OldValue = "PASSWORD=hunter2-hunter2",
                NewValue = $"-  password: {base64}\n+  password: hunter2-hunter2"
            },
            new DiffEntry { ServiceName = "web", Kind = DiffKind.Unknown, Reason = "login with hunter2-hunter2 failed" }
        ], registry.Snapshot());

        var text = JsonSerializer.Serialize(result, JsonOutput.SerializerOptions);
        text.ShouldNotContain("hunter2");
        text.ShouldNotContain(base64);
        result.Entries[0].OldValue.ShouldBe("PASSWORD=" + SensitiveValue.Mask);
        result.Entries[0].NewValue.ShouldBe($"-  password: {SensitiveValue.Mask}\n+  password: {SensitiveValue.Mask}");
        result.Entries[1].Reason.ShouldBe($"login with {SensitiveValue.Mask} failed");
    }

    [Fact]
    public void Diff_IsIdentical_OnlyWhenEverythingIsUnchanged()
    {
        DiffCommand.ToJson([new DiffEntry { ServiceName = "a", Kind = DiffKind.Unchanged }]).Identical.ShouldBeTrue();
        DiffCommand.ToJson([]).Identical.ShouldBeTrue();
        DiffCommand.ToJson([new DiffEntry { ServiceName = "a", Kind = DiffKind.Unknown }]).Identical.ShouldBeFalse();
    }

    // ── explain ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Explain_DescribesEachService()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: Ct);
        await Harness.WriteManifestAsync(dir.Path, ct: Ct);

        var run = await RunJsonAsync("explain", "--project-path", dir.Path);

        run.ExitCode.ShouldBe(0);
        var services = run.Result.GetProperty("services").EnumerateArray().ToList();
        services.Select(s => s.GetProperty("name").GetString()).Order().ShouldBe(["api", "redis", "worker"]);

        var worker = services.Single(s => s.GetProperty("name").GetString() == "worker");
        Strings(worker.GetProperty("dependsOn")).ShouldContain("api");
        worker.GetProperty("count").GetInt32().ShouldBeGreaterThan(0);
        worker.GetProperty("details").ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task Explain_FiltersByService_AndAnswersEmptyForNoMatch()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: Ct);
        await Harness.WriteManifestAsync(dir.Path, ct: Ct);

        var one = await RunJsonAsync("explain", "--project-path", dir.Path, "--service", "API");
        one.Result.GetProperty("services").EnumerateArray().Single().GetProperty("name").GetString().ShouldBe("api");

        var none = await RunJsonAsync("explain", "--project-path", dir.Path, "--service", "nope");
        none.ExitCode.ShouldBe(0);
        none.Result.GetProperty("services").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void Explain_LeavesEnvironmentVariablesOut()
    {
        var result = ExplainCommand.ToJson(
        [
            new ServiceExplanation
            {
                Name = "api", Image = "reg/api", ImageTag = "v1", Count = 2,
                Env = { ["ConnectionStrings__db"] = "Password=hunter2" },
                AdditionalInfo = { ["Namespace"] = "prod" }
            }
        ]);

        var text = JsonSerializer.Serialize(result, JsonOutput.SerializerOptions);
        text.ShouldNotContain("hunter2");
        text.ShouldContain("\"Namespace\": \"prod\"");
    }

    // ── status ────────────────────────────────────────────────────────────────

    private static async Task<string> WriteJournalAsync(string dir, DateTimeOffset? startedAt = null)
    {
        await using var journal = await RolloutJournal.BeginAsync(dir, "local", "AWS ECS (Fargate)", [["api"]],
            startedAt ?? DateTimeOffset.UtcNow, Ct);
        var api = journal.Record.Service("api");
        api.Snapshot = new ServiceSnapshot { ServiceName = "api", Existed = true, Revision = "arn:td/api:6" };
        api.Revision = "arn:td/api:7";
        api.State = ServiceRolloutState.RolledBack;
        api.RestoredRevision = "arn:td/api:6";
        journal.Record.State = RolloutState.RolledBack;
        journal.Record.Reason = "api failed its health check";
        await journal.LogAsync(null, "Rolling back 1 service(s)");
        await journal.SaveAsync(Ct);
        return journal.Record.Id;
    }

    [Fact]
    public async Task Status_WithoutRollouts_ListsNone()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: Ct);

        var run = await RunJsonAsync("status", "--project-path", dir.Path);

        run.ExitCode.ShouldBe(0);
        run.Result.GetProperty("rollouts").GetArrayLength().ShouldBe(0);
        run.Result.GetProperty("rollout").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Status_ListsRollouts_AndShowsOne()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: Ct);
        var id = await WriteJournalAsync(dir.Path);

        var list = await RunJsonAsync("status", "--project-path", dir.Path);
        var summary = list.Result.GetProperty("rollouts").EnumerateArray().Single();
        summary.GetProperty("id").GetString().ShouldBe(id);
        summary.GetProperty("operation").GetString().ShouldBe("rollout");
        summary.GetProperty("state").GetString().ShouldBe("rolledBack");
        summary.GetProperty("reason").GetString().ShouldBe("api failed its health check");
        list.Result.GetProperty("rollout").ValueKind.ShouldBe(JsonValueKind.Null);

        var detail = await RunJsonAsync("status", "--project-path", dir.Path, "--rollout", id);
        detail.Result.GetProperty("rollouts").ValueKind.ShouldBe(JsonValueKind.Null);
        var rollout = detail.Result.GetProperty("rollout");
        rollout.GetProperty("id").GetString().ShouldBe(id);
        rollout.GetProperty("waves").EnumerateArray().Single().EnumerateArray().Single().GetString().ShouldBe("api");
        rollout.GetProperty("journal").GetString()!.ShouldEndWith(".json");

        var api = rollout.GetProperty("services").EnumerateArray().Single();
        api.GetProperty("state").GetString().ShouldBe("rolledBack");
        api.GetProperty("before").GetProperty("existed").GetBoolean().ShouldBeTrue();
        api.GetProperty("before").GetProperty("revision").GetString().ShouldBe("arn:td/api:6");
        api.GetProperty("revision").GetString().ShouldBe("arn:td/api:7");
        api.GetProperty("restoredRevision").GetString().ShouldBe("arn:td/api:6");

        rollout.GetProperty("events").EnumerateArray()
            .ShouldContain(e => e.GetProperty("message").GetString() == "Rolling back 1 service(s)");
    }

    [Fact]
    public async Task Status_UnknownRollout_FailsWithTheReason()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: Ct);

        var run = await RunJsonAsync("status", "--project-path", dir.Path, "--rollout", "nope");

        run.ExitCode.ShouldBe(1);
        run.Json.GetProperty("error").GetString()!.ShouldContain("'nope'");
        run.Result.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Status_LimitsTheList()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: Ct);
        await WriteJournalAsync(dir.Path, DateTimeOffset.UtcNow.AddHours(-2));
        var newest = await WriteJournalAsync(dir.Path, DateTimeOffset.UtcNow.AddHours(-1));

        (await RunJsonAsync("status", "--project-path", dir.Path)).Result.GetProperty("rollouts").GetArrayLength().ShouldBe(2);

        var run = await RunJsonAsync("status", "--project-path", dir.Path, "--limit", "1");
        run.Result.GetProperty("rollouts").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe(newest);
    }

    // ── history ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task History_WithoutReleases_ListsNone()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: Ct);

        var run = await RunJsonAsync("history", "--project-path", dir.Path);

        run.ExitCode.ShouldBe(0);
        run.Result.GetProperty("releases").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task History_ListsReleases_NewestFirst_AndLimitsThem()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: Ct);
        var t0 = new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
        await using (var state = new StateSession(dir.Path, "local", null))
        {
            await ReleaseLog.AppendAsync(state, new ReleaseRecord
            {
                Id = "publish00001", Kind = ReleaseKind.Publish, Environment = "local", At = t0,
                Images = [new ReleaseImage("api", "reg/api", "4f2c9e1", ApiDigest)],
                Tag = "4f2c9e1", Commit = "4f2c9e1a7b3d", Actor = "lv3rmeulen", ActorSource = "GITHUB_ACTOR",
                RunUrl = "https://github.com/acme/shop/actions/runs/42", DeployifyVersion = "1.1.0"
            }, ct: Ct);
            await ReleaseLog.AppendAsync(state, new ReleaseRecord
            {
                Id = "rollback0001", Kind = ReleaseKind.Rollback, Environment = "local", At = t0.AddMinutes(5),
                RolloutId = "20261122-090100-local", Summary = "Undid rollout 20261122-090100-local: api restored."
            }, ct: Ct);
        }

        var run = await RunJsonAsync("history", "--project-path", dir.Path);

        var releases = run.Result.GetProperty("releases").EnumerateArray().ToList();
        releases.Select(r => r.GetProperty("id").GetString()).ShouldBe(["rollback0001", "publish00001"]);

        var rollback = releases[0];
        rollback.GetProperty("kind").GetString().ShouldBe("rollback");
        rollback.GetProperty("outcome").GetString().ShouldBe("succeeded");
        rollback.GetProperty("rolloutId").GetString().ShouldBe("20261122-090100-local");
        rollback.GetProperty("tag").ValueKind.ShouldBe(JsonValueKind.Null);
        rollback.GetProperty("images").GetArrayLength().ShouldBe(0);

        var publish = releases[1];
        publish.GetProperty("kind").GetString().ShouldBe("publish");
        publish.GetProperty("commit").GetString().ShouldBe("4f2c9e1a7b3d");
        publish.GetProperty("actor").GetString().ShouldBe("lv3rmeulen");
        publish.GetProperty("runUrl").GetString().ShouldBe("https://github.com/acme/shop/actions/runs/42");
        var image = publish.GetProperty("images").EnumerateArray().Single();
        image.GetProperty("service").GetString().ShouldBe("api");
        image.GetProperty("digest").GetString().ShouldBe(ApiDigest);

        var limited = await RunJsonAsync("history", "--project-path", dir.Path, "--limit", "1");
        limited.Result.GetProperty("releases").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe("rollback0001");
    }

    [Fact]
    public async Task Promote_RecordsARelease_WithTheSourcesCommit()
    {
        using var dir = new TempDir();
        await WritePromotableStateAsync(dir.Path, ("api", ApiDigest), ("worker", WorkerDigest));
        await using (var staging = new StateSession(dir.Path, "staging", null))
        {
            await ReleaseLog.AppendAsync(staging, new ReleaseRecord
            {
                Id = "publish00001", Kind = ReleaseKind.Publish, Environment = "staging",
                At = DateTimeOffset.UtcNow.AddMinutes(-5), Tag = "v1.0.0", Commit = "4f2c9e1a7b3d"
            }, ct: Ct);
        }

        (await RunJsonAsync("promote", "--project-path", dir.Path, "--from", "staging", "--to", "prod")).ExitCode.ShouldBe(0);
        // Nothing new to record the second time, so no second release.
        (await RunJsonAsync("promote", "--project-path", dir.Path, "--from", "staging", "--to", "prod")).ExitCode.ShouldBe(0);

        var run = await RunJsonAsync("history", "--project-path", dir.Path, "--env", "prod");
        var release = run.Result.GetProperty("releases").EnumerateArray().Single();
        release.GetProperty("kind").GetString().ShouldBe("promote");
        release.GetProperty("fromEnvironment").GetString().ShouldBe("staging");
        release.GetProperty("tag").GetString().ShouldBe("v1.0.0");
        release.GetProperty("commit").GetString().ShouldBe("4f2c9e1a7b3d");
        release.GetProperty("images").EnumerateArray()
            .Select(i => (i.GetProperty("service").GetString(), i.GetProperty("digest").GetString()))
            .ShouldBe([("api", ApiDigest), ("worker", WorkerDigest)]);
    }

    // ── promote ───────────────────────────────────────────────────────────────

    private const string Registry = "reg.example.com/app";
    private const string ApiDigest = "sha256:" + "aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11";
    private const string WorkerDigest = "sha256:" + "bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22";

    /// <summary>staging and prod on the same registry; staging published the given images at v1.0.0.</summary>
    private static async Task WritePromotableStateAsync(string dir, params (string Service, string Digest)[] published)
    {
        await Harness.WriteStateAsync(dir, new DeployifyStateFile
        {
            ActiveEnvironment = "staging",
            Environments =
            {
                ["staging"] = Harness.NomadConfig(registry: Registry),
                ["prod"] = Harness.NomadConfig("http://nomad.prod:4646", registry: Registry)
            }
        }, Ct);
        await Harness.WriteManifestAsync(dir, ct: Ct);

        var images = new PublishedImages();
        foreach (var (service, digest) in published)
        {
            images.Record($"{Registry}/{service}", "v1.0.0", digest, DateTimeOffset.UtcNow);
        }

        await using var state = new StateSession(dir, "staging", null);
        await PublishedImageStore.SaveAsync(state, images, Ct);
    }

    [Fact]
    public async Task Promote_RecordsTheDigests_ThenReportsThemAsAlreadyPromoted()
    {
        using var dir = new TempDir();
        await WritePromotableStateAsync(dir.Path, ("api", ApiDigest), ("worker", WorkerDigest));

        var run = await RunJsonAsync("promote", "--project-path", dir.Path, "--from", "staging", "--to", "prod");

        run.ExitCode.ShouldBe(0, run.Stderr);
        run.Json.GetProperty("environment").GetString().ShouldBe("prod");
        var result = run.Result;
        result.GetProperty("from").GetString().ShouldBe("staging");
        result.GetProperty("to").GetString().ShouldBe("prod");
        result.GetProperty("imageTag").GetString().ShouldBe("v1.0.0");
        result.GetProperty("dryRun").GetBoolean().ShouldBeFalse();
        result.GetProperty("promoted").GetBoolean().ShouldBeTrue();
        result.GetProperty("recorded").GetInt32().ShouldBe(2);
        result.GetProperty("copyTool").ValueKind.ShouldBe(JsonValueKind.Null);

        var api = result.GetProperty("images").EnumerateArray().Single(i => i.GetProperty("service").GetString() == "api");
        api.GetProperty("repository").GetString().ShouldBe($"{Registry}/api");
        api.GetProperty("tag").GetString().ShouldBe("v1.0.0");
        api.GetProperty("digest").GetString().ShouldBe(ApiDigest);
        api.GetProperty("needsCopy").GetBoolean().ShouldBeFalse();
        api.GetProperty("alreadyPromoted").GetBoolean().ShouldBeFalse();
        api.GetProperty("copied").GetBoolean().ShouldBeFalse();
        api.GetProperty("problem").ValueKind.ShouldBe(JsonValueKind.Null);

        var again = await RunJsonAsync("promote", "--project-path", dir.Path, "--from", "staging", "--to", "prod");
        again.ExitCode.ShouldBe(0);
        again.Result.GetProperty("recorded").GetInt32().ShouldBe(0);
        again.Result.GetProperty("images").EnumerateArray().ShouldAllBe(i => i.GetProperty("alreadyPromoted").GetBoolean());
    }

    [Fact]
    public async Task Promote_DryRun_RecordsNothing()
    {
        using var dir = new TempDir();
        await WritePromotableStateAsync(dir.Path, ("api", ApiDigest), ("worker", WorkerDigest));

        var run = await RunJsonAsync("promote", "--project-path", dir.Path, "--from", "staging", "--to", "prod", "--dry-run");

        run.ExitCode.ShouldBe(0);
        run.Result.GetProperty("dryRun").GetBoolean().ShouldBeTrue();
        run.Result.GetProperty("promoted").GetBoolean().ShouldBeFalse();
        run.Result.GetProperty("recorded").GetInt32().ShouldBe(0);
        run.Result.GetProperty("images").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Promote_WithAMissingDigest_FailsAndSaysWhichImage()
    {
        using var dir = new TempDir();
        await WritePromotableStateAsync(dir.Path, ("api", ApiDigest));

        var run = await RunJsonAsync("promote", "--project-path", dir.Path, "--from", "staging", "--to", "prod");

        run.ExitCode.ShouldBe(1);
        run.Json.GetProperty("error").GetString()!.ShouldContain("can't be promoted");
        run.Result.GetProperty("promoted").GetBoolean().ShouldBeFalse();
        var worker = run.Result.GetProperty("images").EnumerateArray().Single(i => i.GetProperty("service").GetString() == "worker");
        worker.GetProperty("digest").ValueKind.ShouldBe(JsonValueKind.Null);
        worker.GetProperty("problem").GetString()!.ShouldContain("no published digest");
    }

    [Fact]
    public async Task Promote_RequireSignature_WithoutSigningSettings_RecordsNothing()
    {
        using var dir = new TempDir();
        await WritePromotableStateAsync(dir.Path, ("api", ApiDigest), ("worker", WorkerDigest));

        var run = await RunJsonAsync("promote", "--project-path", dir.Path, "--from", "staging", "--to", "prod", "--require-signature");

        run.ExitCode.ShouldBe(1);
        run.Json.GetProperty("error").GetString()!.ShouldContain("signing.identity");
        run.Result.GetProperty("promoted").GetBoolean().ShouldBeFalse();
        run.Result.GetProperty("recorded").GetInt32().ShouldBe(0);
        run.Result.GetProperty("images").EnumerateArray()
            .ShouldAllBe(i => i.GetProperty("signatureVerified").ValueKind == JsonValueKind.False);

        // Nothing was recorded: promoting without the flag still has both to record.
        var again = await RunJsonAsync("promote", "--project-path", dir.Path, "--from", "staging", "--to", "prod");
        again.Result.GetProperty("recorded").GetInt32().ShouldBe(2);
        again.Result.GetProperty("images").EnumerateArray()
            .ShouldAllBe(i => i.GetProperty("signatureVerified").ValueKind == JsonValueKind.Null &&
                              i.GetProperty("signaturesCopied").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Promote_SameEnvironmentTwice_FailsBeforeResolvingAnything()
    {
        var run = await RunJsonAsync("promote", "--from", "prod", "--to", "prod");

        run.ExitCode.ShouldBe(1);
        run.Json.GetProperty("error").GetString().ShouldBe("--from and --to name the same environment.");
        run.Result.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ── The envelope ──────────────────────────────────────────────────────────

    /// <summary>A command with --output whose body is <paramref name="body"/>.</summary>
    private static async Task<(int Code, string Stdout, string Stderr)> RunBodyAsync(Func<JsonRun?, Task<int>> body)
    {
        var cmd = new RootCommand("t") { GlobalOptions.Output };
        cmd.SetAction((parseResult, _) => JsonOutput.RunAsync(parseResult, "plan", body));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await cmd.Parse(["--output", "json"])
            .InvokeAsync(new InvocationConfiguration { Output = stdout, Error = stderr }, Ct);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Envelope_RecordsReportedMessages_AndPrintsThemOnStderr()
    {
        var (code, stdout, stderr) = await RunBodyAsync(_ =>
        {
            ConsoleDeploymentReporter.Instance.Info("checking");
            ConsoleDeploymentReporter.Instance.Warn("careful");
            ConsoleDeploymentReporter.Instance.Error("first problem");
            ConsoleDeploymentReporter.Instance.Error("second problem");
            return Task.FromResult(3);
        });

        code.ShouldBe(3);
        stderr.ShouldContain("careful");
        stderr.ShouldContain("first problem");

        var json = JsonDocument.Parse(stdout).RootElement;
        JsonOutputSchema.Validate(json);
        json.GetProperty("exitCode").GetInt32().ShouldBe(3);
        json.GetProperty("error").GetString().ShouldBe("first problem");
        json.GetProperty("diagnostics").EnumerateArray()
            .Select(d => (d.GetProperty("severity").GetString(), d.GetProperty("message").GetString()))
            .ShouldBe([("info", "checking"), ("warning", "careful"), ("error", "first problem"), ("error", "second problem")]);
    }

    [Fact]
    public async Task Envelope_FailureWithoutAMessage_StillHasAnError()
    {
        var (code, stdout, _) = await RunBodyAsync(_ => Task.FromResult(2));

        code.ShouldBe(2);
        JsonDocument.Parse(stdout).RootElement.GetProperty("error").GetString().ShouldBe("The command failed with exit code 2.");
    }

    [Fact]
    public async Task Envelope_AnExceptionBecomesAnError_AndTheConsoleIsRestored()
    {
        var before = AnsiConsole.Console;

        var (code, stdout, stderr) = await RunBodyAsync(_ => throw new InvalidOperationException("boom"));

        code.ShouldBe(1);
        stderr.ShouldContain("boom");
        var json = JsonDocument.Parse(stdout).RootElement;
        JsonOutputSchema.Validate(json);
        json.GetProperty("error").GetString().ShouldBe("boom");
        AnsiConsole.Console.ShouldBeSameAs(before);
        JsonOutput.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Envelope_SendsTheConsoleAndTheLogsToStderr()
    {
        LogLevel? threshold = null;
        var (_, stdout, stderr) = await RunBodyAsync(_ =>
        {
            AnsiConsole.MarkupLine("human output");
            using var services = new ServiceCollection().AddDeployifyCore().BuildServiceProvider();
            threshold = services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.LogToStandardErrorThreshold;
            return Task.FromResult(0);
        });

        stderr.ShouldContain("human output");
        stdout.ShouldNotContain("human output");
        threshold.ShouldBe(LogLevel.Trace);

        // Text output keeps logging where it always did.
        using var text = new ServiceCollection().AddDeployifyCore().BuildServiceProvider();
        text.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.LogToStandardErrorThreshold.ShouldBe(LogLevel.None);
    }

    [Fact]
    public void Envelope_PropertyNamesAreCamelCase_AndNullsAreWritten()
    {
        var run = new JsonRun("explain");
        var text = JsonOutput.Serialize(run.ToEnvelope(0));

        text.ShouldContain("\"schemaVersion\": 1");
        text.ShouldContain("\"error\": null");
        text.ShouldContain("\"target\": null");
        text.ShouldContain("\"result\": null");
    }
}
