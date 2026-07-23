using Deployify.Core.Models;

namespace Deployify.Core.Interfaces;

public interface IManifestReader
{
    Task<AspireManifest> ReadAsync(string manifestPath, CancellationToken ct = default);
    Task<string> GenerateAsync(string appHostPath, string outputPath, CancellationToken ct = default);
}
