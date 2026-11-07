using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Deployify.Target.Nomad;

/// <summary>
/// Secret env vars for Nomad jobs live in Nomad Variables at <c>nomad/jobs/{job}</c>, a path
/// every task of that job can read through its workload identity without an extra ACL policy.
/// The job file only carries a <c>template</c> block that reads them back into the task's
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

    /// <summary>Appends a <c>template</c> block (inside a task) that loads the job's secrets.</summary>
    public static void AppendTemplate(StringBuilder sb, string jobName, IEnumerable<string> keys, string indent)
    {
        sb.AppendLine();
        sb.AppendLine($"{indent}template {{");
        sb.AppendLine($"{indent}  data        = {Hcl.Quote(TemplateData(jobName, keys))}");
        sb.AppendLine($"{indent}  destination = \"secrets/deployify.env\"");
        sb.AppendLine($"{indent}  env         = true");
        sb.AppendLine($"{indent}  change_mode = \"restart\"");
        sb.AppendLine($"{indent}}}");
    }
}
