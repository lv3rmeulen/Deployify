using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Deployify.Core.SecretBackends.KeyVault;
using Deployify.Tests.Shared;
using Xunit;

namespace Deployify.Target.Kubernetes.Tests;

/// <summary>
/// Every file the generator writes, compared with checked-in snapshots by meaning rather than
/// layout (<see cref="YamlSemantics"/>). Moving the renderers to typed models must leave these
/// unchanged; a deliberate change to the output updates the snapshots
/// (<c>DEPLOYIFY_UPDATE_SNAPSHOTS=1</c>) in the same commit.
/// </summary>
public sealed class KubernetesManifestSnapshotTests
{
    private readonly KubernetesManifestGenerator _generator = new();

    private static KubernetesTargetConfig KubernetesConfig() => new()
    {
        NamespacePrefix = "shop",
        DefaultReplicas = 2,
        CpuRequest = "100m", CpuLimit = "500m",
        MemoryRequest = "128Mi", MemoryLimit = "512Mi",
        IngressClassName = "nginx"
    };

    public static TheoryData<string> Scenarios => new(
        "plain", "inline-secret", "kubernetes-secret", "vault", "keyvault-workload-identity", "keyvault-pod-identity");

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Output_MatchesSnapshot(string scenario)
    {
        var (manifest, config, kubernetesConfig, adjust) = Scenario(scenario);
        var services = await _generator.GenerateAsync(manifest, config, kubernetesConfig, TestContext.Current.CancellationToken);

        using var dir = new TempDir();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var svc in services)
        {
            adjust(svc);
            foreach (var path in await _generator.WriteYamlAsync(svc, dir.Path, TestContext.Current.CancellationToken))
            {
                files[$"{svc.ServiceName}/{Path.GetFileName(path)}"] = File.ReadAllText(path);
            }
        }

        Snapshot.Verify($"kubernetes/{scenario}", files,
            (name, content) => name.EndsWith(".yaml", StringComparison.Ordinal) ? YamlSemantics.Normalize(content) : content);
    }

    private static (AspireManifest, EnvironmentConfig, KubernetesTargetConfig, Action<KubernetesServiceManifest>) Scenario(string name)
    {
        switch (name)
        {
            case "plain":
            {
                // Ingress, Dapr, a PVC, image pull secrets and values YAML would misread if unquoted.
                var manifest = Fixtures.SimpleManifest();
                manifest.Resources["api"].Env!["FEATURE_FLAG"] = "yes";
                manifest.Resources["api"].Env!["PORT_TEXT"] = "8080";
                manifest.Resources["api"].Env!["EMPTY"] = "";
                manifest.Resources["api"].Env!["QUOTES"] = "say \"hi\": #1";
                manifest.Resources["api"].Bindings!["http"].IsExternal = true;
                manifest.Resources["worker"].Annotations = new Dictionary<string, string>
                {
                    ["dapr/enabled"] = "true",
                    ["dapr/appId"] = "worker-app"
                };

                var config = Fixtures.KubernetesConfig(prefix: "shop");
                config.ContainerRegistry = "registry.example.com:5000/team";
                config.ImageTag = "1.2.3";

                var k8s = KubernetesConfig();
                k8s.GenerateIngress = true;
                k8s.IngressBaseDomain = "apps.example.com";
                k8s.ImagePullSecrets = ["existing-pull"];
                k8s.RegistryCredentials = new RegistryCredentialsConfig { Username = "ci", Password = "env:REGISTRY_PASSWORD" };
                k8s.HealthCheckType = "http";

                return (manifest, config, k8s, svc =>
                {
                    if (svc.ServiceName == "redis")
                    {
                        svc.HasPvc = true;
                        svc.PvcStorageSize = "5Gi";
                    }
                });
            }

            case "inline-secret":
                return (SecretManifests.Manifest(), SecretManifests.Config(SecretBackendType.Inline), KubernetesConfig(), _ => { });

            case "kubernetes-secret":
                return (SecretManifests.Manifest(), SecretManifests.Config(SecretBackendType.KubernetesSecret), KubernetesConfig(), _ => { });

            case "vault":
                return (SecretManifests.Manifest(), SecretManifests.Config(SecretBackendType.Vault), KubernetesConfig(), _ => { });

            case "keyvault-workload-identity":
            case "keyvault-pod-identity":
            {
                var config = SecretManifests.Config(SecretBackendType.KeyVault);
                config.Secrets.KeyVault.VaultName = "shop-kv";
                config.Secrets.KeyVault.TenantId = "11111111-2222-3333-4444-555555555555";
                config.Secrets.KeyVault.ClientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
                config.Secrets.KeyVault.KubernetesIdentity = name == "keyvault-workload-identity"
                    ? KeyVaultKubernetesIdentity.WorkloadIdentity
                    : Enum.GetValues<KeyVaultKubernetesIdentity>().First(v => v != KeyVaultKubernetesIdentity.WorkloadIdentity);
                return (SecretManifests.Manifest(), config, KubernetesConfig(), _ => { });
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, null);
        }
    }
}
