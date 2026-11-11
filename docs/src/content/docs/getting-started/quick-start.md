---
title: "Quick start"
description: "Initialize an environment and deploy your AppHost."
---

:::tip
Want to try it without your own app? The [hello-aspire sample](/Deployify/getting-started/first-deployment/) deploys to Docker Compose on your machine.
:::

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

## Initializing for your target

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
