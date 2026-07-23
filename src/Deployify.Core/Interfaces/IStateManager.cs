using Deployify.Core.Models;

namespace Deployify.Core.Interfaces;

public interface IStateManager
{
    Task<DeployifyStateFile> LoadFileAsync(string directory, CancellationToken ct = default);
    Task SaveFileAsync(DeployifyStateFile stateFile, string directory, CancellationToken ct = default);
    Task<(EnvironmentConfig Config, string EnvName)> LoadActiveAsync(string directory, string? envOverride = null, CancellationToken ct = default);
    bool Exists(string directory);
}
