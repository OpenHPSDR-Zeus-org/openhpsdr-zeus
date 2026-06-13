// SPDX-License-Identifier: GPL-2.0-or-later
//
// PluginQrzLookup — host adapter that surfaces the core QrzService to plugins
// via the IQrzLookup contract. Maps the internal Zeus.Contracts.QrzStation to
// the SDK-stable Zeus.Plugins.Contracts.QrzLookupResult so the plugin contract
// never depends on the app wire format. Plugins reach this through
// IPluginContext.Qrz (gated on the NetworkAccess capability in PluginManager),
// reusing the operator's stored QRZ credentials + the QrzService rate-limit
// gate — no second login, no key handling in plugin code.

using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;

namespace Zeus.Server;

public sealed class PluginQrzLookup : IQrzLookup
{
    private readonly QrzService _qrz;
    private readonly ILogger<PluginQrzLookup> _log;

    public PluginQrzLookup(QrzService qrz, ILogger<PluginQrzLookup> log)
    {
        _qrz = qrz;
        _log = log;
    }

    public async Task<QrzLookupResult?> LookupAsync(string callsign, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(callsign)) return null;
        try
        {
            var s = await _qrz.LookupAsync(callsign, ct).ConfigureAwait(false);
            if (s is null) return null;
            return new QrzLookupResult(
                s.Callsign, s.Name, s.FirstName, s.Country,
                s.State, s.City, s.Grid, s.Lat, s.Lon, s.Dxcc);
        }
        catch (OperationCanceledException)
        {
            throw; // honour caller cancellation; not a lookup failure
        }
        catch (Exception ex)
        {
            // IQrzLookup contract: never throw on a failed lookup — return null.
            _log.LogDebug(ex, "plugin QRZ lookup failed for {Call}", callsign);
            return null;
        }
    }
}
