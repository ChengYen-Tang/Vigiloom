using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;

namespace Vigiloom.Infrastructure.Security;

public static class DataProtectionRegistration
{
    public static IServiceCollection AddVigiloomDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var applicationName = configuration["Vigiloom:DataProtection:ApplicationName"] ?? "Vigiloom";
        var builder = services.AddDataProtection().SetApplicationName(applicationName);
        var keyPath = configuration["Vigiloom:DataProtection:KeyRingPath"];
        if (!string.IsNullOrWhiteSpace(keyPath)) builder.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        var certificatePath = configuration["Vigiloom:DataProtection:CertificatePath"];
        if (!string.IsNullOrWhiteSpace(certificatePath))
        {
            var password = configuration["Vigiloom:DataProtection:CertificatePassword"];
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, password, X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey) throw new InvalidOperationException("The data protection certificate must include a private key.");
            builder.ProtectKeysWithCertificate(certificate);
        }
        return services;
    }
}
