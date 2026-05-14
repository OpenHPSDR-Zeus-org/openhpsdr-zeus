using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Zeus.Server;

// Self-signed certificate for serving Zeus over HTTPS on the LAN. Browsers
// require a secure context (HTTPS or localhost) before they hand over
// getUserMedia — without that, the Zeus mobile shell on a phone can never
// open the mic. Localhost is exempt for the dev box itself; everywhere else
// the certificate signed here is what gets the operator past the secure-
// context gate.
//
// Trust is on the operator: every device that visits will see a "Not secure"
// warning the first time and has to tap through it. That's expected for a
// self-signed LAN cert. Once accepted, getUserMedia works and the rest of
// the Zeus stack treats the origin like any other HTTPS one.
//
// The cert is regenerated when the machine's LAN IPs change so the SAN list
// stays in sync with reality (laptop moved between networks, router
// reassigned a new prefix, etc.). Anything else is cached at
// {LocalAppData}/Zeus/certs/zeus-lan.pfx so a restart isn't a fresh Trust-
// On-First-Use moment.
public static class LanCertificate
{
    private const string CertFileName = "zeus-lan.pfx";
    private const string SanExtensionOid = "2.5.29.17";
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private static readonly TimeSpan Validity = TimeSpan.FromDays(1825); // 5 years

    public static X509Certificate2 GetOrCreate(ILogger? log = null)
    {
        var path = ResolveCertPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var ips = GetLanIps().ToHashSet();

        if (File.Exists(path))
        {
            try
            {
                // PersistKeySet would route the private key through the
                // macOS login keychain (X509MoveToKeychain), which fails with
                // "User interaction is not allowed" when the backend is
                // launched from a non-GUI parent (CI, SSH, Claude Code's
                // PTY). The PFX file on disk already handles persistence
                // between runs; keychain persistence is redundant.
                var existing = X509CertificateLoader.LoadPkcs12FromFile(
                    path,
                    string.Empty,
                    X509KeyStorageFlags.Exportable);
                if (CoversAllIps(existing, ips) && existing.NotAfter > DateTime.UtcNow.AddDays(30))
                {
                    log?.LogInformation("LAN certificate loaded from {Path} ({Subject}, expires {Expires:yyyy-MM-dd})",
                        path, existing.Subject, existing.NotAfter);
                    return existing;
                }
                log?.LogInformation("LAN certificate regenerating: SAN list out of date or near expiry");
                existing.Dispose();
            }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "Existing LAN certificate at {Path} failed to load — regenerating", path);
            }
        }

        var fresh = Generate(ips);
        File.WriteAllBytes(path, fresh.Export(X509ContentType.Pfx, string.Empty));
        log?.LogInformation("LAN certificate generated at {Path} for {IpCount} IP(s): {Ips}",
            path, ips.Count, string.Join(", ", ips));
        return fresh;
    }

    public static int GetHttpsPort()
        => int.TryParse(Environment.GetEnvironmentVariable("ZEUS_HTTPS_PORT"), out var p) ? p : 6443;

    public static IReadOnlyList<IPAddress> GetLanIps()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(i => i.OperationalStatus == OperationalStatus.Up &&
                        i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(i => i.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address)
            .Distinct()
            .ToArray();
    }

    private static X509Certificate2 Generate(HashSet<IPAddress> ips)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN=Zeus on {Environment.MachineName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        if (!string.IsNullOrEmpty(Environment.MachineName))
            san.AddDnsName(Environment.MachineName);
        san.AddDnsName("zeus.local");
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in ips) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid(ServerAuthOid) }, false));

        var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.Add(Validity));
        // Round-trip via PFX so the returned cert owns an exportable private
        // key — needed when we serialise it to disk for the next run.
        var pfx = cert.Export(X509ContentType.Pfx, string.Empty);
        return X509CertificateLoader.LoadPkcs12(
            pfx,
            string.Empty,
            X509KeyStorageFlags.Exportable);
    }

    private static bool CoversAllIps(X509Certificate2 cert, HashSet<IPAddress> required)
    {
        var sanExt = cert.Extensions.FirstOrDefault(e => e.Oid?.Value == SanExtensionOid);
        if (sanExt is null) return false;
        // Format(false) returns one entry per line, e.g.
        //   "IP Address=192.168.1.5"
        //   "DNS Name=localhost"
        // Substring match against the IP literal is good enough — false
        // positives would need an IP literal to appear inside an unrelated
        // SAN entry, which the SAN format doesn't allow.
        var rendered = sanExt.Format(false);
        foreach (var ip in required)
        {
            if (!rendered.Contains(ip.ToString(), StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static string ResolveCertPath()
    {
        var dir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(dir, "Zeus", "certs", CertFileName);
    }
}
