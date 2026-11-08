using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;

namespace Deployify.Core.State;

/// <summary>
/// An S3 bucket, through the AWS CLI (so every credential source the CLI knows works). The
/// version tag is the object's ETag; writes use S3 conditional writes (<c>--if-match</c>, or
/// <c>--if-none-match *</c> to create), which need AWS CLI 2.22 or later. S3 has no
/// conditional delete for general-purpose buckets, so a delete re-reads the tag first; the
/// window that leaves is one request wide and only matters for a lock that has already expired.
/// </summary>
public sealed class S3StateBackend(S3StateConfig config, IProcessRunner? processes = null) : IStateBackend
{
    private readonly IProcessRunner _processes = processes ?? ProcessRunner.Default;

    public string Description => $"s3://{config.Bucket}";

    public async Task<StateBlob?> ReadAsync(string key, CancellationToken ct = default)
    {
        var file = TempFile();
        try
        {
            var result = await RunAsync(["get-object", "--bucket", config.Bucket, "--key", key, file], ct, sensitive: true);
            if (!result.Ok)
            {
                if (IsNotFound(result))
                {
                    return null;
                }

                throw Fail("read", key, result);
            }

            using var doc = JsonDocument.Parse(result.Stdout);
            return new StateBlob(await File.ReadAllBytesAsync(file, ct), doc.RootElement.GetProperty("ETag").GetString()!);
        }
        finally
        {
            TryDelete(file);
        }
    }

    public async Task<string> WriteAsync(string key, byte[] data, string? expectedETag, CancellationToken ct = default)
    {
        var file = TempFile();
        try
        {
            await File.WriteAllBytesAsync(file, data, ct);
            var result = await RunAsync(
            [
                "put-object", "--bucket", config.Bucket, "--key", key, "--body", file,
                "--content-type", "application/json",
                .. expectedETag is null ? (string[])["--if-none-match", "*"] : ["--if-match", expectedETag]
            ], ct);

            if (!result.Ok)
            {
                if (IsConflict(result))
                {
                    throw new StateConflictException(key, expectedETag is null ? "it already exists" : null);
                }

                throw Fail("write", key, result);
            }

            using var doc = JsonDocument.Parse(result.Stdout);
            return doc.RootElement.GetProperty("ETag").GetString()!;
        }
        finally
        {
            TryDelete(file);
        }
    }

    public async Task DeleteAsync(string key, string? expectedETag, CancellationToken ct = default)
    {
        if (expectedETag is not null)
        {
            var current = await ReadAsync(key, ct);
            if (current is null)
            {
                return;
            }

            if (current.ETag != expectedETag)
            {
                throw new StateConflictException(key);
            }
        }

        var result = await RunAsync(["delete-object", "--bucket", config.Bucket, "--key", key], ct);
        if (!result.Ok && !IsNotFound(result))
        {
            throw Fail("delete", key, result);
        }
    }

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        var result = await RunAsync(
            ["list-objects-v2", "--bucket", config.Bucket, "--prefix", prefix, "--query", "Contents[].Key"], ct);
        if (!result.Ok)
        {
            throw Fail("list", prefix, result);
        }

        var text = result.Stdout.Trim();
        if (text.Length == 0 || text == "null")
        {
            return [];
        }

        return [.. (JsonSerializer.Deserialize<List<string>>(text) ?? []).Order(StringComparer.Ordinal)];
    }

    internal IReadOnlyList<string> BuildArgs(IReadOnlyList<string> command)
    {
        var args = new List<string>();
        if (config.Profile is not null)
        {
            args.AddRange(["--profile", config.Profile]);
        }

        if (config.Region is not null)
        {
            args.AddRange(["--region", config.Region]);
        }

        args.Add("s3api");
        args.AddRange(command);
        args.AddRange(["--output", "json"]);
        return args;
    }

    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct, bool sensitive = false)
    {
        try
        {
            return await _processes.RunAsync(new ProcessSpec("aws", BuildArgs(command)) { Sensitive = sensitive }, ct);
        }
        catch (ToolNotFoundException ex)
        {
            throw new StateBackendException("The S3 state backend needs the AWS CLI (aws) on PATH.", ex);
        }
    }

    private static bool IsNotFound(ProcessResult r)
        => r.Stderr.Contains("NoSuchKey", StringComparison.Ordinal) ||
           r.Stderr.Contains("(404)", StringComparison.Ordinal) ||
           r.Stderr.Contains("Not Found", StringComparison.OrdinalIgnoreCase);

    private static bool IsConflict(ProcessResult r)
        => r.Stderr.Contains("PreconditionFailed", StringComparison.Ordinal) ||
           r.Stderr.Contains("ConditionalRequestConflict", StringComparison.Ordinal) ||
           r.Stderr.Contains("(412)", StringComparison.Ordinal);

    private StateBackendException Fail(string action, string key, ProcessResult r)
    {
        var detail = r.ErrorOrOutput.Trim();
        var hint = detail.Contains("Unknown options", StringComparison.Ordinal) && detail.Contains("if-", StringComparison.Ordinal)
            ? " The S3 state backend needs AWS CLI 2.22 or later for conditional writes."
            : "";
        return new StateBackendException($"S3 state {action} of s3://{config.Bucket}/{key} failed: {detail}{hint}");
    }

    /// <summary>An empty owner-only temp file: state can hold generated parameter values.</summary>
    private static string TempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deployify-state-{Guid.NewGuid():N}");
        File.WriteAllBytes(path, []);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
