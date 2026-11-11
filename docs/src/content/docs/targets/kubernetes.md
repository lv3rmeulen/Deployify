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

To get the same deployment as Helm charts instead (to install them yourself, or hand them to
another team), run `deployify generate --target helm` on a Kubernetes environment: see below.
