# Deployify

Deploy .NET Aspire AppHost projects to any platform — Nomad, Kubernetes (with `kubectl`, Helm, or GitOps through Argo CD or Flux), Docker Compose, AWS ECS, Azure Container Apps, and Nomad + Consul Connect.

---

## Requirements

- .NET 10 SDK
- A running deployment target (Nomad agent, Kubernetes cluster, Docker daemon, AWS CLI, etc.)
- For the Helm target: `helm` 3 and `kubectl`; the helm-diff plugin for `plan` and `diff`
- For the Argo CD and Flux targets: `git` with access to the config repository, `kubectl`, and the controller installed in the cluster

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
| `destroy` | Remove everything Deployify deployed in an environment |

### State

| Command | Description |
|---|---|
| `state show` | Where state is kept, who holds the lock, what is recorded as deployed |
| `state unlock <id>` | Free a lock left behind by a run that died |
| `state adopt` | Record services deployed before Deployify kept an inventory |
| `state push` | Copy local state into a newly configured remote backend |

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

Writes artifacts (HCL jobs, Kubernetes YAML, Helm charts, `docker-compose.yml`, ECS task definitions, ACA specs) to `deployify-output/` without contacting the cluster. Use this as a standalone step in CI or to inspect outputs before planning.

```bash
deployify generate
deployify generate --env prod --target Kubernetes
deployify generate --env prod --target Helm   # one Helm chart per service, from the same settings
```

### `plan`

Generates artifacts and compares them against the live cluster state. Exits 0 even when the cluster is unreachable — the diff is best-effort.

A service that exists on the target but couldn't be compared is listed under **Could not be checked**, with the reason, instead of being guessed as an update. Typical reasons: `kubectl diff` failed (on Windows it needs a `diff` program — add Git's `usr\bin` folder to `PATH` or set `KUBECTL_EXTERNAL_DIFF`), only the in-cluster API fallback is available, Nomad's plan API returned an error, or Docker Compose is older than 2.18 and has no `up --dry-run`. `plan` and `diff` use the same comparison, so they always agree.

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

#### Health-gated rollouts

On every target (**AWS ECS**, **Nomad**, **Nomad + Consul**, **Kubernetes**, **Azure Container Apps** and **Docker Compose**), `apply` and `ci` deploy services in
dependency order, one wave at a time, and wait for each wave to become healthy before moving on.
If a service fails, doesn't become healthy within the timeout, or the run is cancelled with
Ctrl+C, every service the rollout touched is put back on the revision it ran before, in reverse
order, and each restored service is checked for health again.

```bash
deployify apply --auto-approve --health-timeout 300   # seconds per service (default 600)
deployify apply --auto-approve --no-rollback          # gate on health, leave a failure in place
deployify apply --auto-approve --no-health-gate       # old behaviour: deploy and return
deployify apply --auto-approve --rollback-timeout 600 # budget for the whole rollback (default 900)
```

Exit codes: `0` healthy, `1` failed (nothing rolled back), `2` failed and rolled back,
`3` failed and the rollback did not complete — check the services listed in the output.

Every rollout writes a journal to `.deployify/rollouts/<id>.json` next to `deployify-state.json`
with what ran before, what was deployed and what happened; a lock file there stops two rollouts
to the same environment from running at once. Add `.deployify/` to your `.gitignore`.

Use a unique image tag per release: with `latest`, a rollback re-deploys the same tag, which may
already point at the new image. The rollout warns when it sees a mutable tag.

On ECS the deployment circuit breaker with rollback is turned on by default
(`"deploymentCircuitBreaker": false` under `targetConfig.ecs` turns it off). When ECS reverts a
service itself, Deployify waits for the old task definition to be healthy instead of rolling back
a second time. Rolling back also puts the service's SSM parameters back: the journal records each
parameter's version (never its value), and the rollback writes that version's value back as the
newest version before the tasks restart. SSM keeps a parameter's last 100 versions.

On Nomad, jobs get `auto_revert = true` in their update block by default (`"autoRevert": false`
under `targetConfig.nomad` or `targetConfig.nomadconsul` turns it off). A revision is a job
version; rolling back reverts the job, which Nomad registers as a new version, and Deployify waits
for that one. Job secrets in Nomad Variables are put back first. Nomad keeps no history of a
variable, so before a rollout changes a job, Deployify copies its secrets to
`deployify/rollback/<job>/v<version>` (a path the job's own tasks can't read; the newest 5 copies
are kept) and the journal records only that path, which lets `deployify rollback` restore them
later too. The Deployify token needs write access to `deployify/*` variables; without it, secrets
are only restored by a rollback in the same run. With Nomad + Consul, a service that registers a Consul check must also have a
passing instance in Consul before it counts as healthy.

