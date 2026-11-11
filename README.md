# Deployify

Deploy .NET Aspire AppHost projects to any platform — Nomad, Kubernetes (with `kubectl`, Helm, or GitOps through Argo CD or Flux), OVHcloud Managed Kubernetes, Docker Compose, AWS ECS, Azure Container Apps, and Nomad + Consul Connect.

---

## Requirements

- .NET 10 SDK
- A running deployment target (Nomad agent, Kubernetes cluster, Docker daemon, AWS CLI, etc.)
- For the Helm target: `helm` 3 and `kubectl`; the helm-diff plugin for `plan` and `diff`
- For the Argo CD and Flux targets: `git` with access to the config repository, `kubectl`, and the controller installed in the cluster
- For the OVHcloud target: `kubectl`, and OVHcloud API credentials (an API application or a service account)

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
| [Deployment targets](https://lv3rmeulen.github.io/Deployify/targets/compose/) | Compose, Kubernetes, Helm, Argo CD / Flux, OVHcloud, Nomad, Nomad + Consul, AWS ECS, Azure Container Apps |
| [Health-gated rollouts](https://lv3rmeulen.github.io/Deployify/guides/rollouts/) | Wave-by-wave deploys with automatic rollback |
| [Parameters and secrets](https://lv3rmeulen.github.io/Deployify/guides/parameters-and-secrets/) | Aspire parameters, secret backends (Vault, Key Vault, SSM) |
| [Running from `aspire deploy`](https://lv3rmeulen.github.io/Deployify/guides/aspire-deploy/) | `builder.AddDeployify()` in your AppHost |
| [CI/CD](https://lv3rmeulen.github.io/Deployify/guides/ci-cd/) | `deployify ci`, plan gates, GitHub Actions |
| [State and locking](https://lv3rmeulen.github.io/Deployify/concepts/state/) | Inventory, ownership, the environment lock, remote state |
| [Writing target plugins](https://lv3rmeulen.github.io/Deployify/extending/plugins/) | Add any platform as an out-of-process plugin |
| [CLI reference](https://lv3rmeulen.github.io/Deployify/reference/cli/) | Every command and option |

## Samples

Runnable apps in [`samples/`](samples/), built on every pull request and deployed nightly:
[hello-aspire](samples/hello-aspire/), [aspire-deploy](samples/aspire-deploy/) and a
[plugin](samples/Deployify.Plugin.Sample.Compose/).

## Contributing

The docs site lives in [`docs/`](docs/) (Astro Starlight); see
[Working on the docs and samples](https://lv3rmeulen.github.io/Deployify/contributing/docs/). The repository layout and how to
add a built-in target are described under [Contributing](https://lv3rmeulen.github.io/Deployify/contributing/project-structure/).
