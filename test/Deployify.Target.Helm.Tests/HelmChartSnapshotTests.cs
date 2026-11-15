using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Deployify.Target.Helm.Tests;

/// <summary>
/// The whole chart for each service, compared as text with checked-in snapshots. Templates
/// aren't valid YAML until Helm renders them, so this comparison is exact; a deliberate change
/// updates the snapshots (<c>DEPLOYIFY_UPDATE_SNAPSHOTS=1</c>) in the same commit.
/// </summary>
public sealed class HelmChartSnapshotTests
{
    [Theory]
    [InlineData("plain", false)]
    [InlineData("ingress-and-secret", true)]
    public async Task Chart_MatchesSnapshot(string scenario, bool ingressAndSecret)
    {
        using var dir = new TempDir();
        var target = new HelmDeploymentTarget(Substitute.For<IPublisher>(), NullLoggerFactory.Instance, new FakeProcessRunner());
        await target.GenerateAsync(
            ingressAndSecret ? Fixtures.ManifestWithSecret() : Fixtures.Manifest(),
            Fixtures.Config(domain: ingressAndSecret ? "apps.example.com" : null),
            dir.Path,
            TestContext.Current.CancellationToken);

        var helmDir = Path.Combine(dir.Path, "helm");
        var files = Directory.EnumerateFiles(helmDir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(helmDir, f).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal);

        Snapshot.Verify($"helm/{scenario}", files);
    }
}
