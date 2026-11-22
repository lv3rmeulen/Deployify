---
title: "Release checklist for the cloud targets"
description: "Manual checks to run against AWS ECS, Azure Container Apps, OVHcloud, IIS and Cloud Run before a release, since CI can't deploy to them."
---

CI builds every sample and deploys `hello-aspire` to Docker Compose and to kind, but it can't
deploy to the targets that need a paid account or a Windows server: **AWS ECS**, **Azure Container
Apps**, **OVHcloud Managed Kubernetes**, **IIS** and **Google Cloud Run**. Their unit and snapshot
tests run in CI; the platform itself is only exercised by this checklist.

## When to run it

| Release | Run |
|---|---|
| Major or minor (`x.0.0`, `1.x.0`) | Every section below |
| Patch | Only the sections for targets whose project changed since the last tag, plus all of them if `Deployify.Core`, `Deployify.Targets` or the rollout engine changed |
| Prerelease (`-rc.N`) | As for the release it leads to; the final release can then skip targets with no changes since the RC |

To see what changed since the last release:

```bash
git diff --stat v1.2.0..HEAD -- src/Deployify.Core src/Deployify.Targets \
  src/Deployify.Target.AwsEcs src/Deployify.Target.AzureContainerApps \
  src/Deployify.Target.OvhCloud src/Deployify.Target.Iis src/Deployify.Target.CloudRun
```

Record the result of each run (date, commit, who, pass or the issue it raised) in the release's
pull request or GitHub release notes, so the next release knows what was last checked.

## Before you start

- [ ] `main` is green in CI (Build, Samples, Aspire preview) at the commit you're releasing.
- [ ] Install the release candidate as a tool from that commit, not from your working copy:
  ```bash
  git switch --detach <commit>
  dotnet pack src/Deployify/Deployify.csproj -c Release
  dotnet tool update --global --add-source ./src/Deployify/nupkg deployify
  deployify --version   # matches the version you're about to tag
  ```
- [ ] Work in a scratch copy of `samples/hello-aspire`, with one environment per target in its
  `deployify-state.json`: `rc-ecs`, `rc-aca`, `rc-ovh`, `rc-iis`, `rc-run`. Never reuse an
  environment that serves anything real.
- [ ] Give each environment a `ContainerRegistry` the platform can pull from, and a remote state
  backend (S3, Azure Blob, …) for at least one of them, so the run also covers remote state.
- [ ] Set the secret parameter: `export Parameters__admin_key=rc-test`.
- [ ] Sign in to each platform the way CI users do where possible (OIDC or a service account
  rather than your own admin login), so missing permissions show up here and not in a user's
  pipeline. See [signing in from CI](/Deployify/guides/ci-cloud-sign-in/).

## The run, per target

Go through this flow once for each target, with `--env rc-<target>` on every command. The
target-specific checks follow below.

1. **Init and validate**
   - [ ] `deployify init` with the target's init flags writes the expected `targetConfig`.
   - [ ] `deployify validate` passes, and `deployify plan --strict` lists exactly the resources
     you expect the target can't deploy.
2. **First deploy**
   - [ ] `deployify publish` pushes both images and records their digests in `images.json`.
   - [ ] `deployify plan` shows every service as created, with its rollout strategy.
   - [ ] `deployify apply --require-digest` succeeds and the health gate waits for every service.
   - [ ] `deployify status` and `deployify status --output json` show every service healthy.
   - [ ] Smoke test: the `api` and `web` endpoints answer, and `api` reaches Redis and Postgres
     (where the target runs them).
   - [ ] `deployify logs api --tail 50` and `deployify logs api --follow` show the app's output.
3. **No-op and change**
   - [ ] Running `plan` again right away shows no changes, and `apply` leaves everything alone.
   - [ ] Change an environment variable on `api`, then `plan` shows only that service changing
     and `apply` deploys only it.
4. **Drift and history**
   - [ ] Change something on the platform by hand (scale a service, edit an env var), then
     `deployify drift` reports it and `deployify diff` shows it.
   - [ ] `deployify history` lists the deploys from this run.
