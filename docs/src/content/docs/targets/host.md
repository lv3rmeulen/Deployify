---
title: "Host (systemd and Windows services)"
description: "Deploying an Aspire app's projects to plain Linux or Windows machines with Deployify, as systemd units or Windows services."
---

Target ID: `Host`

Deploys each Aspire **project** as an operating-system service on one or more plain machines: a
**systemd unit** on Linux, reached over SSH, or a **Windows service** on Windows, reached over
PowerShell remoting (WinRM or SSH). It can also deploy to the machine Deployify runs on. Projects are
published to a folder (`dotnet publish -r linux-x64` or `win-x64`, self-contained by default)
instead of being built as images. No container runtime, cluster or web server is involved.

Use it for a VM or a few machines where Kubernetes or Nomad would be overkill, and for background
workers on Windows, which the [IIS target](/Deployify/targets/iis/) can't host.

```json
"target": "Host",
"targetConfig": {
  "host": {
    "hosts": ["app01.example.com", "app02.example.com"],
    "os": "Linux",
    "userName": "deploy",
    "root": "/opt/deployify",
    "healthPath": "/health",
    "externalHosts": { "cache": "redis.internal", "db": "postgres.internal" }
  }
}
```

- `hosts`: the machines to deploy to, as host names or SSH config aliases. Every service runs on
  every host. Leave it empty to deploy to the machine Deployify runs on.
