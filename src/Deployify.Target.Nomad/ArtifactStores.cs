using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Nomad;

/// <summary>
/// Creates the appropriate <see cref="IArtifactStore"/> for a given <see cref="ArtifactStoreConfig"/>.
/// </summary>
public static class ArtifactStoreFactory
{
    public static IArtifactStore Create(ArtifactStoreConfig config, ILogger logger)
        => config.Type switch
        {
            ArtifactStoreType.S3 => new S3ArtifactStore(config, logger),
            ArtifactStoreType.AzureBlob => new AzureBlobArtifactStore(config, logger),
            ArtifactStoreType.Http => new HttpArtifactStore(config, logger),
            ArtifactStoreType.Nexus => new NexusArtifactStore(config, logger),
            ArtifactStoreType.Artifactory => new ArtifactoryArtifactStore(config, logger),
            ArtifactStoreType.Gcs => new GcsArtifactStore(config, logger),
            _ => throw new NotSupportedException($"Artifact store type '{config.Type}' is not supported.")
        };
}

// ── HTTP ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Uploads artifacts via HTTP PUT to a plain file server.
/// Artifacts are PUT to {HttpBaseUrl}/{name}-{version}.zip.
/// </summary>
public sealed class HttpArtifactStore(ArtifactStoreConfig config, ILogger logger) : IArtifactStore
{
    public async Task<string> UploadAsync(string serviceName, string version, string zipPath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(config.HttpBaseUrl))
        {
            throw new InvalidOperationException("HttpBaseUrl must be set for Http artifact store.");
        }

        var fileName = $"{serviceName}-{version}.zip";
        var url = $"{config.HttpBaseUrl.TrimEnd('/')}/{fileName}";

        logger.LogInformation("Uploading {Service} artifact to {Url}...", serviceName, url);

        using var http = new HttpClient();
        if (!string.IsNullOrEmpty(Secrets.Resolve(config.HttpToken)))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secrets.Resolve(config.HttpToken));
        }

        using var content = new StreamContent(File.OpenRead(zipPath));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        var response = await http.PutAsync(url, content, ct);
        response.EnsureSuccessStatusCode();

        logger.LogInformation("Uploaded {Service} → {Url}", serviceName, url);
        return url;
    }
}

// ── S3 ────────────────────────────────────────────────────────────────────────

/// <summary>
/// Uploads artifacts to an AWS S3 bucket using the AWS CLI.
/// Uses the AWS CLI so no SDK dependency is needed — credential chain is handled by aws-cli.
/// </summary>
public sealed class S3ArtifactStore(ArtifactStoreConfig config, ILogger logger, IProcessRunner? runner = null) : IArtifactStore
{
    private readonly IProcessRunner _runner = runner ?? ProcessRunner.Default;

    public async Task<string> UploadAsync(string serviceName, string version, string zipPath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(config.S3Bucket))
        {
            throw new InvalidOperationException("S3Bucket must be set for S3 artifact store.");
        }

        var prefix = string.IsNullOrEmpty(config.S3Prefix) ? string.Empty : $"{config.S3Prefix.TrimEnd('/')}/";
        var key = $"{prefix}{serviceName}-{version}.zip";
        var s3Uri = $"s3://{config.S3Bucket}/{key}";

        logger.LogInformation("Uploading {Service} artifact to {S3Uri}...", serviceName, s3Uri);

        List<string> args = ["s3", "cp", zipPath, s3Uri];
        if (!string.IsNullOrEmpty(config.S3AwsProfile))
        {
            args.AddRange(["--profile", config.S3AwsProfile]);
        }

        if (!string.IsNullOrEmpty(config.S3Region))
        {
            args.AddRange(["--region", config.S3Region]);
        }

        (await _runner.RunAsync(new ProcessSpec("aws", args) { Log = logger }, ct)).EnsureSuccess("aws s3 cp");

        // Return go-getter compatible S3 URL for Nomad
        var region = config.S3Region ?? "us-east-1";
        var httpsUrl = $"s3::https://s3.{region}.amazonaws.com/{config.S3Bucket}/{key}";

        logger.LogInformation("Uploaded {Service} → {Url}", serviceName, httpsUrl);
        return httpsUrl;
    }
}

// ── Azure Blob ────────────────────────────────────────────────────────────────

/// <summary>
/// Uploads artifacts to Azure Blob Storage using the Azure CLI.
/// </summary>
public sealed class AzureBlobArtifactStore(ArtifactStoreConfig config, ILogger logger, IProcessRunner? runner = null)
    : IArtifactStore
{
    private readonly IProcessRunner _runner = runner ?? ProcessRunner.Default;

    public async Task<string> UploadAsync(string serviceName, string version, string zipPath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(config.AzureStorageAccount) || string.IsNullOrEmpty(config.AzureContainer))
        {
            throw new InvalidOperationException("AzureStorageAccount and AzureContainer must be set for AzureBlob artifact store.");
        }

        var blobName = $"{serviceName}-{version}.zip";
        // A connection string holds the account key, so it goes through the environment
        // (which az reads), not the command line, where any process listing would show it.
        var connectionString = Secrets.Resolve(config.AzureConnectionString);
        List<string> auth = string.IsNullOrEmpty(connectionString)
            ? ["--account-name", config.AzureStorageAccount, "--auth-mode", "login"]
            : [];
        var env = string.IsNullOrEmpty(connectionString)
            ? null
            : new Dictionary<string, string?> { ["AZURE_STORAGE_CONNECTION_STRING"] = connectionString };

        logger.LogInformation("Uploading {Service} artifact to Azure Blob {Container}/{Blob}...",
            serviceName, config.AzureContainer, blobName);

        (await _runner.RunAsync(new ProcessSpec("az",
        [
            "storage", "blob", "upload", "--file", zipPath, "--container-name", config.AzureContainer,
            "--name", blobName, .. auth, "--overwrite"
        ]) { Env = env, Log = logger }, ct)).EnsureSuccess("az storage blob upload");

        // Get the blob URL
        var output = (await _runner.RunAsync(new ProcessSpec("az",
        [
            "storage", "blob", "url", "--container-name", config.AzureContainer,
            "--name", blobName, .. auth, "--output", "tsv"
        ]) { Env = env }, ct)).EnsureSuccess("az storage blob url").Stdout;

        var url = output.Trim();
        logger.LogInformation("Uploaded {Service} → {Url}", serviceName, url);
        return url;
    }
}

