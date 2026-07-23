namespace Deployify.Target.Nomad;

/// <summary>
/// Configures where zipped build artefacts are uploaded for raw_exec Nomad deployments.
/// Set <see cref="NomadTargetConfig.ArtifactStore"/> to enable zip-and-upload for raw_exec jobs.
/// </summary>
public sealed class ArtifactStoreConfig
{
    /// <summary>Which backing store to use.</summary>
    public ArtifactStoreType Type { get; set; } = ArtifactStoreType.Http;

    // ── S3 ────────────────────────────────────────────────────────────────────

    /// <summary>S3 bucket name.</summary>
    public string? S3Bucket { get; set; }

    /// <summary>AWS region for the S3 bucket (e.g. "us-east-1").</summary>
    public string? S3Region { get; set; }

    /// <summary>Optional key prefix / folder inside the bucket.</summary>
    public string? S3Prefix { get; set; }

    /// <summary>AWS profile to use for authentication (uses default chain if null).</summary>
    public string? S3AwsProfile { get; set; }

    // ── Azure Blob ────────────────────────────────────────────────────────────

    /// <summary>Azure Storage account name.</summary>
    public string? AzureStorageAccount { get; set; }

    /// <summary>Azure Blob container name.</summary>
    public string? AzureContainer { get; set; }

    /// <summary>
    /// Azure Storage connection string or SAS token.
    /// If null, uses DefaultAzureCredential (managed identity / az login).
    /// </summary>
    public string? AzureConnectionString { get; set; }

    // ── Plain HTTP ────────────────────────────────────────────────────────────

    /// <summary>
    /// Base URL of the HTTP server that accepts PUT requests.
    /// Artefacts are PUT to {HttpBaseUrl}/{name}-{version}.zip.
    /// </summary>
    public string? HttpBaseUrl { get; set; }

    /// <summary>Bearer token for HTTP authentication (optional).</summary>
    public string? HttpToken { get; set; }

    // ── Nexus ─────────────────────────────────────────────────────────────────

    /// <summary>Nexus server base URL (e.g. "https://nexus.example.com").</summary>
    public string? NexusUrl { get; set; }

    /// <summary>Nexus raw repository name.</summary>
    public string? NexusRepository { get; set; }

    /// <summary>Nexus username for basic auth.</summary>
    public string? NexusUsername { get; set; }

    /// <summary>Nexus password for basic auth.</summary>
    public string? NexusPassword { get; set; }

    // ── Artifactory ───────────────────────────────────────────────────────────

    /// <summary>Artifactory server base URL (e.g. "https://mycompany.jfrog.io/artifactory").</summary>
    public string? ArtifactoryUrl { get; set; }

    /// <summary>Artifactory repository key.</summary>
    public string? ArtifactoryRepository { get; set; }

    /// <summary>Artifactory API key or access token.</summary>
    public string? ArtifactoryToken { get; set; }

    // ── GCS ───────────────────────────────────────────────────────────────────

    /// <summary>Google Cloud Storage bucket name.</summary>
    public string? GcsBucket { get; set; }

    /// <summary>Optional object prefix / folder inside the bucket.</summary>
    public string? GcsPrefix { get; set; }

    /// <summary>
    /// Path to a GCP service account JSON key file.
    /// If null, uses Application Default Credentials (gcloud auth / workload identity).
    /// </summary>
    public string? GcsCredentialsFile { get; set; }
}

public enum ArtifactStoreType
{
    Http,
    S3,
    AzureBlob,
    Nexus,
    Artifactory,
    Gcs
}