- `os`: `Linux` or `Windows`. Default: Linux, or the OS Deployify runs on when `hosts` is empty.
- `userName`: the user to connect as. On Linux the services also run as this user (see
  [the hosts](#linux-hosts)). Default: what your SSH client would use.
- `keyFilePath` / `port`: the SSH key and port. Default: the SSH client's own key lookup and port 22.
- `transport`: Windows hosts only, `WinRm` (default) or `Ssh`, as on the IIS target. `useSsl`
  connects to WinRM over HTTPS, and `password` is the WinRM password. It falls back to
  `HOST_PASSWORD`; use an `"env:NAME"` reference rather than storing it.
- `root`: the folder on each host for the services and their releases. Default `/opt/deployify`
  or `C:\deployify`.
- `namePrefix`: prefix for service names. Default: the environment name and a dash. The service
  `api` in `prod` becomes the unit or Windows service `deployify-prod-api`, so several environments
  can share a host.
- `runtime` / `selfContained`: the `dotnet publish` runtime identifier (default `linux-x64` or
  `win-x64`) and whether to bundle the .NET runtime (default **yes**, so the hosts don't need .NET).
  For framework-dependent apps on Linux, `dotnetRoot` sets `DOTNET_ROOT` when .NET isn't installed
  where apps find it by themselves.
- `serviceAccount` / `serviceAccountPassword`: Windows hosts only, the account services run as.
  Default `NT AUTHORITY\LocalService`. `NT AUTHORITY\NetworkService`, `LocalSystem` and group
  managed service accounts (ending in `$`) need no password; a user account needs
  `serviceAccountPassword`, which falls back to `HOST_SERVICE_PASSWORD`.
- `listenAddress`: where services listen. `*` (default) is every interface, so services can be
  reached from other machines; `localhost` keeps them on the host, behind a reverse proxy you run.
- `basePort`: first port given to http bindings without one in the manifest (default 8080).
- `keepReleases`: release folders kept per service (default 3, at least 2). Rollback can only go
  back to a release that is still kept.
- `healthPath`, `startupSeconds`, `hostHealthTimeoutSeconds`: see
  [health checks](#health-checks-and-rollback).
- `powerShell`: Windows hosts, the local PowerShell to run. Default `pwsh`, then Windows PowerShell
  on Windows.

Init flags: `--host-hosts`, `--host-os`, `--host-transport`, `--host-user` and `--host-root`.

## Linux hosts

Each host needs systemd, `bash`, `tar` and `base64`, plus `curl` for http health checks. Deployify
installs nothing else. Where Deployify runs, you need the OpenSSH client (`ssh` and `scp`), with the
hosts' keys in your `known_hosts`: the connection is non-interactive, and Deployify never turns
off host key checking.

The deploy user does three things:

- **It owns the root folder.** Create it once and hand it over:
  `sudo install -d -o deploy -g deploy /opt/deployify`.
- **It runs the services.** Each unit has `User=deploy`, so the services can read their own
  release folders and secrets, and nothing else needs root.
- **It runs `systemctl` and `journalctl` through `sudo` without a password**, unless it is root:

  ```
  # /etc/sudoers.d/deployify
  deploy ALL=(root) NOPASSWD: /usr/bin/systemctl, /usr/bin/journalctl
  ```

  Note that `systemctl` through sudo can start any unit, so this user is close to root on the
  host. If it is in the `systemd-journal` group, `journalctl` runs without sudo.

When something is missing (systemd, the sudo rule, or write access to the root folder), `apply`
stops on that host with a message saying what to fix. Drift checks report the same problems, and
also a missing .NET runtime for framework-dependent apps.

## Windows hosts

Each host needs:

- An administrator account for Deployify, which installing Windows services requires.
- For WinRM: remoting enabled (`Enable-PSRemoting`). For SSH: the OpenSSH server with PowerShell 7
  registered as a subsystem.

Where Deployify runs, you need PowerShell 7 (`pwsh`), or Windows PowerShell on Windows. From a Linux
runner, use the SSH transport.

A .NET app only runs as a Windows service when it tells the service manager it started, so every
project must use **Microsoft.Extensions.Hosting.WindowsServices**:

```bash
dotnet add package Microsoft.Extensions.Hosting.WindowsServices
```

```csharp
builder.Services.AddWindowsService(); // does nothing when the app isn't started as a service
```

`deployify publish` checks the published app for the package and stops with this advice when it's
missing, instead of deploying a service that Windows kills after 30 seconds (error 1053).

## Publish, plan, apply

```bash
deployify publish        # dotnet publish each project into deployify-output/{service}/package
deployify plan
deployify apply --auto-approve
```

`apply` deploys one service at a time, **host by host**. On each host it copies the package into a
new release folder (`{root}/{name}/releases/{tag}-{fingerprint}`) unless the host already has it,
switches the service to that release and restarts it. Before it moves on to the next host, it
waits until the service is healthy on the one it just deployed. A build that doesn't start stops
at the first host, and the others keep the release they ran. A service whose settings, secrets and
package all match what a host runs is left alone there.

On Linux, Deployify links a unit `deployify-{name}.service` into systemd that starts
`{root}/{name}/current/run` with `current/env`. A deploy repoints the `current` symlink, so the
executable and its environment always switch together, in one step. On Windows, the service's
executable path is switched to the release's `.exe`.

`plan` compares a fingerprint of each service (ports, environment variables, secret values and the
published package) with the one recorded on each host, and shows hosts that don't have the service
yet. It uses the package that was last published here, so run `deployify publish` before `plan`
when the plan should show code changes.

## Health checks and rollback

`apply` and `ci` wait for each service to become healthy on every host, as on the other targets
(see [health-gated rollouts](/Deployify/guides/rollouts/)). A service is healthy on a host when it:

- runs the new release, and its unit or Windows service is running;
- hasn't been restarted since the deploy, which would mean it crashed (both systemd and the Windows
  service manager restart a crashed service after 5 seconds);
- answers a request from the host to `healthPath` on its first http port with a status below 500,
  for a service with an http endpoint. A worker without one is healthy once it has kept running
  for `startupSeconds` (default 10).

Set `healthPath` to an endpoint that checks your dependencies, such as `/health`. Note that Aspire's
service defaults only map `/health` in Development unless you change that.
`hostHealthTimeoutSeconds` (default 120) is how long `apply` waits on one host before giving up on
the next ones.

If a service fails, the rollout switches every host back to the release it ran before and removes
services it created. `deployify rollback` does the same for an earlier rollout. The rollout journal
on your machine only holds release names: each release's files and settings stay on the hosts.
The release that ran before the current one is always kept, so a rollback can go back to it even
after an earlier rollback.

## Service-to-service calls and ports

Every service runs on every host, so the services call each other at `http://localhost:{port}`.
Deployify sets `ASPNETCORE_URLS` from each project's http bindings and `listenAddress`, and drops
the port settings Aspire adds (`HTTP_PORTS` and the like).

Aspire's https bindings are left out: there's no certificate to give Kestrel. Terminate TLS in a
reverse proxy or load balancer in front of the hosts. Deployify doesn't configure one, so it never
touches an nginx, Caddy or IIS setup you already have.

## Environment variables and secrets

- **Linux**: each release has an environment file, `{root}/{name}/releases/{release}/env`, with mode
  600, owned by the deploy user, which the unit reads with `EnvironmentFile=`. Secrets never appear
  in the unit file, on a command line, in `service.json` or in the rollout journal. Deployify sends
  them over SSH on standard input.
- **Windows**: variables are set in the service's registry key, which Deployify closes to everyone
  but Administrators and SYSTEM. For rollback, each release's values are also kept in
  `{root}\{name}\settings\{release}.json`, which only Administrators and SYSTEM can read.

## Logs

[`deployify logs <service>`](/Deployify/guides/logs/) reads every host that runs the service.
With several hosts, each line names its host.

- **Linux**: the unit's systemd journal, everything the app writes to stdout and stderr, with times
  and priorities (`journalctl -o json`). `--since` works, and `--follow` keeps one `journalctl -f`
  running per host.
- **Windows**: a Windows service has no console, so only what the app writes to the Application
  event log is shown, along with the service manager's entries about the service (crashes,
  failures to start, shown as `[service-manager]`). `AddWindowsService()` logs there under the app's
  name, but only **warnings and errors** by default. Deployify registers that event source when it
  installs the service, since the service's own account can't. To see more, lower the level for
  the `EventLog` provider in `appsettings.json`, or write to a file. Following asks the host again
  every few seconds.

## What the host target can't run

The host target runs .NET projects. Validation fails for **containers and executables** (Redis,
PostgreSQL and so on). Run them elsewhere and list where in `externalHosts`; references to them, such as
connection strings, then use that host. They are left out of the deployment and shown as "not
deployed" in `explain`. Load balancing between hosts is also up to you.

## Destroy and prune

Each service's folder holds a marker (`deployify.marker` on Linux, `deployify.json` on Windows)
naming its service and environment. `destroy` and `apply --prune` only remove services with that
marker: they stop and remove the unit or Windows service, and its folder with every release. A
folder or unit that Deployify didn't create is never touched, and a deploy that would overwrite one
stops instead. The root folder stays.

Only the `Rolling` strategy is available: traffic between hosts is up to your load balancer. See
[canary and blue-green rollouts](/Deployify/guides/rollout-strategies/).
