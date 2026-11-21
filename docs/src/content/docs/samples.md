---
title: Samples
description: Runnable Aspire apps that show Deployify end to end.
---

Every sample lives in [`samples/`](https://github.com/lv3rmeulen/Deployify/tree/main/samples), is
built on each pull request and deployed nightly, so what you read here runs.

| Sample | Shows | Target |
|---|---|---|
| [hello-aspire](https://github.com/lv3rmeulen/Deployify/tree/main/samples/hello-aspire) | A first deployment covering every kind of resource: a project, a Dockerfile build, containers (Redis, Postgres) and a secret parameter. Walkthrough: [Your first deployment](/Deployify/getting-started/first-deployment/) | Docker Compose |
| [aspire-deploy](https://github.com/lv3rmeulen/Deployify/tree/main/samples/aspire-deploy) | Running Deployify from `aspire deploy` with `Deployify.Hosting`. Guide: [Running from aspire deploy](/Deployify/guides/aspire-deploy/) | Docker Compose |
| [Deployify.Plugin.Sample.Compose](https://github.com/lv3rmeulen/Deployify/tree/main/samples/Deployify.Plugin.Sample.Compose) | A deployment target written as an out-of-process plugin. Guide: [Writing target plugins](/Deployify/extending/plugins/) | — |
