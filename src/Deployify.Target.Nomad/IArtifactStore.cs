using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Target.Nomad;

/// <summary>
/// Uploads a zipped build artifact to a backing store and returns the download URL
/// that Nomad will use in the <c>artifact</c> block of the HCL job.
/// </summary>
public interface IArtifactStore
{
    /// <summary>
    /// Uploads <paramref name="zipPath"/> to the store.
    /// </summary>
    /// <param name="serviceName">Service name (e.g. "api").</param>
    /// <param name="version">Image tag / version string (e.g. "v1.0.0").</param>
    /// <param name="zipPath">Absolute local path of the zip file to upload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The URL Nomad should use to download the artifact.</returns>
    Task<string> UploadAsync(
        string serviceName,
        string version,
        string zipPath,
        CancellationToken ct = default);
}
