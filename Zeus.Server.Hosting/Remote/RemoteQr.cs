using QRCoder;

namespace Zeus.Server.Hosting.Remote;

/// <summary>
/// Builds the operator's canonical remote address and renders it as a scannable
/// QR code for the Server menu (ADR-0006/0007). The QR encodes the
/// <c>/go/&lt;callsign&gt;</c> URL so a phone camera opens the remote client
/// directly — no typing a URL.
/// </summary>
public static class RemoteQr
{
    public const string BrokerOrigin = "https://openhpsdrzeus.com";

    /// <summary>
    /// Canonical remote address for a callsign, e.g.
    /// <c>https://openhpsdrzeus.com/go/EI6LF</c>. The callsign is uppercased and
    /// URL-escaped so portable/special calls (<c>EI6LF/P</c>) stay a single path
    /// segment. Returns null when no callsign is available.
    /// </summary>
    public static string? AddressFor(string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign)) return null;
        var normalized = callsign.Trim().ToUpperInvariant();
        return $"{BrokerOrigin}/go/{Uri.EscapeDataString(normalized)}";
    }

    /// <summary>
    /// Render text (a URL) to a standalone SVG QR code. SVG keeps it crisp at any
    /// size and needs no raster/System.Drawing dependency (cross-platform).
    /// </summary>
    public static string Svg(string data, int pixelsPerModule = 6)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(data);
        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(data, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(qrData).GetGraphic(pixelsPerModule);
    }
}
