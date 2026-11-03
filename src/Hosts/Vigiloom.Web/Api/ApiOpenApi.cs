using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Vigiloom.Web.Api;

internal sealed class ApiOpenApi(IOptionsMonitor<CookieAuthenticationOptions> cookies) : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["ApiBearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "Vigiloom opaque credential",
            Description = "A third-party API credential. The access endpoint requires monitoring.read."
        };
        document.Components.SecuritySchemes["AgentBearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "Vigiloom Agent credential",
            Description = "A registered Agent credential, distinct from third-party API and human credentials."
        };
        document.Components.SecuritySchemes["BrowserSession"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie,
            Name = cookies.Get(IdentityConstants.ApplicationScheme).Cookie.Name,
            Description = "A signed-in human session. Account management requires Administrator."
        };
        document.Components.SecuritySchemes["Antiforgery"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "RequestVerificationToken",
            Description = "Required together with the session and antiforgery cookies on browser-session writes."
        };
        foreach (var (path, item) in document.Paths)
        foreach (var (method, operation) in item.Operations ?? [])
        {
            if (path.StartsWith("/api/v1/agent-management/", StringComparison.OrdinalIgnoreCase))
            {
                var session = new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("BrowserSession", document)] = [] };
                if (!method.ToString().Equals("Get", StringComparison.OrdinalIgnoreCase))
                    session[new OpenApiSecuritySchemeReference("Antiforgery", document)] = [];
                operation.Security = [session];
            }
            else if (path.Equals("/api/v1/agent/identity", StringComparison.OrdinalIgnoreCase) ||
                     path.Equals("/api/v1/agent/heartbeat", StringComparison.OrdinalIgnoreCase) ||
                     path.Equals("/api/v1/agent/queue-session", StringComparison.OrdinalIgnoreCase) ||
                     path.Equals("/api/v1/agent/settings", StringComparison.OrdinalIgnoreCase) ||
                     path.Equals("/api/v1/agent/queue-capacity", StringComparison.OrdinalIgnoreCase) ||
                     path.Equals("/api/v1/agent/batches", StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith("/api/v1/agent/rotation/", StringComparison.OrdinalIgnoreCase))
                operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("AgentBearer", document)] = [] }];
            else if (path.Equals("/api/v1/agent/recover", StringComparison.OrdinalIgnoreCase))
            {
                operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("AgentBearer", document)] = [] }];
                operation.Description = "Use a vlr_<enrollment ID>.<machine secret> bearer proof and the enrollment secret in the body. This only recovers an existing binding.";
            }
            else if (path.Equals("/api/v1/agent/enroll", StringComparison.OrdinalIgnoreCase))
                operation.Security = [];
            else if (path.Equals("/api/v1/access", StringComparison.OrdinalIgnoreCase))
                operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("ApiBearer", document)] = [] }];
            else if (path.Equals("/api/v1/devices", StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith("/api/v1/devices/", StringComparison.OrdinalIgnoreCase))
            {
                var isMutation = !method.ToString().Equals("Get", StringComparison.OrdinalIgnoreCase);
                var needsWrite = isMutation || path.EndsWith("/address-matches", StringComparison.OrdinalIgnoreCase);
                var session = new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("BrowserSession", document)] = [] };
                if (isMutation) session[new OpenApiSecuritySchemeReference("Antiforgery", document)] = [];
                operation.Security = [session, new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("ApiBearer", document)] = [] }];
                var authorization = needsWrite
                    ? "Requires an Administrator session or an API credential with monitoring.write."
                    : "Requires an Administrator or Read only session, or an API credential with monitoring.read.";
                operation.Description = string.IsNullOrWhiteSpace(operation.Description) ? authorization : $"{operation.Description}\n\n{authorization}";
            }
            else if (path.StartsWith("/api/v1/speed-tests", StringComparison.OrdinalIgnoreCase))
            {
                if (method.ToString().Equals("Get", StringComparison.OrdinalIgnoreCase))
                    operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("BrowserSession", document)] = [] },
                        new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("ApiBearer", document)] = [] }];
                else
                    operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("BrowserSession", document)] = [],
                        [new OpenApiSecuritySchemeReference("Antiforgery", document)] = [] }];
            }
            else if (path.Equals("/api/v1/operations/audit", StringComparison.OrdinalIgnoreCase) ||
                     path.Equals("/api/v1/operations/audit/ping", StringComparison.OrdinalIgnoreCase) ||
                     path.Equals("/api/v1/operations/audit/agent-replay", StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith("/api/v1/operations/audit-feed/", StringComparison.OrdinalIgnoreCase) ||
                     path.Equals("/api/v1/operations/platform-status", StringComparison.OrdinalIgnoreCase))
            {
                operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("BrowserSession", document)] = [] }];
                operation.Description = "Requires a signed-in Administrator session.";
            }
            else if (path.StartsWith("/api/v1/accounts", StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith("/api/v1/settings", StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith("/api/v1/session", StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith("/api/v1/workspace", StringComparison.OrdinalIgnoreCase))
            {
                var requirement = new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("BrowserSession", document)] = [] };
                if (!method.ToString().Equals("Get", StringComparison.OrdinalIgnoreCase))
                    requirement[new OpenApiSecuritySchemeReference("Antiforgery", document)] = [];
                operation.Security = [requirement];
            }
        }
        return Task.CompletedTask;
    }
}
