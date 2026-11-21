using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Models;

namespace Deployify.Target.DigitalOcean;

/// <summary>
/// A Postgres or Redis container from the manifest that becomes a <c>databases</c> component
/// instead of a running container. Services reach it through App Platform's bindable variables
/// (<c>${pg.HOSTNAME}</c> and so on), which the platform fills in at run time.
/// </summary>
public sealed record ManagedDatabase
{
    /// <summary>The Aspire resource name.</summary>
    public required string Resource { get; init; }

    /// <summary>The database component's name in the app spec; also the bindable variable prefix.</summary>
    public required string Component { get; init; }

    /// <summary><c>PG</c>, <c>VALKEY</c> or <c>REDIS</c>.</summary>
    public required string Engine { get; init; }

    public required DoDatabaseMode Mode { get; init; }

    /// <summary>The cluster to attach in Cluster mode.</summary>
    public string? ClusterName { get; init; }

    /// <summary>Create the cluster on apply when it's missing.</summary>
    public bool Create { get; init; }

    public string Size { get; init; } = "db-s-1vcpu-1gb";

    public int Nodes { get; init; } = 1;

    public string? Version { get; init; }

    /// <summary>The databases the app's child resources (<c>AddDatabase</c>) name, in manifest order.</summary>
    public IReadOnlyList<string> DatabaseNames => [.. Children.Values.Distinct()];

    /// <summary>The child resources (<c>AddDatabase</c>) and the database each one names.</summary>
    public IReadOnlyDictionary<string, string> Children { get; init; } = new Dictionary<string, string>();

    /// <summary>Parameters that hold this server's password in the manifest; they resolve to the managed password.</summary>
    public IReadOnlySet<string> PasswordParameters { get; init; } = new HashSet<string>();

    /// <summary>Parameters that hold this server's user name.</summary>
    public IReadOnlySet<string> UserParameters { get; init; } = new HashSet<string>();

    /// <summary>
    /// The user name the container is set up with as plain text (<c>postgres</c>), which Aspire
    /// copies into connection properties such as <c>APPDB_USERNAME</c>. Null when it's a parameter.
    /// </summary>
    public string? LiteralUser { get; init; }

    /// <summary>The URI scheme for connections: managed Redis-compatible engines require TLS.</summary>
    public string Scheme => IsPostgres ? "postgresql" : "rediss";

    public bool IsPostgres => Engine == "PG";

    /// <summary>The engine slug of the Databases API, for creating a cluster.</summary>
    public string ApiEngine => Engine.ToLowerInvariant();

    public string Variable(string name) => "${" + Component + "." + name + "}";

    /// <summary>The connection string <c>{server.connectionString}</c> resolves to.</summary>
    public string ConnectionString => ConnectionStringFor(null);

    /// <summary>
    /// The connection string for one database on the server; null means the database App
    /// Platform names in <c>DATABASE</c>. Redis-compatible engines have no databases by name.
    /// </summary>
    public string ConnectionStringFor(string? database) => IsPostgres
        ? $"Host={Variable("HOSTNAME")};Port={Variable("PORT")};Username={Variable("USERNAME")};" +
          $"Password={Variable("PASSWORD")};Database={database ?? Variable("DATABASE")};SSL Mode=Require"
        : $"{Variable("HOSTNAME")}:{Variable("PORT")},user={Variable("USERNAME")},password={Variable("PASSWORD")},ssl=true";

    public DatabaseSpec ToSpec() => new()
    {
        Name = Component,
        Engine = Engine,
        Version = Version,
        Production = Mode == DoDatabaseMode.Cluster ? true : null,
        ClusterName = Mode == DoDatabaseMode.Cluster ? ClusterName : null,
        DbName = IsPostgres && DatabaseNames.Count == 1 ? DatabaseNames[0] : null
    };
}

