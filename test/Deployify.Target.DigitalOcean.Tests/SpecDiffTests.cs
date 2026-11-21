using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Deployify.Core.Models;
using Shouldly;
using Xunit;

namespace Deployify.Target.DigitalOcean.Tests;

public sealed class SpecDiffTests
{
    private static readonly Dictionary<string, string> s_resources = new() { ["api"] = "api", ["pg"] = "pg" };

    private static JsonObject Desired() => JsonNode.Parse(
        """
        {
          "name": "shop",
          "services": [{
            "name": "api", "http_port": 8080, "instance_count": 1,
            "image": { "registry_type": "DOCR", "repository": "api", "tag": "v1" },
            "envs": [
              { "key": "B", "value": "2", "scope": "RUN_TIME", "type": "GENERAL" },
              { "key": "TOKEN", "value": "plain-secret", "scope": "RUN_TIME", "type": "SECRET" }
            ]
          }],
          "databases": [{ "name": "pg", "engine": "PG", "version": "17" }],
          "ingress": { "rules": [{ "match": { "path": { "prefix": "/" } }, "component": { "name": "api" } }] }
        }
        """)!.AsObject();

    /// <summary>What App Platform returns for the same spec: defaults filled in, envs reordered, the secret encrypted.</summary>
    private static JsonObject Live() => JsonNode.Parse(
        """
        {
          "name": "shop", "region": "ams",
          "services": [{
            "name": "api", "http_port": 8080, "instance_count": 1, "instance_size_slug": "apps-s-1vcpu-0.5gb",
            "image": { "registry_type": "DOCR", "repository": "api", "tag": "v1" },
            "envs": [
              { "key": "TOKEN", "value": "EV[1:abc:def]", "scope": "RUN_TIME", "type": "SECRET" },
              { "key": "B", "value": "2", "scope": "RUN_TIME", "type": "GENERAL" }
            ]
          }],
          "databases": [{ "name": "pg", "engine": "PG", "version": "17", "production": false }],
          "ingress": { "rules": [{ "match": { "path": { "prefix": "/" } }, "component": { "name": "api", "preserve_path_prefix": false } }] }
        }
        """)!.AsObject();

    [Fact]
    public void PlatformDefaultsAndEncryptedSecrets_AreNotChanges()
    {
        var entries = SpecDiff.Compare(Desired(), Live(), s_resources);

        entries.ShouldAllBe(e => e.Kind == DiffKind.Unchanged);
        entries.Select(e => e.ServiceName).ShouldBe(["api", "pg"], ignoreOrder: true);
    }

    [Fact]
    public void NoApp_EverythingIsAdded()
    {
        var entries = SpecDiff.Compare(Desired(), null, s_resources);

        entries.Select(e => (e.ServiceName, e.Kind)).ShouldBe([("api", DiffKind.Added), ("pg", DiffKind.Added)], ignoreOrder: true);
    }

    [Fact]
    public void ChangedFields_AreReportedOneByOne()
    {
        var desired = Desired();
        desired["services"]![0]!["image"]!["tag"] = "v2";
        desired["services"]![0]!["envs"]!.AsArray().Add(new JsonObject { ["key"] = "NEW", ["value"] = "x", ["type"] = "GENERAL" });

        var changes = SpecDiff.Compare(desired, Live(), s_resources).Where(e => e.Kind == DiffKind.Changed).ToList();

        changes.Select(c => (c.ServiceName, c.Field, c.OldValue, c.NewValue)).ShouldBe(
        [
            ("api", "image.tag", "v1", "v2"),
            ("api", "env.NEW", null, "x")
        ], ignoreOrder: true);
    }

    [Fact]
    public void ARemovedSecret_IsAChange_ButItsValueIsNotShown()
    {
        var desired = Desired();
        var envs = desired["services"]![0]!["envs"]!.AsArray();
        envs.RemoveAt(1);

        var change = SpecDiff.Compare(desired, Live(), s_resources).Single(e => e.Kind == DiffKind.Changed);

        (change.Field, change.OldValue, change.NewValue).ShouldBe(("env.TOKEN", "(secret)", (string?)null));
    }

    [Fact]
    public void ComponentsOnlyTheLiveAppHas_AreRemoved()
    {
        var live = Live();
        live["workers"] = new JsonArray(new JsonObject { ["name"] = "old-worker" });

        var entries = SpecDiff.Compare(Desired(), live, s_resources);

        entries.ShouldContain(e => e.ServiceName == "old-worker" && e.Kind == DiffKind.Removed);
    }

    [Fact]
    public void ARouteChange_IsReportedUnderTheAppName()
    {
        var desired = Desired();
        desired["ingress"]!["rules"]![0]!["match"]!["path"]!["prefix"] = "/shop";

        var change = SpecDiff.Compare(desired, Live(), s_resources).Single(e => e.Kind == DiffKind.Changed);

        (change.ServiceName, change.Field).ShouldBe(("shop", "ingress"));
    }
}
