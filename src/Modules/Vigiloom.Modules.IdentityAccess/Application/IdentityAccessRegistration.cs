using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Modules.IdentityAccess.Application;

public static class IdentityAccessRegistration
{
    public static IServiceCollection AddIdentityAccessOperations(this IServiceCollection services)
    {
        services.AddScoped<InitialAdministrator>();
        services.AddScoped<AccountAdministration>();
        services.AddScoped<AccountPasswordReset>();
        services.AddScoped<SelfPasswordChange>();
        services.AddScoped<TwoFactorManagement>();
        services.AddScoped<ApiCredentials>();
        services.AddScoped<ApiCredentialVerifier>();
        services.AddScoped<AccountSessionRevocation>();
        services.AddIdentityCore<Account>(options =>
        {
            options.Password.RequiredLength = 12;
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        }).AddRoles<Role>()
          .AddEntityFrameworkStores<IdentityAccessDbContext>()
          .AddDefaultTokenProviders();
        services.AddScoped<IUserStore<Account>, ProtectedUserStore>();
        services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromMinutes(15));
        return services;
    }
}