5. **Rollout strategies** (skip on IIS, which only has `Rolling`)
   - [ ] Set `"rollout": { "strategy": "Canary", "canary": { "steps": [{ "weight": 20, "pause": "1m" }] } }`
     and apply a change: the candidate starts, gets traffic, is promoted, and the old version is
     removed.
   - [ ] The same with `BlueGreen` and `"previewPause": "1m"`: the preview address answers
     before the switch.
   - [ ] Set `Rolling` again and apply: the service goes back to plain rolling updates.
6. **Failure and rollback**
   - [ ] Make `api` fail on start (for example `throw new Exception("rc test");` at the top of
     `Program.cs`), publish and apply: the health gate fails and every service is rolled back to
     the release before.
   - [ ] Do the same during a canary step: all traffic goes back to the old version and the
     candidate is removed.
   - [ ] Undo the break, deploy again, then `deployify rollback --auto-approve` goes back one
     release and the app still works.
   - [ ] Ctrl+C during an apply rolls back cleanly and releases the lock.
7. **Signing** (if the release touches signing or publishing)
   - [ ] With signing configured, `apply --require-signature` passes for signed images and fails
     for an unsigned one.
8. **Prune and destroy**
   - [ ] Remove `web` from the AppHost, then `apply --prune` removes only `web`.
   - [ ] Create a service on the platform by hand with a name like Deployify's; `destroy` leaves
     it alone (no ownership marker).
   - [ ] `deployify destroy --auto-approve` removes everything Deployify created, and nothing
     else. Check the platform by hand, using the
     [destroy table](/Deployify/guides/prune-and-destroy/) for what's deliberately kept.

## AWS ECS

Needs: a cluster, subnets and a security group, an execution role and task role, a registry (ECR),
the AWS CLI on `PATH`.

- [ ] Deploy with `serviceConnectNamespace` set: `api` and `web` reach each other by name.
- [ ] `waitForStable`: a crash-looping task makes `apply` fail instead of reporting success.
- [ ] Secrets with the default SSM backend: the SecureStrings are written under `secretsPrefix`,
  and the task definition references them.
- [ ] Repeat one deploy with `"secrets": { "backend": "SecretsManager" }`: one secret per service,
  pinned to a version, and a rollback brings back that version's values.
- [ ] Canary becomes `CANARY`, equal steps up to 100% become `LINEAR`, blue-green becomes
  `BLUE_GREEN` (check with `aws ecs describe-services`). A service set back to `Rolling` gets
  `strategy=ROLLING`.