On Kubernetes, rollouts need `kubectl` (without it, `apply` uses the API fallback as before, with no
health gate). A service is healthy when its Deployment has caught up with the change and every pod
is updated and available, the same test `kubectl rollout status` uses; a Deployment that exceeds its
progress deadline fails. Rolling back puts the service's ConfigMap and Secret back first, then runs
`kubectl rollout undo` to the captured revision and restores the replica count; a Deployment that
the rollout created is scaled to zero. So that a later `deployify rollback` can restore them too,
the rollout keeps copies of the ConfigMap and Secret in the service's namespace
(`<service>-config-rollback-g<generation>`, `<service>-secrets-rollback-g<generation>`, labelled
`deployify/rollback-of=<service>`; the newest 5 are kept), and the journal records only their
names. Key Vault–backed Secrets are managed by the CSI driver and aren't copied. Kubernetes keeps 10 old ReplicaSets by default (`revisionHistoryLimit`); older revisions can't be
rolled back to.

On Azure Container Apps, a revision is the Container Apps revision name. A revision is healthy once
it is provisioned, active and running (scaled to zero counts), isn't reported unhealthy, and the
app has made it its latest ready revision. Rolling back runs `az containerapp revision copy` from
the captured revision, which creates a new revision with the old template, and waits for that one;
an app the rollout created is deleted. With the Key Vault secret backend, the app's secrets are put
back first: the journal records each secret's version (never its value), and the rollback writes
that version's value back as the newest version, since apps reference secrets without a version.
Other app-level settings outside the revision template (ingress, secret definitions) are not
rolled back. Without probes, Container Apps can only report whether
containers start, so add probes to get a meaningful gate.

