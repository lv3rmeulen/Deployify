using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging;

namespace Deployify.Core.SecretBackends;

/// <summary>
/// The home of generated parameter values (passwords from <c>default.generate</c>) when an
/// external backend is configured. It replaces deployify-parameters.json, so a generated
/// password lives only in the backend and is reused on every deploy.
/// Keys are <c>parameter.input</c>, the same as the parameters file.
/// </summary>
public interface ISecretStore
{
    /// <summary>Where the values are kept, for messages. Never includes a value.</summary>
    string Description { get; }

    /// <summary>Reads every stored value. Returns an empty map when nothing has been stored yet.</summary>
    Task<IReadOnlyDictionary<string, SensitiveValue>> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Stores <paramref name="all"/>, the complete set of values. Backends that replace a whole
    /// document (a Kubernetes Secret, a Vault KV entry) write all of them; backends with one
    /// entry per value only write <paramref name="changed"/>.
    /// </summary>
    Task SaveAsync(
        IReadOnlyDictionary<string, SensitiveValue> all,
        IReadOnlyCollection<string> changed,
        CancellationToken ct = default);
}

/// <summary>Implemented by targets that can keep secrets in an external backend.</summary>
public interface ISecretStoreProvider
{
    /// <summary>The backends this target supports, besides <see cref="SecretBackendType.Inline"/>.</summary>
    IReadOnlyCollection<SecretBackendType> SupportedSecretBackends { get; }

    /// <summary>The store for generated values with the environment's configured backend.</summary>
    ISecretStore CreateSecretStore(EnvironmentConfig config, ILogger logger);
}

/// <summary>
/// Generated parameter values for one run against an external store. Values the store already
/// holds are reused; new ones are generated in memory and only written to the store by
/// <see cref="FlushAsync"/>, which apply calls before deploying anything that uses them.
/// </summary>
public sealed class GeneratedSecretSession
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stored = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    private GeneratedSecretSession(ISecretStore store) => Store = store;

    public ISecretStore Store { get; }

    /// <summary>Set when the store could not be read. Flushing is refused then, see <see cref="FlushAsync"/>.</summary>
    public Exception? LoadError { get; private set; }

    /// <summary>The backing dictionary for <see cref="GeneratedParameterSource"/>.</summary>
    public IDictionary<string, string> Values => _values;

    /// <summary>Keys generated or migrated in this run and not yet written to the store.</summary>
    public IReadOnlyCollection<string> Pending
    {
        get
        {
            lock (_lock)
            {
                return [.. _pending];
            }
        }
    }

    /// <summary>
    /// Reads the store. <paramref name="legacy"/> holds values from deployify-parameters.json:
    /// any the store doesn't have yet are carried over (and saved on the next apply), so
    /// switching backends doesn't rotate an existing database password.
    /// </summary>
    public static async Task<GeneratedSecretSession> OpenAsync(
        ISecretStore store,
        IReadOnlyDictionary<string, string>? legacy = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var session = new GeneratedSecretSession(store);
        try
        {
            foreach (var (key, value) in await store.LoadAsync(ct))
            {
                session._values[key] = value.Reveal();
                session._stored.Add(key);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            session.LoadError = ex;
            logger?.LogWarning(
                "Could not read generated secrets from {Store}: {Message}. Plan output may show new values; " +
                "apply will refuse to run until the store can be read.", store.Description, ex.Message);
        }

        foreach (var (key, value) in legacy ?? new Dictionary<string, string>())
        {
            if (!session._values.ContainsKey(key))
            {
                session._values[key] = value;
                session._pending.Add(key);
            }
        }

        return session;
    }

    /// <summary>Called by <see cref="GeneratedParameterSource"/> when it makes a new value.</summary>
    public void OnGenerated(string key, string value)
    {
        lock (_lock)
        {
            _pending.Add(key);
        }
    }

    /// <summary>
    /// Writes new values to the store. Refuses to run when the store couldn't be read: values
    /// generated then may replace ones a running database already uses.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        string[] changed;
        lock (_lock)
        {
            changed = [.. _pending];
        }

        if (changed.Length == 0)
        {
            return;
        }

        if (LoadError is not null)
        {
            throw new InvalidOperationException(
                $"Not saving generated secrets: {Store.Description} could not be read ({LoadError.Message}). " +
                "Saving now could replace a password that is already in use.", LoadError);
        }

        Dictionary<string, SensitiveValue> all;
        lock (_lock)
        {
            all = _values.ToDictionary(kv => kv.Key, kv => new SensitiveValue(kv.Value), StringComparer.Ordinal);
        }

        await Store.SaveAsync(all, changed, ct);
        lock (_lock)
        {
            foreach (var key in changed)
            {
                _pending.Remove(key);
                _stored.Add(key);
            }
        }
    }
}
