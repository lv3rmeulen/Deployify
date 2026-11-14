---
title: CLI reference
description: Every deployify command and option.
sidebar:
  order: 0
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

| Command | Description |
|---|---|
| [`init`](./init/) | Initialize deployify in an Aspire AppHost project. |
| [`env`](./env/) | Manage deployment environments. |
| [`generate`](./generate/) | Generate deployment artifacts (HCL, manifests, compose files) without contacting the cluster. |
| [`validate`](./validate/) | Validate generated artifacts against the target. |
| [`plan`](./plan/) | Preview changes that would be applied to the target. |
| [`apply`](./apply/) | Deploy to the target. |
| [`publish`](./publish/) | Build and push service images or artifacts. |
| [`package`](./package/) | Zip build output and upload to the artifact store (raw_exec Nomad deployments only). |
| [`ci`](./ci/) | Run the full generate → validate → plan → publish → apply pipeline for CI/CD. |
| [`watch`](./watch/) | Watch for source changes and automatically re-run generate + plan. |
| [`explain`](./explain/) | Display a human-readable summary of all services. |
| [`graph`](./graph/) | Display a dependency graph of all services. |
| [`diff`](./diff/) | Show differences between local manifest and the running target. |
| [`status`](./status/) | Show recent rollouts and how they ended. |
| [`rollback`](./rollback/) | Undo a rollout: restore the revisions that ran before it. |
| [`destroy`](./destroy/) | Remove everything Deployify deployed in an environment. |
| [`state`](./state/) | Inspect and manage deployment state (inventory, lock, remote backend). |
| [`plugin`](./plugin/) | Show and lock the target plugins this project can use. |
