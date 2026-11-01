using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Security;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.IdentityAccess.Recovery;

namespace Vigiloom.Migrator.Recovery;

internal static partial class LocalRecoveryCommand
{
    public static async Task RunAsync(string userName)
    {
        if (!HasLocalAdministratorPrivileges())
            throw new UnauthorizedAccessException("Run recovery with local administrator privileges.");
        if (Console.IsOutputRedirected)
            throw new InvalidOperationException("Recovery must be run in an interactive terminal; do not redirect its secret output.");
        var connection = Environment.GetEnvironmentVariable("VIGILOOM_MIGRATION_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Set VIGILOOM_MIGRATION_SQL_CONNECTION to the existing target database.");
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVigiloomDataProtection(configuration);
        services.AddModuleSqlServer<IdentityAccessDbContext>(connection, "identity");
        services.AddIdentityCore<Account>().AddRoles<Role>().AddEntityFrameworkStores<IdentityAccessDbContext>().AddDefaultTokenProviders();
        services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromMinutes(15));
        services.AddScoped<AdministratorRecovery>();
        using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var code = await scope.ServiceProvider.GetRequiredService<AdministratorRecovery>().IssueCodeAsync(userName);
        if (code is null) throw new InvalidOperationException("The specified administrator account was not found.");
        Console.WriteLine("Use this one-time code on the Vigiloom password setup page within 15 minutes:");
        Console.WriteLine(code);
    }

    private static bool HasLocalAdministratorPrivileges()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        return OperatingSystem.IsLinux() && GetEffectiveUserId() == 0;
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();
}
