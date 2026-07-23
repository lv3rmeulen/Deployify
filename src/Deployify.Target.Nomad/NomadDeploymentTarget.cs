using System.Text.Json;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Nomad;

/// <summary>
/// IDeploymentTarget implementation for HashiCorp Nomad.
/// Config is read from environmentConfig.TargetConfig["nomad"].
/// </summary>
public sealed class NomadDeploymentTarget(
	JobGenerator jobGenerator,
	IPublisher publisher,
	ILogger<NomadDeploymentTarget> logger,
	ILoggerFactory loggerFactory) : IDeploymentTarget
{
	public DeploymentTargetType TargetType => DeploymentTargetType.Nomad;
	public string DisplayName => "HashiCorp Nomad";

	public async Task<(bool Ok, string? Error)> PingAsync(EnvironmentConfig config, CancellationToken ct = default)
	{
		var cfg = GetConfig(config);
		using var client = BuildClient(cfg);
		var ok = await client.PingAsync(ct);
		return ok ? (true, null) : (false, $"Cannot reach Nomad at {cfg.Address}. Is it running?");
	}

	public async Task<IReadOnlyList<string>> GenerateAsync(
		AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
	{
		var jobs = jobGenerator.Generate(manifest, config);
		var paths = new List<string>();
		foreach (var job in jobs)
		{
			paths.Add(await jobGenerator.WriteHclAsync(job, config, outputDirectory, ct));
		}

		return paths;
	}

	public async Task<IReadOnlyList<ValidationResult>> ValidateAsync(
		AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
	{
		var cfg = GetConfig(config);
		var jobs = jobGenerator.Generate(manifest, config);
		var results = new List<ValidationResult>();
		using var client = BuildClient(cfg);
		foreach (var job in jobs)
		{
			var hclPath = await jobGenerator.WriteHclAsync(job, config, outputDirectory, ct);
			var errors = await client.ValidateJobAsync(hclPath, ct);
			results.Add(new ValidationResult { ArtefactName = hclPath, Errors = errors });
		}

		return results;
	}

	public async Task<PlanResult> PlanAsync(
		AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
	{
		// Generate artefacts to disk first — apply will read these rather than regenerate
		var jobs = jobGenerator.Generate(manifest, config);
		foreach (var job in jobs)
		{
			await jobGenerator.WriteHclAsync(job, config, outputDirectory, ct);
		}

		var desired = jobs.Select(j => j.Name).ToHashSet();
		var cfg = GetConfig(config);
		using var client = BuildClient(cfg);

		HashSet<string> running;
		try { running = (await client.GetRunningJobNamesAsync(ct)).ToHashSet(); }
		catch (Exception ex)
		{
			logger.LogWarning("Could not reach Nomad at {Address} — assuming all services are new. ({Message})", cfg.Address, ex.Message);
			running = [];
		}

		return new PlanResult
		{
			ToCreate = desired.Except(running).Select(n => (object)new NomadJob { Name = n }).ToList(),
			ToUpdate = desired.Intersect(running).Select(n => (object)new NomadJob { Name = n }).ToList(),
			ToDelete = running.Except(desired).ToList(),
			Unchanged = running.Intersect(desired).Where(n => !desired.Except(running).Contains(n)).ToList()
		};
	}

	public async Task<IReadOnlyList<DeploymentResult>> ApplyAsync(
		AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
	{
		var cfg = GetConfig(config);
		var jobs = jobGenerator.Generate(manifest, config);
		var results = new List<DeploymentResult>();
		using var client = BuildClient(cfg);

		foreach (var job in jobs)
		{
			// Read the file plan already wrote — do not regenerate
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
				var evalId = await client.SubmitJobAsync(hclPath, ct);
				results.Add(new DeploymentResult { ServiceName = job.Name, DeploymentId = evalId, Success = true });
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Failed to submit job '{Name}'.", job.Name);
				results.Add(new DeploymentResult { ServiceName = job.Name, Success = false, Error = ex.Message });
			}
		}

		return results;
	}

	public async Task PublishAsync(
		AspireManifest manifest, EnvironmentConfig config, string appHostDirectory, CancellationToken ct = default) =>
		await publisher.PublishAsync(manifest, config, appHostDirectory, ct);

	public IReadOnlyList<ServiceExplanation> Explain(AspireManifest manifest, EnvironmentConfig config)
	{
		var cfg = GetConfig(config);
		var jobs = jobGenerator.Generate(manifest, config);
		return jobs.Select(j => new ServiceExplanation
		{
			Name = j.Name,
			Image = j.Image,
			ImageTag = j.ImageTag ?? config.ImageTag,
			Count = j.Count,
			Env = j.Env,
			Ports = j.Ports.Select(p => p.To.HasValue ? $"{p.Label}:{p.To}" : p.Label).ToList(),
			Dependencies = j.Dependencies,
			AdditionalInfo = new Dictionary<string, string>
			{
				["namespace"] = cfg.Namespace,
				["datacenters"] = string.Join(", ", cfg.Datacenters),
				["mode"] = cfg.DeploymentMode.ToString()
			}
		}).ToList();
	}

	public IReadOnlyList<ServiceDependency> GetDependencies(AspireManifest manifest, EnvironmentConfig config) =>
		jobGenerator.Generate(manifest, config)
			.SelectMany(j => j.Dependencies.Select(dep => new ServiceDependency { From = dep, To = j.Name }))
			.ToList();

	public async Task<IReadOnlyList<DiffEntry>> DiffAsync(
		AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
	{
		var cfg = GetConfig(config);
		var jobs = jobGenerator.Generate(manifest, config).ToDictionary(j => j.Name);
		var entries = new List<DiffEntry>();
		using var client = BuildClient(cfg);
		HashSet<string> running;
		try
		{
			running = (await client.GetRunningJobNamesAsync(ct)).ToHashSet();
		}
		catch
		{
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

			var remoteHcl = await client.GetJobHclAsync(name, ct);
			var localPath = await jobGenerator.WriteHclAsync(job, config, outputDirectory, ct);
			var localHcl = await File.ReadAllTextAsync(localPath, ct);
			var kind = (remoteHcl is null || Normalize(remoteHcl) != Normalize(localHcl))
				? DiffKind.Changed : DiffKind.Unchanged;
			entries.Add(new DiffEntry { ServiceName = name, Kind = kind, Field = kind == DiffKind.Changed ? "job spec" : null });
		}

		foreach (var name in running.Except(jobs.Keys))
		{
			entries.Add(new DiffEntry { ServiceName = name, Kind = DiffKind.Removed });
		}

		return entries;
	}

	private static NomadTargetConfig GetConfig(EnvironmentConfig config)
	{
		if (config.TargetConfig.TryGetValue("nomad", out var raw) && raw is JsonElement el)
		{
			return el.Deserialize<NomadTargetConfig>(TargetConfigJsonOptions.Default) ?? new NomadTargetConfig();
		}

		return new NomadTargetConfig();
	}

	private NomadClient BuildClient(NomadTargetConfig cfg) => new NomadClient(cfg.Address, cfg.Token, loggerFactory.CreateLogger<NomadClient>());

	private static string Normalize(string hcl) =>
		string.Concat(hcl.Split('\n').Select(l => l.Trim())).ToLowerInvariant();
}
