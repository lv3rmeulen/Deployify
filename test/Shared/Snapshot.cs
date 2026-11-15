using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace Deployify.Tests.Shared;

/// <summary>
/// Compares generated output with files checked in under a <c>Snapshots</c> folder next to the
/// test. Run with <c>DEPLOYIFY_UPDATE_SNAPSHOTS=1</c> to write the current output as the new
/// expected files, then review the diff before committing it.
/// </summary>
internal static class Snapshot
{
    private const string UpdateVariable = "DEPLOYIFY_UPDATE_SNAPSHOTS";

    public static bool Updating => Environment.GetEnvironmentVariable(UpdateVariable) == "1";

    /// <summary>
    /// The directory of the source file at <paramref name="callerFile"/>. CI builds are
    /// deterministic (DotNet.ReproducibleBuilds maps the checkout to <c>/_/</c> in compiled paths),
    /// so a mapped path is resolved against the repository root, found from the test binaries.
    /// </summary>
    public static string SourceDirectory([CallerFilePath] string callerFile = "")
    {
        var directory = Path.GetDirectoryName(callerFile)!;
        if (Directory.Exists(directory))
        {
            return directory;
        }

        var relative = callerFile.Replace('\\', '/');
        relative = relative.StartsWith("/_/", StringComparison.Ordinal) ? relative[3..] : relative.TrimStart('/');
        for (var root = new DirectoryInfo(AppContext.BaseDirectory); root is not null; root = root.Parent)
        {
            if (File.Exists(Path.Combine(root.FullName, "Deployify.slnx")))
            {
                return Path.GetDirectoryName(Path.Combine(root.FullName, relative))!;
            }
        }

        throw new DirectoryNotFoundException($"Could not find the source folder of {callerFile}.");
    }

    /// <summary>
    /// Checks a set of files (name → content) against <c>Snapshots/{name}/</c>. Each pair is
    /// compared after <paramref name="normalize"/>, so a test can ignore differences that don't
    /// change meaning (YAML quoting, key order).
    /// </summary>
    public static void Verify(
        string name,
        IReadOnlyDictionary<string, string> files,
        Func<string, string, string>? normalize = null,
        [CallerFilePath] string callerFile = "")
    {
        normalize ??= (_, content) => content;
        var dir = Path.Combine(SourceDirectory(callerFile), "Snapshots", name);

        if (Updating)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

            foreach (var (file, content) in files)
            {
                var path = Path.Combine(dir, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content.ReplaceLineEndings("\n"));
            }

            return;
        }

        Assert.True(Directory.Exists(dir),
            $"No snapshot at {dir}. Run the tests with {UpdateVariable}=1 to create it.");

        var expected = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal);

        Assert.Equal(
            expected.Keys.Order(StringComparer.Ordinal),
            files.Keys.Select(k => k.Replace('\\', '/')).Order(StringComparer.Ordinal));

        foreach (var (file, content) in files)
        {
            var key = file.Replace('\\', '/');
            Assert.True(
                normalize(key, expected[key].ReplaceLineEndings("\n")) == normalize(key, content.ReplaceLineEndings("\n")),
                $"{name}/{key} differs from its snapshot.\n--- expected ---\n{normalize(key, expected[key])}\n--- actual ---\n{normalize(key, content)}");
        }
    }

    /// <summary>Checks one piece of text against <c>Snapshots/{name}</c>.</summary>
    public static void Verify(string name, string content, [CallerFilePath] string callerFile = "")
    {
        var path = Path.Combine(SourceDirectory(callerFile), "Snapshots", name);
        content = content.ReplaceLineEndings("\n");

        if (Updating)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return;
        }

        Assert.True(File.Exists(path), $"No snapshot at {path}. Run the tests with {UpdateVariable}=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), content);
    }
}
