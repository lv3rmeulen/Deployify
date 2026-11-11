var app = WebApplication.CreateBuilder(args).Build();

app.MapGet("/", () => "Deployed with aspire deploy + Deployify");
app.MapGet("/health", () => Results.Ok("healthy"));

app.Run();
