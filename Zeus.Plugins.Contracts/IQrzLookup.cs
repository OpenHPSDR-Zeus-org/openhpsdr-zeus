namespace Zeus.Plugins.Contracts;

/// <summary>
/// Host-mediated QRZ.com callsign lookup, granted via
/// <see cref="PluginCapabilities.NetworkAccess"/>. The host performs the
/// lookup using the OPERATOR'S OWN stored QRZ credentials, session, and shared
/// rate-limit gate — so a plugin never asks the user for QRZ credentials a
/// second time, never stores keys, and can't stampede QRZ. Surfaced as
/// <see cref="IPluginContext.Qrz"/>; null there when the capability wasn't
/// granted or the host has no QRZ subscription configured.
/// </summary>
public interface IQrzLookup
{
    /// <summary>
    /// Resolve a callsign against QRZ. Returns null if the callsign isn't
    /// found, QRZ is unconfigured / unreachable, or the lookup failed — this
    /// method NEVER throws. Subject to the host's shared QRZ rate limit.
    /// </summary>
    Task<QrzLookupResult?> LookupAsync(string callsign, CancellationToken ct = default);
}

/// <summary>
/// A resolved QRZ station record — an SDK-stable subset of the host's internal
/// QRZ model, mapped by the host adapter so the plugin contract stays decoupled
/// from the app's wire format.
/// </summary>
public sealed record QrzLookupResult(
    string Callsign,
    string? Name,
    string? FirstName,
    string? Country,
    string? State,
    string? City,
    string? Grid,
    double? Lat,
    double? Lon,
    int? Dxcc);
