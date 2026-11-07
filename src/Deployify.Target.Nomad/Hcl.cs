using System.Text;

namespace Deployify.Target.Nomad;

/// <summary>Helpers for writing values into HCL job files safely.</summary>
public static class Hcl
{
    /// <summary>
    /// Written into job files in place of the registry password, so the plaintext secret
    /// never lands in the output directory. <see cref="NomadClient"/> swaps the real value
    /// in, in memory, just before sending the job to Nomad.
    /// </summary>
    public const string RegistryPasswordPlaceholder = "__DEPLOYIFY_REGISTRY_PASSWORD__";

    /// <summary>Replaces the quoted password placeholder with the quoted real value.</summary>
    public static string InjectRegistryPassword(string hcl, string? password)
        => password is null ? hcl : hcl.Replace(Quote(RegistryPasswordPlaceholder), Quote(password));

    /// <summary>
    /// Returns <paramref name="value"/> as a quoted HCL string literal. Backslashes, quotes
    /// and control characters are escaped, and <c>${</c> / <c>%{</c> are doubled so Nomad
    /// does not treat user values (passwords, connection strings) as interpolation.
    /// </summary>
    public static string Quote(string? value)
    {
        if (value is null)
        {
            return "\"\"";
        }

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '$' or '%' when i + 1 < value.Length && value[i + 1] == '{':
                    sb.Append(c).Append(c); break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append($"\\u{(int)c:x4}");
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}
