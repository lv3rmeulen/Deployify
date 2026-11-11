using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Plugin.Contracts;
using Deployify.Plugin.Sdk;
using Deployify.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Nerdbank.Streams;

namespace Deployify.Plugin.Testing;

/// <summary>What to run the checks against.</summary>
public sealed class ConformanceOptions
{
    /// <summary>A manifest the target can handle. Secret parameters in it get a canary value the checks look for.</summary>
    public required AspireManifest Manifest { get; init; }

    /// <summary>The environment, with the target's settings in <c>TargetConfig</c>.</summary>
    public required EnvironmentConfig Config { get; init; }

    /// <summary>Where generate may write. Use a temporary directory.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>
    /// Also run the checks that read from the platform (ping, plan, diff, capture, observe,
    /// list managed). Needs a reachable test environment.
    /// </summary>
    public bool Live { get; init; }

    /// <summary>
    /// Also run checks that change the platform: deleting a service that doesn't exist must
    /// succeed. Only with <see cref="Live"/>, and only against a throwaway environment.
    /// </summary>
    public bool Destructive { get; init; }

    /// <summary>How long one <c>ObserveAsync</c> may take; it must take one look, not wait.</summary>
    public TimeSpan ObserveBudget { get; init; } = TimeSpan.FromSeconds(10);
}

public enum CheckOutcome
{
    Passed,
    Failed,
    Skipped
}

public sealed record ConformanceCheck(string Name, CheckOutcome Outcome, string Detail = "");

public sealed class ConformanceReport(IReadOnlyList<ConformanceCheck> checks)
{
    public IReadOnlyList<ConformanceCheck> Checks { get; } = checks;

    public bool Passed => Checks.All(c => c.Outcome != CheckOutcome.Failed);

