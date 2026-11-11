---
title: "Pruning and destroying"
description: "Removing services that left the AppHost, or a whole environment."
---

## Pruning services that left the manifest

By default `apply` never deletes anything: when a service disappears from the AppHost, plan lists
it as "to delete" and says it stays in place. Pass `--prune` (or set `"Prune": true` on the
environment) to remove such services after a successful deploy:

```bash
deployify apply --auto-approve --prune
deployify apply --auto-approve --prune --force-prune   # allow removing more than half, or all
```

A service is pruned only when it is **both** in the environment's inventory (see
[Deployment state](/Deployify/concepts/state/)) **and** carries Deployify's
ownership marker on the platform, so neither a stale inventory nor something another team runs
in the same cluster is ever deleted. The prune runs after every service passed its health gate,
in reverse dependency order, and is journaled like a rollout (`deployify status`). It is refused,
and `apply` exits `4` with the deploy itself done, when:

- the target can't be asked what Deployify manages (an outage never looks like "nothing there"),
- plan could not check some service (`?` in the plan),
- it would remove every managed service, or more than half of them, without `--force-prune`.

Compose is the exception: `docker compose up --remove-orphans` has always removed a project's
orphaned containers, and still does.

## Destroying an environment

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
inventory as soon as it is gone and every step is journaled, so after a failure (it stops at the
first one) running `destroy` again continues with what is left. Services recorded in state but
already gone are only dropped from state; services marked as Deployify's but not in this
environment's state are listed and left alone (`deployify state adopt` takes them in).

Set `"Protected": true` on an environment to make `destroy` refuse to run against it.

What each target removes, and keeps:

| Target | Removes | Keeps |
|---|---|---|
| Nomad, Nomad + Consul | purges each job; its `nomad/jobs/<job>` variable and rollback copies (unless `--keep-secrets`) | the namespace |
| Kubernetes | each service's namespace (only ones labeled `app.kubernetes.io/managed-by=deployify`) | the `<prefix>-system` secrets namespace, Vault / Key Vault secrets |
| Compose | the services' containers, then the project network; volumes with `--volumes` | images, volumes by default |
| AWS ECS | `delete-service --force`, every revision of its task definition family, its SSM parameters under `<secretsPrefix>/<service>` (unless `--keep-secrets`) | the cluster, networking, load balancers, log groups, generated parameter values |
| Azure Container Apps | each Container App | the resource group, the Container Apps environment, Key Vault secrets |

Registry images are never deleted.
