---
title: "IIS (Windows Server)"
description: "Deploying an Aspire app's projects to IIS on Windows Server with Deployify."
---

Target ID: `Iis`

Deploys each Aspire **project** as an IIS site with its own application pool, on a Windows Server
you reach through PowerShell remoting (WinRM or SSH), or on the machine Deployify runs on. Projects
are published to a folder for Windows (`dotnet publish -r win-x64`) instead of being built as
images, and the ASP.NET Core Module hosts them. No container runtime is involved.

```json
"target": "Iis",
"targetConfig": {
  "iis": {
    "host": "web01.corp.example.com",
    "transport": "WinRm",
    "userName": "CORP\\deployer",
    "sitesRoot": "C:\\inetpub\\deployify",
    "hostNames": { "web": "shop.example.com" },
    "certificateThumbprint": "3A1F...",
    "externalHosts": { "cache": "redis.corp.example.com" }
  }
}
```

- `host`: the server. Leave it out to deploy to this machine, for example from a build agent that
  runs on the web server itself (Windows only).
- `transport`: `WinRm` (default) or `Ssh`. `port` overrides the default port (5985, 5986 with
  `useSsl`, or 22).
- `userName` / `password`: WinRM credentials. Without `userName`, the current Windows identity is
  used (Kerberos, on a domain). The password falls back to `IIS_PASSWORD`; use an `"env:NAME"`
  reference rather than storing it. SSH uses key authentication: `keyFilePath`, or the SSH client's
  own key lookup.
- `sitesRoot`: the folder on the server for the sites and their releases. Default
  `C:\inetpub\deployify`.
- `siteNamePrefix`: prefix for site and pool names. Default: the environment name and a dash
  (`prod-api`), so several environments can share a server.
- `runtime` / `selfContained`: the `dotnet publish` runtime identifier (default `win-x64`) and
  whether to bundle the .NET runtime (default no: the Hosting Bundle supplies it).
- `appPoolIdentity`: `ApplicationPoolIdentity` (default), `NetworkService`, `LocalService` or
  `LocalSystem`.
- `alwaysRunning`: start pools with IIS and never stop them for being idle (default on), so hosted
  services and consumers keep running. Turn it off to let IIS start an app on its first request.
- `keepReleases`: release folders kept per site, the current one included (default 3, at least 2).
  Rollback can only go back to a release that is still kept.
- `healthPath`: what the health check requests on each site (default `/`).
- `basePort`: first port given to bindings without one in the manifest (default 8080).
- `powerShell`: the local PowerShell to run. Default `pwsh`, then Windows PowerShell on Windows.

Init flags: `--iis-host`, `--iis-transport`, `--iis-user` and `--iis-sites-root`.

## The server

The server needs:

- IIS with its management API: the `Web-Server` and `Web-Scripting-Tools` features.
- The [ASP.NET Core Hosting Bundle](https://learn.microsoft.com/aspnet/core/host-and-deploy/iis/hosting-bundle)
  for the .NET version your projects target.
- An administrator account for Deployify, which changing IIS requires.
- For WinRM: remoting enabled (`Enable-PSRemoting`). For SSH: the OpenSSH server with PowerShell 7
  registered as a subsystem, and the server's host key in your `known_hosts` (the connection is
  non-interactive).

When the management API or the Hosting Bundle is missing, `plan` or `apply` stops with a message
saying which to install.

Where Deployify runs, you need PowerShell 7 (`pwsh`), or Windows PowerShell on Windows. From a Linux
runner, use the SSH transport: WinRM from Linux needs extra modules.

## Publish, plan, apply

```bash
deployify publish        # dotnet publish each project into deployify-output/{service}/package
deployify plan
deployify apply --auto-approve
```

`apply` copies each package into a new release folder on the server
(`{sitesRoot}\{site}\releases\{tag}-{fingerprint}`), points the site at it, and recycles its pool.
The previous releases stay on disk. A service whose settings, secrets and package all match what the
server runs is left alone.

`plan` compares a fingerprint of each site (bindings, environment variables, secret values and the
published package) with the one recorded on the server. It uses the package that was last published
here, so run `deployify publish` before `plan` when the plan should show code changes.

## Health checks and rollback

`apply` and `ci` deploy one wave of sites at a time and wait for each to become healthy, as on the
other targets (see [health-gated rollouts](/Deployify/guides/rollouts/)). A site is healthy when
its pool and site are started and a request from the server to `healthPath` gets a response below
500. Set `healthPath` to an endpoint that checks your dependencies, such as `/health`. Note that
Aspire's service defaults only map `/health` in Development unless you change that.

If a site fails, the rollout switches every site it touched back to the release it ran before, with
the settings that release ran with, and removes sites it created. `deployify rollback` does the same
for an earlier rollout:

```bash
deployify rollback --auto-approve
```

The previous release's files and settings stay on the server: each apply keeps them in
`{sitesRoot}\{site}\settings\{release}.json`, readable by Administrators and SYSTEM only, so the
rollout journal on your machine only holds release names. Rollback can only go back as far as
`keepReleases` (default 3, at least 2).

## Bindings and service-to-service calls

Every service gets an http binding on its port with no host name, and the other services call it at
`http://localhost:{port}`, since they run on the same server. Port settings Aspire adds to projects
(`HTTP_PORTS`, `ASPNETCORE_URLS` and the like) are dropped: IIS decides where the app listens.

- `hostNames`: a host name per service adds a binding on port 80 for that name, so the service can
  be reached from outside on its own domain.
- `certificateThumbprint`: a certificate in the server's `LocalMachine\My` store (or
  `certificateStore`). With it, Aspire's https bindings are added, and every host name also gets a
  binding on 443 using SNI. Without it, services use http only.

## Environment variables and secrets

Environment variables are set on each site through the ASP.NET Core Module, in
`applicationHost.config`, which only administrators can read. They are not written to the app's
folder or to `web.config`, and secret values never appear in `site.json` or the rollout journal.
Deployify sends the values to the server through the remoting session. For rollback, the server
also keeps each release's values in its `settings` folder, which is locked to Administrators and
SYSTEM like `applicationHost.config`.

Don't also set the same variable in your project's `web.config`: IIS rejects the duplicate entry.

## What IIS can't host

IIS hosts web apps. Validation fails for:

- **Projects without an http endpoint**, such as worker services. Deploy them with another target or
  give them an endpoint.
- **Containers and executables** (Redis, PostgreSQL and so on). Run them elsewhere and list where
  in `externalHosts`; references to them, such as connection strings, then use that host. They are
  left out of the deployment and shown as "not deployed" in `explain`.

## Destroy and prune

Each site's folder holds a `deployify.json` marker naming its service and environment. `destroy`
and `apply --prune` only remove sites with that marker: the site, its application pool, its
settings in `applicationHost.config`, and its folder with every release and its saved settings. Sites you created by hand,
IIS itself and the Hosting Bundle are never touched.
