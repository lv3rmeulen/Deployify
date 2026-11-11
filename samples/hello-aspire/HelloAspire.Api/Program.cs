using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Connection string "cache" comes from the AppHost's WithReference(cache): locally from Aspire,
// once deployed from the environment variable Deployify generates for the target.
builder.AddRedisClient("cache");

var app = builder.Build();

app.MapGet("/", async (IConnectionMultiplexer redis) =>
{
    var visits = await redis.GetDatabase().StringIncrementAsync("visits");
    return Results.Ok(new { message = "Hello from Aspire, deployed by Deployify", visits });
});

// Liveness for health checks and the end-to-end test.
app.MapGet("/health", () => Results.Ok("healthy"));

app.Run();