public static partial class ManagedDatabases
{
    /// <summary>
    /// Finds the Postgres and Redis containers that become managed databases. Problems that
    /// stop a resource from being deployed the way it's configured go to <paramref name="problems"/>.
    /// </summary>
    public static IReadOnlyList<ManagedDatabase> Find(
        AspireManifest manifest, DoTargetConfig cfg, ICollection<string> problems)
    {
        var result = new List<ManagedDatabase>();
        foreach (var (name, resource) in manifest.Resources)
        {
            cfg.Databases.TryGetValue(name, out var settings);
            if (!resource.IsDeployable || resource.IsBuiltImage || resource.Image is null)
            {
                continue;
            }

            var engine = settings?.Engine?.ToUpperInvariant() ?? DetectEngine(resource.Image);
            if (engine is null)
            {
                continue;
            }

            var mode = settings?.Mode ?? (engine == "PG" ? DoDatabaseMode.Dev : DoDatabaseMode.Cluster);
            if (mode == DoDatabaseMode.Container)
            {
                continue;
            }

            var component = Names.Component(name);
            var children = ChildDatabases(name, manifest);
            var databases = children.Values.Distinct().ToList();
            if (mode == DoDatabaseMode.Dev && engine != "PG")
            {
                problems.Add($"{name}: App Platform dev databases are Postgres only. Set Databases.{name}.Mode to " +
                             "\"Cluster\" for a Managed Database cluster, or \"Container\" to run the container (without persistent storage).");
            }

            if (mode == DoDatabaseMode.Dev && databases.Count > 1)
            {
                problems.Add($"{name}: a dev database holds one database, but the app uses {string.Join(", ", databases)}. " +
                             $"Set Databases.{name}.Mode to \"Cluster\".");
            }

            var (passwords, users) = CredentialParameters(resource);
            var literalUser = resource.Env?.FirstOrDefault(e => e.Key.EndsWith("_USER", StringComparison.OrdinalIgnoreCase)).Value is { } u &&
                              !u.Contains('{')
                ? u
                : resource.ConnectionString is { } connection && LiteralUsername().Match(connection) is { Success: true } lu
                    ? lu.Groups["u"].Value
                    : null;
            result.Add(new ManagedDatabase
            {
                Resource = name,
                Component = component,
                Engine = engine,
                Mode = mode,
                ClusterName = mode == DoDatabaseMode.Cluster
                    ? settings?.ClusterName ?? Names.Component($"{cfg.EffectiveAppName}-{name}")
                    : null,
                Create = settings?.Create ?? true,
                Size = settings?.Size ?? "db-s-1vcpu-1gb",
                Nodes = settings?.Nodes ?? 1,
                // A Redis image's version says nothing about the Valkey version to ask for.
                Version = settings?.Version ?? (engine == "PG" ? DoImage.MajorVersion(resource.Image) : null),
                Children = children,
                PasswordParameters = passwords,
                UserParameters = users,
                LiteralUser = literalUser
            });
        }

        return result;
    }

    /// <summary><c>PG</c> for Postgres images, <c>VALKEY</c> for Redis and Valkey images, otherwise null.</summary>
    public static string? DetectEngine(string image) => DoImage.ShortName(image) switch
    {
        "postgres" or "postgis" or "pgvector" => "PG",
        "redis" or "valkey" or "redis-stack-server" => "VALKEY",
        _ => null
    };

