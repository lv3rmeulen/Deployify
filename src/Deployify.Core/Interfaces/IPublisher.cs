using Deployify.Core.Models;

namespace Deployify.Core.Interfaces;

public interface IPublisher
{
    Task PublishAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string appHostDirectory,
        CancellationToken ct = default);
}
