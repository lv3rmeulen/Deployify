# Deployify

Deploy .NET Aspire AppHost projects to any platform — Nomad, Kubernetes, Docker Compose, AWS ECS, Azure Container Apps, and Nomad + Consul Connect.

---

## Requirements

- .NET 10 SDK
- A running deployment target (Nomad agent, Kubernetes cluster, Docker daemon, AWS CLI, etc.)

---

## Install

```bash
dotnet pack src/Deployify/Deployify.csproj -c Release
dotnet tool install --global --add-source ./src/Deployify/nupkg deployify
```

---

## Quick start

```bash
cd MyApp.AppHost

# Initialize once — creates deployify-state.json
deployify init

# Generate artifacts, preview changes, then deploy
deployify generate
deployify plan
deployify apply
```

Or run the full pipeline in one command:

```bash
deployify ci --skip-validate
```

---

## Commands

### Environment management

| Command | Description |
|---|---|
| `init` | Scaffold `deployify-state.json` interactively or with flags |
| `env list` | List all configured environments |
| `env use <name>` | Switch the active environment |
| `env add <name>` | Add a new environment |
| `env set <name>` | Update settings on an existing environment |
| `env remove <name>` | Remove an environment |

### Artifact generation

| Command | Description |
|---|---|
| `generate` | Write deployment artifacts to disk without querying the cluster |
| `validate` | Validate generated artifacts against the target platform |

### Deployment

| Command | Description |
|---|---|
| `plan` | Generate artifacts and preview what would change on the cluster |
| `apply` | Deploy artifacts to the target (prompts for confirmation) |
| `package` | Zip build output and upload to an artifact store (raw_exec Nomad only) |
| `diff` | Compare live cluster state against local artifacts |

### Insight

| Command | Description |
|---|---|
| `explain` | Human-readable summary of every service and its configuration |
| `graph` | Print the service dependency tree |

### Automation

| Command | Description |
|---|---|
| `ci` | Run the full generate → validate → plan → apply pipeline for CI/CD |
| `watch` | Watch source files for changes and re-run generate + plan automatically |

---

## Command reference

### `init`

```bash
deployify init
deployify init --non-interactive --target Nomad --nomad-address http://localhost:4646
deployify init --non-interactive --target Kubernetes --kubernetes-context my-cluster
deployify init --non-interactive --target AwsEcs --ecs-region us-east-1 --ecs-cluster my-cluster
deployify init --non-interactive --target AzureContainerApps \
    --aca-subscription 00000000-0000-0000-0000-000000000000 \
    --aca-resource-group my-rg --aca-location eastus
deployify init --non-interactive --target NomadConsul \
    --nc-nomad-address http://localhost:4646 \
    --nc-consul-address http://localhost:8500
```

### `generate`

Writes artifacts (HCL jobs, Kubernetes YAML, `docker-compose.yml`, ECS task definitions, ACA specs) to `deployify-output/` without contacting the cluster. Use this as a standalone step in CI or to inspect outputs before planning.

```bash
deployify generate
deployify generate --env prod --target Kubernetes
```

### `plan`

Generates artifacts and compares them against the live cluster state. Exits 0 even when the cluster is unreachable — the diff is best-effort.

```bash
deployify plan
deployify plan --env prod
```

### `apply`

```bash
deployify apply
deployify apply --auto-approve          # skip confirmation prompt
deployify apply --env prod --auto-approve
deployify apply --dry-run               # generate + plan only, no changes applied
```

### `package`

Zips the published build output for each project service and uploads it to the configured artifact store. Only applicable for Nomad `RawExec` deployments.

```bash
# Zip and upload using the configured artifact store
deployify package

# Preview what would be zipped without uploading
deployify package --dry-run

# Specify a custom publish output directory
deployify package --publish-dir ./out/publish

# Tag the uploaded artifacts (defaults to the environment's imageTag)
deployify package --image-tag v1.0.0
```

After running `package`, the artifact URLs are written back to `deployify-state.json` under `serviceOverrides[name].artifactUrl`. The next `generate`/`plan`/`apply` picks them up and emits an `artifact { source = "..." }` block in the HCL.

**Typical raw_exec workflow:**

```bash
dotnet publish src/MyApp.AppHost -c Release
deployify package --image-tag v1.0.0
deployify plan
deployify apply --auto-approve
```

---

## Artifact stores (raw_exec Nomad)

When `deploymentMode` is `RawExec`, Deployify can zip your published binaries and upload them to an artifact store. Nomad then downloads and unzips the artifact before running the binary via `raw_exec`.

