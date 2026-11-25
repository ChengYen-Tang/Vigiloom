using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Identity;
using Vigiloom.Contracts.Identity;
using Vigiloom.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Vigiloom.Modules.IdentityAccess.Domain;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddVigiloomDataProtection(builder.Configuration);
builder.Services.AddAuthorization(options => options.AddPolicy(BuiltInRoles.Administrator, policy => policy.RequireRole(BuiltInRoles.Administrator)));
builder.Services.AddModuleSqlServer<IdentityAccessDbContext>(
    _ => builder.Configuration.GetConnectionString("VigiloomIdentity")
        ?? throw new InvalidOperationException("ConnectionStrings:VigiloomIdentity is required."), "identity");
builder.Services.AddIdentityAccessOperations();
builder.Services.AddScoped<AdministratorActorValidator>();
builder.Services.AddScoped<AccountActorValidator>();
builder.Services.AddScoped<IdentityAuditFeed>();
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
