using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Core.State;

/// <summary>
/// Keeps state as files under a root directory (by default <c>.deployify/state</c> next to
/// deployify-state.json). The version tag is a hash of the content; compare-and-swap holds an
/// exclusive guard file while it reads, compares and writes, so two processes on this machine
/// can't interleave.
/// </summary>
public sealed class LocalStateBackend(string root) : IStateBackend
{
    /// <summary>Folder under the state directory that local state lives in.</summary>
    public const string DirectoryName = ".deployify/state";

    private static readonly TimeSpan s_guardTimeout = TimeSpan.FromSeconds(10);

    public string Root { get; } = Path.GetFullPath(root);

    public string Description => Root;

    public static LocalStateBackend ForStateDirectory(string stateDirectory)
        => new(Path.Combine(stateDirectory, DirectoryName));

    public async Task<StateBlob?> ReadAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        var data = await File.ReadAllBytesAsync(path, ct);
        return new StateBlob(data, Tag(data));
    }

    public async Task<string> WriteAsync(string key, byte[] data, string? expectedETag, CancellationToken ct = default)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var guard = await GuardAsync(path, ct);

        var current = File.Exists(path) ? Tag(await File.ReadAllBytesAsync(path, ct)) : null;
        if (current != expectedETag)
        {
            throw new StateConflictException(key, expectedETag is null ? "it already exists" : null);
        }

        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, data, ct);
        if (!OperatingSystem.IsWindows())
        {
            // State can hold generated parameter values.
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tmp, path, overwrite: true);
        return Tag(data);
    }

    public async Task DeleteAsync(string key, string? expectedETag, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return;
        }

        await using (await GuardAsync(path, ct))
        {
            if (!File.Exists(path))
            {
                return;
            }

            if (expectedETag is not null && Tag(await File.ReadAllBytesAsync(path, ct)) != expectedETag)
            {
                throw new StateConflictException(key);
            }

            File.Delete(path);
        }
    }

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        if (!Directory.Exists(Root))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        IReadOnlyList<string> keys =
        [
            .. Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".tmp", StringComparison.Ordinal) && !f.EndsWith(".guard", StringComparison.Ordinal))
                .Select(f => Path.GetRelativePath(Root, f).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
        ];
        return Task.FromResult(keys);
    }

    private string PathFor(string key)
    {
        var parts = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".."))
        {
            throw new ArgumentException($"Invalid state key '{key}'.", nameof(key));
        }

        return Path.Combine([Root, .. parts]);
    }

    private static string Tag(byte[] data) => Convert.ToHexString(SHA256.HashData(data))[..32];

    /// <summary>Holds <c>{path}.guard</c> exclusively; waits briefly while another process holds it.</summary>
    private static async Task<FileStream> GuardAsync(string path, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + s_guardTimeout;
        while (true)
        {
            try
            {
                // Not DeleteOnClose: on Unix a second process could then lock the unlinked file
                // while a third creates and locks a new one.
                return new FileStream(path + ".guard", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, ct);
            }
            catch (IOException ex)
            {
                throw new StateBackendException($"Timed out waiting for {path}.guard; another deployify process is writing it.", ex);
            }
        }
    }
}
