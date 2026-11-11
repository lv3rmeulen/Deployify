using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Schema;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Plugin.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Deployify.Plugin.Sdk;

/// <summary>Describes a plugin: its name and the targets it serves. Passed to <see cref="DeployifyPlugin.RunAsync(Action{PluginBuilder}, System.Threading.CancellationToken)"/>.</summary>
public sealed class PluginBuilder
{
    private readonly List<TargetRegistration> _targets = [];

    internal PluginBuilder(string defaultName, string? defaultVersion)
    {
        Name = defaultName;
        Version = defaultVersion;
    }

    internal string Name { get; private set; }

    internal string? Version { get; private set; }

    internal IReadOnlyList<TargetRegistration> Targets => _targets;

    /// <summary>
    /// Services the targets are built from. Logging (forwarded to Deployify's log), an
    /// <see cref="Core.Interfaces.IPublisher"/> for building and pushing images, and an
    /// <see cref="Core.Processes.IProcessRunner"/> are registered already.
    /// </summary>
    public IServiceCollection Services { get; } = new ServiceCollection();

    /// <summary>Sets the plugin name and version Deployify shows. Defaults to the entry assembly's.</summary>
    public PluginBuilder Named(string name, string? version = null)
    {
        Name = name;
        Version = version ?? Version;
        return this;
    }

    /// <summary>
    /// Serves <typeparamref name="TTarget"/> as target <paramref name="id"/>. Its capabilities come
    /// from the interfaces it implements: <see cref="IRolloutTarget"/> adds health-gated rollouts,
    /// <see cref="IResourceLifecycle"/> adds destroy and prune.
    /// </summary>
    public PluginBuilder AddTarget<TTarget>(string id, string displayName, Action<TargetOptions>? configure = null)
        where TTarget : class, IDeploymentTarget
    {
        if (!DeploymentTargetType.TryParse(id, out var targetId))
        {
            throw new ArgumentException($"'{id}' is not a valid target ID: use letters, digits, '-', '_' or '.', starting with a letter.", nameof(id));
        }

        if (targetId.IsBuiltIn)
        {
            throw new ArgumentException($"'{id}' is a built-in target; a plugin target needs an ID of its own.", nameof(id));
        }

        if (_targets.Any(t => t.Id == targetId))
        {
            throw new ArgumentException($"Target '{id}' is added twice.", nameof(id));
        }

        var options = new TargetOptions();
        configure?.Invoke(options);
        Services.AddSingleton<TTarget>();
        _targets.Add(new TargetRegistration(targetId, displayName, typeof(TTarget), options));
        return this;
    }

    /// <summary>Changes how services are registered, e.g. to replace the publisher in tests.</summary>
    public PluginBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        configure(Services);
        return this;
    }

    internal sealed record TargetRegistration(DeploymentTargetType Id, string DisplayName, Type Type, TargetOptions Options)
    {
        public IReadOnlyList<string> Capabilities
        {
            get
            {
                var list = new List<string>();
                if (typeof(IRolloutTarget).IsAssignableFrom(Type))
                {
                    list.Add(PluginProtocol.Capabilities.Rollout);
                }

                if (typeof(IResourceLifecycle).IsAssignableFrom(Type))
                {
                    list.Add(PluginProtocol.Capabilities.Lifecycle);
                }

                return list;
            }
        }

        public PluginTargetInfo ToInfo() => new()
        {
            Id = Id.Value,
            DisplayName = DisplayName,
            Capabilities = Capabilities,
            ConfigKey = Options.ConfigKey,
            ConfigSchema = Options.ConfigSchema
        };
    }
}

/// <summary>Per-target settings.</summary>
public sealed class TargetOptions
{
    /// <summary>The <c>TargetConfig</c> key the target reads its settings from. Defaults to the target ID.</summary>
    public string? ConfigKey { get; set; }

    /// <summary>JSON Schema for the target's settings, which Deployify checks before calling the plugin.</summary>
    public JsonElement? ConfigSchema { get; set; }

    /// <summary>Derives <see cref="ConfigSchema"/> from the class the target reads its settings into.</summary>
    public TargetOptions WithConfigSchema<TConfig>()
    {
        var options = new JsonSerializerOptions(TargetConfigJsonOptions.Default)
        {
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
        };
        var node = JsonSchemaExporter.GetJsonSchemaAsNode(
            options, typeof(TConfig),
            new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
        ConfigSchema = JsonSerializer.SerializeToElement(node);
        return this;
    }
}
