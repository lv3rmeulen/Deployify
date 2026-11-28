# Deployify

Deploy .NET Aspire AppHost projects to any platform — Nomad, Kubernetes (with `kubectl`, Helm, or GitOps through Argo CD or Flux), OVHcloud Managed Kubernetes, Docker Compose, AWS ECS, Azure Container Apps, Google Cloud Run, Nomad + Consul Connect, IIS on Windows Server, plain Linux and Windows machines (as systemd units or Windows services), and DigitalOcean App Platform (as a plugin).

---

## Requirements

- .NET 10 SDK
- A running deployment target (Nomad agent, Kubernetes cluster, Docker daemon, AWS CLI, etc.)
- For the Helm target: `helm` 3 and `kubectl`; the helm-diff plugin for `plan` and `diff`
- For the Argo CD and Flux targets: `git` with access to the config repository, `kubectl`, and the controller installed in the cluster
- For the OVHcloud target: `kubectl`, and OVHcloud API credentials (an API application or a service account)
- For the Cloud Run target: the Google Cloud CLI (`gcloud`), signed in with access to the project
- For the IIS target: PowerShell 7 (or Windows PowerShell on Windows), and a Windows Server with IIS and the ASP.NET Core Hosting Bundle
- For the host target: the OpenSSH client and Linux hosts with systemd, or PowerShell 7 (or Windows PowerShell on Windows) for Windows hosts
- For the DigitalOcean target: the `Deployify.Target.DigitalOcean` plugin and a DigitalOcean API token in `DIGITALOCEAN_ACCESS_TOKEN`

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

**Documentation: <https://lv3rmeulen.github.io/Deployify/>**

---

## Learn more

| | |
|---|---|
| [Your first deployment](https://lv3rmeulen.github.io/Deployify/getting-started/first-deployment/) | Deploy the `hello-aspire` sample to Docker Compose in five minutes |
| [Deployment targets](https://lv3rmeulen.github.io/Deployify/targets/compose/) | Compose, Kubernetes, Helm, Argo CD / Flux, OVHcloud, Nomad, Nomad + Consul, AWS ECS, Azure Container Apps, Google Cloud Run, IIS, plain hosts (systemd or Windows services), and DigitalOcean App Platform as a plugin |
| [Health-gated rollouts](https://lv3rmeulen.github.io/Deployify/guides/rollouts/) | Wave-by-wave deploys with automatic rollback |
| [Reading logs](https://lv3rmeulen.github.io/Deployify/guides/logs/) | `deployify logs <service>`: one command for a service's output on every target |
| [Drift alerts](https://lv3rmeulen.github.io/Deployify/guides/drift-alerts/) | Scheduled checks that alert (Slack, Teams, webhook, GitHub issue) when an environment no longer matches its last deploy |
| [Parameters and secrets](https://lv3rmeulen.github.io/Deployify/guides/parameters-and-secrets/) | Aspire parameters, secret backends (Vault, Key Vault, SSM, Secrets Manager) |
| [Running from `aspire deploy`](https://lv3rmeulen.github.io/Deployify/guides/aspire-deploy/) | `builder.AddDeployify()` in your AppHost |
| [CI/CD](https://lv3rmeulen.github.io/Deployify/guides/ci-cd/) | `deployify ci`, plan gates, GitHub Actions |
| [GitHub Action](https://lv3rmeulen.github.io/Deployify/guides/github-action/) | Plan as a pull request comment, deploy on merge, scheduled drift checks |
| [Signing in from CI](https://lv3rmeulen.github.io/Deployify/guides/ci-cloud-sign-in/) | OIDC sign-in to AWS and Azure, no stored keys |
| [State and locking](https://lv3rmeulen.github.io/Deployify/concepts/state/) | Inventory, ownership, the environment lock, remote state |
| [Writing target plugins](https://lv3rmeulen.github.io/Deployify/extending/plugins/) | Add any platform as an out-of-process plugin |
| [CLI reference](https://lv3rmeulen.github.io/Deployify/reference/cli/) | Every command and option |
| [Stability and versioning](https://lv3rmeulen.github.io/Deployify/reference/stability/) | What 1.x promises not to break |

## Samples

Runnable apps in [`samples/`](samples/), built on every pull request and deployed nightly:
[hello-aspire](samples/hello-aspire/), [aspire-deploy](samples/aspire-deploy/) and a
[plugin](samples/Deployify.Plugin.Sample.Compose/).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md), and [SECURITY.md](SECURITY.md) to report a vulnerability.
Changes are listed in [CHANGELOG.md](CHANGELOG.md).

The docs site lives in [`docs/`](docs/) (Astro Starlight); see
[Working on the docs and samples](https://lv3rmeulen.github.io/Deployify/contributing/docs/). The repository layout and how to
add a built-in target are described under [Contributing](https://lv3rmeulen.github.io/Deployify/contributing/project-structure/).
