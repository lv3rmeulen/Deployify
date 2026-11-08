using System;

namespace Deployify.Core.State;

public static class StateBackendFactory
{
    /// <summary>The backend <paramref name="config"/> describes; local files under <paramref name="stateDirectory"/> by default.</summary>
    public static IStateBackend Create(StateBackendConfig? config, string stateDirectory)
    {
        config ??= new StateBackendConfig();
        return config.Backend switch
        {
            StateBackendType.Local => LocalStateBackend.ForStateDirectory(stateDirectory),
            StateBackendType.AzureBlob => new AzureBlobStateBackend(Require(config.AzureBlob, "azureBlob")),
            StateBackendType.S3 => string.IsNullOrWhiteSpace(Require(config.S3, "s3").Bucket)
                ? throw new StateBackendException("state.s3.bucket is required for the S3 state backend.")
                : new S3StateBackend(config.S3!),
            StateBackendType.Consul => new ConsulStateBackend(config.Consul ?? new ConsulStateConfig()),
            StateBackendType.Kubernetes => new KubernetesStateBackend(config.Kubernetes ?? new KubernetesStateConfig()),
            _ => throw new NotSupportedException($"Unknown state backend '{config.Backend}'.")
        };
    }

    private static T Require<T>(T? section, string name) where T : class
        => section ?? throw new StateBackendException(
            $"The state backend needs a 'state.{name}' section in the environment config.");
}
