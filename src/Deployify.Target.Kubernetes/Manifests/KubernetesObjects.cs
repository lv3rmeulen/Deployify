using System.Collections.Generic;
using Deployify.Yaml;

namespace Deployify.Target.Kubernetes.Manifests;

// The subset of the Kubernetes API that Deployify writes. Property order is the order in the
// YAML. Names and other identifiers Deployify derives are plain strings; anything that comes
// from the user or the app is Quoted. Optional parts are null (or empty) and left out.

internal sealed class ObjectMeta
{
    public required string Name { get; init; }
    public string? Namespace { get; init; }
    public Dictionary<string, string>? Labels { get; init; }
    public Dictionary<string, Quoted>? Annotations { get; init; }
}

internal sealed class NamespaceManifest
{
    public string ApiVersion => "v1";
    public string Kind => "Namespace";
    public required ObjectMeta Metadata { get; init; }
}

internal sealed class ConfigMapManifest
{
    public string ApiVersion => "v1";
    public string Kind => "ConfigMap";
    public required ObjectMeta Metadata { get; init; }
    public required Dictionary<string, Quoted> Data { get; init; }
}

internal sealed class SecretManifest
{
    public string ApiVersion => "v1";
    public string Kind => "Secret";
    public required ObjectMeta Metadata { get; init; }
    public string Type => "Opaque";
    public required Dictionary<string, Quoted> StringData { get; init; }
}

internal sealed class ServiceAccountManifest
{
    public string ApiVersion => "v1";
    public string Kind => "ServiceAccount";
    public required ObjectMeta Metadata { get; init; }
}

// ── Deployment ────────────────────────────────────────────────────────────────

internal sealed class DeploymentManifest
{
    public string ApiVersion => "apps/v1";
    public string Kind => "Deployment";
    public required ObjectMeta Metadata { get; init; }
    public required DeploymentSpec Spec { get; init; }
}

internal sealed class DeploymentSpec
{
    public required int Replicas { get; init; }
    public required LabelSelector Selector { get; init; }
    public required PodTemplate Template { get; init; }
}

internal sealed class LabelSelector
{
    public required Dictionary<string, string> MatchLabels { get; init; }
}

internal sealed class PodTemplate
{
    public required PodTemplateMeta Metadata { get; init; }
    public required PodSpec Spec { get; init; }
}

internal sealed class PodTemplateMeta
{
    public required Dictionary<string, string> Labels { get; init; }
    public Dictionary<string, Quoted>? Annotations { get; init; }
}

internal sealed class PodSpec
{
    public string? ServiceAccountName { get; init; }
    public List<PullSecretReference>? ImagePullSecrets { get; init; }
    public required List<Container> Containers { get; init; }
    public List<Volume>? Volumes { get; init; }
}

internal sealed class PullSecretReference
{
    public required Quoted Name { get; init; }
}

internal sealed class Container
{
    public required string Name { get; init; }
    public required Quoted Image { get; init; }
    public string ImagePullPolicy => "Always";
    public List<NamedContainerPort>? Ports { get; init; }
    public List<EnvFromSource>? EnvFrom { get; init; }
    public List<EnvVar>? Env { get; init; }
    public required ResourceRequirements Resources { get; init; }
    public Probe? LivenessProbe { get; init; }
    public Probe? ReadinessProbe { get; init; }
    public List<VolumeMount>? VolumeMounts { get; init; }
}

internal sealed class NamedContainerPort
{
    public required string Name { get; init; }
    public required int ContainerPort { get; init; }
    public required string Protocol { get; init; }
}

internal sealed class EnvFromSource
{
    public LocalObjectReference? ConfigMapRef { get; init; }
    public LocalObjectReference? SecretRef { get; init; }
}

internal sealed class LocalObjectReference
{
    public required string Name { get; init; }
}

internal sealed class EnvVar
{
    public required string Name { get; init; }
    public Quoted? Value { get; init; }
    public EnvVarSource? ValueFrom { get; init; }
}

internal sealed class EnvVarSource
{
    public required SecretKeySelector SecretKeyRef { get; init; }
}

internal sealed class SecretKeySelector
{
    public required string Name { get; init; }
    public required Quoted Key { get; init; }
}

internal sealed class ResourceRequirements
{
    public required ResourceList Requests { get; init; }
    public required ResourceList Limits { get; init; }
}

