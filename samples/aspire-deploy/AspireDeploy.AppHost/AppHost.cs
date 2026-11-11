var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.AspireDeploy_Api>("api")
    .WithExternalHttpEndpoints();

// Adds the deployify-deploy step to `aspire deploy`. It deploys the active environment in
// deployify-state.json (Docker Compose here); settings can be overridden with Deployify__* variables.
builder.AddDeployify();

builder.Build().Run();
