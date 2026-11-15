using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Deployify.Core.SecretBackends.KeyVault;
using Deployify.Core.SecretBackends.Vault;
using Deployify.Yaml;

namespace Deployify.Target.Kubernetes.Manifests;

/// <summary>
/// Builds the Kubernetes objects of one service as typed models. <see cref="KubernetesYaml"/>
/// writes them; the Helm chart writer and the GitOps tree reuse the same models, so every way of
/// deploying a service produces the same objects.
/// </summary>
internal static class KubernetesManifestBuilder
{
    /// <summary>
    /// Every file of one service, in apply order: (file name, model). The model is a manifest
    /// object, or a string for a file that isn't a manifest (the Vault policy).
    /// </summary>
    public static IReadOnlyList<(string FileName, object Model)> Build(KubernetesServiceManifest svc)
    {
        var files = new List<(string, object)> { ("namespace.yaml", Namespace(svc)) };

        if (svc.HasConfigMap)
        {
            files.Add(("configmap.yaml", ConfigMap(svc)));
        }

        if (svc.WritesSecretManifest)
        {
            files.Add((KubernetesManifestGenerator.SecretFileName, Secret(svc)));
        }

        // Vault: the pod logs in with its own service account, bound to a read-only policy.
        if (svc.NeedsServiceAccount)
        {
            files.Add(("serviceaccount.yaml", ServiceAccount(svc)));
        }

        if (svc.UsesVaultAgent)
        {
            // For reference (and for setting up roles by hand when manageAuth is off); kubectl ignores .hcl files.
            files.Add(("vault-policy.hcl", VaultPolicy.ReadOnly(svc.VaultKvApiPath!)));
        }

        // Key Vault: the CSI driver reads the secrets with the pod's identity and syncs them into a Secret.
        if (svc.UsesKeyVaultCsi)
        {
            files.Add(("secretproviderclass.yaml", SecretProviderClass(svc)));
        }

        files.Add(("deployment.yaml", Deployment(svc)));
        if (svc.HasService)
        {
            files.Add(("service.yaml", Service(svc)));
        }

        if (svc.HasIngress)
        {
            files.Add(("ingress.yaml", Ingress(svc)));
        }

        if (svc.HasPvc)
        {
            files.Add(("pvc.yaml", Pvc(svc)));
        }

        return files;
    }

    private static List<T>? NullIfEmpty<T>(List<T> items) => items.Count > 0 ? items : null;

    /// <summary>The text of a file from <see cref="Build"/>.</summary>
    public static string ToText(object model) => model as string ?? YamlWriter.CamelCase.Serialize(model);

    // ── Objects ───────────────────────────────────────────────────────────────

    private static Dictionary<string, string> AppLabels(KubernetesServiceManifest svc) => new(StringComparer.Ordinal)
    {
        ["app"] = svc.ServiceName,
        ["app.kubernetes.io/managed-by"] = "deployify"
    };

    private static ObjectMeta Meta(KubernetesServiceManifest svc, string? name = null, Dictionary<string, Quoted>? annotations = null) => new()
    {
        Name = name ?? svc.ServiceName,
        Namespace = svc.Namespace,
        Labels = AppLabels(svc),
        Annotations = annotations
    };