The generated HCL looks like:

```hcl
task "api" {
  driver = "raw_exec"

  artifact {
    source      = "https://artifacts.example.com/api-v1.0.0.zip"
    destination = "local/"
    options {
      archive = true
    }
  }

  config {
    command = "local/api"
  }
}
```

### Supported stores

#### HTTP

```json
"artifactStore": {
  "type": "Http",
  "httpBaseUrl": "https://artifacts.example.com",
  "httpToken": "optional-bearer-token"
}
```

Artifacts are PUT to `{httpBaseUrl}/{name}-{version}.zip`.

#### S3

```json
"artifactStore": {
  "type": "S3",
  "s3Bucket": "my-artifacts",
  "s3Region": "us-east-1",
  "s3Prefix": "deployify",
  "s3AwsProfile": "default"
}
```

Uses the AWS CLI. Credentials from the default chain (`~/.aws/credentials`, env vars, instance role). Nomad downloads via `s3::https://s3.{region}.amazonaws.com/{bucket}/{key}`.

#### Azure Blob

```json
"artifactStore": {
  "type": "AzureBlob",
  "azureStorageAccount": "myaccount",
  "azureContainer": "artifacts",
  "azureConnectionString": "optional-connection-string-or-sas"
}
```

Uses the Azure CLI. If `azureConnectionString` is omitted, uses `az login` / managed identity.

#### Nexus

```json
"artifactStore": {
  "type": "Nexus",
  "nexusUrl": "https://nexus.example.com",
  "nexusRepository": "raw-hosted",
  "nexusUsername": "deployer",
  "nexusPassword": "secret"
}
```

Uploads to `{nexusUrl}/repository/{nexusRepository}/{name}/{name}-{version}.zip` via HTTP PUT.

#### Artifactory

```json
"artifactStore": {
  "type": "Artifactory",
  "artifactoryUrl": "https://mycompany.jfrog.io/artifactory",
  "artifactoryRepository": "releases",
  "artifactoryToken": "my-api-key"
}
```

Uploads to `{artifactoryUrl}/{repository}/{name}/{name}-{version}.zip` via HTTP PUT.

#### GCS

```json
"artifactStore": {
  "type": "Gcs",
  "gcsBucket": "my-artifacts",
  "gcsPrefix": "deployify",
  "gcsCredentialsFile": "/path/to/service-account.json"
}
```

Uses the `gcloud storage cp` CLI. If `gcsCredentialsFile` is omitted, uses Application Default Credentials. Nomad downloads via `gcs::https://storage.googleapis.com/{bucket}/{key}`.

---



```bash
deployify publish
deployify publish --image-tag v1.2.3
deployify publish --container-runtime podman
```

### `ci`

Runs the full pipeline sequentially. Any step failure aborts and exits non-zero.

```bash
# Full pipeline
deployify ci

# Generate + plan only (no deploy)
deployify ci --skip-apply

# Skip validation when the cluster is not reachable during the build phase
deployify ci --skip-validate --skip-apply

# Exit non-zero if the plan detects any drift (useful for change gating)
deployify ci --skip-apply --fail-on-changes

# Deploy without a prompt (required when there is no interactive terminal)
deployify ci --auto-approve
```

When `ci` reaches the apply step it asks for confirmation in an interactive terminal. In a
non-interactive run (a CI agent) it refuses to apply unless `--auto-approve` is passed, so
a pipeline never deploys by accident.

Steps printed during a run:

```
 1/4 Generate... ✓
 2/4 Validate... ✓
 3/4 Plan... ✓
 4/4 Apply... ✓

✓ CI pipeline complete.
```

### `watch`

Watches `*.cs`, `*.csproj`, `*.json`, `*.yaml`, `*.yml`, and `*.toml` files for changes (debounced 500 ms) and re-runs generate + plan on every change. Does not apply — run `deployify apply` separately when ready.

```bash
deployify watch
deployify watch --debounce 1000         # wait 1 s after last change
deployify watch --no-generate           # re-run plan diff only
```

Press **Ctrl+C** to stop.

### `diff`

```bash
deployify diff
deployify diff --env prod
```

### `validate`

```bash
deployify validate
deployify validate --env prod
```

### `explain` / `graph`

```bash
deployify explain
deployify graph
```

---

## Deployment targets

### `Nomad` — HashiCorp Nomad

Generates one HCL job file per service using the Docker driver. Submits jobs via the Nomad HTTP API.

