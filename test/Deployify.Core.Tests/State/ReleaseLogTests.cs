using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Deployify.Core.State;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.State;

/// <summary>
/// Release history: one create-only record per release under {env}/releases/, listed newest
/// first, pruned to a fixed number, and versioned like every other state file.
/// </summary>
public sealed class ReleaseLogTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset s_t0 = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly TempDir _local = new();
    private readonly TempDir _remoteDir = new();
    private readonly LocalStateBackend _remote;

    public ReleaseLogTests() => _remote = new LocalStateBackend(_remoteDir.Path);

    public void Dispose()
    {
        _local.Dispose();
        _remoteDir.Dispose();
    }

    private StateSession Session(string env = "prod")
        => new(_local.Path, env, new StateBackendConfig { Backend = StateBackendType.Consul, Prefix = "deployify" }, _remote);

    private static ReleaseRecord Record(DateTimeOffset at, ReleaseKind kind = ReleaseKind.Deploy, string env = "prod", string? id = null) => new()
    {
        Id = id ?? ReleaseLog.NewId(),
        Kind = kind,
        Environment = env,
        At = at
    };

    [Fact]
    public async Task A_record_round_trips_with_every_field()
    {
        await using var state = Session();
        var record = new ReleaseRecord
        {
            Id = "a1b2c3d4e5f6",
            Kind = ReleaseKind.Promote,
            Environment = "prod",
            At = s_t0,
            Outcome = ReleaseOutcome.Succeeded,
            Images = [new ReleaseImage("api", "ghcr.io/acme/api", "4f2c9e1", Digest)],
            Tag = "4f2c9e1",
            Commit = "4f2c9e1a7b3d",
            FromEnvironment = "staging",
            Actor = "lv3rmeulen",
            ActorSource = "GITHUB_ACTOR",
            RunUrl = "https://github.com/acme/shop/actions/runs/42",
            DeployifyVersion = "1.1.0"
        };

        await ReleaseLog.AppendAsync(state, record, ct: Ct);

        var stored = (await ReleaseLog.ListAsync(state, ct: Ct)).ShouldHaveSingleItem();
        stored.ShouldBe(record with { Images = stored.Images });
        stored.Images.ShouldBe(record.Images);
    }

    [Fact]
    public async Task Records_are_stored_one_per_key_under_the_environment()
    {
        await using var state = Session();
        await ReleaseLog.AppendAsync(state, Record(s_t0, id: "abc123def456"), ct: Ct);

        (await _remote.ListAsync("deployify/prod/releases/", Ct))
            .ShouldBe(["deployify/prod/releases/20261122T090000000Z-abc123def456.json"]);
    }

    [Fact]
    public async Task The_stored_format_is_versioned_camelCase_with_enum_names()
    {
        await using var state = Session();
        var record = Record(s_t0, ReleaseKind.Rollback) with { Outcome = ReleaseOutcome.RolledBack };
        await ReleaseLog.AppendAsync(state, record, ct: Ct);

        var json = JsonNode.Parse((await _remote.ReadAsync(ReleaseLog.Key(state, record), Ct))!.Data)!.AsObject();
        json["formatVersion"]!.GetValue<int>().ShouldBe(StateFormat.Releases);
        json.First().Key.ShouldBe("formatVersion");
        json["kind"]!.GetValue<string>().ShouldBe("rollback");
        json["outcome"]!.GetValue<string>().ShouldBe("rolledBack");
        json.ContainsKey("fromEnvironment").ShouldBeFalse("unset properties are left out");
    }

    [Fact]
    public async Task History_is_listed_newest_first_and_limited()
    {
        await using var state = Session();
        for (var i = 0; i < 5; i++)
        {
            await ReleaseLog.AppendAsync(state, Record(s_t0.AddMinutes(i), id: $"id{i:D10}"), ct: Ct);
        }

        var history = await ReleaseLog.ListAsync(state, limit: 3, ct: Ct);

        history.Select(r => r.Id).ShouldBe(["id0000000004", "id0000000003", "id0000000002"]);
    }

    [Fact]
    public async Task Two_releases_in_the_same_millisecond_are_both_kept()
    {
        await using var state = Session();
        await Task.WhenAll(
            ReleaseLog.AppendAsync(state, Record(s_t0), ct: Ct),
            ReleaseLog.AppendAsync(state, Record(s_t0), ct: Ct));

        (await ReleaseLog.ListAsync(state, ct: Ct)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_existing_record_is_never_overwritten()
    {
        await using var state = Session();
        var record = Record(s_t0, id: "fixedid00001");
        await ReleaseLog.AppendAsync(state, record, ct: Ct);

        await Should.ThrowAsync<StateConflictException>(
            () => ReleaseLog.AppendAsync(state, record with { Actor = "someone-else" }, ct: Ct));
        (await ReleaseLog.ListAsync(state, ct: Ct)).ShouldHaveSingleItem().Actor.ShouldBeNull();
    }

    [Fact]
    public async Task Appending_prunes_the_oldest_records_beyond_keep()
    {
        await using var state = Session();
        for (var i = 0; i < 6; i++)
        {
            await ReleaseLog.AppendAsync(state, Record(s_t0.AddMinutes(i), id: $"id{i:D10}"), keep: 4, ct: Ct);
        }

        var history = await ReleaseLog.ListAsync(state, limit: 100, ct: Ct);

        history.Select(r => r.Id).ShouldBe(["id0000000005", "id0000000004", "id0000000003", "id0000000002"]);
    }

    [Fact]
    public async Task Keep_zero_keeps_everything()
    {
        await using var state = Session();
        for (var i = 0; i < 3; i++)
        {
            await ReleaseLog.AppendAsync(state, Record(s_t0.AddMinutes(i)), keep: 0, ct: Ct);
        }

        (await ReleaseLog.ListAsync(state, limit: 100, ct: Ct)).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Keep_minus_one_records_nothing_and_leaves_earlier_records_alone()
    {
        await using var state = Session();
        (await ReleaseLog.AppendAsync(state, Record(s_t0), ct: Ct)).ShouldBeTrue();

        for (var i = 1; i <= 3; i++)
        {
            (await ReleaseLog.AppendAsync(state, Record(s_t0.AddMinutes(i)), keep: ReleaseLog.Disabled, ct: Ct)).ShouldBeFalse();
        }

        (await ReleaseLog.ListAsync(state, ct: Ct)).ShouldHaveSingleItem().At.ShouldBe(s_t0);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-100)]
    public async Task Negative_keep_other_than_minus_one_is_refused(int keep)
    {
        await using var state = Session();

        var ex = await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => ReleaseLog.AppendAsync(state, Record(s_t0), keep, Ct));

        ex.Message.ShouldContain($"state.history.keep is {keep}");
        (await ReleaseLog.ListAsync(state, ct: Ct)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    public void A_state_config_with_negative_keep_other_than_minus_one_is_refused(int keep)
    {
        var ex = Should.Throw<StateBackendException>(
            () => new StateSession(_local.Path, "prod", new StateBackendConfig { History = { Keep = keep } }, _remote));

        ex.Message.ShouldContain($"state.history.keep is {keep}");
        ex.Message.ShouldContain("-1 to record none");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Minus_one_zero_and_positive_keep_are_accepted(int keep)
    {
        await using var state = new StateSession(_local.Path, "prod", new StateBackendConfig { History = { Keep = keep } }, _remote);

        state.Config.History.Keep.ShouldBe(keep);
    }

    [Fact]
    public async Task Each_environment_has_its_own_history()
    {
        await using var prod = Session("prod");
        await using var staging = Session("staging");
        await ReleaseLog.AppendAsync(prod, Record(s_t0, env: "prod"), ct: Ct);
        await ReleaseLog.AppendAsync(staging, Record(s_t0, env: "staging"), ct: Ct);
        await ReleaseLog.AppendAsync(staging, Record(s_t0.AddMinutes(1), env: "staging"), ct: Ct);

        (await ReleaseLog.ListAsync(prod, ct: Ct)).ShouldHaveSingleItem().Environment.ShouldBe("prod");
        (await ReleaseLog.ListAsync(staging, ct: Ct)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_record_for_another_environment_is_refused()
    {
        await using var state = Session("prod");

        await Should.ThrowAsync<ArgumentException>(
            () => ReleaseLog.AppendAsync(state, Record(s_t0, env: "staging"), ct: Ct));
    }

    [Fact]
    public async Task Other_keys_under_the_prefix_are_ignored()
    {
        await using var state = Session();
        await _remote.WriteAsync("deployify/prod/releases/archive/old.json", "{}"u8.ToArray(), null, Ct);
        await _remote.WriteAsync("deployify/prod/releases/README.txt", "hi"u8.ToArray(), null, Ct);
        await ReleaseLog.AppendAsync(state, Record(s_t0), keep: 1, ct: Ct);

        (await ReleaseLog.ListAsync(state, ct: Ct)).ShouldHaveSingleItem();
        (await _remote.ReadAsync("deployify/prod/releases/archive/old.json", Ct)).ShouldNotBeNull("prune leaves unknown keys alone");
    }

    [Fact]
    public async Task A_record_from_a_newer_Deployify_is_refused_with_an_explanation()
    {
        await using var state = Session();
        await _remote.WriteAsync("deployify/prod/releases/20261122T090000000Z-future000001.json",
            Encoding.UTF8.GetBytes($$"""{ "formatVersion": {{StateFormat.Releases + 1}}, "id": "future000001" }"""), null, Ct);

        var ex = await Should.ThrowAsync<StateFormatException>(() => ReleaseLog.ListAsync(state, ct: Ct));

        ex.Message.ShouldContain($"format version {StateFormat.Releases + 1}");
        ex.Message.ShouldContain("update Deployify");
    }

    [Fact]
    public void A_file_without_a_version_is_not_a_release_record()
    {
        var ex = Should.Throw<StateFormatException>(
            () => ReleaseLog.Read("""{ "id": "x", "kind": "deploy" }"""u8.ToArray(), "test record"));

        ex.Message.ShouldContain("not a release record");
    }

    [Fact]
    public void A_newer_record_with_extra_properties_of_the_same_version_still_reads()
    {
        // Adding optional properties doesn't need a version bump, so older readers must tolerate them.
        var record = ReleaseLog.Read("""
            { "formatVersion": 1, "id": "x", "kind": "deploy", "environment": "prod",
              "at": "2026-11-22T09:00:00+00:00", "approvedBy": "someone" }
            """u8.ToArray(), "test record");

        record.Kind.ShouldBe(ReleaseKind.Deploy);
    }

    // ── ReleaseContext ─────────────────────────────────────────────────────

    private static Func<string, string?> Env(params (string Name, string Value)[] vars)
    {
        var map = vars.ToDictionary(v => v.Name, v => v.Value);
        return name => map.GetValueOrDefault(name);
    }

    [Fact]
    public void GitHub_Actions_supplies_the_actor_and_run_link()
    {
        var context = ReleaseContext.Resolve(Env(
            ("GITHUB_ACTOR", "lv3rmeulen"),
            ("GITHUB_SERVER_URL", "https://github.com"),
            ("GITHUB_REPOSITORY", "acme/shop"),
            ("GITHUB_RUN_ID", "42")));

        context.ShouldBe(new ReleaseContext("lv3rmeulen", "GITHUB_ACTOR", "https://github.com/acme/shop/actions/runs/42"));
    }

    [Fact]
    public void Azure_Pipelines_supplies_the_actor_and_run_link()
    {
        var context = ReleaseContext.Resolve(Env(
            ("BUILD_REQUESTEDFOR", "Luk"),
            ("SYSTEM_COLLECTIONURI", "https://dev.azure.com/acme/"),
            ("SYSTEM_TEAMPROJECT", "My Shop"),
            ("BUILD_BUILDID", "7")));

        context.ShouldBe(new ReleaseContext("Luk", "BUILD_REQUESTEDFOR", "https://dev.azure.com/acme/My%20Shop/_build/results?buildId=7"));
    }

    [Fact]
    public void GitLab_supplies_the_actor_and_job_link()
    {
        var context = ReleaseContext.Resolve(Env(("GITLAB_USER_LOGIN", "luk"), ("CI_JOB_URL", "https://gitlab.com/acme/shop/-/jobs/9")));

        context.ShouldBe(new ReleaseContext("luk", "GITLAB_USER_LOGIN", "https://gitlab.com/acme/shop/-/jobs/9"));
    }

    [Fact]
    public void DEPLOYIFY_ACTOR_and_DEPLOYIFY_RUN_URL_win_over_the_CI_variables()
    {
        var context = ReleaseContext.Resolve(Env(
            ("DEPLOYIFY_ACTOR", "release-bot"),
            ("DEPLOYIFY_RUN_URL", "https://ci.acme/runs/5"),
            ("GITHUB_ACTOR", "lv3rmeulen"),
            ("GITHUB_REPOSITORY", "acme/shop"),
            ("GITHUB_RUN_ID", "42")));

        context.ShouldBe(new ReleaseContext("release-bot", "DEPLOYIFY_ACTOR", "https://ci.acme/runs/5"));
    }

    [Fact]
    public void Outside_CI_the_local_account_is_the_actor()
    {
        ReleaseContext.Resolve(Env(), localUser: "luk").ShouldBe(new ReleaseContext("luk", "local", null));
    }

    [Fact]
    public void In_CI_without_a_user_variable_the_local_account_is_not_guessed()
    {
        // A CI runner's OS account ("runner", "vsts") says nothing about who started the run.
        ReleaseContext.Resolve(Env(("BUILD_URL", "https://jenkins.acme/job/shop/12/")), localUser: "jenkins")
            .ShouldBe(new ReleaseContext(null, null, "https://jenkins.acme/job/shop/12/"));
    }
}
