using System.Linq;
using System.Text.RegularExpressions;

namespace Deployify.Target.DigitalOcean;

/// <summary>App Platform naming rules for apps, components and databases.</summary>
public static partial class Names
{
    /// <summary>True when <paramref name="name"/> is a valid app or component name.</summary>
    public static bool IsValid(string name) => Pattern().IsMatch(name);

    /// <summary>
    /// The closest valid name: lowercase, anything other than letters and digits turned into
    /// '-', trimmed to 32 characters. Aspire names are usually valid already.
    /// </summary>
    public static string Component(string name)
    {
        var cleaned = new string([.. name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
        cleaned = Dashes().Replace(cleaned, "-").Trim('-');
        if (cleaned.Length > 32)
        {
            cleaned = cleaned[..32].TrimEnd('-');
        }

        if (cleaned.Length == 0 || !char.IsAsciiLetter(cleaned[0]))
        {
            cleaned = ("c-" + cleaned).TrimEnd('-');
        }

        return cleaned.Length < 2 ? cleaned + "0" : cleaned;
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,30}[a-z0-9]$")]
    private static partial Regex Pattern();

    [GeneratedRegex("-{2,}")]
    private static partial Regex Dashes();
}