    /// <summary>
    /// The database names of <c>AddDatabase</c> children: their <c>databaseName</c>, their
    /// <c>Database=</c> value, or their resource name. A child is a resource whose parent is the
    /// server, or (as Aspire writes them, as <c>value.v0</c>) whose connection string extends the server's.
    /// </summary>
    private static Dictionary<string, string> ChildDatabases(string server, AspireManifest manifest)
    {
        var children = new Dictionary<string, string>(StringComparer.Ordinal);
        var serverReference = "{" + server + ".connectionString}";
        foreach (var (name, resource) in manifest.Resources)
        {
            var isChild = name != server &&
                          (resource.Parent == server ||
                           resource.ConnectionString?.StartsWith(serverReference, StringComparison.Ordinal) == true);
            if (!isChild)
            {
                continue;
            }

            var clause = resource.ConnectionString is { } cs ? DatabaseClause().Match(cs) : null;
            var database =
                resource.ExtensionData is not null &&
                resource.ExtensionData.TryGetValue("databaseName", out var dn) && dn.ValueKind == JsonValueKind.String
                    ? dn.GetString()
                    : clause is { Success: true }
                        ? clause.Groups["db"].Value.Trim()
                        : resource.Type.EndsWith(".database.v0", StringComparison.Ordinal) ? name : null;
            if (!string.IsNullOrEmpty(database))
            {
                children[name] = database;
            }
        }

        return children;
    }

