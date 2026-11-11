---
title: deployify init
description: "Initialize deployify in an Aspire AppHost project."
sidebar:
  label: init
  order: 1
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Initialize deployify in an Aspire AppHost project.

```
deployify init [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--non-interactive` | Disable interactive prompts; fail on missing required values. | `false` |
| `--env-name` | Environment name. | `local` |
| `--target` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| ovhcloud \| compose \| awsecs \| azurecontainerapps \| nomadconsul, or the ID of a plugin target. |  |
| `--container-registry` | Container registry URL. |  |
| `--image-tag` | Default image tag. | `latest` |
| `--output-path` | Output folder for artifacts. | `deployify-output` |
| `--container-runtime` | Container runtime for publishing: docker (default) \| podman. | `Docker` |
| `--container-os` | Target OS for container images (default: linux). | `linux` |
| `--container-arch` | Target CPU architecture for container images: x64 (default) \| arm64 \| arm. | `x64` |
| `--deploy-dapr` | Inject a Dapr sidecar into every service. | `false` |
| `--dapr-version` | Dapr sidecar image tag. | `latest` |
| `--dapr-http-port` | Base Dapr HTTP port. | `3500` |
| `--dapr-grpc-port` | Base Dapr gRPC port. | `50001` |
| `--dapr-components-path` | Path to Dapr components directory. |  |
| `--dapr-config-path` | Path to Dapr config file. |  |
| `--dapr-log-level` | Dapr sidecar log level. | `info` |
| `--deploy-dashboard` | Inject the Aspire Dashboard container into every deployment. | `false` |
| `--dashboard-tag` | Image tag for mcr.microsoft.com/dotnet/aspire-dashboard. | `latest` |
| `--dashboard-port` | Host port for the dashboard UI. | `18888` |
| `--dashboard-otlp-port` | Host port for the OTLP gRPC endpoint. | `18889` |
| `--dashboard-token` | Browser auth token (omit for unauthenticated). |  |
| `--nomad-address` | Nomad cluster address. | `http://localhost:4646` |
| `--nomad-token` | Nomad ACL token. |  |
| `--nomad-namespace` | Nomad namespace. | `default` |
| `--nomad-datacenter` | Nomad datacenter. | `dc1` |
| `--nomad-mode` | Nomad deployment mode: Docker (default) \| RawExec. | `Docker` |
| `--nomad-registry-username` | Docker registry username for private image pulls. |  |
| `--nomad-registry-password` | Docker registry password or access token. |  |
| `--nomad-register-services` | Register services in Nomad/Consul service catalog. | `true` |
| `--nomad-service-provider` | Service registration provider: nomad (default) \| consul. | `nomad` |
| `--nomad-health-check-path` | HTTP path for service health checks. | `/health` |
| `--kubernetes-context` | kubectl context name. |  |
| `--kubernetes-namespace-prefix` | Kubernetes namespace prefix. | `deployify` |
| `--kubernetes-ingress-domain` | Ingress base domain. |  |
| `--gitops-repo` | Config repository the GitOps controller watches (https or ssh URL). |  |
| `--gitops-branch` | Branch Deployify commits to. | `main` |
| `--gitops-path` | Folder in the repository for this environment. Default: deployify/{env}. |  |
| `--ovh-endpoint` | OVHcloud API endpoint: ovh-eu \| ovh-ca \| ovh-us. | `ovh-eu` |
| `--ovh-project` | OVHcloud Public Cloud project ID. Default: OVH_CLOUD_PROJECT_SERVICE at run time. |  |
| `--ovh-cluster` | OVHcloud Managed Kubernetes cluster ID. |  |
| `--compose-project` | Docker Compose project name. | `deployify` |
| `--compose-restart` | Restart policy. | `unless-stopped` |
| `--ecs-region` | AWS region. | `us-east-1` |
| `--ecs-cluster` | ECS cluster name. | `deployify` |
| `--ecs-profile` | AWS CLI profile name. |  |
| `--ecs-subnets` | Comma-separated subnet IDs for Fargate tasks. |  |
| `--ecs-security-groups` | Comma-separated security group IDs. |  |
| `--ecs-execution-role` | ECS task execution role ARN. |  |
| `--aca-subscription` | Azure subscription ID. |  |
| `--aca-resource-group` | Azure resource group. | `deployify-rg` |
| `--aca-location` | Azure region. | `eastus` |
| `--aca-environment` | Container Apps environment name. | `deployify-env` |
| `--aca-acr` | Azure Container Registry login server. |  |
| `--nc-nomad-address` | Nomad address (Consul mesh target). | `http://localhost:4646` |
| `--nc-nomad-token` | Nomad ACL token (Consul mesh target). |  |
| `--nc-nomad-namespace` | Nomad namespace (Consul mesh target). | `default` |
| `--nc-datacenter` | Nomad/Consul datacenter. | `dc1` |
| `--nc-consul-address` | Consul address. | `http://localhost:8500` |
| `--nc-consul-token` | Consul ACL token. |  |
| `--nc-ingress-gateway` | Enable Consul Ingress Gateway for HTTP services. | `false` |

