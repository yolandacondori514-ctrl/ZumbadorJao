using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace ZumbidoJao;

internal static class TlsConfiguration
{
    private const string CertThumbprintVariable = "ZUMBIDOJAO_CERT_THUMBPRINT";
    private const string TrustedThumbprintsVariable = "ZUMBIDOJAO_TRUSTED_THUMBPRINTS";

    public static X509Certificate2 LoadLocalCertificate()
    {
        string thumbprint = Normalize(Environment.GetEnvironmentVariable(CertThumbprintVariable));
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            throw new InvalidOperationException($"No se ha configurado {CertThumbprintVariable}.");
        }

        using X509Store store = new(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        X509Certificate2Collection certificates = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            thumbprint,
            validOnly: false);

        X509Certificate2? certificate = certificates
            .Cast<X509Certificate2>()
            .FirstOrDefault(item => item.HasPrivateKey && item.NotAfter > DateTime.UtcNow);

        if (certificate is null)
        {
            throw new InvalidOperationException($"No se encontró un certificado privado válido con la huella configurada: {thumbprint}.");
        }

        return certificate;
    }

    public static RemoteCertificateValidationCallback ValidatePeerCertificate => ValidateCertificate;

    public static string GetShortFingerprint(X509Certificate? certificate)
    {
        if (certificate is null)
            return "desconocido";

        string thumbprint = Normalize(certificate.GetCertHashString());
        return thumbprint.Length > 8 ? thumbprint[^8..] : thumbprint;
    }

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CertThumbprintVariable)) &&
        GetTrustedThumbprints().Count > 0;

    private static bool ValidateCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (certificate is null)
            return false;

        string thumbprint = Normalize(certificate.GetCertHashString());
        return GetTrustedThumbprints().Contains(thumbprint, StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> GetTrustedThumbprints()
    {
        string value = Environment.GetEnvironmentVariable(TrustedThumbprintsVariable) ?? string.Empty;
        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(item => item.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Replace(" ", string.Empty).Replace(":", string.Empty).Trim().ToUpperInvariant();
}
