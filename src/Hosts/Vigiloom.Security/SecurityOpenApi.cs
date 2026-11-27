using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Vigiloom.Security;

internal sealed class SecurityOpenApi(IOptionsMonitor<CookieAuthenticationOptions> cookies) : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["ApiBearer"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "Vigiloom opaque credential" };
        document.Components.SecuritySchemes["BrowserSession"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, Name = cookies.Get(IdentityConstants.ApplicationScheme).Cookie.Name };
        document.Components.SecuritySchemes["Antiforgery"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "RequestVerificationToken" };
        return Task.CompletedTask;
    }
}

internal sealed class SecurityOpenApiOperation : IOpenApiOperationTransformer
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
        else if (policies.Contains("MonitoringApiRead"))
            operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("BrowserSession", document)] = [] },
                new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("ApiBearer", document)] = [] }];
        return Task.CompletedTask;
    }
}
