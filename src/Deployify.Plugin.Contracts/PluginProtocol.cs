using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deployify.Plugin.Contracts;

/// <summary>
/// The Deployify target plugin protocol: JSON-RPC 2.0 over the plugin's stdin and stdout, each
/// message framed with a <c>Content-Length</c> header as in the Language Server Protocol.
/// stdout carries protocol messages only; anything a plugin writes to stderr is shown in
/// Deployify's log. Every request takes a single object parameter.
/// </summary>
/// <remarks>
/// A session runs: Deployify starts the plugin, sends <see cref="Methods.Initialize"/>, calls
/// target methods (possibly several at once), sends <see cref="Methods.Shutdown"/> and closes
/// stdin. A plugin must exit when stdin closes.
/// </remarks>
public static class PluginProtocol
{
    /// <summary>
    /// The protocol version this contract describes. Deployify talks to a plugin when the major
    /// versions match; a higher minor version only adds optional methods and fields.
    /// </summary>
    public static readonly Version Version = new(1, 0);

    /// <summary>The executable name prefix Deployify looks for: <c>deployify-target-&lt;id&gt;</c>.</summary>
    public const string ExecutablePrefix = "deployify-target-";

    /// <summary>JSON settings for every message: camelCase names, enums as camelCase strings, nulls left out.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>Method names. Deployify calls the ones under the target, rollout and lifecycle groups.</summary>
    public static class Methods
    {
        // Session
        public const string Initialize = "initialize";
        public const string Shutdown = "shutdown";

        // IDeploymentTarget
        public const string Ping = "target/ping";
        public const string Generate = "target/generate";
        public const string Validate = "target/validate";
        public const string Plan = "target/plan";
        public const string Apply = "target/apply";
        public const string Publish = "target/publish";
        public const string Explain = "target/explain";
        public const string Dependencies = "target/dependencies";
        public const string Diff = "target/diff";

        // IRolloutTarget (capability "rollout")
        public const string RolloutCapture = "rollout/capture";
        public const string RolloutApplyService = "rollout/applyService";
        public const string RolloutObserve = "rollout/observe";
        public const string RolloutRestore = "rollout/restore";
        public const string RolloutNativeRollback = "rollout/nativeRollback";
        public const string RolloutUnavailableReason = "rollout/unavailableReason";
        public const string RolloutComplete = "rollout/complete";

        // IResourceLifecycle (capability "lifecycle")
        public const string LifecycleListManaged = "lifecycle/listManaged";
        public const string LifecycleDeleteService = "lifecycle/deleteService";
        public const string LifecycleDestroyEnvironment = "lifecycle/destroyEnvironment";
        public const string LifecycleDescribeKept = "lifecycle/describeKept";

        // Calls from the plugin to Deployify
        /// <summary>Request: <see cref="ParameterValueRequest"/>, result: <see cref="ParameterValueResult"/>.</summary>
        public const string ResolveParameter = "host/resolveParameter";

        /// <summary>Notification: <see cref="LogMessage"/>.</summary>
        public const string Log = "host/log";
    }

    /// <summary>Optional capabilities a target declares in <see cref="PluginTargetInfo.Capabilities"/>.</summary>
    public static class Capabilities
    {
        /// <summary>Implements the <c>rollout/*</c> methods: health-gated rollouts with automatic rollback.</summary>
        public const string Rollout = "rollout";

        /// <summary>Implements the <c>lifecycle/*</c> methods: destroy, prune and adopt.</summary>
        public const string Lifecycle = "lifecycle";
    }

    /// <summary>
    /// JSON-RPC error codes a plugin returns in addition to the standard ones. They sit outside
    /// -32768..-32000, which JSON-RPC reserves (and which JSON-RPC libraries give meanings of their own).
    /// </summary>
    public static class ErrorCodes
    {
        /// <summary>The request named a target this plugin doesn't serve.</summary>
        public const int UnknownTarget = 4001;

        /// <summary>The target doesn't support the method (a capability it didn't declare), or the protocol version.</summary>
        public const int NotSupported = 4002;

        /// <summary>The target's own code failed; the message is shown to the user.</summary>
        public const int TargetFailed = 5000;
    }
}
