namespace Deployify.Target.Helm;

/// <summary>
/// Helm-specific settings stored under targetConfig["helm"] in deployify-state.json. The
/// cluster, namespaces and resources come from targetConfig["kubernetes"], shared with the
/// Kubernetes target.
/// </summary>
public sealed class HelmTargetConfig
{
    /// <summary>Version written to each chart's Chart.yaml.</summary>
    public string ChartVersion { get; set; } = Kubernetes.HelmChartWriter.DefaultChartVersion;

    /// <summary>Release revisions Helm keeps per service (<c>--history-max</c>); 0 keeps all.</summary>
    public int HistoryMax { get; set; } = 10;

    /// <summary>
    /// Pass <c>--wait</c> to <c>helm upgrade</c>, so a plain apply (without the health gate)
    /// only returns once the release's resources are ready.
    /// </summary>
    public bool Wait { get; set; }

    /// <summary>How long Helm waits for one operation (<c>--timeout</c>), e.g. "5m".</summary>
    public string Timeout { get; set; } = "5m";
}
