using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Deployify.Target.Nomad;

/// <summary>
/// Lets services find each other on a multi-node Nomad cluster.
/// </summary>
/// <remarks>
/// <para>
/// A reference to another service, such as <c>{api.bindings.http.url}</c>, can't be filled in
/// when the job is generated, because nobody knows yet which node the scheduler will place
/// <c>api</c> on. The generator writes a <see cref="Token"/> where the host goes instead, and
/// the job's <c>template</c> block replaces it at runtime with the address the dependency is
/// registered under in the service catalog (<c>nomadService</c>, or <c>service</c> when the
/// provider is Consul). Ports stay static, so only the host needs looking up.
/// </para>
/// <para>
/// The template uses <c>change_mode = "restart"</c>, so when a dependency moves to another
/// node the task restarts with its new address.
/// </para>
/// </remarks>
public static partial class ServiceDiscovery
{
    private const string Prefix = "@@deployify-svc:";
    private const string Suffix = "@@";

    /// <summary>Destination of the task's env template, inside the task's private secrets dir.</summary>
    public const string TemplateDestination = "secrets/deployify.env";

    /// <summary>Stands in for the host of <paramref name="service"/> until the template runs.</summary>
    public static string Token(string service) => $"{Prefix}{service}{Suffix}";

    /// <summary>True when <paramref name="value"/> refers to another service's address.</summary>
    public static bool HasReference(string? value) => value is not null && value.Contains(Prefix, StringComparison.Ordinal);

    /// <summary>The services <paramref name="value"/> refers to, in order of first appearance.</summary>
    public static IEnumerable<string> References(string value)
        => TokenRegex().Matches(value).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal);

    /// <summary>Replaces every token with <paramref name="host"/> (used where no template can run).</summary>
    public static string Replace(string value, Func<string, string> host)
        => TokenRegex().Replace(value, m => host(m.Groups[1].Value));

    /// <summary>A readable form of <paramref name="value"/> for plan and explain output.</summary>
    public static string ForDisplay(string value)
        => Replace(value, service => $"<{service} via service discovery>");

    /// <summary>
    /// Builds the template text that writes the task's env file: discovered variables, then
    /// secrets from Nomad Variables, whose values may also contain tokens.
    /// </summary>
    public static string TemplateData(
        string jobName,
        IReadOnlyDictionary<string, string> discoveredEnv,
        IReadOnlyDictionary<string, string> secretEnv,
        string provider)
    {
        var services = discoveredEnv.Values.Concat(secretEnv.Values)
            .SelectMany(References)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var vars = services.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => $"$svc{x.i}", StringComparer.Ordinal);

        var sb = new StringBuilder();
        foreach (var service in services)
        {
            AppendLookup(sb, service, vars[service], provider);
        }

        foreach (var (key, value) in discoveredEnv.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(key).Append('=').Append(ToTemplateText(value, vars)).Append('\n');
        }

        if (secretEnv.Count > 0)
        {
            sb.Append("{{ with nomadVar \"").Append(NomadVariables.JobPath(jobName)).Append("\" -}}\n");
            foreach (var (key, value) in secretEnv.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                // The variable holds the value with its tokens; swap them for the looked-up hosts.
                // index works for any key, including ones that aren't Go identifiers.
                sb.Append(key).Append("={{ index . \"").Append(key).Append('"');
                foreach (var service in References(value))
                {
                    sb.Append(" | replaceAll \"").Append(Token(service)).Append("\" ").Append(vars[service]);
                }

                sb.Append(" }}\n");
            }

            sb.Append("{{- end }}\n");
        }

        return sb.ToString();
    }

    /// <summary>Appends a <c>template</c> block (inside a task) that writes the task's env file.</summary>
    public static void AppendTemplate(
        StringBuilder sb, string jobName,
        IReadOnlyDictionary<string, string> discoveredEnv,
        IReadOnlyDictionary<string, string> secretEnv,
        string provider, string indent)
    {
        sb.AppendLine();
        sb.AppendLine($"{indent}template {{");
        sb.AppendLine($"{indent}  data        = {Hcl.Quote(TemplateData(jobName, discoveredEnv, secretEnv, provider))}");
        sb.AppendLine($"{indent}  destination = \"{TemplateDestination}\"");
        sb.AppendLine($"{indent}  env         = true");
        sb.AppendLine($"{indent}  change_mode = \"restart\"");
        sb.AppendLine($"{indent}}}");
    }

    /// <summary>
    /// Declares <paramref name="variable"/> and sets it to the address of one registered
    /// instance of <paramref name="service"/>. The trailing <c>-}}</c> keeps the line out of the env file.
    /// </summary>
    private static void AppendLookup(StringBuilder sb, string service, string variable, string provider)
    {
        sb.Append("{{ ").Append(variable).Append(" := \"\" }}");
        if (string.Equals(provider, "consul", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("{{ range service \"").Append(service).Append("\" }}");
        }
        else
        {
            // Picks one instance per allocation by a stable hash, so callers spread over replicas.
            sb.Append("{{ range nomadService 1 (env \"NOMAD_ALLOC_ID\") \"").Append(service).Append("\" }}");
        }

        sb.Append("{{ ").Append(variable).Append(" = .Address }}{{ end -}}\n");
    }

    /// <summary>Literal text with tokens swapped for template variables; any literal <c>{{</c> is escaped.</summary>
    private static string ToTemplateText(string value, IReadOnlyDictionary<string, string> vars)
    {
        var sb = new StringBuilder();
        var last = 0;
        foreach (Match m in TokenRegex().Matches(value))
        {
            sb.Append(EscapeLiteral(value[last..m.Index]));
            sb.Append("{{ ").Append(vars[m.Groups[1].Value]).Append(" }}");
            last = m.Index + m.Length;
        }

        sb.Append(EscapeLiteral(value[last..]));
        return sb.ToString();
    }

    private static string EscapeLiteral(string text) => text.Replace("{{", "{{ \"{{\" }}", StringComparison.Ordinal);

    [GeneratedRegex("@@deployify-svc:(.+?)@@")]
    private static partial Regex TokenRegex();
}
