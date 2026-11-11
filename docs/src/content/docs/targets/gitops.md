---
title: "Argo CD and Flux (GitOps)"
description: "Deploying an Aspire app to Argo CD and Flux (GitOps) with Deployify."
---

Target ID: `ArgoCd` / `Flux`

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
allowed to prompt) and, to register services, put secrets or a registry login
(`kubernetes.registryCredentials`) in the cluster or wait, `kubectl`.
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
