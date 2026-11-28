using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Deployify.Target.Host;

internal static class HostScripts
{
    public static string Load(string resourceName)
    {
        using var stream = typeof(HostScripts).Assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"{resourceName} is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The script, written once per version to the temp folder, so every command runs the
    /// script this build of Deployify ships, whatever is in the output folder.
    /// </summary>
    public static string TempCopy(string fileName, string content)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..12];
        var dir = Path.Combine(Path.GetTempPath(), "deployify");
        var path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(fileName)}-{hash}{Path.GetExtension(fileName)}");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(dir);
            var temp = path + "." + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }

        return path;
    }
}