internal sealed class ResourceList
{
    public required string Cpu { get; init; }
    public required string Memory { get; init; }
}

internal sealed class Probe
{
    public HttpGetAction? HttpGet { get; init; }
    public TcpSocketAction? TcpSocket { get; init; }
    public required int InitialDelaySeconds { get; init; }
    public required int PeriodSeconds { get; init; }
}

internal sealed class HttpGetAction
{
    public required Quoted Path { get; init; }
    public required string Port { get; init; }
}

internal sealed class TcpSocketAction
{
    public required string Port { get; init; }
}

internal sealed class VolumeMount
{
    public required string Name { get; init; }
    public required string MountPath { get; init; }
    public bool ReadOnly { get; init; }
}

internal sealed class Volume
{
    public required string Name { get; init; }
    public required CsiVolumeSource Csi { get; init; }
}

internal sealed class CsiVolumeSource
{
    public required string Driver { get; init; }
    public bool ReadOnly { get; init; }
    public required Dictionary<string, string> VolumeAttributes { get; init; }
}

// ── Service, Ingress, PVC ─────────────────────────────────────────────────────

internal sealed class ServiceManifest
{
    public string ApiVersion => "v1";
    public string Kind => "Service";
    public required ObjectMeta Metadata { get; init; }
    public required ServiceSpec Spec { get; init; }
}

internal sealed class ServiceSpec
{
    public required Dictionary<string, string> Selector { get; init; }
    public required List<ServicePort> Ports { get; init; }
    public string Type => "ClusterIP";
}

internal sealed class ServicePort
{
    public required string Name { get; init; }
    public required int Port { get; init; }
    public required string TargetPort { get; init; }
    public required string Protocol { get; init; }
}

internal sealed class IngressManifest
{
    public string ApiVersion => "networking.k8s.io/v1";
    public string Kind => "Ingress";
    public required ObjectMeta Metadata { get; init; }
    public required IngressSpec Spec { get; init; }
}

internal sealed class IngressSpec
{
    public required string IngressClassName { get; init; }
    public required List<IngressRule> Rules { get; init; }
}

internal sealed class IngressRule
{
    public required string Host { get; init; }
    public required HttpIngressRuleValue Http { get; init; }
}

internal sealed class HttpIngressRuleValue
{
    public required List<HttpIngressPath> Paths { get; init; }
}

internal sealed class HttpIngressPath
{
    public required string Path { get; init; }
    public required string PathType { get; init; }
    public required IngressBackend Backend { get; init; }
}

internal sealed class IngressBackend
{
    public required IngressServiceBackend Service { get; init; }
}

internal sealed class IngressServiceBackend
{
    public required string Name { get; init; }
    public required ServiceBackendPort Port { get; init; }
}

internal sealed class ServiceBackendPort
{
    public required string Name { get; init; }
}

internal sealed class PersistentVolumeClaimManifest
{
    public string ApiVersion => "v1";
    public string Kind => "PersistentVolumeClaim";
    public required ObjectMeta Metadata { get; init; }
    public required PersistentVolumeClaimSpec Spec { get; init; }
}

internal sealed class PersistentVolumeClaimSpec
{
    public required List<string> AccessModes { get; init; }
    public required StorageRequest Resources { get; init; }
}

internal sealed class StorageRequest
{
    public required Dictionary<string, string> Requests { get; init; }
}

// ── Secrets Store CSI driver ──────────────────────────────────────────────────

internal sealed class SecretProviderClassManifest
{
    public string ApiVersion => "secrets-store.csi.x-k8s.io/v1";
    public string Kind => "SecretProviderClass";
    public required ObjectMeta Metadata { get; init; }
    public required SecretProviderClassSpec Spec { get; init; }
}

internal sealed class SecretProviderClassSpec
{
    public required string Provider { get; init; }
    public required List<SecretObject> SecretObjects { get; init; }
    public required Dictionary<string, Quoted> Parameters { get; init; }
}

internal sealed class SecretObject
{
    public required string SecretName { get; init; }
    public string Type => "Opaque";
    public required Dictionary<string, string> Labels { get; init; }
    public required List<SecretObjectData> Data { get; init; }
}

internal sealed class SecretObjectData
{
    public required Quoted ObjectName { get; init; }
    public required Quoted Key { get; init; }
}