// ── Nexus ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Uploads artifacts to a Sonatype Nexus raw repository via HTTP PUT.
/// </summary>
public sealed class NexusArtifactStore(ArtifactStoreConfig config, ILogger logger) : IArtifactStore
{
    public async Task<string> UploadAsync(string serviceName, string version, string zipPath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(config.NexusUrl) || string.IsNullOrEmpty(config.NexusRepository))
        {
            throw new InvalidOperationException("NexusUrl and NexusRepository must be set for Nexus artifact store.");
        }

        var fileName = $"{serviceName}-{version}.zip";
        var url = $"{config.NexusUrl.TrimEnd('/')}/repository/{config.NexusRepository}/{serviceName}/{fileName}";

        logger.LogInformation("Uploading {Service} artifact to Nexus {Url}...", serviceName, url);

        using var http = new HttpClient();
        if (!string.IsNullOrEmpty(config.NexusUsername) && !string.IsNullOrEmpty(Secrets.Resolve(config.NexusPassword)))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{config.NexusUsername}:{Secrets.Resolve(config.NexusPassword)}"));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        using var content = new StreamContent(File.OpenRead(zipPath));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        var response = await http.PutAsync(url, content, ct);
        response.EnsureSuccessStatusCode();

        logger.LogInformation("Uploaded {Service} → {Url}", serviceName, url);
        return url;
    }
}

// ── Artifactory ───────────────────────────────────────────────────────────────

/// <summary>
/// Uploads artifacts to JFrog Artifactory via the REST API.
/// </summary>
public sealed class ArtifactoryArtifactStore(ArtifactStoreConfig config, ILogger logger) : IArtifactStore
{
    public async Task<string> UploadAsync(string serviceName, string version, string zipPath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(config.ArtifactoryUrl) || string.IsNullOrEmpty(config.ArtifactoryRepository))
        {
            throw new InvalidOperationException("ArtifactoryUrl and ArtifactoryRepository must be set for Artifactory artifact store.");
        }

        var fileName = $"{serviceName}-{version}.zip";
        var url = $"{config.ArtifactoryUrl.TrimEnd('/')}/{config.ArtifactoryRepository}/{serviceName}/{fileName}";

        logger.LogInformation("Uploading {Service} artifact to Artifactory {Url}...", serviceName, url);

        using var http = new HttpClient();
        if (!string.IsNullOrEmpty(Secrets.Resolve(config.ArtifactoryToken)))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secrets.Resolve(config.ArtifactoryToken));
        }

        using var content = new StreamContent(File.OpenRead(zipPath));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        var response = await http.PutAsync(url, content, ct);
        response.EnsureSuccessStatusCode();

        logger.LogInformation("Uploaded {Service} → {Url}", serviceName, url);
        return url;
    }
}

// ── GCS ───────────────────────────────────────────────────────────────────────

/// <summary>
/// Uploads artifacts to Google Cloud Storage using the gcloud CLI.
/// </summary>
public sealed class GcsArtifactStore(ArtifactStoreConfig config, ILogger logger, IProcessRunner? runner = null) : IArtifactStore
{
    private readonly IProcessRunner _runner = runner ?? ProcessRunner.Default;

    public async Task<string> UploadAsync(string serviceName, string version, string zipPath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(config.GcsBucket))
        {
            throw new InvalidOperationException("GcsBucket must be set for GCS artifact store.");
        }

        var prefix = string.IsNullOrEmpty(config.GcsPrefix) ? string.Empty : $"{config.GcsPrefix.TrimEnd('/')}/";
        var objectName = $"{prefix}{serviceName}-{version}.zip";
        var gcsUri = $"gs://{config.GcsBucket}/{objectName}";
        var keyFileEnv = string.IsNullOrEmpty(config.GcsCredentialsFile)
            ? null : config.GcsCredentialsFile;

        logger.LogInformation("Uploading {Service} artifact to GCS {Uri}...", serviceName, gcsUri);

        // Use gcloud storage cp (newer) or gsutil cp (older)
        var env = keyFileEnv is not null
            ? new Dictionary<string, string?> { ["GOOGLE_APPLICATION_CREDENTIALS"] = keyFileEnv }
            : null;

        (await _runner.RunAsync(new ProcessSpec("gcloud", ["storage", "cp", zipPath, gcsUri]) { Env = env, Log = logger }, ct))
            .EnsureSuccess("gcloud storage cp");

        // Make the object publicly readable so Nomad can download it
        // (skip if using signed URLs / private bucket with Nomad having credentials)
        // Return go-getter compatible GCS URL
        var downloadUrl = $"gcs::https://storage.googleapis.com/{config.GcsBucket}/{objectName}";

        logger.LogInformation("Uploaded {Service} → {Uri}", serviceName, downloadUrl);
        return downloadUrl;
    }
}
