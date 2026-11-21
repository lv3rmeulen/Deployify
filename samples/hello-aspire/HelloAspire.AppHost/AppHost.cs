var builder = DistributedApplication.CreateBuilder(args);

// A container resource: Deployify deploys it as-is from its image.
var cache = builder.AddRedis("cache");

// Another container, with a database on it. Aspire creates the database when it runs the app
// locally; once deployed, the postgres image creates it on first start from POSTGRES_DB.
var postgres = builder.AddPostgres("postgres");
var visitsDb = postgres.AddDatabase("visitsdb");
postgres.WithEnvironment("POSTGRES_DB", visitsDb.Resource.DatabaseName);

// A secret parameter without a default: you supply the value. Deployify reads it from the
// Parameters__admin_key environment variable and keeps it out of compose.yml.
var adminKey = builder.AddParameter("admin-key", secret: true);

// A project resource: Deployify builds it into an image with `dotnet publish -t:PublishContainer`.
builder.AddProject<Projects.HelloAspire_Api>("api")
    .WithReference(cache)
    .WaitFor(cache)
    .WithReference(visitsDb)
    .WaitFor(visitsDb)
    .WithEnvironment("ADMIN_KEY", adminKey)
    .WithExternalHttpEndpoints();

// A Dockerfile resource: Deployify builds it into an image with `docker build`.
builder.AddDockerfile("web", "../HelloAspire.Web")
    .WithHttpEndpoint(targetPort: 8085)
    .WithExternalHttpEndpoints();

builder.Build().Run();
