using Deployify.Core.SecretBackends;
using Deployify.Core.SecretBackends.KeyVault;

namespace Deployify.Target.AzureContainerApps;

/// <summary>Key Vault naming for Container Apps.</summary>
public static class AcaKeyVault
{
    /// <summary>The prefix secret names start with: <c>secretPrefix</c>, or <c>deployify-{environment}</c>.</summary>
    public static string Prefix(KeyVaultConfig keyVault, AcaTargetConfig aca)
        => KeyVaultSecrets.SecretName(string.IsNullOrWhiteSpace(keyVault.SecretPrefix) ? $"deployify-{aca.EnvironmentName}" : keyVault.SecretPrefix);

    /// <summary>Key Vault secret holding one secret env var of an app.</summary>
    public static string AppSecretName(KeyVaultConfig keyVault, AcaTargetConfig aca, string app, string acaSecretName)
        => KeyVaultSecrets.SecretName(Prefix(keyVault, aca), app, acaSecretName);

}