On Docker Compose, a revision is the image ID (`sha256:…`) the service's containers run, so a
rollback works even with a `latest` tag as long as the old image is still in the local image store
(don't `docker image prune` between deploys). Services are brought up one at a time with
`up -d --no-deps`, and once all are healthy a final whole-project `up -d --remove-orphans` removes
services no longer in the manifest, as a plain apply does. A service is healthy when its containers
run the new image, aren't restarting, and pass their healthcheck if they have one
(`generateHealthChecks`); a container that exits, restarts twice or turns unhealthy fails the
rollout. Rolling back brings the service back up from the same compose files with a small
`rollback.<service>.override.yml` next to them that pins the old image ID; a service the rollout
created has its containers removed. The environment comes back too: before changing a service,
the rollout saves its container's environment to `.deployify-rollback/<service>.<image>.json` next
to the compose files (owner-readable only, like the generated `.env` files, since it holds the same
secrets; the newest 5 per service are kept), and the override replaces the service's environment
with it. That uses Compose's `!override` tag, which needs Docker Compose 2.24.4 or later.

```bash
deployify status                        # recent rollouts in this environment
deployify status --rollout <id>         # per service: before, deployed, restored, detail
deployify rollback --auto-approve       # undo the latest rollout from its journal
deployify rollback --rollout <id>       # undo a specific one
```

`rollback` restores every service the rollout touched that isn't already back, in reverse order,
and waits for each one to be healthy. It exits `0` when everything is restored and `3` when a
service could not be.

#### Pruning services that left the manifest

By default `apply` never deletes anything: when a service disappears from the AppHost, plan lists
it as "to delete" and says it stays in place. Pass `--prune` (or set `"Prune": true` on the
environment) to remove such services after a successful deploy:

```bash
deployify apply --auto-approve --prune
deployify apply --auto-approve --prune --force-prune   # allow removing more than half, or all
```

A service is pruned only when it is **both** in the environment's inventory (see
[Deployment state](#deployment-state-locking-and-remote-backends)) **and** carries Deployify's
ownership marker on the platform, so neither a stale inventory nor something another team runs
in the same cluster is ever deleted. The prune runs after every service passed its health gate,
in reverse dependency order, and is journalled like a rollout (`deployify status`). It is refused,
and `apply` exits `4` with the deploy itself done, when:

- the target can't be asked what Deployify manages (an outage never looks like "nothing there"),
- plan could not check some service (`?` in the plan),
- it would remove every managed service, or more than half of them, without `--force-prune`.

Compose is the exception: `docker compose up --remove-orphans` has always removed a project's
orphaned containers, and still does.

### `destroy`

```bash
deployify destroy --dry-run                     # show the destroy plan, change nothing
deployify destroy                               # asks you to type the environment name
deployify destroy --auto-approve                # required without an interactive terminal
deployify destroy --service worker,api          # only these services
deployify destroy --keep-secrets                # keep Nomad variables / SSM parameters
deployify destroy --volumes                     # Compose: also remove data volumes
```

Destroy takes the environment lock, lists what the inventory records and what the platform marks
as Deployify's, and deletes what is in both, dependents first. Each service is dropped from the
inventory as soon as it is gone and every step is journalled, so after a failure (it stops at the
first one) running `destroy` again continues with what is left. Services recorded in state but
already gone are only dropped from state; services marked as Deployify's but not in this
environment's state are listed and left alone (`deployify state adopt` takes them in).

Set `"Protected": true` on an environment to make `destroy` refuse to run against it.

What each target removes, and keeps:

| Target | Removes | Keeps |
|---|---|---|
| Nomad, Nomad + Consul | purges each job; its `nomad/jobs/<job>` variable and rollback copies (unless `--keep-secrets`) | the namespace |
| Kubernetes | each service's namespace (only ones labelled `app.kubernetes.io/managed-by=deployify`) | the `<prefix>-system` secrets namespace, Vault / Key Vault secrets |
| Compose | the services' containers, then the project network; volumes with `--volumes` | images, volumes by default |
| AWS ECS | `delete-service --force`, every revision of its task definition family, its SSM parameters under `<secretsPrefix>/<service>` (unless `--keep-secrets`) | the cluster, networking, load balancers, log groups, generated parameter values |
| Azure Container Apps | each Container App | the resource group, the Container Apps environment, Key Vault secrets |

Registry images are never deleted.

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

# Exit non-zero if the plan detects any drift (useful for change gating).
# A service that could not be checked counts as a change here.
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

To get the same deployment as Helm charts instead (to install them yourself, or hand them to
another team), run `deployify generate --target helm` on a Kubernetes environment: see below.

---

### `Helm` — Kubernetes via Helm

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

---

### `ArgoCd` / `Flux` — Kubernetes through GitOps

Instead of applying to the cluster, Deployify commits the Kubernetes target's manifests to a config
repository and lets the GitOps controller apply them. Both targets write the same thing: a folder per
service under `{path}/{service}` with the usual manifests and a `kustomization.yaml`, a root
`kustomization.yaml` listing the services, and the controller's objects under `{path}/_registration/`.
They differ only in those objects and in how status is read:

| | Argo CD (`ArgoCd`) | Flux (`Flux`) |
|---|---|---|
| Per service | `Application` `{namespacePrefix}-{service}` in `argocd` | `Kustomization` `{namespacePrefix}-{service}` in `flux-system` |
| Shared | — (repository credentials are configured in Argo CD) | a `GitRepository` (or an existing one: `createSource: false`) |
| Removing a service | the Application's resources finalizer | `prune: true` on the Kustomization |
| `wait` watches | `status.sync.revision`, sync and health status | `lastAppliedRevision` and the `Ready` condition |

```json
"target": "ArgoCd",
"targetConfig": {
  "kubernetes": { "context": "my-cluster", "namespacePrefix": "myapp", "ingressBaseDomain": "example.com" },
  "gitops": {
    "repoUrl": "https://github.com/acme/platform-config.git",
    "branch": "main",
    "path": "clusters/prod",
    "push": true,
    "register": true,
    "wait": false,
    "waitTimeoutSeconds": 300
  },
  "argocd": { "namespace": "argocd", "project": "default", "autoSync": true, "prune": true, "selfHeal": true }
}
```

For Flux, use `"target": "Flux"` and
`"flux": { "namespace": "flux-system", "sourceName": null, "createSource": true, "secretRef": null, "interval": "1m", "timeout": "5m", "prune": true }`.
`path` defaults to `deployify/{environment}`. Init flags: the Kubernetes ones plus `--gitops-repo`,
`--gitops-branch` and `--gitops-path`.

Needs `git` (with access to the repository through your credential helper or SSH agent; git is never
allowed to prompt) and, to register services, put secrets in the cluster or wait, `kubectl`.
Deployify works in its own clone (`gitops.checkoutDirectory`, by default under the temp folder) and
resets it to the remote branch on every run.

**Generate, validate, plan and diff** write the folder to `deployify-output/gitops/tree/`. `validate`
builds each service folder with `kubectl kustomize`. `plan` and `diff` compare it with what is
committed on the branch — the desired state the controller works from, not the live cluster:
a service without a folder is new, one whose files differ is changed (the files are listed), and a
committed folder the AppHost no longer has is listed for removal.

**Apply** puts secrets in their backend, copies the folders into the clone, commits
(`deployify: update {env} (…)`) and pushes; nothing is committed when nothing changed. A rejected
push (someone pushed in between) fails the run: run apply again. With `register` on, it then applies
the controller's objects from the committed folder (`kubectl apply`), fails with a hint when the
controller's CRD isn't installed, and asks the controller to look now (Argo CD's refresh annotation,
Flux's `reconcile.fluxcd.io/requestedAt`). Turn `register` off when the objects are managed elsewhere,
for instance with an app-of-apps pointing at `{path}/_registration`. With `push` off, apply only
commits, for a pipeline that pushes or opens a pull request itself. With `wait` on, apply returns once
every service's object has applied the new commit and is healthy, and fails a service the controller
reports as failed or degraded. Health-gated rollouts and `rollback` don't apply: the controller owns
the rollout, and a rollback is a `git revert`.

**Secrets** must stay out of git, so the `Inline` backend is refused for services with secrets: use
`KubernetesSecret`, `Vault` or `KeyVault`. As on the Kubernetes target, the `{service}-secrets` Secret
is applied out of band before the commit (it is not in the repository, so the controller never prunes
it), and the pods are restarted when only those values changed.

**Destroy** and `apply --prune` delete the service's Application or Kustomization and wait while the
controller removes what it deployed (including the namespace), then remove the folder and push. With
`register` off only the folder is removed; delete the object where it is managed.

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

## Deployment state, locking and remote backends

Next to the configuration in `deployify-state.json` (which you commit), Deployify keeps the
**deployment state** of each environment:

| Key | What |
|---|---|
| `{prefix}/{env}/inventory.json` | every service Deployify deployed, with its dependencies (what destroy and prune work from) |
| `{prefix}/{env}/lock.json` | who is changing the environment right now |
| `{prefix}/{env}/parameters.json` | generated parameter values (remote backends only; locally they are `deployify-parameters.json`) |
| `{prefix}/{env}/rollouts/{id}.json` | rollout, prune and destroy journals (remote backends only; locally `.deployify/rollouts/`) |

By default it all lives in `.deployify/state/` next to the state file, which is fine for one
machine. Keep `.deployify/` and `deployify-parameters.json` out of git.

### Ownership

Every resource Deployify creates carries an ownership marker:

| Target | Marker |
|---|---|
| Nomad, Nomad + Consul | job meta `deployify-managed = "true"` |
| Kubernetes | namespace label `app.kubernetes.io/managed-by=deployify` |
| Compose | the Compose project (`com.docker.compose.project` label) |
| AWS ECS | service tags `deployify-managed-by=deployify` and `deployify-env=<environment>` |
| Azure Container Apps | resource tags `deployify-managed-by=deployify` and `deployify-env=<environment>` |

ECS services and Container Apps are tagged when they are created and on every later deploy, so
services that existed before tagging are adopted on their next `apply`. Plan only lists a service
as "to delete" when it carries the marker (and, on ECS and ACA, this environment's tag).

Deployments made before Deployify kept an inventory aren't in it. Run `deployify state adopt` once
to record everything the platform marks as Deployify's in this environment.

### The lock

`apply`, `ci` (unless `--skip-apply`), `rollback`, `destroy`, `state adopt` and `state push` take
the environment's lock first; `plan`, `diff`, `status` and `state show` don't need it. A second run
fails at once with who holds it:

```
✗ Environment 'prod' is locked by ci@runner-7, apply, started 4 min ago (lock id 3f9a1c2e7b4d),
  run: https://github.com/acme/shop/actions/runs/123. If that run is no longer going, wait for the
  lock to expire (14:31:07) or run 'deployify state unlock 3f9a1c2e7b4d'.
```

The lock is a lease: its holder renews it every third of `LockTtlSeconds` (default 180), so a run
that crashes frees it within that time. Set `LockWaitSeconds` to wait for a busy lock instead of
failing (useful when CI jobs queue up). The CI run URL comes from GitHub Actions, GitLab CI or
Azure Pipelines variables, or from `DEPLOYIFY_RUN_URL`. Every state write is compare-and-swap,
so a run that lost its lock (it expired while the run hung) can't overwrite the next run's state.
Machines sharing a lock need roughly synchronized clocks (NTP is plenty).

### Remote state

Configure a backend per environment under `State`, so CI runners and colleagues share the lock,
the inventory, the generated parameter values and the rollout journals:

```json
"prod": {
  "Target": "AwsEcs",
  "Protected": true,
  "State": {
    "Backend": "S3",
    "Prefix": "shop",
    "S3": { "Bucket": "acme-deploy-state", "Region": "eu-west-1" }
  }
}
```

| Backend | Section | Lock and writes use | Needs |
|---|---|---|---|
| `Local` (default) | — | a content hash, guarded by a file lock | — |
| `AzureBlob` | `"AzureBlob": { "AccountName": "...", "Container": "deployify-state", "SasToken": "env:STATE_SAS" }` | blob ETags (`If-Match` / `If-None-Match: *`) | a SAS token, or `az login` with Storage Blob Data Contributor |
| `S3` | `"S3": { "Bucket": "...", "Region": "...", "Profile": "..." }` | S3 conditional writes | AWS CLI 2.22+ |
| `Consul` | `"Consul": { "Address": "http://consul:8500", "Token": "env:CONSUL_HTTP_TOKEN" }` | KV check-and-set | — |
| `Kubernetes` | `"Kubernetes": { "Namespace": "deployify-state", "Context": "..." }` | Secret `resourceVersion` | kubectl; the namespace must exist |

Commands work on local copies as before: when they start they pull this environment's parameter
values and journals from the backend, and they push them back as they change (each journal save,
and the parameter values before anything is deployed). Read-only commands fall back to the local
copy, with a warning, when the backend can't be reached; commands that take the lock fail instead.

Remote state holds generated passwords: keep the bucket, container, Consul path or namespace
private, and encrypted at rest.

To move an existing environment to remote state, add the `State` section and run
`deployify state push` once. It refuses to replace remote state of another lineage, or a newer one,
without `--force`.

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
      DestroyCommand.cs
      StateCommand.cs                     state show | unlock | adopt | push
      StateCli.cs                         prune, removal plans, lock output
      GlobalOptions.cs
      DependencyInjection.cs
    Program.cs
  Deployify.Core/                         Domain models and interfaces
    Interfaces/
      IDeploymentTarget.cs                implement this to add a target
      IResourceLifecycle.cs               optional: list / delete what Deployify deployed
      IManifestReader.cs
      IPublisher.cs
      IJobGenerator.cs
    Models/
      DeploymentTargetType.cs             Nomad | Kubernetes | Compose | AwsEcs | AzureContainerApps | NomadConsul | Helm
      DeployifyState.cs                   EnvironmentConfig, ContainerRuntime
      AspireManifest.cs
      PlanResult.cs
    State/                                IStateBackend (local, AzureBlob, S3, Consul, Kubernetes),
                                          StateLease (the lock), InventoryStore, StateSession
    Lifecycle/                            RemovalPlanner (what may be deleted), ServiceRemover
    Services/
      ManifestReader.cs
      Publisher.cs                        Podman socket auto-detection
      StateManager.cs
  Deployify.Target.Nomad/
  Deployify.Target.Kubernetes/            also writes Helm charts (HelmChartWriter)
  Deployify.Target.Helm/                  Kubernetes via helm upgrade --install
  Deployify.Target.Compose/
  Deployify.Target.AwsEcs/
  Deployify.Target.AzureContainerApps/
  Deployify.Target.NomadConsul/
test/
  Deployify.Core.Tests/
  Deployify.Target.Nomad.Tests/
  Deployify.Target.Kubernetes.Tests/
  Deployify.Target.Helm.Tests/
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
6. For `destroy` and `apply --prune`, also implement `IResourceLifecycle`: put an ownership marker
   on everything the target creates, list what carries it (throwing when the platform can't be
   asked), and delete one service. Which services may be deleted is decided for you.
