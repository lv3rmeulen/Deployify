using System.Security.Cryptography;
using System.Text;
using Npgsql;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Connection strings "cache" and "visitsdb" come from the AppHost's WithReference calls: locally
// from Aspire, once deployed from the environment variables Deployify generates for the target.
builder.AddRedisClient("cache");
builder.AddNpgsqlDataSource("visitsdb");

var app = builder.Build();

// Counts the visit in Redis and logs it in Postgres.
app.MapGet("/", async (IConnectionMultiplexer redis, NpgsqlDataSource db) =>
{
    var visits = await redis.GetDatabase().StringIncrementAsync("visits");

    await using var command = db.CreateCommand("""
        CREATE TABLE IF NOT EXISTS visits (at timestamptz NOT NULL DEFAULT now());
        INSERT INTO visits DEFAULT VALUES;
        SELECT count(*) FROM visits;
        """);
    var logged = (long)(await command.ExecuteScalarAsync())!;

    return Results.Ok(new { message = "Hello from Aspire, deployed by Deployify", visits, logged });
});

// Starts both counts over. Needs the admin key, which the AppHost passes in as ADMIN_KEY.
app.MapPost("/reset", async (HttpRequest request, IConfiguration config, IConnectionMultiplexer redis, NpgsqlDataSource db) =>
{
    if (!Matches(request.Headers["X-Admin-Key"], config["ADMIN_KEY"]))
    {
        return Results.Unauthorized();
    }

    await redis.GetDatabase().KeyDeleteAsync("visits");
    await using var command = db.CreateCommand("DROP TABLE IF EXISTS visits");
    await command.ExecuteNonQueryAsync();
    return Results.NoContent();
});

// Liveness for health checks and the end-to-end test.
app.MapGet("/health", () => Results.Ok("healthy"));

app.Run();

static bool Matches(string? given, string? expected)
    => !string.IsNullOrEmpty(given) && !string.IsNullOrEmpty(expected) &&
       CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));
