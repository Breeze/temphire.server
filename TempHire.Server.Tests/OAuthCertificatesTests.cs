using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using TempHire.Server.Auth;
using Xunit;

namespace TempHire.Server.Tests
{
    /// <summary>OpenIddict's certificates outside Development: PFX files named in configuration.</summary>
    public sealed class OAuthCertificatesTests : IDisposable
    {
        private readonly string _folder = Directory.CreateTempSubdirectory("temphire-certs-").FullName;

        public void Dispose() => Directory.Delete(_folder, recursive: true);

        [Fact]
        public void A_missing_setting_says_which_certificate_and_what_to_set()
        {
            var e = Assert.Throws<InvalidOperationException>(() => OAuthCertificates.Load(Config(), OAuthCertificates.Signing));

            Assert.Contains("OAuth:SigningCertificate:Path", e.Message);
            Assert.Contains("signing", e.Message);
        }

        [Fact]
        public void A_pfx_file_loads_with_its_private_key()
        {
            var path = Path.Combine(_folder, "signing.pfx");
            WriteCertificate(path, "secret", X509KeyUsageFlags.DigitalSignature);

            using var certificate = OAuthCertificates.Load(
                Config(("OAuth:SigningCertificate:Path", path), ("OAuth:SigningCertificate:Password", "secret")),
                OAuthCertificates.Signing);

            Assert.True(certificate.HasPrivateKey);
            Assert.Equal("CN=TempHire test", certificate.Subject);
        }

        [Fact]
        public void A_wrong_password_names_the_file()
        {
            var path = Path.Combine(_folder, "encryption.pfx");
            WriteCertificate(path, "secret", X509KeyUsageFlags.KeyEncipherment);

            var e = Assert.Throws<InvalidOperationException>(() => OAuthCertificates.Load(
                Config(("OAuth:EncryptionCertificate:Path", path), ("OAuth:EncryptionCertificate:Password", "wrong")),
                OAuthCertificates.Encryption));

            Assert.Contains(path, e.Message);
        }

        private static IConfiguration Config(params (string Key, string Value)[] values) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value)))
                .Build();

        private static void WriteCertificate(string path, string password, X509KeyUsageFlags usage)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=TempHire test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        }
    }
}
