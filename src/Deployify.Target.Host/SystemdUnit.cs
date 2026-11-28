using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Deployify.Target.Host;

/// <summary>
/// The systemd unit and environment file for a service. The unit never changes between
/// releases: it starts <c>{root}/{name}/current/run</c> with <c>current/env</c>, and a deploy
/// or rollback only repoints the <c>current</c> symlink, so both switch together.
/// </summary>
internal static partial class SystemdUnit
{
    /// <summary>Stands in for the deploy user in a rendered unit; the host script fills it in.</summary>
    public const string UserToken = "%DEPLOY_USER%";

    public static string ServiceDirectory(string root, HostService service) => $"{root.TrimEnd('/')}/{service.Name}";

    public static string Render(HostService service, string root, string user = UserToken)
    {
        var dir = ServiceDirectory(root, service);
        var sb = new StringBuilder();
        sb.Append("# Written by deployify for ").Append(service.Environment ?? "-").Append('/').Append(service.Service)
            .Append(". Do not edit: the next deploy rewrites it.\n");
        sb.Append("[Unit]\n");
        sb.Append("Description=").Append(service.Service).Append(" (").Append(service.Environment ?? "deployify").Append(", deployed by Deployify)\n");
        sb.Append("After=network-online.target\n");
        sb.Append("Wants=network-online.target\n");
        sb.Append('\n');
        sb.Append("[Service]\n");
        sb.Append("Type=simple\n");
        sb.Append("User=").Append(user).Append('\n');
        sb.Append("WorkingDirectory=").Append(dir).Append("/current/app\n");
        sb.Append("EnvironmentFile=").Append(dir).Append("/current/env\n");
        sb.Append("ExecStart=").Append(dir).Append("/current/run\n");
        sb.Append("Restart=on-failure\n");
        sb.Append("RestartSec=5\n");
        sb.Append("TimeoutStopSec=30\n");
        sb.Append("SyslogIdentifier=").Append(service.Name).Append('\n');
        sb.Append('\n');
        sb.Append("[Install]\n");
        sb.Append("WantedBy=multi-user.target\n");
        return sb.ToString();
    }

    /// <summary>
    /// The EnvironmentFile: one <c>KEY="value"</c> per variable, sorted. Inside double quotes
    /// systemd keeps the character after a backslash for <c>" \ ` $</c>, so those are escaped.
    /// </summary>
    public static string RenderEnvironment(IEnumerable<KeyValuePair<string, string>> variables)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in variables.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(key).Append("=\"");
            foreach (var ch in value)
            {
                if (ch is '"' or '\\' or '`' or '$')
                {
                    sb.Append('\\');
                }

                sb.Append(ch);
            }

            sb.Append("\"\n");
        }

        return sb.ToString();
    }

    /// <summary>True for a variable name systemd and Windows both accept.</summary>
    public static bool IsValidVariableName(string name) => VariableName().IsMatch(name);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.:-]*$")]
    private static partial Regex VariableName();
}
