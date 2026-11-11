---
title: "Installation"
description: "Install the deployify .NET tool."
---

## Requirements

- .NET 10 SDK
- A running deployment target (Nomad agent, Kubernetes cluster, Docker daemon, AWS CLI, etc.)
- For the Helm target: `helm` 3 and `kubectl`; the helm-diff plugin for `plan` and `diff`
- For the Argo CD and Flux targets: `git` with access to the config repository, `kubectl`, and the controller installed in the cluster

## Install

```bash
dotnet pack src/Deployify/Deployify.csproj -c Release
dotnet tool install --global --add-source ./src/Deployify/nupkg deployify
```
