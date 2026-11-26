using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Telemetry;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.AddVigiloomSelfTelemetry("Vigiloom.Security");
builder.Services.AddModuleSqlServer<SecurityAnalysisDbContext>(services =>
    services.GetRequiredService<IConfiguration>().GetConnectionString("Security")
        ?? throw new InvalidOperationException("ConnectionStrings:Security is required."), "security", pooled: true);

var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok());
app.Run();

public partial class Program;
