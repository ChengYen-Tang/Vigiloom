using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Modules.IdentityAccess.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDataProtection();
builder.Services.AddAuthorization();
builder.Services.AddModuleSqlServer<IdentityAccessDbContext>(
    _ => builder.Configuration.GetConnectionString("VigiloomIdentity")
        ?? throw new InvalidOperationException("ConnectionStrings:VigiloomIdentity is required."), "identity");
builder.Services.AddIdentityAccessOperations();

var app = builder.Build();

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

/// <summary>API credential supplied by an internal service.</summary>
public sealed record VerifyApiCredentialRequest(string? Value);

/// <summary>Validated API credential identity and access scope.</summary>
public sealed record VerifiedApiCredentialResponse(Guid Id, string Name, bool CanWrite);

public partial class Program;
