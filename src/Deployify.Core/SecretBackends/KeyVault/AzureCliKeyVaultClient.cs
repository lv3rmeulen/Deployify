using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Core.SecretBackends.KeyVault;

/// <summary>
/// <see cref="IKeyVaultClient"/> on top of the Azure CLI, so it honors <c>az login</c>, service
/// principals and managed identities the same way the Container Apps target does. Secret values
/// never appear on a command line or in a log.
/// </summary>
public sealed class AzureCliKeyVaultClient(string? subscriptionId = null) : IKeyVaultClient
{
    /// <inheritdoc />
    public async Task<KeyVaultSecret?> GetSecretAsync(string vaultName, string name, CancellationToken ct = default)
    {
        // Output holds the secret value: it's parsed and wrapped, never logged.
        var (exit, output) = await RunAsync("az", BuildArgs(
            $"keyvault secret show --vault-name {vaultName} --name {name} --output json"), ct);
        if (exit != 0)
        {
            if (output.Contains("SecretNotFound", StringComparison.Ordinal) ||
                output.Contains("was not found", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            throw new InvalidOperationException($"Failed to read Key Vault secret {name}: {FirstLine(output)}");
        }

        return ParseSecret(output);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KeyVaultSecretInfo>> ListSecretsAsync(string vaultName, CancellationToken ct = default)
    {
        var (exit, output) = await RunAsync("az", BuildArgs($"keyvault secret list --vault-name {vaultName} --output json"), ct);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Failed to list Key Vault secrets in {vaultName}: {FirstLine(output)}");
        }

        return ParseSecretList(output);
    }

    /// <inheritdoc />
    public async Task<string> SetSecretAsync(
        string vaultName, string name, string value, IReadOnlyDictionary<string, string> tags, CancellationToken ct = default)
    {
        // The value goes through an owner-only temporary file, not the command line, so it
        // doesn't show up in the process list.
        var valuePath = Path.Combine(Path.GetTempPath(), $"deployify-kv-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(valuePath, value, new System.Text.UTF8Encoding(false), ct);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(valuePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var tagArgs = tags.Count == 0 ? "" : " --tags " + string.Join(' ', tags.Select(t => $"{t.Key}={t.Value}"));
            // --query id keeps the value out of the output.
            var (exit, output) = await RunAsync("az", BuildArgs(
                $"keyvault secret set --vault-name {vaultName} --name {name} --file \"{valuePath}\" --encoding utf-8" +
                $"{tagArgs} --query id --output tsv"), ct);
            if (exit != 0)
            {
                throw new InvalidOperationException($"Failed to store Key Vault secret {name}: {FirstLine(output)}");
            }

            return VersionOf(output.Trim());
        }
        finally
        {
            File.Delete(valuePath);
        }
    }

    public static KeyVaultSecret ParseSecret(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetString()!;
        var name = root.TryGetProperty("name", out var n) && n.GetString() is { } nm ? nm : id.TrimEnd('/').Split('/')[^2];
        return new KeyVaultSecret(name, new SensitiveValue(root.GetProperty("value").GetString() ?? ""), VersionOf(id));
    }

    public static IReadOnlyList<KeyVaultSecretInfo> ParseSecretList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return
        [
            .. doc.RootElement.EnumerateArray().Select(e => new KeyVaultSecretInfo(
                e.TryGetProperty("name", out var n) && n.GetString() is { } name ? name : e.GetProperty("id").GetString()!.Split('/')[^1],
                e.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Object
                    ? t.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal)
                    : new Dictionary<string, string>()))
        ];
    }

    /// <summary>The version from a secret id: <c>https://v.vault.azure.net/secrets/name/{version}</c>.</summary>
    public static string VersionOf(string secretId) => secretId.TrimEnd('/').Split('/')[^1];

    // az errors can run to pages; the first line names the problem.
    private static string FirstLine(string output)
        => output.Trim().Split('\n', 2)[0].Trim();

    private string BuildArgs(string command)
        => string.IsNullOrWhiteSpace(subscriptionId) ? command : $"--subscription {subscriptionId} {command}";

    private static async Task<(int ExitCode, string Output)> RunAsync(string command, string args, CancellationToken ct)
    {
        // On Windows the Azure CLI is a batch file (az.cmd).
        if (command == "az" && OperatingSystem.IsWindows())
        {
            command = "az.cmd";
        }

        var psi = new ProcessStartInfo(command, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {command}.");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                /* already exited */
            }

            throw;
        }

        var output = await stdoutTask;
        var stderr = await stderrTask;
        return (proc.ExitCode, string.IsNullOrWhiteSpace(output) ? stderr : output);
    }
}