    /// <summary>Throws with the whole report when a check failed. Call it at the end of a test.</summary>
    public void ThrowIfFailed()
    {
        if (!Passed)
        {
            throw new ConformanceException(ToString());
        }
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var check in Checks)
        {
            var mark = check.Outcome switch { CheckOutcome.Passed => "✓", CheckOutcome.Failed => "✗", _ => "-" };
            sb.Append(mark).Append(' ').Append(check.Name);
            if (check.Detail.Length > 0)
            {
                sb.Append(": ").Append(check.Detail);
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}

public sealed class ConformanceException(string report) : Exception($"The plugin failed conformance checks:{Environment.NewLine}{report}");

/// <summary>
/// Checks that a plugin keeps the contracts Deployify relies on: a valid handshake, consistent
/// explain and dependency answers, no secret values in snapshots or explanations, rejected
/// requests for targets it doesn't serve, an observe that doesn't wait, and a delete that can be
/// repeated. Run it from the plugin's tests:
/// <code>
/// var report = await PluginConformance.RunInProcessAsync(p => p.AddTarget&lt;ExampleTarget&gt;("example", "Example"), "example", options);
/// report.ThrowIfFailed();
/// </code>
/// </summary>
public static class PluginConformance
{
    public const string Canary = "deployify-conformance-canary-7f3e";

    /// <summary>Starts the plugin executable at <paramref name="executablePath"/> and checks target <paramref name="targetId"/>.</summary>
    public static async Task<ConformanceReport> RunAsync(
        string executablePath, string targetId, ConformanceOptions options, CancellationToken ct = default)
    {
        await using var host = new PluginHost(
            new PluginLaunchSpec { ExecutablePath = executablePath, Source = "conformance" }, NullLoggerFactory.Instance);
        return await RunAsync(host, targetId, options, ct);
    }

    /// <summary>Runs a plugin built with the SDK in this process (no executable needed) and checks it.</summary>
    public static async Task<ConformanceReport> RunInProcessAsync(
        Action<PluginBuilder> configure, string targetId, ConformanceOptions options, CancellationToken ct = default)
    {
        var servers = new List<(Stream Stream, Task Server)>();
        var host = new PluginHost(
            new PluginLaunchSpec { ExecutablePath = $"{PluginProtocol.ExecutablePrefix}{targetId}", Source = "in-process" },
            NullLoggerFactory.Instance, options: null,
            connect: async (h, token) =>
            {
                var (hostSide, pluginSide) = FullDuplexStream.CreatePair();
                servers.Add((pluginSide, Task.Run(() => DeployifyPlugin.RunAsync(configure, pluginSide, pluginSide, CancellationToken.None), CancellationToken.None)));
                return await PluginConnection.ConnectAsync(h.Spec, hostSide, hostSide, h, h.Logger, h.Options.HandshakeTimeout, token);
            });
        try
        {
            return await RunAsync(host, targetId, options, ct);
        }
        finally
        {
            await host.DisposeAsync();
            foreach (var (stream, server) in servers)
            {
                await stream.DisposeAsync();
                await server.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task<ConformanceReport> RunAsync(PluginHost host, string targetId, ConformanceOptions options, CancellationToken ct)
    {
        var checks = new List<ConformanceCheck>();
        var config = WithCanaries(options.Manifest, options.Config);

        // ── Handshake ────────────────────────────────────────────────────────
        PluginConnection connection;
        try
        {
            connection = await host.GetConnectionAsync(ct);
        }
        catch (PluginException ex)
        {
            checks.Add(new("handshake", CheckOutcome.Failed, ex.Message));
            return new ConformanceReport(checks);
        }

        checks.Add(CheckHandshake(connection.Info));
        if (!DeploymentTargetType.TryParse(targetId, out var id) ||
            connection.Info.Targets.All(t => !string.Equals(t.Id, targetId, StringComparison.OrdinalIgnoreCase)))
        {
            checks.Add(new("serves the target", CheckOutcome.Failed, $"the handshake doesn't list '{targetId}'"));
            return new ConformanceReport(checks);
        }

        var target = new RemoteDeploymentTarget(host, id);
        await target.StartAsync(ct);
        IDeploymentTarget asTarget = target;

        checks.Add(await CheckUnknownTargetAsync(connection, config, ct));

        // ── Offline ──────────────────────────────────────────────────────────
        IReadOnlyList<ServiceExplanation>? explained = null;
        checks.Add(await RunCheckAsync("explain answers", async () =>
        {
            explained = await target.ExplainAsync(options.Manifest, config, ct);
            var names = explained.Select(e => e.Name).ToList();
            if (names.Any(string.IsNullOrWhiteSpace))
            {
                return Fail("a service has no name");
            }

            var duplicates = names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            return duplicates.Count > 0 ? Fail($"services listed twice: {string.Join(", ", duplicates)}") : Pass($"{names.Count} service(s)");
        }));

        checks.Add(await RunCheckAsync("explain is repeatable", async () =>
        {
            var again = await target.ExplainAsync(options.Manifest, config, ct);
            return explained is not null && again.Select(e => e.Name).SequenceEqual(explained.Select(e => e.Name))
                ? Pass()
                : Fail("a second explain listed different services");
        }));

        checks.Add(await RunCheckAsync("explain hides secret values", async () =>
        {
            var all = await target.ExplainAsync(options.Manifest, config, ct);
            var leaks = all.Where(e => e.Env.Values.Concat(e.AdditionalInfo.Values).Any(v => v.Contains(Canary, StringComparison.Ordinal)))
                .Select(e => e.Name).ToList();
            return leaks.Count == 0 ? Pass() : Fail($"secret parameter values shown for {string.Join(", ", leaks)}; mask them");
        }));

        checks.Add(await RunCheckAsync("dependencies name known services", async () =>
        {
            var known = (explained ?? []).Select(e => e.Name).Concat(options.Manifest.Resources.Keys).ToHashSet(StringComparer.Ordinal);
            var edges = await target.GetDependenciesAsync(options.Manifest, config, ct);
            var unknown = edges.SelectMany(e => new[] { e.From, e.To }).Where(n => !known.Contains(n)).Distinct().ToList();
            return unknown.Count == 0 ? Pass($"{edges.Count} edge(s)") : Fail($"unknown services: {string.Join(", ", unknown)}");
        }));

        checks.Add(await RunCheckAsync("generate writes the files it returns", async () =>
        {
            var files = await target.GenerateAsync(options.Manifest, config, options.OutputDirectory, ct);
            var missing = files.Where(f => !File.Exists(Path.IsPathRooted(f) ? f : Path.Combine(options.OutputDirectory, f))).ToList();
            return missing.Count == 0 ? Pass($"{files.Count} file(s)") : Fail($"returned but not written: {string.Join(", ", missing)}");
        }));

        checks.Add(await RunCheckAsync("validate runs on generated files", async () =>
        {
            var results = await target.ValidateAsync(options.Manifest, config, options.OutputDirectory, ct);
            var invalid = results.Where(r => !r.IsValid).Select(r => $"{r.ArtifactName} ({string.Join("; ", r.Errors)})").ToList();
            return invalid.Count == 0 ? Pass() : Fail($"its own output doesn't validate: {string.Join(", ", invalid)}");
        }));

        // ── Live ─────────────────────────────────────────────────────────────
        var rollout = asTarget.GetCapability<IRolloutTarget>();
        var lifecycle = asTarget.GetCapability<IResourceLifecycle>();
        var service = (explained ?? []).Select(e => e.Name).FirstOrDefault();

        if (!options.Live)
        {
            checks.Add(new("live checks", CheckOutcome.Skipped, "set Live to run ping, plan, diff, rollout and lifecycle checks"));
            return new ConformanceReport(checks);
        }

        checks.Add(await RunCheckAsync("ping", async () =>
        {
            var (ok, error) = await target.PingAsync(config, ct);
            return ok ? Pass() : Fail(error ?? "not reachable, no reason given");
        }));

        checks.Add(await RunCheckAsync("plan and diff answer", async () =>
        {
            var plan = await target.PlanAsync(options.Manifest, config, options.OutputDirectory, ct);
            var diff = await target.DiffAsync(options.Manifest, config, options.OutputDirectory, ct);
            var unexplained = diff.Where(d => d.Kind == DiffKind.Unknown && string.IsNullOrWhiteSpace(d.Reason)).Select(d => d.ServiceName).ToList();
            return unexplained.Count == 0
                ? Pass($"{plan.CreateCount} to create, {plan.UpdateCount} to update")
                : Fail($"unknown diff entries need a reason: {string.Join(", ", unexplained)}");
        }));

        if (rollout is null || service is null)
        {
            checks.Add(new("rollout", CheckOutcome.Skipped, rollout is null ? "the target has no rollout capability" : "no service to check"));
        }
        else
        {
            ServiceSnapshot? snapshot = null;
            checks.Add(await RunCheckAsync("snapshots hold no secret values", async () =>
            {
                snapshot = await rollout.CaptureAsync(options.Manifest, config, service, ct);
                var text = string.Join("\n", new[] { snapshot.Revision ?? "" }.Concat(snapshot.Data.Keys).Concat(snapshot.Data.Values));
                return text.Contains(Canary, StringComparison.Ordinal)
                    ? Fail("the snapshot of a service holds a secret parameter value; store a reference instead")
                    : Pass();
            }));

            if (snapshot?.Revision is { } revision)
            {
                checks.Add(await RunCheckAsync("observe takes one look", async () =>
                {
                    var watch = Stopwatch.StartNew();
                    await rollout.ObserveAsync(config, service, revision, ct);
                    return watch.Elapsed <= options.ObserveBudget
                        ? Pass($"{watch.Elapsed.TotalMilliseconds:0} ms")
                        : Fail($"took {watch.Elapsed.TotalSeconds:0.0}s; observe must not poll or wait");
                }));
            }
            else
            {
                checks.Add(new("observe takes one look", CheckOutcome.Skipped, $"'{service}' has no running revision to observe"));
            }
        }

        if (lifecycle is null)
        {
            checks.Add(new("lifecycle", CheckOutcome.Skipped, "the target has no lifecycle capability"));
        }
        else
        {
            checks.Add(await RunCheckAsync("list managed answers", async () =>
                Pass($"{(await lifecycle.ListManagedAsync(config, ct)).Count} service(s)")));

            if (options.Destructive)
            {
                checks.Add(await RunCheckAsync("deleting a missing service succeeds", async () =>
                {
                    const string missing = "deployify-conformance-missing";
                    await lifecycle.DeleteServiceAsync(config, missing, new DeleteOptions(), ct);
                    await lifecycle.DeleteServiceAsync(config, missing, new DeleteOptions(), ct);
                    return Pass();
                }));
            }
            else
            {
                checks.Add(new("deleting a missing service succeeds", CheckOutcome.Skipped, "set Destructive to run it"));
            }
        }

        return new ConformanceReport(checks);
    }

    private static ConformanceCheck CheckHandshake(InitializeResult info)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(info.Plugin.Name))
        {
            problems.Add("the plugin has no name");
        }

        if (string.IsNullOrWhiteSpace(info.Plugin.Version))
        {
            problems.Add("the plugin reports no version, so it can't be locked by version");
        }

        foreach (var t in info.Targets)
        {
            if (!DeploymentTargetType.TryParse(t.Id, out var id))
            {
                problems.Add($"'{t.Id}' isn't a valid target ID");
            }
            else if (id.IsBuiltIn)
            {
                problems.Add($"'{t.Id}' is a built-in target ID; Deployify will never use the plugin for it");
            }

            if (string.IsNullOrWhiteSpace(t.DisplayName))
            {
                problems.Add($"'{t.Id}' has no display name");
            }

            var unknown = t.Capabilities.Except([PluginProtocol.Capabilities.Rollout, PluginProtocol.Capabilities.Lifecycle], StringComparer.OrdinalIgnoreCase).ToList();
            if (unknown.Count > 0)
            {
                problems.Add($"'{t.Id}' declares unknown capabilities: {string.Join(", ", unknown)}");
            }

            if (t.ConfigSchema is { ValueKind: not System.Text.Json.JsonValueKind.Object })
            {
                problems.Add($"'{t.Id}' has a config schema that isn't a JSON object");
            }
        }

        return problems.Count == 0
            ? new("handshake", CheckOutcome.Passed, $"{info.Plugin.Name} {info.Plugin.Version}, protocol {info.ProtocolVersion}")
            : new("handshake", CheckOutcome.Failed, string.Join("; ", problems));
    }

    private static async Task<ConformanceCheck> CheckUnknownTargetAsync(PluginConnection connection, EnvironmentConfig config, CancellationToken ct)
    {
        try
        {
            await connection.InvokeAsync<object?>(PluginProtocol.Methods.Explain, new TargetRequest
            {
                TargetId = "deployify-conformance-unknown",
                Environment = WireMapping.ToWire(config)
            }, ct);
            return new("rejects targets it doesn't serve", CheckOutcome.Failed, "a call for an unknown target ID succeeded");
        }
        catch (PluginException)
        {
            return new("rejects targets it doesn't serve", CheckOutcome.Passed);
        }
    }

    /// <summary>Gives every secret parameter in the manifest a recognisable value, so leaks can be spotted.</summary>
    private static EnvironmentConfig WithCanaries(AspireManifest manifest, EnvironmentConfig config)
    {
        var copy = WireMapping.EnvironmentFromWire(WireMapping.ToWire(config));
        foreach (var (name, value) in config.Parameters)
        {
            copy.Parameters[name] = value;
        }

        foreach (var (name, resource) in manifest.Resources.Where(r => r.Value.Type == "parameter.v0"))
        {
            foreach (var (inputName, input) in ParameterInput.ReadAll(resource))
            {
                if (input.Secret)
                {
                    copy.Parameters[inputName == "value" ? name : $"{name}.{inputName}"] = $"{Canary}-{name}";
                }
            }
        }

        return copy;
    }

    private static async Task<ConformanceCheck> RunCheckAsync(string name, Func<Task<(bool Ok, string Detail)>> check)
    {
        try
        {
            var (ok, detail) = await check();
            return new(name, ok ? CheckOutcome.Passed : CheckOutcome.Failed, detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(name, CheckOutcome.Failed, ex.Message);
        }
    }

    private static (bool, string) Pass(string detail = "") => (true, detail);

    private static (bool, string) Fail(string detail) => (false, detail);
}