- [ ] `previewPause` with blue-green is rejected by `plan`.
- [ ] `logs` reads the CloudWatch streams; `--follow` picks up a task that starts later.
- [ ] Run once with the permissions policy from
  [signing in from CI](/Deployify/guides/ci-cloud-sign-in/#a-permissions-policy-for-the-ecs-target)
  and nothing more: every command above works with it.
- [ ] After `destroy`: services, task definition revisions and SSM parameters are gone (Secrets
  Manager secrets are scheduled for deletion). Delete the CloudWatch log group, cluster and
  networking yourself if you made them for this run.

## Azure Container Apps

Needs: a resource group, a Container Apps environment, an ACR with an identity that has `AcrPull`,
the Azure CLI on `PATH`.

- [ ] Only `api` and `web` get external ingress; Redis and Postgres get internal ingress.
- [ ] Image pull from ACR works with the user-assigned identity in `acrIdentityResourceId`, and
  again with the system-assigned identity when it's left out.
- [ ] Leave `subscriptionId` empty: the CLI's current subscription is used.
- [ ] Key Vault backend: secrets are written to the vault and referenced by `keyVaultUrl`; a
  changed value restarts the revision instead of waiting for the platform's refresh.
- [ ] Canary and blue-green switch the app to multiple-revision mode, the candidate answers at
  `https://<app>---candidate.<environment domain>`, traffic weights are exact
  (`az containerapp ingress traffic show`), and the old revision is deactivated after promotion.
- [ ] Going back to `Rolling` sets single-revision mode again.
- [ ] `logs` shows the latest ready revision's replicas (at most 300 lines each).
- [ ] After `destroy`: the Container Apps are gone; the resource group, environment and Key Vault
  secrets remain. Delete the resource group yourself to stop billing.

## OVHcloud Managed Kubernetes

Needs: a Public Cloud project with a Managed Kubernetes cluster, an ingress controller (for example
ingress-nginx) if you test ingress, a Managed Private Registry project, `kubectl` on `PATH`.

- [ ] With only the API credentials in the environment (no kubeconfig on disk), `apply` fetches
  the kubeconfig and writes it owner-only to the default path.
- [ ] Credentials: run once with an API application (`OVH_APPLICATION_KEY`, `_SECRET`,
  `OVH_CONSUMER_KEY`) and once with a service account (`OVH_CLIENT_ID`, `OVH_CLIENT_SECRET`).
- [ ] An application with only the two documented rights is enough.
- [ ] While the cluster is `UPDATING` (start a minor version or node pool change), `apply` waits
  and then continues; a short `readyTimeoutSeconds` makes it stop with a clear message.
- [ ] `generate`, `explain` and `graph` work with no credentials set.
- [ ] Private registry: the `deployify-registry` pull Secret appears in every service namespace,
  and pods pull with a Harbor robot account.
- [ ] Canary creates `<service>-canary`; blue-green creates `<service>-preview` and its Service,
  and the switch moves the Service selector.
- [ ] Without `projectId` and `clusterId`, with `kubernetes.kubeConfigPath` set, it behaves like
  the Kubernetes target.
- [ ] After `destroy`: the service namespaces are gone; the cluster and node pools are untouched.
  Delete the cluster yourself if you made it for this run.

## IIS

Needs: a Windows Server with IIS (`Web-Server`, `Web-Scripting-Tools`), the ASP.NET Core Hosting
Bundle for the samples' .NET version, an administrator account, WinRM and/or OpenSSH with
PowerShell 7 as a subsystem. A local VM is enough and costs nothing.

IIS only hosts projects with an http endpoint, so for `hello-aspire`:

- add `"ignoreResources": ["web"]` (a Dockerfile resource);
- point `cache` and `postgres` at a Redis and a Postgres the server can reach, under
  `iis.externalHosts`.

Checks:

- [ ] `deployify publish` produces a `dotnet publish -r win-x64` package per project, not an image.
- [ ] Deploy over WinRM from Windows, over SSH from Linux or macOS, and with `host` left out on
  the server itself.
- [ ] Remove the Hosting Bundle or the management API (or test on a server without them):
  `plan` stops and says which to install.
- [ ] Each `apply` adds a release folder, keeps `keepReleases`, and recycles only the pools that
  changed. A second apply with nothing changed touches nothing.
- [ ] Environment variables and secrets are in `applicationHost.config`, not in `web.config` or
  the site folder; the `settings` folder is readable by Administrators and SYSTEM only.
- [ ] `hostNames` adds a port 80 binding; with `certificateThumbprint`, 443 bindings with SNI work.
- [ ] With `healthPath` set to a path that returns 500, `apply` rolls every site back to its
  previous release and settings.
- [ ] `captureStdout` on: `logs` shows stdout files and event log entries; off: only event log
  entries. Toggling it redeploys every site.
- [ ] `plan` with `"strategy": "Canary"` fails (only `Rolling` is available).
- [ ] After `destroy`: only sites with a `deployify.json` marker are removed, with their pools,
  settings and folders; a site created by hand stays.

## Google Cloud Run

Needs: a Google Cloud project with Cloud Run and Secret Manager enabled, Artifact Registry,
`gcloud` on `PATH`. Cloud Run only runs HTTP services, so put Redis and Postgres elsewhere as on
IIS.

- [ ] Services reach each other at `https://<service>-<projectNumber>.<region>.run.app`, with
  `projectNumber` both set and looked up.
- [ ] Secrets go to Secret Manager; a rollback only moves traffic and keeps the secret versions
  the old revision was deployed with.
- [ ] Canary and blue-green tag the candidate `candidate` (`https://candidate---<service host>`)
  and split traffic exactly.
- [ ] After `destroy`: the services are gone. Delete images and secrets you made for this run.

## Afterwards

- [ ] Check each platform's billing page for anything still running.
- [ ] Remove the `rc-*` environments' remote state, or keep it if the next release should start
  from it (that also tests upgrading state from the previous version).
- [ ] File an issue for every failure, and don't tag the release until the ones that would hit
  users are fixed or listed in [known limitations](/Deployify/reference/known-limitations/).
