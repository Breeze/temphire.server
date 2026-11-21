using System.Security.Cryptography.X509Certificates;

namespace TempHire.Server.Auth
{
    /// <summary>
    /// The certificates OpenIddict signs and encrypts its tokens with, outside Development: PFX files
    /// named in configuration.
    ///
    /// <code>
    /// "OAuth": {
    ///   "SigningCertificate":    { "Path": "/certs/signing.pfx",    "Password": "..." },
    ///   "EncryptionCertificate": { "Path": "/certs/encryption.pfx", "Password": "..." }
    /// }
    /// </code>
    ///
    /// <para>
    /// In Development the server uses OpenIddict's development certificates instead, which it
    /// creates in the current user's certificate store.  Anywhere else there is no such store to rely
    /// on - a container has none that survives a restart - and the keys must be the same on every
    /// instance and after every restart, or tokens issued earlier stop validating.  Keep the files
    /// and passwords in a secret store; a container gets them as a mounted volume and environment
    /// variables (<c>OAuth__SigningCertificate__Path</c> and so on).
    /// </para>
    /// <para>
    /// The signing certificate's key needs the DigitalSignature usage, the encryption certificate's
    /// KeyEncipherment, as OpenIddict checks.  Self-signed certificates are fine: nobody else
    /// validates them.  Tokens are signed but not encrypted (see Program.cs), so the encryption
    /// certificate protects OpenIddict's other tokens, such as authorization codes.
    /// </para>
    /// </summary>
    public static class OAuthCertificates
    {
        public const string Signing = "SigningCertificate";
        public const string Encryption = "EncryptionCertificate";

        /// <summary>Loads the certificate named <paramref name="name"/> under <c>OAuth</c>.</summary>
        /// <exception cref="InvalidOperationException">The setting is missing, or the file can't be read.</exception>
        public static X509Certificate2 Load(IConfiguration configuration, string name)
        {
            var section = configuration.GetSection($"{OAuthSettings.SectionName}:{name}");
            var path = section["Path"];
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException(
                    $"{OAuthSettings.SectionName}:{name}:Path is not set.  Outside Development the server needs a PFX " +
                    $"file for OpenIddict's {(name == Signing ? "signing" : "encryption")} key; see OAuthCertificates.");
            }

            try
            {
                return X509CertificateLoader.LoadPkcs12FromFile(path, section["Password"]);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                throw new InvalidOperationException(
                    $"The certificate in {OAuthSettings.SectionName}:{name} ({path}) can't be read: {e.Message}", e);
            }
        }
    }
}