```json
"targetConfig": {
  "nomad": {
    "address": "http://localhost:4646",
    "token": "optional-acl-token",
    "namespace": "default",
    "datacenters": ["dc1"],
    "deploymentMode": "Docker",
    "healthCheckType": "tcp",
    "registryUsername": "myuser",
    "registryPassword": "env:REGISTRY_PASSWORD"
  }
}
```

Init flags: `--nomad-address`, `--nomad-token`, `--nomad-namespace`, `--nomad-datacenter`

Notes:

- **Networking.** Docker tasks use host networking and each service gets its own static port
  (starting at `basePort`, default 8080). Services find each other at `localhost:<port>`, which
  assumes a single Nomad client node. For multi-node clusters use the `NomadConsul` target.
- **Health checks.** `healthCheckType` is `tcp` (default), `http` (uses `healthCheckPath`,
  default `/health`) or `none`. Aspire's `MapDefaultEndpoints()` only maps `/health` in the
  Development environment, so `http` checks fail in production unless you map it yourself.
- **Private registries.** `registryUsername` / `registryPassword` add an `auth` block for
  images that aren't on a public registry. The password is never written to the generated
  job file — the file holds a placeholder and the real value is filled in, in memory, when
  the job is sent to Nomad. If `registryPassword` is empty, `DEPLOYIFY_REGISTRY_PASSWORD` is used.
- **Ownership.** Generated jobs carry `meta { deployify-managed = "true" }`; `plan` only ever
  proposes deleting jobs with that marker.

---

### `Kubernetes` — Kubernetes

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

---

### `Compose` — Docker Compose

Generates a `docker-compose.yml` (and optionally a merged override file and `.env`). Applies via `docker compose up`.

```json
"targetConfig": {
  "compose": {
    "projectName": "myapp",
    "restartPolicy": "unless-stopped",
    "generateProfiles": false,
    "generateEnvFile": false,
    "generateMergedFile": true,
    "generateHealthChecks": false
  }
}
```

Init flags: `--compose-project`, `--compose-restart`

`generateHealthChecks` is off by default: the images `dotnet publish` produces contain neither
`curl` nor `wget`, so a generated health check would mark every service unhealthy. Turn it on
only when your images include a tool the check can use.

---

### `AwsEcs` — AWS ECS (Fargate)

Generates ECS task definition JSON files and registers/updates services via the AWS CLI.

```json
"targetConfig": {
  "ecs": {
    "region": "us-east-1",
    "clusterName": "my-cluster",
    "launchType": "Fargate",
    "subnetIds": ["subnet-abc123"],
    "securityGroupIds": ["sg-abc123"],
    "executionRoleArn": "arn:aws:iam::123456789:role/ecsTaskExecutionRole",
    "taskRoleArn": "arn:aws:iam::123456789:role/ecsTaskRole",
    "logGroupName": "/ecs/myapp",
    "defaultCpu": 256,
    "defaultMemory": 512,
    "awsProfile": "default",
    "serviceConnectNamespace": "myapp.local",
    "waitForStable": true
  }
}
```

- `serviceConnectNamespace` enables ECS Service Connect so services reach each other by name.
  Without it, inter-service URLs can't be resolved on ECS.
- `waitForStable` (default `true`) makes `apply` wait until each service reaches a steady
  state, so a task that crash-loops is reported as a failure instead of a success.
- The CloudWatch log group is created automatically (`awslogs-create-group`); the execution
  role needs `logs:CreateLogGroup`.

Init flags: `--ecs-region`, `--ecs-cluster`, `--ecs-profile`, `--ecs-subnets`, `--ecs-security-groups`, `--ecs-execution-role`

