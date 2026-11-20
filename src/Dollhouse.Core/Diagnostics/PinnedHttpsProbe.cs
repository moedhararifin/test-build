using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Dollhouse.Core.Diagnostics;

public static class PinnedHttpsProbe
{
    public static SocketsHttpHandler CreateHandler(X509Certificate2 trustedCertificate, string? expectedPin = null)
    {
        expectedPin ??= Security.LocalCertificate.Fingerprint(trustedCertificate);
        return new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    ValidateCertificate(certificate, trustedCertificate, expectedPin, errors)
            }
        };
    }

    public static bool ValidateCertificate(X509Certificate? certificate, X509Certificate2 trusted,
        string expectedPin, SslPolicyErrors errors)
    {
        if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNotAvailable
            | SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
            return false;
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var actual = SHA256.HashData(leaf.RawData);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedPin)))
            return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trusted);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        return chain.Build(leaf);
    }

    public static async Task<object> RunAsync(WorkspacePaths paths, int port = 7900, CancellationToken cancellationToken = default)
    {
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(paths.Certificate);
        using var client = new HttpClient(CreateHandler(certificate)) { Timeout = TimeSpan.FromSeconds(10) };
        var uri = new Uri($"https://127.0.0.1:{port}/__echo_probe?source=diagnostic");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Version = HttpVersion.Version11,
            Content = new StringContent("{\"hello\":\"local\"}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Echo-Probe", "true");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (payload.RootElement.GetProperty("body_text").GetString() != "{\"hello\":\"local\"}")
            throw new InvalidOperationException("Unexpected echo response.");
        return new
        {
            Url = $"https://127.0.0.1:{port}",
            CertificateSha256 = Security.LocalCertificate.Fingerprint(certificate),
            PinVerifiedBeforeHttp = true,
            Status = (int)response.StatusCode,
            Echo = payload.RootElement.Clone()
        };
    }
}
