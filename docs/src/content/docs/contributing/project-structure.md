---
title: "Project structure"
description: "How the repository is laid out."
---

```
Deployify.slnx
Directory.Build.props                     settings every project shares
Directory.Packages.props                  package versions (Central Package Management)
src/
  Deployify/                              the CLI (dotnet tool "deployify")
    Program.cs
    ConsoleSetup.cs                       console width when there's no terminal
    Commands/
      DeployifyCli.cs                     the command tree; also used by tools/Deployify.DocGen
      InitCommand.cs                      init: settings every target shares
      Init/                               one init flow (flags + prompts) per target, listed in TargetInitFlows
      EnvCommand.cs                       env list | use | add | set | remove
      GenerateCommand.cs
      ValidateCommand.cs
      PlanCommand.cs
      PlanOutput.cs                       the plan table, shared by plan, apply, ci and watch
      ApplyCommand.cs
      PublishCommand.cs
      PackageCommand.cs                   zip and upload raw_exec builds (Nomad)
      PromoteCommand.cs                   reuse another environment's images by digest
      CiCommand.cs                        generate → validate → plan → publish → apply
      WatchCommand.cs
      ExplainCommand.cs
      GraphCommand.cs
      DiffCommand.cs
      StatusCommand.cs                    recent rollouts and how they ended
      LogsCommand.cs                      deployify logs: reads through IServiceLogSource
      DashboardCommand.cs                 the standalone resource service for the Aspire dashboard
      RollbackCommand.cs
      RolloutCli.cs                       health-gate options and rollout output, shared by apply, ci, rollback and destroy
      DestroyCommand.cs
      StateCommand.cs                     state show | unlock | adopt | push | migrate-secrets
      StateCli.cs                         prune, removal plans, lock output
      PluginCommand.cs                    plugin list | lock
      PluginCommand.Index.cs              plugin search | install, from the plugin index
      GlobalOptions.cs                    options most commands take (--project-path, --env, --target, …)
      TargetOption.cs                     parses --target: a built-in target or a plugin's ID
      ConsoleRouting.cs                   keeps stdout for a command's own output (logs)
      ConsoleDeploymentReporter.cs        messages in the CLI's style
      ConsolePrompts.cs, ConsoleStatus.cs
      SecretPrompts.cs, SecretsWarning.cs, PlaintextSecretFlags.cs
      SignatureCheck.cs                   --require-signature
      Json/                               --output json: the envelope and the documents
      DependencyInjection.cs              services, logging, resolving an environment
  Deployify.Core/                         domain models, interfaces and the engine every target shares
    Interfaces/
      IDeploymentTarget.cs                implement this to add a target
      DeploymentTargetCapabilities.cs     GetCapability<T>(): optional capabilities, also for plugin targets
      IRolloutTarget.cs                   optional: health-gated rollouts and rollback
      IResourceLifecycle.cs               optional: list / delete what Deployify deployed
      IDeploymentTargetFactory.cs
      IManifestReader.cs
      IPublisher.cs
      IStateManager.cs
    Models/
      DeploymentTargetType.cs             the built-in target IDs, and plugin target IDs
      DeployifyState.cs                   EnvironmentConfig, ContainerRuntime
      AspireManifest.cs
      PlanResult.cs
      PublishedImages.cs, SigningConfig.cs, SbomConfig.cs, Secrets.cs
    Deployments/                          DeploymentSession(Resolver) (one environment, its target and state),
                                          DeploymentRunner, ManifestAudit, ImagePromotion
    Expressions/                          resolving Aspire's {resource.property} expressions and parameters
    Rollouts/                             RolloutEngine, HealthGate, RolloutJournal (what status shows)
    Lifecycle/                            RemovalPlanner (what may be deleted), ServiceRemover, InventoryRecorder
    ResourceSources/                      IResourceSource: what the dashboard shows (resources, logs, commands)
    ServiceLogs/                          IServiceLogSource (optional: read a service's output),
                                          LogLines (merging, tails, timestamps); not "Logs/",
                                          which .gitignore ignores
    SecretBackends/                       SecretStore, SecretRegistry, leak scanning; Vault/, KeyVault/
    State/                                IStateBackend (local, AzureBlob, S3, Consul, Kubernetes),
                                          StateLease (the lock), InventoryStore, StateSession, StateFormat
    Processes/                            IProcessRunner: every external tool is started through it
    Services/
      ManifestReader.cs
      Publisher.cs                        image builds; Podman socket auto-detection
      StateManager.cs
      ParameterStore.cs
      ImageSigner.cs, SbomAttester.cs, ImageCopier.cs, AttestationCopier.cs
      DeploymentTargetFactory.cs
  Deployify.Targets/                      registers Deployify.Core and every built-in target
  Deployify.Target.Nomad/
  Deployify.Target.NomadConsul/           Nomad with Consul service mesh
  Deployify.Target.Kubernetes/            also writes Helm charts (HelmChartWriter)
    Manifests/                            typed Kubernetes objects, written with YamlDotNet
  Deployify.Target.Helm/                  Kubernetes via helm upgrade --install
  Deployify.Target.GitOps/                Kubernetes via a config repository: Argo CD and Flux
  Deployify.Target.OvhCloud/              Kubernetes on OVHcloud, cluster reached via the OVHcloud API
  Deployify.Target.Compose/
  Deployify.Target.AwsEcs/
  Deployify.Target.AzureContainerApps/
  Deployify.Target.Iis/                   IIS sites on Windows Server, driven through PowerShell remoting
    Scripts/deployify-iis.ps1             runs on the server; embedded in the assembly
  Deployify.Target.DigitalOcean/          DigitalOcean App Platform, shipped as a plugin (deployify-target-digitalocean)
  Deployify.ResourceServer/               the Aspire dashboard's resource service (gRPC), fed by an IResourceSource
  Deployify.Hosting/                      AddDeployify(): running Deployify from `aspire deploy`
  Deployify.Plugin.Contracts/             the plugin protocol: method names and message shapes
  Deployify.Plugins/                      the host side: finding, verifying, locking and starting plugins
  Deployify.Plugin.Sdk/                   writing a plugin in .NET
  Deployify.Plugin.Testing/               conformance checks for plugins
  Shared/Yaml/                            YAML writer compiled into the Kubernetes and Compose targets
test/
  Deployify.Core.Tests/
  Deployify.Hosting.Tests/
  Deployify.Plugins.Tests/                the protocol end to end, through the real SDK server and host
  Deployify.ResourceServer.Tests/
  Deployify.Target.<Name>.Tests/          one per target
  Deployify.Integration.Tests/            the commands, against real state files
  Shared/                                 FakeProcessRunner, Snapshot (DEPLOYIFY_UPDATE_SNAPSHOTS=1), …
tools/
  Deployify.DocGen/                       writes docs/src/content/docs/reference/cli from the commands
  Deployify.PluginIndex/                  builds and signs the plugin index
samples/
  hello-aspire/                           the sample in "Your first deployment"
  aspire-deploy/                          Deployify inside `aspire deploy`
  Deployify.Plugin.Sample.Compose/        a minimal plugin
plugin-index/                             the entries the published plugin index is built from
docs/                                     this site (Astro Starlight)
```