    private static NamespaceManifest Namespace(KubernetesServiceManifest svc) => new()
    {
        Metadata = new ObjectMeta
        {
            Name = svc.Namespace,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["app.kubernetes.io/managed-by"] = "deployify",
                ["deployify/service"] = svc.ServiceName
            }
        }
    };

    private static ConfigMapManifest ConfigMap(KubernetesServiceManifest svc) => new()
    {
        Metadata = Meta(svc, $"{svc.ServiceName}-config"),
        Data = svc.Env.ToDictionary(e => e.Key, e => new Quoted(e.Value), StringComparer.Ordinal)
    };

    /// <summary>
    /// Env vars that contain a secret parameter go in an Opaque Secret instead of the
    /// ConfigMap, so they are stored and access-controlled as secrets in the cluster.
    /// </summary>
    private static SecretManifest Secret(KubernetesServiceManifest svc) => new()
    {
        Metadata = Meta(svc, KubernetesManifestGenerator.SecretName(svc)),
        StringData = svc.SecretEnv.ToDictionary(e => e.Key, e => new Quoted(e.Value.ToString()), StringComparer.Ordinal)
    };

    private static ServiceAccountManifest ServiceAccount(KubernetesServiceManifest svc) => new()
    {
        // Workload ID exchanges this ServiceAccount's token for one of the managed identity.
        Metadata = Meta(svc, annotations: UsesWorkloadIdentity(svc)
            ? new Dictionary<string, Quoted>(StringComparer.Ordinal)
            {
                ["azure.workload.identity/client-id"] = svc.KeyVault!.ClientId ?? "",
                ["azure.workload.identity/tenant-id"] = svc.KeyVault.TenantId ?? ""
            }
            : null)
    };

    private static bool UsesWorkloadIdentity(KubernetesServiceManifest svc)
        => svc.UsesKeyVaultCsi && svc.KeyVault!.KubernetesIdentity == KeyVaultKubernetesIdentity.WorkloadIdentity;

    /// <summary>
    /// A SecretProviderClass for the Azure provider: which vault secrets to mount (under their
    /// parameter id) and the Secret to sync them into, which the env vars reference.
    /// </summary>
    private static SecretProviderClassManifest SecretProviderClass(KubernetesServiceManifest svc)
    {
        var keyVault = svc.KeyVault!;
        var objects = svc.KeyVaultObjectNames.OrderBy(o => o.Key, StringComparer.Ordinal).ToList();

        var parameters = new Dictionary<string, Quoted>(StringComparer.Ordinal) { ["usePodIdentity"] = "false" };
        if (keyVault.KubernetesIdentity == KeyVaultKubernetesIdentity.VmManagedIdentity)
        {
            parameters["useVMManagedIdentity"] = "true";
            parameters["userAssignedIdentityID"] = keyVault.ClientId ?? "";
        }
        else
        {
            parameters["clientID"] = keyVault.ClientId ?? "";
        }

        parameters["keyvaultName"] = KeyVaultSecrets.VaultName(keyVault);
        parameters["tenantId"] = keyVault.TenantId ?? "";
        if (!string.IsNullOrWhiteSpace(keyVault.CloudName))
        {
            parameters["cloudName"] = keyVault.CloudName;
        }

        // The provider reads "objects" as a YAML document of its own.
        var list = new StringBuilder("array:");
        foreach (var (id, name) in objects)
        {
            list.Append("\n  - |\n")
                .Append($"    objectName: {name}\n")
                .Append("    objectType: secret\n")
                .Append($"    objectAlias: {id}");
        }

        parameters["objects"] = list.ToString();

        return new SecretProviderClassManifest
        {
            Metadata = Meta(svc, KubernetesManifestGenerator.SecretProviderClassName(svc)),
            Spec = new SecretProviderClassSpec
            {
                Provider = "azure",
                SecretObjects =
                [
                    new SecretObject
                    {
                        SecretName = KubernetesManifestGenerator.SecretName(svc),
                        Labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["app"] = svc.ServiceName },
                        Data = [.. objects.Select(o => new SecretObjectData { ObjectName = o.Key, Key = o.Key })]
                    }
                ],
                Parameters = parameters
            }
        };
    }

    /// <summary>Pod-template annotations: Dapr sidecar and Vault agent.</summary>
    private static Dictionary<string, Quoted>? PodAnnotations(KubernetesServiceManifest svc)
    {
        var annotations = new Dictionary<string, Quoted>(StringComparer.Ordinal);
        if (svc.DaprEnabled)
        {
            // The Dapr sidecar injector only reads dapr.io/* annotations. The sidecar's own
            // HTTP/gRPC ports are fixed by Dapr on Kubernetes (3500/50001).
            annotations["dapr.io/enabled"] = "true";
            annotations["dapr.io/app-id"] = svc.DaprAppId ?? svc.ServiceName;
            if (svc.DaprAppPort.HasValue)
            {
                annotations["dapr.io/app-port"] = svc.DaprAppPort.Value.ToString(CultureInfo.InvariantCulture);
            }
        }

        if (svc.UsesVaultAgent)
        {
            var file = svc.VaultSecretsFileName;
            annotations["vault.hashicorp.com/agent-inject"] = "true";
            annotations["vault.hashicorp.com/role"] = svc.VaultRole!;
            annotations[$"vault.hashicorp.com/agent-inject-secret-{file}"] = svc.VaultKvApiPath!;
            annotations[$"vault.hashicorp.com/agent-inject-template-{file}"] =
                VaultTemplate.JsonConfigFile(svc.VaultKvApiPath!, svc.SecretEnv).TrimEnd('\n');
            foreach (var (k, v) in svc.VaultAgentAnnotations.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                // A multi-line value is written as a |- block, which drops trailing newlines.
                annotations[k] = v.Contains('\n') ? v.TrimEnd('\n') : v;
            }
        }

        return annotations.Count > 0 ? annotations : null;
    }

    private static DeploymentManifest Deployment(KubernetesServiceManifest svc)
    {
        var podLabels = new Dictionary<string, string>(StringComparer.Ordinal) { ["app"] = svc.ServiceName };
        if (UsesWorkloadIdentity(svc))
        {
            podLabels["azure.workload.identity/use"] = "true";
        }

        return new DeploymentManifest
        {
            Metadata = Meta(svc),
            Spec = new DeploymentSpec
            {
                Replicas = svc.Replicas,
                Selector = new LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>(StringComparer.Ordinal) { ["app"] = svc.ServiceName }
                },
                Template = new PodTemplate
                {
                    // Pod annotations belong under template.metadata, next to its labels.
                    Metadata = new PodTemplateMeta { Labels = podLabels, Annotations = PodAnnotations(svc) },
                    Spec = new PodSpec
                    {
                        ServiceAccountName = svc.NeedsServiceAccount ? svc.ServiceName : null,
                        ImagePullSecrets = NullIfEmpty([.. svc.ImagePullSecrets.Select(n => new PullSecretReference { Name = n })]),
                        Containers = [Container(svc)],
                        Volumes = svc.UsesKeyVaultCsi
                            ?
                            [
                                new Volume
                                {
                                    Name = "keyvault-secrets",
                                    Csi = new CsiVolumeSource
                                    {
                                        Driver = "secrets-store.csi.k8s.io",
                                        ReadOnly = true,
                                        VolumeAttributes = new Dictionary<string, string>(StringComparer.Ordinal)
                                        {
                                            ["secretProviderClass"] = KubernetesManifestGenerator.SecretProviderClassName(svc)
                                        }
                                    }
                                }
                            ]
                            : null
                    }
                }
            }
        };
    }

    private static Container Container(KubernetesServiceManifest svc)
    {
        var envFrom = new List<EnvFromSource>();
        if (svc.HasConfigMap)
        {
            envFrom.Add(new EnvFromSource { ConfigMapRef = new LocalObjectReference { Name = $"{svc.ServiceName}-config" } });
        }

        if (svc.WritesSecretManifest)
        {
            envFrom.Add(new EnvFromSource { SecretRef = new LocalObjectReference { Name = KubernetesManifestGenerator.SecretName(svc) } });
        }

        var env = new List<KubernetesEnvVar>();
        if (svc.UsesSecretRefs)
        {
            env.AddRange(KubernetesSecretEnv.Render(KubernetesManifestGenerator.SecretName(svc), svc.SecretEnv, svc.Env.Keys));
        }

        if (svc.UsesVaultAgent)
        {
            // Lets the app find the file: builder.Configuration.AddJsonFile(
            //   Environment.GetEnvironmentVariable("DEPLOYIFY_SECRETS_FILE")!, optional: true, reloadOnChange: true)
            env.Add(new KubernetesEnvVar(KubernetesManifestGenerator.SecretsFileVariable,
                KubernetesManifestGenerator.VaultSecretsFilePath(svc)));
        }

        // Probes: TCP by default; HTTP only on an HTTP port when explicitly configured.
        var probePort = svc.Ports.FirstOrDefault(p => p.IsHttp) ?? svc.Ports.FirstOrDefault();
        Probe? Probe(int delay, int period)
        {
            if (probePort is null || svc.HealthCheckType == "none")
            {
                return null;
            }

            var useHttp = svc.HealthCheckType == "http" && probePort.IsHttp;
            return new Probe
            {
                HttpGet = useHttp ? new HttpGetAction { Path = svc.HealthCheckPath, Port = probePort.Name } : null,
                TcpSocket = useHttp ? null : new TcpSocketAction { Port = probePort.Name },
                InitialDelaySeconds = delay,
                PeriodSeconds = period
            };
        }

        return new Container
        {
            Name = svc.ServiceName,
            Image = ImageReference.Format(svc.Image!, svc.ImageTag),
            Ports = NullIfEmpty([.. svc.Ports.Select(p => new NamedContainerPort { Name = p.Name, ContainerPort = p.ContainerPort, Protocol = p.Protocol })]),
            EnvFrom = NullIfEmpty(envFrom),
            Env = NullIfEmpty(
            [
                .. env.Select(e => e.IsSecretRef
                    ? new EnvVar
                    {
                        Name = e.Name,
                        ValueFrom = new EnvVarSource { SecretKeyRef = new SecretKeySelector { Name = e.SecretName!, Key = e.SecretKey! } }
                    }
                    : new EnvVar { Name = e.Name, Value = e.Value ?? "" })
            ]),
            Resources = new ResourceRequirements
            {
                Requests = new ResourceList { Cpu = svc.CpuRequest, Memory = svc.MemoryRequest },
                Limits = new ResourceList { Cpu = svc.CpuLimit, Memory = svc.MemoryLimit }
            },
            LivenessProbe = Probe(15, 20),
            ReadinessProbe = Probe(5, 10),
            // Mounting the volume is what makes the driver fetch the secrets and sync the
            // Secret the env vars read, so it's needed even though the app reads env vars.
            VolumeMounts = svc.UsesKeyVaultCsi
                ? [new VolumeMount { Name = "keyvault-secrets", MountPath = KubernetesManifestGenerator.KeyVaultMountPath, ReadOnly = true }]
                : null
        };
    }

    private static ServiceManifest Service(KubernetesServiceManifest svc) => new()
    {
        Metadata = Meta(svc),
        Spec = new ServiceSpec
        {
            Selector = new Dictionary<string, string>(StringComparer.Ordinal) { ["app"] = svc.ServiceName },
            Ports = [.. svc.Ports.Select(p => new ServicePort { Name = p.Name, Port = p.ContainerPort, TargetPort = p.Name, Protocol = p.Protocol })]
        }
    };

    private static IngressManifest Ingress(KubernetesServiceManifest svc)
    {
        var httpPort = svc.Ports.First(p => p.IsHttp);
        return new IngressManifest
        {
            Metadata = Meta(svc, annotations: new Dictionary<string, Quoted>(StringComparer.Ordinal)
            {
                ["nginx.ingress.kubernetes.io/rewrite-target"] = "/"
            }),
            Spec = new IngressSpec
            {
                IngressClassName = svc.IngressClassName,
                Rules =
                [
                    new IngressRule
                    {
                        Host = svc.IngressHost!,
                        Http = new HttpIngressRuleValue
                        {
                            Paths =
                            [
                                new HttpIngressPath
                                {
                                    Path = "/",
                                    PathType = "Prefix",
                                    Backend = new IngressBackend
                                    {
                                        Service = new IngressServiceBackend
                                        {
                                            Name = svc.ServiceName,
                                            Port = new ServiceBackendPort { Name = httpPort.Name }
                                        }
                                    }
                                }
                            ]
                        }
                    }
                ]
            }
        };
    }

    private static PersistentVolumeClaimManifest Pvc(KubernetesServiceManifest svc) => new()
    {
        Metadata = Meta(svc, $"{svc.ServiceName}-pvc"),
        Spec = new PersistentVolumeClaimSpec
        {
            AccessModes = ["ReadWriteOnce"],
            Resources = new StorageRequest
            {
                Requests = new Dictionary<string, string>(StringComparer.Ordinal) { ["storage"] = svc.PvcStorageSize ?? "1Gi" }
            }
        }
    };
}
