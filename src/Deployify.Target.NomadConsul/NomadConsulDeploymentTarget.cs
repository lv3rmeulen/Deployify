using System.Text.Json;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Target.Nomad;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.NomadConsul;

/// <summary>
/// Deployment target for Nomad jobs that use Consul Connect service mesh.
///
/// Each Aspire service is rendered as a Nomad job with:
///   - <c>network { mode = "bridge" }</c>
///   - A <c>service { connect { sidecar_service { upstreams { ... } } } }</c> block
///     that registers the service in the Consul catalog and wires upstream
///     dependencies through the Envoy sidecar proxy
///   - Upstream loopback ports injected as env vars so applications can
///     discover peers at <c>localhost:&lt;port&gt;</c>
///
/// Plan pings both Nomad and Consul to check connectivity.
/// Apply submits jobs to Nomad; Consul registration happens automatically
/// through Nomad's built-in Consul integration.
/// </summary>
public sealed class NomadConsulDeploymentTarget(
	IPublisher publisher,
	ILogger<NomadConsulDeploymentTarget> logger,
	ILoggerFactory loggerFactory) : IDeploymentTarget
{
	private readonly Lazy<ConsulJobGenerator> _generator = new Lazy<ConsulJobGenerator>(() => new ConsulJobGenerator(loggerFactory.CreateLogger<ConsulJobGenerator>()));

	private ConsulJobGenerator Generator => _generator.Value;

	public DeploymentTargetType TargetType => DeploymentTargetType.NomadConsul;
	public string DisplayName => "HashiCorp Nomad + Consul Connect";

	// ── Ping ─────────────────────────────────────────────────────────────────

	public async Task<(bool Ok, string? Error)> PingAsync(
		EnvironmentConfig config, CancellationToken ct = default)
	{
		var cfg = GetConfig(config);

		// Check Nomad
		using var nomadClient = BuildNomadClient(cfg);
		if (!await nomadClient.PingAsync(ct))
		{
			return (false, $"Cannot reach Nomad at {cfg.NomadAddress}.");
		}

		// Check Consul
		using var consulClient = BuildConsulClient(cfg);
		if (!await consulClient.PingAsync(ct))
		{
			return (false, $"Cannot reach Consul at {cfg.ConsulAddress}.");
		}

		return (true, null);
	}

	// ── Generate ──────────────────────────────────────────────────────────────

	public async Task<IReadOnlyList<string>> GenerateAsync(
		AspireManifest manifest,
		EnvironmentConfig config,
		string outputDirectory,
		CancellationToken ct = default)
	{
		var cfg = GetConfig(config);
		var jobs = Generator.Generate(manifest, config, cfg);
		var paths = new List<string>();
		foreach (var job in jobs)
		{
			paths.Add(await Generator.WriteHclAsync(job, config, cfg, outputDirectory, ct));
		}

		return paths;
	}

	// ── Validate ─────────────────────────────────────────────────────────────

	public async Task<IReadOnlyList<ValidationResult>> ValidateAsync(
		AspireManifest manifest,
		EnvironmentConfig config,
		string outputDirectory,
		CancellationToken ct = default)
	{
		var cfg = GetConfig(config);
		var jobs = Generator.Generate(manifest, config, cfg);
		var results = new List<ValidationResult>();
		using var nomadClient = BuildNomadClient(cfg);

		foreach (var job in jobs)
		{
			var hclPath = await Generator.WriteHclAsync(job, config, cfg, outputDirectory, ct);
			IReadOnlyList<string> errors;
			try { errors = await nomadClient.ValidateJobAsync(hclPath, ct); }
			catch { errors = []; } // Nomad unreachable — skip live validation

			results.Add(new ValidationResult { ArtefactName = hclPath, Errors = errors });
		}

		return results;
	}

	// ── Plan ─────────────────────────────────────────────────────────────────

	public async Task<PlanResult> PlanAsync(
		AspireManifest manifest,
		EnvironmentConfig config,
		string outputDirectory,
		CancellationToken ct = default)
	{
		var cfg = GetConfig(config);
		var jobs = Generator.Generate(manifest, config, cfg);

		// Write HCL artefacts first — apply reads these
		foreach (var job in jobs)
		{
			await Generator.WriteHclAsync(job, config, cfg, outputDirectory, ct);
		}

		var desired = jobs.Select(j => j.Name).ToHashSet();

		HashSet<string> running;
		try
		{
			using var nomadClient = BuildNomadClient(cfg);
			running = (await nomadClient.GetRunningJobNamesAsync(ct)).ToHashSet();
		}
		catch (Exception ex)
		{
			logger.LogWarning(
				"Could not reach Nomad at {Address} — assuming all services are new. ({Msg})",
				cfg.NomadAddress, ex.Message);
			running = [];
		}

		return new PlanResult
		{
			ToCreate = desired.Except(running).Select(n => (object)n).ToList(),
			ToUpdate = desired.Intersect(running).Select(n => (object)n).ToList(),
			ToDelete = running.Except(desired).ToList(),
			Unchanged = []
		};
	}

	// ── Apply ─────────────────────────────────────────────────────────────────

	public async Task<IReadOnlyList<DeploymentResult>> ApplyAsync(
		AspireManifest manifest,
		EnvironmentConfig config,
		string outputDirectory,
		CancellationToken ct = default)
	{
		var cfg = GetConfig(config);
		var jobs = Generator.Generate(manifest, config, cfg);
		var results = new List<DeploymentResult>();
		using var nomadClient = BuildNomadClient(cfg);

		foreach (var job in jobs)
		{
			var hclPath = Path.Combine(outputDirectory, $"{job.Name}.job.hcl");
			if (!File.Exists(hclPath))
			{
				results.Add(new DeploymentResult
				{
					ServiceName = job.Name,
					Success = false,
					Error = $"Artefact not found: {hclPath}. Run 'deployify plan' first."
				});
				continue;
			}

			try
			{
				var evalId = await nomadClient.SubmitJobAsync(hclPath, ct);
				logger.LogInformation(
					"Submitted Consul Connect job '{Name}', evalId={EvalId}", job.Name, evalId);
				results.Add(new DeploymentResult
				{
					ServiceName = job.Name,
					DeploymentId = evalId,
					Success = true
				});
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Failed to submit job '{Name}'.", job.Name);
				results.Add(new DeploymentResult
				{
					ServiceName = job.Name,
					Success = false,
					Error = ex.Message
				});
			}
		}

		return results;
	}

	// ── Publish ───────────────────────────────────────────────────────────────

	public async Task PublishAsync(
		AspireManifest manifest,
		EnvironmentConfig config,
		string appHostDirectory,
		CancellationToken ct = default) =>
		await publisher.PublishAsync(manifest, config, appHostDirectory, ct);

	// ── Explain ───────────────────────────────────────────────────────────────

	public IReadOnlyList<ServiceExplanation> Explain(
		AspireManifest manifest, EnvironmentConfig config)
	{
		var cfg = GetConfig(config);
		return Generator.Generate(manifest, config, cfg).Select(job => new ServiceExplanation
		{
			Name = job.Name,
			Image = job.Image,
			ImageTag = job.ImageTag,
			Count = job.Count,
			Env = job.Env,
			Ports = job.Ports.Select(p => $"{p.Label}:{p.ContainerPort}/{p.Protocol}").ToList(),
			Dependencies = job.Dependencies,
			AdditionalInfo = new Dictionary<string, string>
			{
				["nomadNamespace"] = cfg.NomadNamespace,
				["datacenters"] = string.Join(", ", cfg.Datacenters),
				["consulAddress"] = cfg.ConsulAddress,
				["networkMode"] = "bridge (Consul Connect)",
				["upstreams"] = job.Upstreams.Count == 0
					? "none"
					: string.Join(", ", job.Upstreams.Select(u =>
						$"{u.DestinationName}→localhost:{u.LocalBindPort}")),
				["ingressGateway"] = job.ExposeViaIngressGateway
					? cfg.IngressGatewayName : "disabled"
			}
		}).ToList();
	}

	// ── GetDependencies ───────────────────────────────────────────────────────

	public IReadOnlyList<ServiceDependency> GetDependencies(
		AspireManifest manifest, EnvironmentConfig config)
	{
		var cfg = GetConfig(config);
		return Generator.Generate(manifest, config, cfg)
			.SelectMany(job => job.Dependencies.Select(dep =>
				new ServiceDependency { From = dep, To = job.Name }))
			.ToList();
	}

	// ── Diff ──────────────────────────────────────────────────────────────────

	public async Task<IReadOnlyList<DiffEntry>> DiffAsync(
		AspireManifest manifest,
		EnvironmentConfig config,
		string outputDirectory,
		CancellationToken ct = default)
	{
		var cfg = GetConfig(config);
		var jobs = Generator.Generate(manifest, config, cfg).ToDictionary(j => j.Name);
		var entries = new List<DiffEntry>();

		using var nomadClient = BuildNomadClient(cfg);
		HashSet<string> running;
		try
		{
			running = (await nomadClient.GetRunningJobNamesAsync(ct)).ToHashSet();
		}
		catch
		{
			// Nomad unreachable — treat every desired service as Added
			return jobs.Keys
				.Select(n => new DiffEntry { ServiceName = n, Kind = DiffKind.Added })
				.ToList();
		}

		foreach (var (name, job) in jobs)
		{
			if (!running.Contains(name))
			{
				entries.Add(new DiffEntry { ServiceName = name, Kind = DiffKind.Added });
				continue;
			}

			var remoteHcl = await nomadClient.GetJobHclAsync(name, ct);
			var localPath = await Generator.WriteHclAsync(job, config, cfg, outputDirectory, ct);
			var localHcl = await File.ReadAllTextAsync(localPath, ct);
			var kind = remoteHcl is null || Normalize(remoteHcl) != Normalize(localHcl)
				? DiffKind.Changed : DiffKind.Unchanged;

			entries.Add(new DiffEntry
			{
				ServiceName = name,
				Kind = kind,
				Field = kind == DiffKind.Changed ? "job spec" : null
			});
		}

		foreach (var name in running.Except(jobs.Keys))
		{
			entries.Add(new DiffEntry { ServiceName = name, Kind = DiffKind.Removed });
		}

		return entries;
	}

	// ── Helpers ───────────────────────────────────────────────────────────────

	private static NomadConsulTargetConfig GetConfig(EnvironmentConfig config)
	{
		if (config.TargetConfig.TryGetValue("nomadconsul", out var raw) && raw is JsonElement el)
		{
			return el.Deserialize<NomadConsulTargetConfig>(TargetConfigJsonOptions.Default) ?? new NomadConsulTargetConfig();
		}

		return new NomadConsulTargetConfig();
	}

	private NomadClient BuildNomadClient(NomadConsulTargetConfig cfg) => new NomadClient(cfg.NomadAddress, cfg.NomadToken, loggerFactory.CreateLogger<NomadClient>());

	private ConsulClient BuildConsulClient(NomadConsulTargetConfig cfg) => new ConsulClient(cfg.ConsulAddress, cfg.ConsulToken, logger);

	private static string Normalize(string hcl) =>
		string.Concat(hcl.Split('\n').Select(l => l.Trim())).ToLowerInvariant();
}
