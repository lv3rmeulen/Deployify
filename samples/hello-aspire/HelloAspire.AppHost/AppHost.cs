var builder = DistributedApplication.CreateBuilder(args);

// A container resource: Deployify deploys it as-is from its image.
var cache = builder.AddRedis("cache");

// A project resource: Deployify builds it into an image with `dotnet publish -t:PublishContainer`.
builder.AddProject<Projects.HelloAspire_Api>("api")
    .WithReference(cache)
    .WaitFor(cache)
    .WithExternalHttpEndpoints();

builder.Build().Run();
