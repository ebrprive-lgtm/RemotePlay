using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemotePlay;

internal sealed partial class WebServer
{
    private static void EnsureHttpsBinding(int port)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("HTTPS mode requires Windows HTTP.sys certificate binding.");

        using var certificate = GetOrCreateHttpsCertificate();
        var ipPort = $"0.0.0.0:{port}";
        var deleteResult = RunNetsh($"http delete sslcert ipport={ipPort}");
        if (deleteResult.ExitCode != 0 && !deleteResult.Output.Contains("The system cannot find the file specified", StringComparison.OrdinalIgnoreCase))
            Logger.Info($"Existing HTTPS binding delete returned {deleteResult.ExitCode}: {deleteResult.Output.Trim()}");

        var addResult = RunNetsh($"http add sslcert ipport={ipPort} certhash={certificate.Thumbprint} appid={{{HttpsCertificateAppId}}}");
        if (addResult.ExitCode != 0)
            throw new InvalidOperationException("Could not configure HTTPS certificate binding. Run RemotePlay as administrator once, then enable HTTPS again. " + addResult.Output.Trim());
    }

    private static X509Certificate2 GetOrCreateHttpsCertificate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);

        var existing = store.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, HttpsCertificateSubject, validOnly: false)
            .OfType<X509Certificate2>()
            .Where(c => c.NotAfter > DateTimeOffset.Now.AddDays(30) && c.HasPrivateKey)
            .OrderByDescending(c => c.NotAfter)
            .FirstOrDefault();

        if (existing is not null)
            return existing;

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(HttpsCertificateSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false));
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        foreach (var address in GetLocalIpAddresses())
            sanBuilder.AddIpAddress(address);
        request.CertificateExtensions.Add(sanBuilder.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
        var exportableCertificate = new X509Certificate2(
            certificate.Export(X509ContentType.Pfx),
            string.Empty,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
        store.Add(exportableCertificate);

        var created = store.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, HttpsCertificateSubject, validOnly: false)
            .OfType<X509Certificate2>()
            .Where(c => c.HasPrivateKey)
            .OrderByDescending(c => c.NotAfter)
            .First();

        Logger.Info($"Created RemotePlay HTTPS certificate: {created.Thumbprint}");
        return created;
    }

    internal static X509Certificate2? TryGetHttpsCertificate()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates
                .Find(X509FindType.FindBySubjectDistinguishedName, HttpsCertificateSubject, validOnly: false)
                .OfType<X509Certificate2>()
                .OrderByDescending(c => c.NotAfter)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.Error("Could not read RemotePlay HTTPS certificate", ex);
            return null;
        }
    }

    private static IEnumerable<IPAddress> GetLocalIpAddresses()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => (a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) && !IPAddress.IsLoopback(a))
                .ToArray();
        }
        catch (Exception ex)
        {
            Logger.Error("Could not add local IP addresses to HTTPS certificate", ex);
            return [];
        }
    }

    private static (int ExitCode, string Output) RunNetsh(string arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start netsh.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(5000);
        return (process.ExitCode, output);
    }
}
