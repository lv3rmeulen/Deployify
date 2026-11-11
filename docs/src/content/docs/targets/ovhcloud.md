---
title: "OVHcloud Managed Kubernetes"
description: "Deploying an Aspire app to OVHcloud Managed Kubernetes Service with Deployify."
---

Target ID: `OvhCloud`

Deploys to an [OVHcloud Managed Kubernetes](https://www.ovhcloud.com/en/public-cloud/kubernetes/)
cluster. The services are deployed exactly as on the [Kubernetes target](/Deployify/targets/kubernetes/)
(same manifests, secret backends, health-gated rollouts, destroy and prune); what this target adds
is the OVHcloud side. Before it touches the cluster it checks the cluster's status through the
OVHcloud API (waiting while it is still being installed or updated), and it fetches the cluster's
kubeconfig from the API, so you don't download one from the Control Panel or keep one in CI.
Needs `kubectl` on `PATH`.

```json
"target": "OvhCloud",
"targetConfig": {
  "kubernetes": { "namespacePrefix": "myapp", "ingressBaseDomain": "example.com" },
  "ovhcloud": {
    "endpoint": "ovh-eu",
    "projectId": "0123456789abcdef0123456789abcdef",
    "clusterId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "waitForReady": true,
    "readyTimeoutSeconds": 600
  }
}
```

- `endpoint`: `ovh-eu` (default), `ovh-ca` or `ovh-us`. Falls back to `OVH_ENDPOINT`.
- `projectId`: the Public Cloud project ID (the API's `serviceName`). Falls back to
  `OVH_CLOUD_PROJECT_SERVICE`.
- `clusterId`: the Managed Kubernetes cluster ID, shown on the cluster's page in the Control Panel
  or by `GET /cloud/project/{serviceName}/kube`.
- `kubeconfigPath`: where the fetched kubeconfig is written. Default:
  `{LocalApplicationData}/deployify/ovhcloud/{projectId}-{clusterId}.kubeconfig`, owner-only on
  Linux and macOS. It is fetched again on every run, so it never goes stale.
- `waitForReady` / `readyTimeoutSeconds`: wait (up to 10 minutes by default) for a cluster that is
  `INSTALLING`, `UPDATING`, `REDEPLOYING`, `RESETTING` or `REOPENING`. A cluster in `ERROR`,
  `SUSPENDED` or another state it won't leave on its own stops the run before anything is applied.

Namespaces, ingress, resources and probes are the Kubernetes target's settings
(`targetConfig.kubernetes`). `kubernetes.context` is optional: the API's kubeconfig has a single
context, which is its current one.

Init flags: `--ovh-endpoint`, `--ovh-project`, `--ovh-cluster`, plus `--kubernetes-namespace-prefix`
and `--kubernetes-ingress-domain`.

## Credentials

The OVHcloud API accepts either an **API application** or a **service account**. Leave the values
out of `deployify-state.json`: each setting falls back to the environment variable the OVHcloud
SDKs and Terraform provider read, or can be an `"env:NAME"` reference.

| Setting | Environment variable | |
|---|---|---|
| `applicationKey` | `OVH_APPLICATION_KEY` | API application, created at `https://eu.api.ovh.com/createToken/` |
| `applicationSecret` | `OVH_APPLICATION_SECRET` | |
| `consumerKey` | `OVH_CONSUMER_KEY` | |
| `clientId` | `OVH_CLIENT_ID` | Service account (OAuth2 client credentials); used when both are set |
| `clientSecret` | `OVH_CLIENT_SECRET` | |

The application or service account needs these rights:

- `GET /cloud/project/{serviceName}/kube/{kubeId}`
- `POST /cloud/project/{serviceName}/kube/{kubeId}/kubeconfig`

`generate`, `explain` and `graph` don't call the API, so they work without credentials.

**Without API access**, leave out `projectId` and `clusterId` and set
`kubernetes.kubeConfigPath` to a kubeconfig downloaded from the Control Panel. The target then
behaves like the Kubernetes target.

## Images and ingress

Set `containerRegistry` to your registry, for example a Managed Private Registry (Harbor) project:
`abc123.gra7.container-registry.ovh.net/myapp`. For a private project, give the cluster a login to
pull with, ideally a Harbor robot account with pull rights only:

```json
"kubernetes": {
  "namespacePrefix": "myapp",
  "registryCredentials": {
    "username": "env:OVH_REGISTRY_USERNAME",
    "password": "env:OVH_REGISTRY_PASSWORD"
  }
}
```

Deployify then keeps a `deployify-registry` pull Secret in every service namespace; the server is
taken from `containerRegistry`. See
[private registries](/Deployify/targets/kubernetes/#private-registries) for the details.

Managed Kubernetes has no ingress controller installed by default. Install one (for example
ingress-nginx, which gets an OVHcloud Load Balancer for its `LoadBalancer` Service) before setting
`ingressBaseDomain`, and point the domain's DNS at the load balancer.

**Destroy** and `apply --prune` remove the service namespaces, as on Kubernetes. The cluster and its
node pools are never touched.
