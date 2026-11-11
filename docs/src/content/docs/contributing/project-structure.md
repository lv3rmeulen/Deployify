---
title: "Project structure"
description: "How the repository is laid out."
---

```
Deployify.slnx
Directory.Packages.props                  package versions (Central Package Management)
src/
  Deployify/                              CLI entry point
    Commands/
      InitCommand.cs
      EnvCommand.cs
      GenerateCommand.cs
      ValidateCommand.cs
      PlanCommand.cs
      ApplyCommand.cs
      PublishCommand.cs
      DiffCommand.cs
      ExplainCommand.cs
      GraphCommand.cs
      CiCommand.cs
      WatchCommand.cs
      DestroyCommand.cs
      StateCommand.cs                     state show | unlock | adopt | push
      StateCli.cs                         prune, removal plans, lock output
      GlobalOptions.cs
      DependencyInjection.cs
    Program.cs
  Deployify.Core/                         Domain models and interfaces
    Interfaces/
      IDeploymentTarget.cs                implement this to add a target
      IResourceLifecycle.cs               optional: list / delete what Deployify deployed
      IManifestReader.cs
      IPublisher.cs
      IJobGenerator.cs
    Models/
      DeploymentTargetType.cs             Nomad | Kubernetes | Compose | AwsEcs | AzureContainerApps | NomadConsul | Helm
      DeployifyState.cs                   EnvironmentConfig, ContainerRuntime
      AspireManifest.cs
      PlanResult.cs
    State/                                IStateBackend (local, AzureBlob, S3, Consul, Kubernetes),
                                          StateLease (the lock), InventoryStore, StateSession
    Lifecycle/                            RemovalPlanner (what may be deleted), ServiceRemover
    Services/
      ManifestReader.cs
      Publisher.cs                        Podman socket auto-detection
      StateManager.cs
  Deployify.Target.Nomad/
  Deployify.Target.Kubernetes/            also writes Helm charts (HelmChartWriter)
  Deployify.Target.Helm/                  Kubernetes via helm upgrade --install
  Deployify.Target.Compose/
  Deployify.Target.AwsEcs/
  Deployify.Target.AzureContainerApps/
  Deployify.Target.NomadConsul/
test/
  Deployify.Core.Tests/
  Deployify.Target.Nomad.Tests/
  Deployify.Target.Kubernetes.Tests/
  Deployify.Target.Helm.Tests/
  Deployify.Target.Compose.Tests/
  Deployify.Target.AwsEcs.Tests/
  Deployify.Target.AzureContainerApps.Tests/
  Deployify.Target.NomadConsul.Tests/
  Deployify.Integration.Tests/
```
