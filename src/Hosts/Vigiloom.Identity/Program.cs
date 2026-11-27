using Vigiloom.Infrastructure.Telemetry;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Identity;
using Vigiloom.Contracts.Identity;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Security;
using Vigiloom.Infrastructure.Operations;
using Vigiloom.Modules.IdentityAccess.Application.Retention;
using Microsoft.AspNetCore.Identity;
using Vigiloom.Modules.IdentityAccess.Domain;

var builder = WebApplication.CreateBuilder(args);
builder.AddVigiloomSelfTelemetry("Vigiloom.Identity");
builder.Services.AddVigiloomDataProtection(builder.Configuration);
builder.Services.AddAuthorization(options => options.AddPolicy(BuiltInRoles.Administrator, policy => policy.RequireRole(BuiltInRoles.Administrator)));
builder.Services.AddModuleSqlServer<IdentityAccessDbContext>(
    _ => builder.Configuration.GetConnectionString("VigiloomIdentity")
        ?? throw new InvalidOperationException("ConnectionStrings:VigiloomIdentity is required."), "identity");
builder.Services.AddIdentityAccessOperations();
builder.Services.AddScoped<AdministratorActorValidator>();
builder.Services.AddScoped<AccountActorValidator>();
builder.Services.AddScoped<IdentityAuditFeed>();
builder.Services.AddScoped<IdentityAuditRetention>();
builder.Services.AddScoped<IndependentIdentityAuditRetention>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthentication().AddIdentityCookies();
builder.Services.AddIdentityCore<Account>().AddSignInManager();

var app = builder.Build();
app.MapIdentitySessionEndpoints();
app.MapCredentialManagementEndpoints();
app.MapAccountManagementEndpoints();
app.MapAccountPasswordEndpoints();
app.MapTwoFactorSettingsEndpoints();
app.MapIdentityAuditEndpoints();
app.MapPost("/internal/identity/audit/retention/count", async (AuditRetentionRequest request, IndependentIdentityAuditRetention retention, CancellationToken token) =>
    request.Cutoff.Offset != TimeSpan.Zero ? Results.BadRequest() : Results.Ok(await retention.CountAsync(request.Cutoff, token)));
app.MapPost("/internal/identity/audit/retention/delete", async (AuditRetentionRequest request, IndependentIdentityAuditRetention retention, CancellationToken token) =>
    request.Cutoff.Offset != TimeSpan.Zero || request.BatchSize is < 1 or > 500 ? Results.BadRequest() : Results.Ok(await retention.DeleteBatchAsync(request.Cutoff, request.BatchSize, token)));

// This endpoint is for trusted internal service traffic and must not be exposed by the gateway.
app.MapPost("/internal/identity/api-credentials/validate", async (
    VerifyApiCredentialRequest request, HttpContext context, ApiCredentialVerifier verifier, CancellationToken token) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (string.IsNullOrEmpty(request.Value) || !request.Value.StartsWith("vlg_", StringComparison.OrdinalIgnoreCase))
        return Results.Unauthorized();
    var credential = await verifier.VerifyAsync(request.Value[4..], token);
    return credential is null
        ? Results.Unauthorized()
        : Results.Ok(new VerifiedApiCredentialResponse(credential.Id, credential.Name, credential.CanWrite));
});

app.Run();

public partial class Program;
