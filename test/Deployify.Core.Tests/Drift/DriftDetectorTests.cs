using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Drift;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Deployify.Core.State;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.Drift;

/// <summary>
/// The pieces of a drift check that don't need a target: turning a diff into drift, ignore rules,
/// fingerprints, the drift section's validation, and the stored baseline and status formats.
/// </summary>
public sealed class DriftDetectorTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset s_t0 = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static readonly Dictionary<string, SensitiveValue> s_noSecrets = [];

    [Fact]
    public void Classify_SpeaksFromTheDeploysSide_AndDropsUnchanged()
    {
        var (entries, ignored) = DriftDetector.Classify(
        [
            new DiffEntry { ServiceName = "web", Kind = DiffKind.Unchanged },
            new DiffEntry { ServiceName = "api", Kind = DiffKind.Changed, Field = "image", OldValue = "api:hotfix", NewValue = "api:1" },
            new DiffEntry { ServiceName = "redis", Kind = DiffKind.Added },
            new DiffEntry { ServiceName = "debug", Kind = DiffKind.Removed },
            new DiffEntry { ServiceName = "db", Kind = DiffKind.Unknown, Reason = "timeout" }
        ], [], s_noSecrets);

        ignored.ShouldBe(0);
        entries.Select(e => (e.Service, e.Kind)).ShouldBe(
        [
            ("redis", DriftKind.Missing),
            ("debug", DriftKind.Extra),
            ("api", DriftKind.Changed),
            ("db", DriftKind.Unknown)
        ]);
        entries.Single(e => e.Service == "api").ShouldBe(new DriftEntry("api", DriftKind.Changed, "image", "api:hotfix", "api:1", null));
        DriftDetector.Status(entries).ShouldBe(DriftStatus.Drifted);
    }

    [Fact]
    public void Classify_MasksSecrets()
    {
        var secrets = new Dictionary<string, SensitiveValue> { ["db-password"] = new("hunter2-hunter2") };
        var (entries, _) = DriftDetector.Classify(
            [new DiffEntry { ServiceName = "api", Kind = DiffKind.Changed, Field = "env", OldValue = "PASSWORD=hunter2-hunter2", NewValue = "PASSWORD=x" }],
            [], secrets);

        entries.Single().Live!.ShouldNotContain("hunter2-hunter2");
    }

    [Fact]
    public void Status_OfOnlyUnknownEntries_IsUnknown_AndOfNoneInSync()
    {
        DriftDetector.Status([new DriftEntry("db", DriftKind.Unknown, null, null, null, "timeout")]).ShouldBe(DriftStatus.Unknown);
        DriftDetector.Status([]).ShouldBe(DriftStatus.InSync);
    }

    [Theory]
    [InlineData("worker", "worker", "instance_count", true)]
    [InlineData("worker:instance_count", "worker", "instance_count", true)]
    [InlineData("worker:INSTANCE_COUNT", "worker", "instance_count", true)]
    [InlineData("worker:image", "worker", "instance_count", false)]
    [InlineData("*:desired_count", "api", "desired_count", true)]
    [InlineData("*", "api", null, true)]
    [InlineData("Worker", "worker", null, false)]
    public void IgnoreRules(string rule, string service, string? field, bool ignored)
        => DriftDetector.IsIgnored(service, field, [rule]).ShouldBe(ignored);

    [Fact]
    public void Classify_CountsIgnoredDifferences_ButNeverHidesAServiceThatCouldNotBeChecked()
    {
        var (entries, ignored) = DriftDetector.Classify(
        [
            new DiffEntry { ServiceName = "api", Kind = DiffKind.Changed, Field = "desired_count" },
            new DiffEntry { ServiceName = "worker", Kind = DiffKind.Unknown, Reason = "timeout" }
        ], ["*:desired_count", "worker"], s_noSecrets);

        ignored.ShouldBe(1);
        entries.ShouldHaveSingleItem().Kind.ShouldBe(DriftKind.Unknown);
    }

    [Fact]
    public void Fingerprint_IsStable_IgnoresOrderAndUnknowns_AndChangesWithTheDrift()
    {
        var a = new DriftEntry("api", DriftKind.Changed, "image", "x", "y", null);
        var b = new DriftEntry("redis", DriftKind.Missing, null, null, null, null);
        var unknown = new DriftEntry("db", DriftKind.Unknown, null, null, null, "timeout");

        DriftDetector.Fingerprint([a, b]).ShouldBe(DriftDetector.Fingerprint([b, unknown, a]));
        DriftDetector.Fingerprint([a, b]).Length.ShouldBe(16);
        DriftDetector.Fingerprint([a]).ShouldNotBe(DriftDetector.Fingerprint([a with { Live = "z" }]));
        DriftDetector.Fingerprint([unknown]).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0.0, "remindAfterHours")]
    [InlineData(-1.0, "remindAfterHours")]
    public void Validate_RemindAfter(double? hours, string? problem)
    {
        var result = new DriftConfig { RemindAfterHours = hours }.Validate();
        if (problem is null)
        {
            result.ShouldBeNull();
        }
        else
        {
            result.ShouldNotBeNull().ShouldContain(problem);
        }
    }

    [Fact]
    public void Validate_Channels_AndIgnoreRules()
    {
        new DriftConfig { Notify = [new DriftChannel { Url = "env:HOOK" }, new DriftChannel { Url = "https://example.test/x" }] }.Validate().ShouldBeNull();
        new DriftConfig { Notify = [new DriftChannel { Url = "" }] }.Validate()!.ShouldContain("drift.notify[0]");
        new DriftConfig { Notify = [new DriftChannel { Url = "hooks.example.test" }] }.Validate()!.ShouldContain("absolute URL");
        new DriftConfig { Ignore = ["api:"] }.Validate()!.ShouldContain("drift.ignore");
    }

    [Fact]
    public void TheDriftSection_IsReadFromTheStateFile_CaseInsensitively()
    {
        var json = """
            {
              "environments": {
                "prod": {
                  "target": "Nomad",
                  "drift": {
                    "notify": [ { "type": "teams", "url": "env:TEAMS_HOOK", "name": "ops" } ],
                    "RemindAfterHours": 24,
                    "ignore": [ "*:desired_count" ]
                  }
                }
              }
            }
            """;
        var file = Services.StateManager.Read(Encoding.UTF8.GetBytes(json), "deployify-state.json");

        var drift = file.Environments["prod"].Drift.ShouldNotBeNull();
        drift.Notify.ShouldHaveSingleItem().Type.ShouldBe(DriftChannelType.Teams);
        drift.Notify[0].DisplayName.ShouldBe("ops");
        drift.RemindAfterHours.ShouldBe(24);
        drift.Enabled.ShouldBeTrue();
        drift.NotifyOnResolve.ShouldBeTrue();
    }

    [Fact]
    public async Task Baseline_RoundTrips_AndPinsTheDeployedImages()
    {
        var config = new EnvironmentConfig
        {
            Name = "prod",
            Extends = "base",
            Target = DeploymentTargetType.Kubernetes,
            ContainerRegistry = "ghcr.io/acme",
            ImageTag = "4f2c9e1",
            Parameters = { ["db-password"] = "env:DB_PASSWORD" }
        };
        var manifest = new AspireManifest
        {
            Resources = { ["api"] = new AspireResource { Type = "project.v0", Path = "Api/Api.csproj", Env = new() { ["A"] = "1" } } }
        };
        var release = new ReleaseRecord
        {
            Id = "a1b2c3d4e5f6", Kind = ReleaseKind.Deploy, Environment = "prod", At = s_t0, Tag = "4f2c9e1",
            Images = [new ReleaseImage("api", "ghcr.io/acme/api", "4f2c9e1", Digest)]
        };

        await using var state = new StateSession(_dir.Path, "prod", null);
        (await DeployedBaselineStore.SaveAsync(state, DeployedBaseline.From(release, manifest, config, "Kubernetes"), null, Ct)).ShouldBeTrue();
        var stored = (await DeployedBaselineStore.ReadAsync(state, Ct)).ShouldNotBeNull();

        stored.ReleaseId.ShouldBe("a1b2c3d4e5f6");
        stored.Target.ShouldBe("Kubernetes");
        var restored = stored.ToConfig();
        restored.Name.ShouldBe("prod");
        restored.Extends.ShouldBeNull();
        restored.ImageTag.ShouldBe("4f2c9e1");
        restored.Parameters["db-password"].ShouldBe("env:DB_PASSWORD");
        restored.PublishedImages.TryGetDigest("ghcr.io/acme/api", "4f2c9e1", out var digest).ShouldBeTrue();
        digest.ShouldBe(Digest);
        var api = stored.ToManifest().Resources["api"];
        api.Path.ShouldBe("Api/Api.csproj");
        api.Env!["A"].ShouldBe("1");

        await DeployedBaselineStore.DeleteAsync(state, Ct);
        (await DeployedBaselineStore.ReadAsync(state, Ct)).ShouldBeNull();
    }

    [Fact]
    public void Baseline_FromANewerDeployify_IsRefused()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["formatVersion"] = StateFormat.Deployed + 1 });
        Should.Throw<StateFormatException>(() => DeployedBaselineStore.Read(json, "deployed.json")).Message.ShouldContain("newer Deployify");
    }

    [Fact]
    public async Task Status_RoundTrips()
    {
        await using var state = new StateSession(_dir.Path, "prod", null);
        var record = new DriftStatusRecord
        {
            Status = DriftStatus.Drifted, Fingerprint = "0123456789abcdef", Since = s_t0, CheckedAt = s_t0.AddHours(1),
            NotifiedAt = s_t0, NotifiedEvent = DriftEvent.Drifted
        };

        await DriftStatusStore.WriteAsync(state, record, Ct);

        (await DriftStatusStore.ReadAsync(state, Ct)).ShouldBe(record);
        var raw = await state.Backend.ReadAsync(DriftStatusStore.Key(state), Ct);
        JsonNode.Parse(raw!.Data)!["status"]!.GetValue<string>().ShouldBe("drifted");
    }
}
