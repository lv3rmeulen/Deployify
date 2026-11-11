---
title: "Helm"
description: "Deploying an Aspire app to Helm with Deployify."
---

Target ID: `Helm`

Deploys the Kubernetes target's manifests as Helm releases: each service becomes a chart, installed
as release `{service}` in namespace `{namespacePrefix}-{service}` with `helm upgrade --install`.
Cluster, namespace and resource settings are the Kubernetes target's (`targetConfig.kubernetes`).
Needs `helm` and `kubectl` on `PATH`. Helm won't take over objects `kubectl apply` created, so to move
an environment that's already deployed with the Kubernetes target, `deployify destroy` it first (or
let Helm adopt the objects by giving them its `meta.helm.sh/*` annotations and
`app.kubernetes.io/managed-by=Helm` label).

```json
"target": "Helm",
"targetConfig": {
  "kubernetes": { "context": "my-cluster", "namespacePrefix": "myapp", "ingressBaseDomain": "example.com" },
  "helm": {
    "chartVersion": "0.1.0",
    "historyMax": 10,
    "wait": false,
    "timeout": "5m"
  }
}
```

Init flags: the Kubernetes ones (`--kubernetes-context`, `--kubernetes-namespace-prefix`,
`--kubernetes-ingress-domain`); `targetConfig.helm` gets the defaults above.

**Charts.** `generate`, `validate`, `plan` and `diff` write one chart per service to
`deployify-output/helm/{service}/`. The templates are the same objects `kubectl apply` would get,
except that the image, replica count, resources and ingress host and class come from `values.yaml`,
so they can be overridden:

```bash
helm upgrade --install api deployify-output/helm/api -n myapp-api --create-namespace \
  --set image.tag=1.4.3 --set replicaCount=3
```

The Namespace is not part of the chart, because Helm keeps its release record in that namespace.
Deployify creates it before installing, with its usual label; install by hand with
`--create-namespace`. Regenerating rewrites `templates/` from scratch. Literal `{{ }}` in the
manifests (the Vault agent's template, values that contain braces) is escaped so Helm prints it.

**Secrets** work as on the Kubernetes target. With the `Inline` backend, values are in the chart's
`templates/secret.yaml` (owner-only file permissions, as `secret.yaml` is on Kubernetes) and in
Helm's release record in the cluster. With an external backend, no value is in the chart: the
`{service}-secrets` Secret is applied out of band before the release, and the pods are restarted
when only those values changed.

**Plan and diff** compare each chart with its release using the
[helm-diff](https://github.com/databus23/helm-diff) plugin
(`helm plugin install https://github.com/databus23/helm-diff`). Without it, an installed service is
listed under *Could not be checked*. A service without a release is new; a release in
`{prefix}-{name}` named `{name}` that the AppHost no longer has is listed for removal.

**Rollouts** are health-gated like on Kubernetes (the Deployment's generation is watched). The
snapshot is the release revision, and a rollback is `helm rollback` to it, which also restores the
ConfigMap and an inline Secret; an out-of-band Secret is restored from memory within the same run.
A release the rollout created is uninstalled. **Destroy** and `apply --prune` run `helm uninstall`,
then delete the labeled namespace.
