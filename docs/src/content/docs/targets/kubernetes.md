---
title: "Kubernetes"
description: "Deploying an Aspire app to Kubernetes with Deployify."
---

Target ID: `Kubernetes`

Generates `Deployment`, `Service`, and optionally `Ingress` manifests. Applies via `kubectl`.

```json
"targetConfig": {
  "kubernetes": {
    "context": "my-cluster",
    "namespacePrefix": "myapp",
    "ingressBaseDomain": "example.com",
    "generateIngress": true,
    "defaultReplicas": 2,
    "cpuRequest": "100m",
    "cpuLimit": "500m",
    "memoryRequest": "128Mi",
    "memoryLimit": "512Mi",
    "ingressClassName": "nginx"
  }
}
```

Init flags: `--kubernetes-context`, `--kubernetes-namespace-prefix`, `--kubernetes-ingress-domain`

Probes default to `tcpSocket`; set `"healthCheckType": "http"` (with `"healthCheckPath"`) for
`httpGet` probes, or `"none"` to omit them. An `Ingress` is only generated for HTTP endpoints
marked external in the AppHost (`WithExternalHttpEndpoints()`). All resources carry the
`app.kubernetes.io/managed-by=deployify` label, and only labeled namespaces are ever deleted.

## Private registries

To pull images from a private registry, either list pull Secrets you manage yourself, or give
Deployify the registry login and let it keep the Secret up to date:

```json
"kubernetes": {
  "imagePullSecrets": ["my-existing-secret"],
  "registryCredentials": {
    "server": "registry.example.com",
    "username": "env:REGISTRY_USERNAME",
    "password": "env:REGISTRY_PASSWORD",
    "secretName": "deployify-registry"
  }
}
```

- `imagePullSecrets`: names added to every pod's `imagePullSecrets`. Each Secret must already exist
  in every service namespace (`{namespacePrefix}-{service}`).
- `registryCredentials`: on `apply`, Deployify creates or updates a `kubernetes.io/dockerconfigjson`
  Secret named `secretName` (default `deployify-registry`) in each service namespace, and adds it to
  the pods. `server` defaults to the host of `containerRegistry`. Use `env:` references for the
  username and password: the Secret is applied from memory and never written to the output folder,
  and a literal password in the state file gets a warning. Destroy removes it with the namespace.

The Helm, Argo CD / Flux and OVHcloud targets read the same settings. On the GitOps targets the
registry Secret is applied with `kubectl` before the commit, like the other secrets, so it never
reaches the repository.

To get the same deployment as Helm charts instead (to install them yourself, or hand them to
another team), run `deployify generate --target helm` on a Kubernetes environment: see below.
