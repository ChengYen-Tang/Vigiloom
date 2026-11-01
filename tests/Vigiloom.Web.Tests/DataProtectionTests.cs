using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class DataProtectionTests
{
    [TestMethod]
    public void KeyRing_PersistsAcrossHosts_IsolatesApplications_AndEncryptsKeys()
    {
        var directory = Directory.CreateTempSubdirectory("vigiloom-dp-test-");
        try
        {
            var keyPath = Path.Combine(directory.FullName, "keys");
            var certificatePath = Path.Combine(directory.FullName, "test.pfx");
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=Vigiloom Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx, "test-only-password"));
            WebApplicationFactory<Program> Factory(string name) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("Vigiloom:DataProtection:ApplicationName", name);
                builder.UseSetting("Vigiloom:DataProtection:KeyRingPath", keyPath);
                builder.UseSetting("Vigiloom:DataProtection:CertificatePath", certificatePath);
                builder.UseSetting("Vigiloom:DataProtection:CertificatePassword", "test-only-password");
            });
            string protectedValue;
            using (var first = Factory("test-installation"))
                protectedValue = first.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("payload");
            var keyFiles = Directory.GetFiles(keyPath, "*.xml");
            Assert.HasCount(1, keyFiles);
            var key = XDocument.Load(keyFiles[0]);
            Assert.IsTrue(key.Descendants().Any(element => element.Name.LocalName == "encryptedSecret"));
            using (var restarted = Factory("test-installation"))
                Assert.AreEqual("payload", restarted.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedValue));
            using (var other = Factory("other-installation"))
                Assert.Throws<CryptographicException>(() => other.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedValue));
        }
        finally
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(directory.FullName);
            if (!target.StartsWith(tempRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove a test key directory outside the temporary root.");
            directory.Delete(recursive: true);
        }
    }
}
