// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Zeus.Server;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// Connection snapshot from <see cref="RadioService"/>: connected flag,
/// endpoint (the builder's central redaction scrubs the IP), protocol
/// (Protocol 1 vs 2), connection status, and sample rate. Strictly read-only —
/// reads <c>IsConnected</c>, <c>ActiveClient</c>, and the cached
/// <c>Snapshot()</c> StateDto; never connects, disconnects, or mutates.
/// </summary>
public sealed class ConnectionProbe : IDiagnosticProbe
{
    public string Id => "connection";

    public DiagnosticSection Collect(DiagnosticContext ctx)
    {
        var items = new List<DiagnosticKeyValue>();
        try
        {
            var radio = ctx.Services.GetService<RadioService>();
            if (radio is null)
            {
                items.Add(new("status", "unavailable"));
                return new DiagnosticSection(Id, "Connection", items);
            }

            var connected = radio.IsConnected;
            items.Add(new("connected", connected ? "true" : "false"));

            // Protocol: a live Protocol-1 client means P1; connected with no
            // P1 client means a Protocol-2 (ANAN / Orion) backend is active.
            string protocol;
            if (radio.ActiveClient is not null)
                protocol = "Protocol 1";
            else if (connected)
                protocol = "Protocol 2";
            else
                protocol = "none";
            items.Add(new("protocol", protocol));

            var state = radio.Snapshot();
            items.Add(new("status", state.Status.ToString()));
            // Endpoint may contain an IP/host — the builder redacts it centrally.
            items.Add(new("endpoint", string.IsNullOrWhiteSpace(state.Endpoint) ? "none" : state.Endpoint!));
            items.Add(new("sample.rate.hz", state.SampleRate.ToString()));

            // Firmware / gateware version: captured at connect from the
            // discovery reply (P1 code-version byte / P2 raw[13]+beta). Lets a
            // "Report a problem" snapshot pin board-specific firmware (e.g. an
            // ANAN-10E on its Protocol-2 beta — issue #1053). "unknown" when
            // connected via a path that skipped discovery (forced/reclaim
            // connect); "n/a (disconnected)" when nothing is connected.
            items.Add(new("firmware.version",
                connected ? (radio.ConnectedFirmware ?? "unknown") : "n/a (disconnected)"));
        }
        catch (Exception ex)
        {
            items.Add(new("status", "error"));
            items.Add(new("error", ex.GetType().Name));
        }

        return new DiagnosticSection(Id, "Connection", items);
    }
}
