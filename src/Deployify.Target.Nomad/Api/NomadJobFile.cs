using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Target.Nomad.Api;

/// <summary>
/// Reads and writes job files: <c>{job}.nomad.json</c>, in the shape Nomad's job API takes
/// (<c>{"Job": {…}}</c>), so the file goes to <c>POST /v1/jobs</c> as is and
/// <c>nomad job run -json</c> runs it by hand.
/// </summary>
public static class NomadJobFile
{
    /// <summary>
    /// Written into job files in place of the registry password, so the plaintext secret
    /// never lands in the output directory. <see cref="NomadClient"/> swaps the real value
    /// in, in memory, just before sending the job to Nomad.
    /// </summary>
    public const string RegistryPasswordPlaceholder = "__DEPLOYIFY_REGISTRY_PASSWORD__";

    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Template text is full of quotes and braces; keep the file readable.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The job file's name for a job.</summary>
    public static string FileName(string jobName) => $"{jobName}.nomad.json";

    /// <summary>The job as the text of a job file.</summary>
    public static string Serialize(NomadApiJob job)
        => JsonSerializer.Serialize(new JobRequest(job), s_options).ReplaceLineEndings("\n") + "\n";

    /// <summary>
    /// Writes <c>{outputDirectory}/{job}.nomad.json</c> and returns its path. A job file from a
    /// version that wrote HCL (<c>{job}.job.hcl</c>) is removed, so the folder holds one file per job.
    /// </summary>
    public static async Task<string> WriteAsync(NomadApiJob job, string outputDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDirectory);
        File.Delete(Path.Combine(outputDirectory, $"{job.Id}.job.hcl"));
        var path = Path.Combine(outputDirectory, FileName(job.Id));
        await File.WriteAllTextAsync(path, Serialize(job), new UTF8Encoding(false), ct);
        return path;
    }

    /// <summary>
    /// Reads a job file and returns the job object, with the registry password placeholder
    /// replaced by <paramref name="registryPassword"/> when one is given.
    /// </summary>
    public static async Task<(string JobId, JsonObject Job)> ReadAsync(string path, string? registryPassword, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(path, ct);
        if (registryPassword is not null)
        {
            // The placeholder needs no JSON escaping, so its quoted form is exact.
            text = text.Replace(JsonSerializer.Serialize(RegistryPasswordPlaceholder), JsonSerializer.Serialize(registryPassword),
                StringComparison.Ordinal);
        }

        var root = JsonNode.Parse(text)?.AsObject()
                   ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not a JSON object.");
        // Accept a bare job too, the way nomad job run -json does. The job is detached from the
        // file's root so it can go into a request body.
        var job = root;
        if (root["Job"] is JsonObject wrapped)
        {
            root.Remove("Job");
            job = wrapped;
        }

        var id = job["ID"]?.GetValue<string>()
                 ?? throw new InvalidDataException($"{Path.GetFileName(path)} has no job ID.");
        return (id, job);
    }

    private sealed record JobRequest(NomadApiJob Job);
}

/// <summary>Go duration strings (<c>10s</c>, <c>1m30s</c>, <c>1500ms</c>) as the nanoseconds Nomad's API takes.</summary>
public static class NomadDuration
{
    /// <summary>Nanoseconds in <paramref name="duration"/>. Throws a readable error for anything Go wouldn't parse.</summary>
    public static long Parse(string duration)
    {
        var text = duration.Trim();
        if (text is "0")
        {
            return 0;
        }

        var negative = text.StartsWith('-');
        var i = negative || text.StartsWith('+') ? 1 : 0;
        if (i == text.Length)
        {
            throw Invalid(duration);
        }

        decimal total = 0;
        while (i < text.Length)
        {
            var start = i;
            while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'))
            {
                i++;
            }

            if (start == i || !decimal.TryParse(text[start..i], System.Globalization.NumberStyles.AllowDecimalPoint,
                    System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                throw Invalid(duration);
            }

            var unitStart = i;
            while (i < text.Length && !char.IsAsciiDigit(text[i]) && text[i] != '.')
            {
                i++;
            }

            total += number * text[unitStart..i] switch
            {
                "ns" => 1m,
                "us" or "µs" or "μs" => 1_000m,
                "ms" => 1_000_000m,
                "s" => 1_000_000_000m,
                "m" => 60_000_000_000m,
                "h" => 3_600_000_000_000m,
                _ => throw Invalid(duration)
            };
        }

        var ns = (long)decimal.Round(total, MidpointRounding.ToZero);
        return negative ? -ns : ns;
    }

    private static FormatException Invalid(string duration)
        => new($"'{duration}' is not a duration. Use a number with a unit, such as 10s, 1m30s or 500ms.");
}
