using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Commands;
using Deployify.Core.Drift;
using Deployify.Core.Models;
using Deployify.Core.State;
using Shouldly;
using Xunit;

namespace Deployify.Integration.Tests;

/// <summary>
/// <c>deployify drift</c> through the real CLI, against an in-process fake Nomad: apply records
/// what was deployed, and drift compares the live jobs with that, not with the working tree.
/// </summary>
/// <remarks>In the console collection: it swaps <see cref="DriftCommand.NotifierFactory"/> and JSON output redirects the console.</remarks>
[Collection(nameof(ConsoleOutput))]
public sealed class DriftCommandTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeNomadServer _nomad = new();
    private readonly TempDir _dir = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly Func<IDriftNotifier> _originalFactory = DriftCommand.NotifierFactory;

    public DriftCommandTests() => DriftCommand.NotifierFactory = () => _notifier;

    public void Dispose()
    {
        DriftCommand.NotifierFactory = _originalFactory;
        _nomad.Dispose();
        _dir.Dispose();
    }

    private async Task SetUpAsync(DriftConfig? drift = null, bool staging = false)
    {
        var prod = Harness.NomadConfig(address: _nomad.Address);
        prod.Drift = drift;
        var file = new DeployifyStateFile { ActiveEnvironment = "prod", Environments = { ["prod"] = prod } };
        if (staging)
        {
            var other = Harness.NomadConfig(address: _nomad.Address, ns: "staging");
            other.Drift = new DriftConfig { Enabled = false };
            file.Environments["staging"] = other;
        }

        await Harness.WriteStateAsync(_dir.Path, file, Ct);
        await Harness.WriteManifestAsync(_dir.Path, ct: Ct);
    }

    private Task<int> RunAsync(params string[] args) => CommandRunner.BuildFull().InvokeAsync([.. args, "--project-path", _dir.Path], Ct);

    private Task<int> ApplyAsync() => RunAsync("apply", "--auto-approve", "--no-health-gate");

    private async Task<(int Code, JsonElement Json)> DriftJsonAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var code = await CommandRunner.BuildFull()
            .Parse(["drift", .. args, "--project-path", _dir.Path, "--output", "json"])
            .InvokeAsync(new InvocationConfiguration { Output = stdout, Error = new StringWriter() }, Ct);

        using var document = JsonDocument.Parse(stdout.ToString());
        var json = document.RootElement.Clone();
        JsonOutputSchema.Validate(json);
        json.GetProperty("command").GetString().ShouldBe("drift");
        json.GetProperty("exitCode").GetInt32().ShouldBe(code);
        return (code, json);
    }

    private static JsonElement Env(JsonElement json, string name = "prod")
        => json.GetProperty("result").GetProperty("environments").EnumerateArray().Single(e => e.GetProperty("environment").GetString() == name);

    private async Task<DriftStatusRecord?> StoredStatusAsync()
    {
        await using var state = new StateSession(_dir.Path, "prod", null);
        return await DriftStatusStore.ReadAsync(state, Ct);
    }

    [Fact]
    public async Task BeforeAnyDeploy_ThereIsNothingToCompare_AndThatIsNotAFailure()
    {
        await SetUpAsync();

        var (code, json) = await DriftJsonAsync();

        code.ShouldBe(0);
        var prod = Env(json);
        prod.GetProperty("status").GetString().ShouldBe("notDeployed");
        prod.GetProperty("deployed").ValueKind.ShouldBe(JsonValueKind.Null);
        json.GetProperty("environment").GetString().ShouldBe("prod");
    }

    [Fact]
    public async Task AfterApply_TheEnvironmentIsInSync_AndTheDeployIsRecorded()
    {
        await SetUpAsync();
        (await ApplyAsync()).ShouldBe(0);

        var (code, json) = await DriftJsonAsync();

        code.ShouldBe(0);
        var prod = Env(json);
        prod.GetProperty("status").GetString().ShouldBe("inSync");
        prod.GetProperty("entries").GetArrayLength().ShouldBe(0);
        prod.GetProperty("deployed").GetProperty("tag").GetString().ShouldBe("v1.0.0");
        prod.GetProperty("deployed").GetProperty("releaseId").GetString().ShouldNotBeNullOrEmpty();

        // The baseline's artifacts are generated in a scratch folder that is removed afterward.
        Directory.Exists(Path.Combine(_dir.Path, "deployify-output", DriftDetector.WorkDirectoryName)).ShouldBeFalse();
    }

    [Fact]
    public async Task AChangeByHand_IsDrift_ExitsTwo_AndListsWhatChanged()
    {
        await SetUpAsync();
        (await ApplyAsync()).ShouldBe(0);
        _nomad.Edited.Add("api");
        _nomad.Jobs.Remove("redis");

        var (code, json) = await DriftJsonAsync();

        code.ShouldBe(DriftCommand.DriftedExitCode);
        json.GetProperty("result").GetProperty("drifted").GetBoolean().ShouldBeTrue();
        json.GetProperty("error").GetString()!.ShouldContain("prod drifted");
        var entries = Env(json).GetProperty("entries").EnumerateArray()
            .Select(e => (e.GetProperty("service").GetString(), e.GetProperty("kind").GetString())).ToList();
        entries.ShouldBe([("redis", "missing"), ("api", "changed")]);
        Env(json).GetProperty("fingerprint").GetString()!.Length.ShouldBe(16);
    }

    [Fact]
    public async Task AnUndeployedChangeToTheAppHost_IsNotDrift()
    {
        await SetUpAsync();
        (await ApplyAsync()).ShouldBe(0);

        // A new service in the working tree: plan would create it, but nothing was changed on the target.
        var manifestPath = Path.Combine(_dir.Path, "deployify-output", "manifest.json");
        var manifest = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(await File.ReadAllTextAsync(manifestPath, Ct))!;
        manifest["resources"]["cache"] = JsonSerializer.SerializeToElement(new { type = "container.v0", image = "memcached:1" });
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest), Ct);

        var (code, json) = await DriftJsonAsync();

        code.ShouldBe(0);
        Env(json).GetProperty("status").GetString().ShouldBe("inSync");
    }

    [Fact]
    public async Task Alerts_AreSentWhenDriftAppears_NotRepeated_AndResolved()
    {
        await SetUpAsync(new DriftConfig { Notify = [new DriftChannel { Type = DriftChannelType.Slack, Url = "https://hooks.example.test/x" }] });
        (await ApplyAsync()).ShouldBe(0);

        (await RunAsync("drift")).ShouldBe(0);
        _notifier.Sent.ShouldBeEmpty();

        _nomad.Edited.Add("api");
        (await RunAsync("drift")).ShouldBe(DriftCommand.DriftedExitCode);
        _notifier.Sent.Select(a => a.Event).ShouldBe([DriftEvent.Drifted]);
        _notifier.Sent[0].Check.Entries.ShouldHaveSingleItem().Service.ShouldBe("api");

        // Same drift on the next scheduled run: no second alert.
        (await RunAsync("drift")).ShouldBe(DriftCommand.DriftedExitCode);
        _notifier.Sent.Count.ShouldBe(1);

        // What drifted changes: alert again.
        _nomad.Edited.Add("worker");
        (await RunAsync("drift")).ShouldBe(DriftCommand.DriftedExitCode);
        _notifier.Sent.Select(a => a.Event).ShouldBe([DriftEvent.Drifted, DriftEvent.Changed]);

        // Redeploying puts back what was deployed.
        (await ApplyAsync()).ShouldBe(0);
        (await RunAsync("drift")).ShouldBe(0);
        _notifier.Sent.Select(a => a.Event).ShouldBe([DriftEvent.Drifted, DriftEvent.Changed, DriftEvent.Resolved]);
        _notifier.Sent[^1].PreviousStatus.ShouldBe(DriftStatus.Drifted);
        (await StoredStatusAsync())!.Status.ShouldBe(DriftStatus.InSync);
    }

    [Fact]
    public async Task AnAlertThatCouldNotBeDelivered_IsTriedAgain_AndFailsTheRun()
    {
        await SetUpAsync(new DriftConfig { Notify = [new DriftChannel { Type = DriftChannelType.Webhook, Url = "https://hooks.example.test/x" }] });
        (await ApplyAsync()).ShouldBe(0);
        _nomad.Edited.Add("api");
        _notifier.Fail = true;

        var (code, json) = await DriftJsonAsync();

        code.ShouldBe(DriftCommand.DriftedExitCode);
        var alert = Env(json).GetProperty("alert");
        alert.GetProperty("event").GetString().ShouldBe("drifted");
        alert.GetProperty("deliveries")[0].GetProperty("ok").GetBoolean().ShouldBeFalse();

        _notifier.Fail = false;
        (await RunAsync("drift")).ShouldBe(DriftCommand.DriftedExitCode);
        _notifier.Sent.Count(a => a.Event == DriftEvent.Drifted).ShouldBe(2);
        _notifier.Delivered.ShouldBe(1);
    }

    [Fact]
    public async Task NoNotify_SendsNothing_AndLeavesTheStoredResultAlone()
    {
        await SetUpAsync(new DriftConfig { Notify = [new DriftChannel { Url = "https://hooks.example.test/x" }] });
        (await ApplyAsync()).ShouldBe(0);
        _nomad.Edited.Add("api");

        (await RunAsync("drift", "--no-notify")).ShouldBe(DriftCommand.DriftedExitCode);

        _notifier.Sent.ShouldBeEmpty();
        (await StoredStatusAsync()).ShouldBeNull();

        // The scheduled run still gets to send the alert.
        (await RunAsync("drift")).ShouldBe(DriftCommand.DriftedExitCode);
        _notifier.Sent.ShouldHaveSingleItem().Event.ShouldBe(DriftEvent.Drifted);
    }

    [Fact]
    public async Task Ignored_Differences_AreNotDrift()
    {
        await SetUpAsync(new DriftConfig { Ignore = ["api:job spec"] });
        (await ApplyAsync()).ShouldBe(0);
        _nomad.Edited.Add("api");

        var (code, json) = await DriftJsonAsync();

        code.ShouldBe(0);
        Env(json).GetProperty("status").GetString().ShouldBe("inSync");
        Env(json).GetProperty("ignored").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task All_ChecksEveryEnvironment_AndSkipsOnesWithChecksOff()
    {
        await SetUpAsync(staging: true);
        (await ApplyAsync()).ShouldBe(0);

        var (code, json) = await DriftJsonAsync("--all");

        code.ShouldBe(0);
        json.GetProperty("environment").ValueKind.ShouldBe(JsonValueKind.Null);
        Env(json, "prod").GetProperty("status").GetString().ShouldBe("inSync");
        Env(json, "staging").GetProperty("status").GetString().ShouldBe("skipped");

        // By name it is still checked.
        var (_, one) = await DriftJsonAsync("--env", "staging");
        Env(one, "staging").GetProperty("status").GetString().ShouldBe("notDeployed");
    }

    [Fact]
    public async Task ADeployInProgress_IsNotReportedAsDrift()
    {
        await SetUpAsync();
        (await ApplyAsync()).ShouldBe(0);
        _nomad.Edited.Add("api");

        await using (var state = new StateSession(_dir.Path, "prod", null))
        {
            await state.LockAsync("apply", Ct, autoRenew: false);
            var (code, json) = await DriftJsonAsync();
            code.ShouldBe(0);
            Env(json).GetProperty("status").GetString().ShouldBe("skipped");
            Env(json).GetProperty("reason").GetString()!.ShouldContain("deploy is in progress");
        }
    }

    [Fact]
    public async Task AnUnreachableTarget_IsACheckFailure_NotDrift()
    {
        await SetUpAsync();
        (await ApplyAsync()).ShouldBe(0);
        _nomad.Dispose();

        var (code, json) = await DriftJsonAsync();

        code.ShouldBe(1);
        Env(json).GetProperty("status").GetString().ShouldBe("error");
        Env(json).GetProperty("reason").GetString()!.ShouldContain("Could not reach");
    }

    [Fact]
    public async Task AnInvalidDriftSection_FailsTheCheck()
    {
        await SetUpAsync(new DriftConfig { Notify = [new DriftChannel { Type = DriftChannelType.Slack, Url = "" }] });
        (await ApplyAsync()).ShouldBe(0);

        var (code, json) = await DriftJsonAsync();

        code.ShouldBe(1);
        Env(json).GetProperty("reason").GetString()!.ShouldContain("drift.notify[0]");
    }

    [Fact]
    public async Task Destroy_ClearsTheBaseline()
    {
        await SetUpAsync();
        (await ApplyAsync()).ShouldBe(0);

        (await RunAsync("destroy", "--auto-approve")).ShouldBe(0);

        var (code, json) = await DriftJsonAsync();
        code.ShouldBe(0);
        Env(json).GetProperty("status").GetString().ShouldBe("notDeployed");
    }

    [Fact]
    public async Task WithTheHistoryOff_TheDeployIsStillRecordedForDrift()
    {
        await SetUpAsync();
        var file = await new Core.Services.StateManager().LoadFileAsync(_dir.Path, Ct);
        file.Environments["prod"].State = new StateBackendConfig { History = new StateHistoryConfig { Keep = ReleaseLog.Disabled } };
        await Harness.WriteStateAsync(_dir.Path, file, Ct);
        (await ApplyAsync()).ShouldBe(0);

        var (code, json) = await DriftJsonAsync();

        code.ShouldBe(0);
        Env(json).GetProperty("status").GetString().ShouldBe("inSync");
        Env(json).GetProperty("deployed").GetProperty("releaseId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("--all", "--env", "prod")]
    [InlineData("--every", "30s")]
    [InlineData("--every", "soon")]
    [InlineData("--every", "1h", "--output", "json")]
    public void InvalidOptionCombinations_AreRefused(params string[] args)
        => DeployifyCli.CreateRootCommand().Parse(["drift", .. args]).Errors.ShouldNotBeEmpty();

    [Theory]
    [InlineData("15m", 15 * 60)]
    [InlineData("1h", 3600)]
    [InlineData("1.5h", 5400)]
    [InlineData("1d", 86400)]
    [InlineData("00:05:00", 300)]
    public void Intervals_AreRead(string text, int seconds)
    {
        DriftCommand.ParseInterval(text, out var interval, out _).ShouldBeTrue();
        interval.ShouldBe(TimeSpan.FromSeconds(seconds));
    }

    private sealed class RecordingNotifier : IDriftNotifier
    {
        public List<DriftAlert> Sent { get; } = [];
        public int Delivered { get; private set; }
        public bool Fail { get; set; }

        public Task<DriftDelivery> SendAsync(DriftChannel channel, DriftAlert alert, CancellationToken ct = default)
        {
            Sent.Add(alert);
            if (Fail)
            {
                return Task.FromResult(new DriftDelivery(channel.DisplayName, channel.Type, false, "HTTP 500"));
            }

            Delivered++;
            return Task.FromResult(new DriftDelivery(channel.DisplayName, channel.Type, true, null));
        }
    }
}
