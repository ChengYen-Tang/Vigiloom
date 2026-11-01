using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.WebAssembly.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class IdentityConfigurationTests
{
    [TestMethod]
    public async Task Policies_EnforceRoleBoundaries()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        var authorization = factory.Services.GetRequiredService<IAuthorizationService>();
        static ClaimsPrincipal Principal(string? role) => role is null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));
        Assert.IsTrue((await authorization.AuthorizeAsync(Principal(BuiltInRoles.Administrator), AccessPolicies.Administrator)).Succeeded);
        Assert.IsFalse((await authorization.AuthorizeAsync(Principal(BuiltInRoles.ReadOnly), AccessPolicies.Administrator)).Succeeded);
        Assert.IsTrue((await authorization.AuthorizeAsync(Principal(BuiltInRoles.ReadOnly), AccessPolicies.MonitoringRead)).Succeeded);
        Assert.IsFalse((await authorization.AuthorizeAsync(Principal(null), AccessPolicies.MonitoringRead)).Succeeded);
    }

    [TestMethod]
    public void Identity_UsesBuiltInStoresAndRestrictiveCookieSettings()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var authentication = services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        Assert.AreEqual(IdentityConstants.ApplicationScheme, authentication.DefaultScheme);
        Assert.IsNotNull(services.GetRequiredService<UserManager<Account>>());
        Assert.IsNotNull(services.GetRequiredService<RoleManager<Role>>());
        var cookie = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        Assert.IsTrue(cookie.Cookie.HttpOnly);
        Assert.AreEqual(Microsoft.AspNetCore.Http.CookieSecurePolicy.Always, cookie.Cookie.SecurePolicy);
        Assert.AreEqual(Microsoft.AspNetCore.Http.SameSiteMode.Lax, cookie.Cookie.SameSite);
        Assert.AreEqual(TimeSpan.FromHours(8), cookie.ExpireTimeSpan);
        Assert.AreEqual(TimeSpan.Zero, services.GetRequiredService<IOptions<SecurityStampValidatorOptions>>().Value.ValidationInterval);
        Assert.IsFalse(services.GetRequiredService<IOptions<AuthenticationStateSerializationOptions>>().Value.SerializeAllClaims);
    }
}
