using System.Collections.Generic;
using System.Linq;
using System.Text;
using Deployify.Target.Nomad.Api;

namespace Deployify.Target.Nomad;

/// <summary>
/// Secret env vars for Nomad jobs live in Nomad Variables at <c>nomad/jobs/{job}</c>, a path
/// every task of that job can read through its workload identity without an extra ACL policy.
/// The job file only carries a <c>template</c> that reads them back into the task's
/// environment, so no secret is written to the output directory. Needs Nomad 1.4 or later.
/// </summary>
public static class NomadVariables
{
    /// <summary>The variable path for a job's secrets.</summary>
    public static string JobPath(string jobName) => $"nomad/jobs/{jobName}";

    /// <summary>The template text that turns the job's variable items into env vars.</summary>
    public static string TemplateData(string jobName, IEnumerable<string> keys)
    {
        var sb = new StringBuilder();
        sb.Append("{{ with nomadVar \"").Append(JobPath(jobName)).Append("\" -}}\n");
        foreach (var key in keys.Order(System.StringComparer.Ordinal))
        {
            // index works for any key, including ones that aren't Go identifiers.
            sb.Append(key).Append("={{ index . \"").Append(key).Append("\" }}\n");
        }

        sb.Append("{{- end }}\n");
        return sb.ToString();
    }

    /// <summary>The task's <c>template</c> that loads the job's secrets into its environment.</summary>
    public static NomadTemplate Template(string jobName, IEnumerable<string> keys) => new()
    {
        EmbeddedTmpl = TemplateData(jobName, keys),
        DestPath = ServiceDiscovery.TemplateDestination,
        Envvars = true,
        ChangeMode = "restart"
    };
}