    /// <summary>
    /// The parameters that carry the server's password and user name: in its own env
    /// (<c>POSTGRES_PASSWORD</c>), its connection string (<c>Password={pg-password.value}</c>)
    /// or its args (<c>--requirepass {cache-password.value}</c>).
    /// </summary>
    private static (HashSet<string> Passwords, HashSet<string> Users) CredentialParameters(AspireResource resource)
    {
        var passwords = new HashSet<string>(StringComparer.Ordinal);
        var users = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (key, value) in resource.Env ?? [])
        {
            var target = key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) ? passwords
                : key.Contains("USER", StringComparison.OrdinalIgnoreCase) ? users
                : null;
            if (target is not null)
            {
                foreach (Match m in ParameterValue().Matches(value))
                {
                    target.Add(m.Groups["p"].Value);
                }
            }
        }

        if (resource.ConnectionString is { } cs)
        {
            foreach (Match m in CredentialClause().Matches(cs))
            {
                (m.Groups["k"].Value.StartsWith("pass", StringComparison.OrdinalIgnoreCase) ? passwords : users).Add(m.Groups["p"].Value);
            }
        }

        if (resource.ExtensionData is not null && resource.ExtensionData.TryGetValue("args", out var args) &&
            args.ValueKind == JsonValueKind.Array)
        {
            var previous = "";
            foreach (var arg in args.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!))
            {
                if (previous.Contains("pass", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (Match m in ParameterValue().Matches(arg))
                    {
                        passwords.Add(m.Groups["p"].Value);
                    }
                }

                previous = arg;
            }
        }

        return (passwords, users);
    }

    /// <summary>
    /// Reference handlers that point every reference to a managed server (its connection
    /// string, its host and port, its password parameter) at App Platform's bindable variables.
    /// Everything else goes to the built-in handlers.
    /// </summary>
    public static IReadOnlyList<IReferenceHandler> Handlers(IReadOnlyList<ManagedDatabase> databases)
    {
        var byResource = databases.ToDictionary(d => d.Resource, StringComparer.Ordinal);
        var children = databases
            .SelectMany(d => d.Children.Select(c => (Child: c.Key, Server: d, Database: c.Value)))
            .ToDictionary(c => c.Child, c => c.Server.ConnectionStringFor(c.Server.IsPostgres ? c.Database : null), StringComparer.Ordinal);
        var credentials = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var db in databases)
        {
            foreach (var p in db.UserParameters)
            {
                credentials[p] = db.Variable("USERNAME");
            }

            foreach (var p in db.PasswordParameters)
            {
                credentials[p] = db.Variable("PASSWORD");
            }
        }

        return
        [
            new Override(new ConnectionStringHandler(), ctx =>
                byResource.TryGetValue(ctx.ResourceName, out var db) ? db.ConnectionString : children.GetValueOrDefault(ctx.ResourceName)),
            new Override(new BindingsHandler(), ctx =>
                byResource.TryGetValue(ctx.ResourceName, out var db) ? BindingProperty(db, ctx) : null),
            new Override(new ValueHandler(), ctx => credentials.GetValueOrDefault(ctx.ResourceName)),
            new Override(new InputsHandler(), ctx => credentials.GetValueOrDefault(ctx.ResourceName))
        ];
    }

    private static string BindingProperty(ManagedDatabase db, ReferenceContext context)
    {
        context.RequireArguments(2, "bindings.<binding>.<url|host|port|targetPort|scheme>");
        return context.Arguments[1] switch
        {
            "host" => db.Variable("HOSTNAME"),
            "port" or "targetPort" => db.Variable("PORT"),
            // Aspire builds URIs such as CACHE_URI from the binding's scheme.
            "scheme" => db.IsPostgres ? "tcp" : db.Scheme,
            "url" => $"{(db.IsPostgres ? "tcp" : db.Scheme)}://{db.Variable("HOSTNAME")}:{db.Variable("PORT")}",
            var other => throw new ExpressionException(ExpressionDiagnosticKind.UnsupportedProperty,
                $"Unknown binding property '{other}'. Supported: url, host, port, targetPort, scheme.")
        };
    }

    /// <summary>
    /// Fixes the connection properties Aspire adds for each reference (<c>APPDB_USERNAME</c>,
    /// <c>APPDB_URI</c>) that carry the container's user name as plain text. Placeholders in them
    /// are already resolved; only the literal user is left to replace. Returns the changed keys.
    /// </summary>
    public static IReadOnlyList<string> RewriteConnectionProperties(
        IDictionary<string, string> env, IReadOnlyList<ManagedDatabase> databases)
    {
        var changed = new List<string>();
        foreach (var db in databases.Where(d => d.LiteralUser is not null))
        {
            var prefixes = db.Children.Keys.Append(db.Resource).Select(EnvPrefix).ToList();
            foreach (var key in env.Keys.ToList())
            {
                var prefix = prefixes.FirstOrDefault(p => key.StartsWith(p + "_", StringComparison.OrdinalIgnoreCase));
                if (prefix is null)
                {
                    continue;
                }

                var suffix = key[(prefix.Length + 1)..].ToUpperInvariant();
                var value = env[key];
                var rewritten = suffix switch
                {
                    "USERNAME" or "USER" when value == db.LiteralUser => db.Variable("USERNAME"),
                    "URI" => value.Replace($"://{db.LiteralUser}:", $"://{db.Variable("USERNAME")}:", StringComparison.Ordinal),
                    _ => value
                };
                if (rewritten != value)
                {
                    env[key] = rewritten;
                    changed.Add(key);
                }
            }
        }

        return changed;
    }

    /// <summary>The environment variable prefix Aspire uses for a reference: <c>my-db</c> becomes <c>MY_DB</c>.</summary>
    private static string EnvPrefix(string resource)
        => new([.. resource.ToUpperInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')]);

    /// <summary>Answers a reference itself when <c>replacement</c> returns text; otherwise asks <c>inner</c>.</summary>
    private sealed class Override(IReferenceHandler inner, Func<ReferenceContext, string?> replacement) : IReferenceHandler
    {
        public string Property => inner.Property;

        public ValueTask<ResolvedValue> ResolveAsync(ReferenceContext context)
            => replacement(context) is { } text ? ValueTask.FromResult(ResolvedValue.Text(text)) : inner.ResolveAsync(context);
    }

    [GeneratedRegex(@"\{(?<p>[A-Za-z0-9_-]+)\.(value|inputs\.value)\}")]
    private static partial Regex ParameterValue();

    [GeneratedRegex(@"(?<k>password|pwd|username|user id|userid|user)\s*=\s*\{(?<p>[A-Za-z0-9_-]+)\.(value|inputs\.value)\}",
        RegexOptions.IgnoreCase)]
    private static partial Regex CredentialClause();

    [GeneratedRegex(@"(?:^|;)\s*(?:Username|User Id|UserId|User)\s*=\s*(?<u>[^;{}]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LiteralUsername();

    [GeneratedRegex(@"(?:^|;)\s*Database\s*=\s*(?<db>[^;{}]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DatabaseClause();
}
