using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Vigiloom.Infrastructure.Authentication;

public sealed class PublicApiOpenApi(IOptionsMonitor<CookieAuthenticationOptions> cookies) : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["ApiBearer"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "Vigiloom opaque credential" };
        document.Components.SecuritySchemes["AgentBearer"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "Vigiloom Agent credential" };
        document.Components.SecuritySchemes["BrowserSession"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, Name = cookies.Get(IdentityConstants.ApplicationScheme).Cookie.Name };
        document.Components.SecuritySchemes["Antiforgery"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "RequestVerificationToken" };
        return Task.CompletedTask;
    }
}

public sealed class PublicApiOpenApiOperation : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var policies = context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>().Select(x => x.Policy).ToArray();
        var document = context.Document;
        if (policies.Contains("Administrator"))
        {
            var session = new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("BrowserSession", document)] = [] };
            if (context.Description.HttpMethod is not ("GET" or "HEAD"))
                session[new OpenApiSecuritySchemeReference("Antiforgery", document)] = [];
            operation.Security = [session];
        }
        else if (policies.Contains("MonitoringApiRead") || policies.Contains("DeviceWrite"))
        {
            var session = new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("BrowserSession", document)] = [] };
            if (policies.Contains("DeviceWrite") && context.Description.HttpMethod is not ("GET" or "HEAD"))
                session[new OpenApiSecuritySchemeReference("Antiforgery", document)] = [];
            operation.Security = [session, new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("ApiBearer", document)] = [] }];
            if (policies.Contains("DeviceWrite"))
                operation.Description = "API credentials require monitoring.write; some changes require an administrator session.";
        }
        else if (context.Description.RelativePath is { } path && path.StartsWith("api/v1/agent/", StringComparison.OrdinalIgnoreCase) &&
            !path.Equals("api/v1/agent/enroll", StringComparison.OrdinalIgnoreCase))
        {
            operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("AgentBearer", document)] = [] }];
            if (path.Equals("api/v1/agent/recover", StringComparison.OrdinalIgnoreCase))
                operation.Description = "Requires a vlr_<enrollment ID>.<machine secret> bearer proof and the enrollment secret in the request body.";
        }
        return Task.CompletedTask;
    }
}