Requires the [AWS CLI](https://aws.amazon.com/cli/) on `PATH`.

---

### `AzureContainerApps` — Azure Container Apps

Generates Container App spec JSON files and deploys via the Azure CLI.

```json
"targetConfig": {
  "aca": {
    "subscriptionId": "00000000-0000-0000-0000-000000000000",
    "resourceGroup": "my-rg",
    "location": "eastus",
    "environmentName": "my-env",
    "minReplicas": 0,
    "maxReplicas": 3,
    "defaultCpu": 0.25,
    "defaultMemory": "0.5Gi",
    "acrLoginServer": "myregistry.azurecr.io",
    "acrIdentityResourceId": "/subscriptions/.../userAssignedIdentities/my-acr-pull",
    "tenantId": "optional-tenant-id"
  }
}
```

- When `subscriptionId` is empty, the Azure CLI's current subscription (`az account show`) is used.
- Only endpoints marked external in the AppHost get external ingress; everything else gets
  internal ingress.
- With `acrLoginServer` set, apps pull from ACR using a managed identity: the user-assigned
  identity in `acrIdentityResourceId` when given, otherwise the app's system-assigned identity
  (which needs the `AcrPull` role).

Init flags: `--aca-subscription`, `--aca-resource-group`, `--aca-location`, `--aca-environment`, `--aca-acr`

Requires the [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/) on `PATH`.

---

### `NomadConsul` — Nomad + Consul Connect

Generates Nomad HCL jobs with Consul Connect service mesh. Every service uses `network { mode = "bridge" }` and a `sidecar_service` block. Inter-service dependencies become `upstreams` bound to loopback ports (starting at `upstreamBasePort`), with `NOMAD_UPSTREAM_ADDR_<SERVICE>` env vars injected automatically so applications discover peers at `localhost:<port>` without hardcoded addresses.

```json
"targetConfig": {
  "nomadconsul": {
    "nomadAddress": "http://localhost:4646",
    "nomadToken": "optional-acl-token",
    "nomadNamespace": "default",
    "datacenters": ["dc1"],
    "consulAddress": "http://localhost:8500",
    "consulToken": "optional-acl-token",
    "upstreamBasePort": 20000,
    "enableIngressGateway": false,
    "ingressGatewayName": "ingress-gateway",
    "defaultCpu": 256,
    "defaultMemory": 256
  }
}
```

Init flags: `--nc-nomad-address`, `--nc-nomad-token`, `--nc-nomad-namespace`, `--nc-datacenter`, `--nc-consul-address`, `--nc-consul-token`, `--nc-ingress-gateway`

With `enableIngressGateway`, one extra job (named `ingressGatewayName`) runs a Consul ingress
gateway with a TCP listener for every HTTP endpoint marked external in the AppHost.
`healthCheckType` works as for the `Nomad` target (`tcp` by default).

---

## Parameters and secrets

Aspire `parameter.v0` resources (`builder.AddParameter(...)`, generated database passwords, …) are
resolved for every target. Values are looked up in this order:

1. `parameters` in the environment's config — use `"env:NAME"` for anything secret
2. A `Parameters__<name>` environment variable (Aspire's convention; a `-` in the name may be written as `_`)
3. The manifest's `default.value`
4. A generated value for inputs with `default.generate`, saved in `deployify-parameters.json` next to
   the state file so every deploy reuses the same password

```json
"environments": {
  "prod": {
    "parameters": {
      "api-key": "env:API_KEY",
      "region": "westeurope"
    }
  }
}
```

`deployify-parameters.json` holds generated secrets in plain text — add it to `.gitignore`.

Env vars whose value contains a **secret** parameter never go into plain-text artifacts. Each target
stores them its own way:

| Target | Where secret values go |
|---|---|
| Compose | The service's `.env` file (`generateEnvFile`), or `${Parameters__name}` in `compose.yml` with the values in a `.env` next to it |
| Kubernetes | A `<service>-secrets` Secret, loaded with `envFrom.secretRef` |
| AzureContainerApps | `configuration.secrets`, referenced with `secretRef` |
| AwsEcs | SSM Parameter Store SecureStrings under `secretsPrefix` (default `/deployify/<cluster>`), written by `apply` and referenced from the task definition's `secrets`. The execution role needs `ssm:GetParameters` |
| Nomad / NomadConsul | Nomad Variables at `nomad/jobs/<job>`, written by `apply` and read by a `template` block (Nomad 1.4+) |

`explain` masks secret values. A reference that can't be resolved is left in place and reported.

To pass a literal `{name.property}` through without resolving it, double the braces: `{{name.property}}`.

### Secret backends: keeping values out of output and state

The table above is the default `Inline` backend: secret values end up in files in
`deployify-output/` (`secret.yaml`, `.env`) and generated values in `deployify-parameters.json`.
An external backend keeps every secret value out of both. Generated artifacts and
`deployify-state.json` then only hold *references*, and the value is joined to the reference by the
platform at run time.

```json
"prod": {
  "secrets": {
    "backend": "Vault",
    "vault": { "address": "https://vault.example.com:8200", "token": "env:VAULT_TOKEN" }
  }
}
```

| `backend` | Targets | Generated artifacts hold | Generated values (`default.generate`) live in |
|---|---|---|---|
| `Inline` (default) | all | the values (see table above) | `deployify-parameters.json` |
| `KubernetesSecret` | Kubernetes | `secretKeyRef`s; values are server-side applied by `apply` from memory | Secret `deployify-parameters` in `<prefix>-system` |
| `Vault` | Kubernetes | Vault Agent annotations and a template | KV v2 `<mount>/<pathPrefix>/<prefix>/_parameters` |
| `Ssm` | AwsEcs | SSM parameter names (as with `Inline`) | SecureStrings under `<secretsPrefix>/_parameters/` |
| `KeyVault` | AzureContainerApps, Kubernetes | ACA: `keyVaultUrl` references. Kubernetes: a `SecretProviderClass` and `secretKeyRef`s | Key Vault secrets `<prefix>-parameters-*`, tagged |

**Kubernetes Secrets.** A value that is exactly one secret becomes a `secretKeyRef`. A value that embeds one, such as
a connection string, uses Kubernetes dependent variables: the secret is loaded into
`Parameters__<name>` first, and the value refers to it as `$(Parameters__<name>)`. The kubelet
expands it when the container starts. `apply` writes the Secret before the Deployment and restarts the
pods when its data changed.

**Vault.** `apply` writes each service's secrets to `<mount>/data/<pathPrefix>/<prefix>/<service>`.
With `manageAuth` (default `true`) it also writes a read-only policy and a Kubernetes auth role
`<prefix>-<service>`, bound to the service's own ServiceAccount, so a pod can read only its own path.
The Vault Agent Injector renders a .NET configuration file into the pod at
`/vault/secrets/appsettings.secrets.json`. Load it in the app (the path is in `DEPLOYIFY_SECRETS_FILE`):

```csharp
builder.Configuration.AddJsonFile(
    Environment.GetEnvironmentVariable("DEPLOYIFY_SECRETS_FILE") ?? "/vault/secrets/appsettings.secrets.json",
    optional: true, reloadOnChange: true);
```

Container resources (postgres, redis, …) can't read that file. They fall back to a Kubernetes Secret
with `secretKeyRef`, as with `KubernetesSecret`. Each service folder also gets a `vault-policy.hcl`
showing the policy, useful when `manageAuth` is off and roles are managed elsewhere.

| `vault` setting | Default | |
|---|---|---|
| `address` | `VAULT_ADDR` | |
| `token` | `VAULT_TOKEN` | Token Deployify writes with. Use `"env:NAME"`; pods never see it |
| `namespace` | `VAULT_NAMESPACE` | Enterprise namespace |
| `mount` | `secret` | KV v2 mount |
| `pathPrefix` | `deployify` | |
| `authMount` | `kubernetes` | Kubernetes auth method mount |
| `manageAuth` | `true` | Write policies and roles |
| `secretsFileName` | `appsettings.secrets.json` | |
| `agentAnnotations` | | Extra `vault.hashicorp.com/*` annotations |

**SSM.** ECS already references secrets from SSM. With `Ssm`, `apply` also skips values that haven't
changed (instead of creating a new parameter version on every deploy) and generated values move from
`deployify-parameters.json` into SSM.

**Azure Key Vault.** Each secret env var of a container app becomes a Key Vault secret named
`<prefix>-<app>-<name>` (prefix defaults to `deployify-<environmentName>`, so environments can share
a vault). The app spec's `configuration.secrets` holds only a `keyVaultUrl` and the identity used to
read it. `apply` writes changed values before deploying, then restarts the latest revision when an
existing secret changed. Container Apps would otherwise only pick up a new value on its next periodic refresh.

```json
"secrets": {
  "backend": "KeyVault",
  "keyVault": {
    "vaultName": "shop-kv",
    "identityResourceId": "/subscriptions/…/resourceGroups/…/providers/Microsoft.ManagedIdentity/userAssignedIdentities/shop-apps"
  }
}
```

Give the identity the **Key Vault Secrets User** role on the vault (or a `get` access policy). Give
whoever runs `deployify apply` **Key Vault Secrets Officer**. Use a user-assigned identity: a
system-assigned one only exists once the app does, so it can't be granted access before the first
revision tries to read its secrets. Without `identityResourceId` the apps use `"system"`, and the first
deploy will fail until you grant that identity access. Set `vaultUri` for sovereign clouds
(e.g. `https://shop-kv.vault.azure.cn`) and `secretPrefix` to choose the name prefix.

**Azure Key Vault on Kubernetes (AKS).** This uses the Azure Key Vault provider for the Secrets
Store CSI driver: the AKS add-on `azure-keyvault-secrets-provider`, or the Helm chart elsewhere.
`apply` writes each secret parameter a service uses to `<prefix>-<service>-<parameter>` (prefix
defaults to `deployify-<namespacePrefix>`). Each service gets a `SecretProviderClass` that mounts
those secrets and syncs them into a `<service>-secrets` Secret. Env vars read that Secret with
`secretKeyRef`, and connection strings use `$(VAR)` as with `KubernetesSecret`, so containers such as
postgres work too. The synced Secret only exists while a pod mounts the volume, which every deployment
that needs it does.

```json
"secrets": {
  "backend": "KeyVault",
  "keyVault": {
    "vaultName": "shop-kv",
    "tenantId": "<tenant id>",
    "clientId": "<client id of the identity pods read secrets with>"
  }
}
```

| `keyVault` setting (Kubernetes) | Default | |
|---|---|---|
| `kubernetesIdentity` | `WorkloadIdentity` | `WorkloadIdentity`: each service gets a ServiceAccount annotated with `clientId`, and pods are labelled `azure.workload.identity/use`. `VmManagedIdentity`: the node pool's identity (e.g. the add-on's), no ServiceAccount |
| `cloudName` | public cloud | e.g. `AzureChinaCloud` |
| `subscriptionId` | `az` default | Subscription of the vault, for `apply` |

With workload identity, enable the OIDC issuer and workload identity on the cluster. Then create a
federated credential on the identity for each service's ServiceAccount (`system:serviceaccount:<prefix>-<service>:<service>`):

```bash
az identity federated-credential create --name shop-api --identity-name shop-apps --resource-group shop-rg \
  --issuer "$(az aks show -n shop-aks -g shop-rg --query oidcIssuerProfile.issuerUrl -o tsv)" \
  --subject system:serviceaccount:shop-api:api
```

Rotation works like the other backends: `apply` only writes changed values and restarts the
deployments whose secrets changed, which makes the driver fetch and sync the new values.

**Safety rails**, active with any external backend:

- Generated values are reused from the backend, never regenerated. New ones are written by `apply`
  *before* anything that uses them is deployed. If the backend can't be read, `plan` warns and `apply`
  refuses to run, so a password a database already uses is never replaced.
- Switching from `Inline` carries existing values over from `deployify-parameters.json` on the next
  `apply`. After that, the file can be deleted.
- After writing artifacts, Deployify scans them for every secret value it resolved. If one is found, the
  file is deleted and the command fails. The state file is checked the same way before it's saved.
- `apply` records where each secret went, never its value, under `secrets.references`
  (`backend`, `location`, `key`, backend `version`).

---

## Dapr

Deployify can inject a [Dapr](https://dapr.io) sidecar alongside every service at deployment time — no changes to your application code or Aspire AppHost required.

### Enable

```bash
# During init
deployify init --non-interactive --target Nomad --deploy-dapr

# Or update an existing environment
deployify env set local --deploy-dapr true

# With custom version and components path
deployify init --non-interactive --target Nomad \
    --deploy-dapr \
    --dapr-version 1.13 \
    --dapr-components-path /etc/dapr/components \
    --dapr-config-path /etc/dapr/config.yaml
```

### How it works

When `deployDapr: true`, every `generate`/`plan`/`apply`/`ci` run injects Dapr sidecar annotations (`dapr/enabled`, `dapr/app-id`, `dapr/app-port`, `dapr/http-port`, `dapr/grpc-port`) into resources that don't already have them. Resources that already carry Dapr annotations from the Aspire Dapr integration are left untouched.

Each target then handles the sidecar in the platform-native way:

| Target | Dapr implementation |
|---|---|
| **Nomad** | Extra `daprd` task in the same task group with `lifecycle { hook = "prestart"; sidecar = true }` |
| **NomadConsul** | Same as Nomad, shares the bridge network with the app and Connect sidecar |
| **Kubernetes** | `dapr.io/*` annotations on the pod template — Dapr control plane injects the sidecar automatically |
| **Compose** | `<service>-dapr` container with `network_mode: service:<name>` so it shares the app's network namespace |
| **ACA** | Native `dapr:` property on the Container App spec |
| **ECS** | `<service>-dapr` essential=false sidecar container in the task definition |

### Aspire Dapr integration

If you use the Aspire Dapr integration (`builder.AddDapr()`), your manifest will already contain `dapr/enabled` annotations. Deployify reads these automatically — you do not need to set `deployDapr: true`. The annotations from the manifest take precedence over any defaults from `DaprConfig`.

### State file

```json
{
  "environments": {
    "prod": {
      "deployDapr": true,
      "dapr": {
        "daprVersion": "1.13",
        "daprHttpPort": 3500,
        "daprGrpcPort": 50001,
        "componentsPath": "/etc/dapr/components",
        "configPath": "/etc/dapr/config.yaml",
        "logLevel": "info"
      }
    }
  }
}
```

### Options

| Option | Default | Description |
|---|---|---|
| `deployDapr` | `false` | Inject Dapr sidecars into all services |
| `dapr.daprVersion` | `latest` | Tag for `daprio/daprd` |
| `dapr.daprHttpPort` | `3500` | Base Dapr HTTP port (incremented per service) |
| `dapr.daprGrpcPort` | `50001` | Base Dapr gRPC port (incremented per service) |
| `dapr.componentsPath` | `null` | Path to Dapr components directory |
| `dapr.configPath` | `null` | Path to Dapr config file |
| `dapr.logLevel` | `info` | Sidecar log level |

---



Deployify can optionally inject the [Aspire Dashboard](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/dashboard/overview) container into every deployment so you can monitor your services' telemetry in production.

### Enable

```bash
# During init
deployify init --non-interactive --target Nomad --deploy-dashboard

# Or update an existing environment
deployify env set local --deploy-dashboard true

# With a browser auth token
deployify init --non-interactive --target Nomad \
    --deploy-dashboard \
    --dashboard-port 18888 \
    --dashboard-otlp-port 18889 \
    --dashboard-token my-secret-token
```

### How it works

When `deployDashboard: true`, every `generate`/`plan`/`apply`/`ci` run injects a synthetic `aspire-dashboard` container resource into the manifest before passing it to the target generator. The resource is treated like any other container — it gets a job file, a compose service, a Kubernetes deployment, etc. depending on the active target.

The dashboard is configured with:
- `DASHBOARD__OTLP__ENDPOINT_URL` — where services should send OTLP telemetry
- `DASHBOARD__FRONTEND__ENDPOINT_URL` — the UI port
- `DASHBOARD__FRONTEND__AUTHMODE` + `DASHBOARD__FRONTEND__BROWSERTOKEN` — when a token is set

### State file

```json
{
  "environments": {
    "prod": {
      "deployDashboard": true,
      "dashboard": {
        "imageTag": "latest",
        "port": 18888,
        "otlpPort": 18889,
        "browserToken": "my-secret-token"
      }
    }
  }
}
```

### Options

| Option | Default | Description |
|---|---|---|
| `deployDashboard` | `false` | Inject the dashboard into every deployment |
| `dashboard.imageTag` | `latest` | Tag for `mcr.microsoft.com/dotnet/aspire-dashboard` |
| `dashboard.port` | `18888` | Host port for the dashboard UI |
| `dashboard.otlpPort` | `18889` | Host port for the OTLP gRPC endpoint |
| `dashboard.browserToken` | `null` | Auth token (omit for unauthenticated) |

---



Deployify uses `dotnet publish -t:PublishContainer` to build images. By default it tries Docker; if Docker is unavailable it automatically falls back to Podman.

### Auto-detection

When `containerRuntime` is `Docker` (the default), Deployify:

1. Runs `docker info` — if it succeeds, uses Docker.
2. If Docker is unreachable, runs `podman info`. If Podman is found, sets `DOCKER_HOST` to the Podman socket automatically — probing well-known paths like `/run/user/<uid>/podman/podman.sock` on Linux and `~/.local/share/containers/podman/machine/qemu/podman.sock` on macOS.
3. If neither runtime is available, logs a warning and proceeds (the publish step may fail with a clear error).

When Podman is used, the publish runs with `-p:LocalRegistry=podman` so the .NET SDK loads
the image into Podman's local store rather than Docker's.

### Explicit configuration

Set `containerRuntime` in the state file or pass `--container-runtime` per run:

```bash
deployify publish --container-runtime podman
deployify init --non-interactive --target Nomad --container-runtime Podman
```

State file:

```json
{
  "environments": {
    "local": {
      "containerRuntime": "Podman"
    }
  }
}
```

---

## State file (`deployify-state.json`)

```json
{
  "activeEnvironment": "local",
  "environments": {
    "local": {
      "target": "Nomad",
      "containerRuntime": "Docker",
      "containerRegistry": "registry.example.com/myapp",
      "imageTag": "latest",
      "outputPath": "deployify-output",
      "serviceOverrides": {
        "api": { "imageTag": "canary", "count": 2 }
      },
      "targetConfig": {
        "nomad": {
          "address": "http://localhost:4646",
          "namespace": "default",
          "datacenters": ["dc1"]
        }
      }
    },
    "prod": {
      "target": "Nomad",
      "containerRuntime": "Docker",
      "containerRegistry": "registry.example.com/myapp",
      "imageTag": "stable",
      "outputPath": "deployify-output",
      "targetConfig": {
        "nomad": {
          "address": "http://nomad.internal:4646",
          "token": "env:NOMAD_TOKEN",
          "namespace": "production",
          "datacenters": ["prod-dc1"]
        }
      }
    }
  }
}
```

### Secrets

Don't put tokens and passwords in `deployify-state.json` as plain text. Any secret field
(Nomad and Consul tokens, registry passwords, artifact store credentials, the dashboard
token) accepts an environment variable reference instead:

```json
"token": "env:NOMAD_TOKEN"
```

When a secret field is left empty, Deployify also falls back to the usual variable for it:
`NOMAD_TOKEN`, `CONSUL_HTTP_TOKEN` and `DEPLOYIFY_REGISTRY_PASSWORD`. `init` masks secret
prompts and suggests the `env:` form.

Add these to your `.gitignore`:

```gitignore
deployify-state.json
deployify-output/
**/Properties/launchSettings.json
```

Deployify prints a warning after saving the state file when it contains a literal secret and
git does not ignore the file. If `launchSettings.json` or the state file was already
committed, remove it from the index with `git rm --cached <file>` and rotate any secrets it held.

### Service overrides

Override image tag, replica count, or environment variables per service:

```json
"serviceOverrides": {
  "api":    { "imageTag": "v1.2.3", "count": 3 },
  "worker": { "count": 2, "env": { "LOG_LEVEL": "debug" } }
}
```

---

## Typical CI/CD workflows

### GitHub Actions — full deploy

```yaml
- name: Deploy
  run: |
    deployify ci \
      --project-path src/MyApp.AppHost \
      --env prod \
      --skip-validate \
      --auto-approve
  env:
    NOMAD_TOKEN: ${{ secrets.NOMAD_TOKEN }}
```

### GitHub Actions — plan-gate (fail on drift)

```yaml
- name: Check for drift
  run: |
    deployify ci \
      --project-path src/MyApp.AppHost \
      --skip-validate \
      --skip-apply \
      --fail-on-changes
```

### Separate steps

```bash
deployify generate --env prod
deployify validate --env prod
deployify plan     --env prod
deployify apply    --env prod --auto-approve
```

---

## Project structure

```
Deployify.slnx
Directory.Packages.props                  package versions (Central Package Management)
src/
  Deployify/                              CLI entry point
    Commands/
      InitCommand.cs
      EnvCommand.cs
      GenerateCommand.cs
      ValidateCommand.cs
      PlanCommand.cs
      ApplyCommand.cs
      PublishCommand.cs
      DiffCommand.cs
      ExplainCommand.cs
      GraphCommand.cs
      CiCommand.cs
      WatchCommand.cs
      GlobalOptions.cs
      DependencyInjection.cs
    Program.cs
  Deployify.Core/                         Domain models and interfaces
    Interfaces/
      IDeploymentTarget.cs                implement this to add a target
      IManifestReader.cs
      IPublisher.cs
      IJobGenerator.cs
    Models/
      DeploymentTargetType.cs             Nomad | Kubernetes | Compose | AwsEcs | AzureContainerApps | NomadConsul
      DeployifyState.cs                   EnvironmentConfig, ContainerRuntime
      AspireManifest.cs
      PlanResult.cs
    Services/
      ManifestReader.cs
      Publisher.cs                        Podman socket auto-detection
      StateManager.cs
  Deployify.Target.Nomad/
  Deployify.Target.Kubernetes/
  Deployify.Target.Compose/
  Deployify.Target.AwsEcs/
  Deployify.Target.AzureContainerApps/
  Deployify.Target.NomadConsul/
test/
  Deployify.Core.Tests/
  Deployify.Target.Nomad.Tests/
  Deployify.Target.Kubernetes.Tests/
  Deployify.Target.Compose.Tests/
  Deployify.Target.AwsEcs.Tests/
  Deployify.Target.AzureContainerApps.Tests/
  Deployify.Target.NomadConsul.Tests/
  Deployify.Integration.Tests/
```

---

## Adding a new deployment target

1. Create `src/Deployify.Target.MyTarget/` with a class implementing `IDeploymentTarget`.
2. Add `DeploymentTargetType.MyTarget` to the enum in `DeploymentTargetType.cs`.
3. Register in `DependencyInjection.cs`:
   ```csharp
   .AddSingleton<IDeploymentTarget, MyTargetDeploymentTarget>()
   ```
4. Add init flags in `InitCommand.cs` and display address handling in `EnvCommand.cs`.
5. All commands (`generate`, `validate`, `plan`, `apply`, `ci`, `watch`, etc.) pick it up automatically via the target factory.
