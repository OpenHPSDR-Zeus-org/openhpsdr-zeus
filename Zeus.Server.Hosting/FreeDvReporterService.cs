// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// FreeDvReporterService — keeps a live mirror of the FreeDV Reporter stations
// network (qso.freedv.org) for the Stations panel (GET /api/freedv/stations).
// It holds a persistent Socket.IO connection (see SocketIoReporterClient) and
// folds the event stream into an in-memory station table keyed by the
// reporter's per-connection session id (sid).
//
// By default Zeus connects read-only ("view" role) — outbound TLS WebSocket in,
// station snapshot out, NOTHING on the radio / DSP / TX path. When the operator
// opts into report mode (FreeDvReporterSettingsStore, default OFF) AND a
// callsign + grid are present, Zeus instead connects in "report" role and
// publishes its own freq / TX / message events to the public map. The radio
// seams are read-only subscriptions (StateChanged / MoxChanged) whose handlers
// only ever EMIT to the reporter socket — they never call back into the radio,
// and every emit is wrapped so a reporter fault can't disturb TX.
//
// Same shape as ActivationSpotsService — a self-contained BackgroundService
// registered as a singleton + hosted service — but driven by a streaming
// Socket.IO link instead of HTTP polling.

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Server.FreeDvReporter;

namespace Zeus.Server;

/// <summary>
/// Background service that mirrors the FreeDV Reporter stations feed and exposes
/// the current snapshot via <see cref="GetSnapshot"/>. Registered as a singleton
/// + hosted service in <c>ZeusHost</c>.
/// </summary>
public sealed class FreeDvReporterService : BackgroundService
{
    private const int ReconnectDelaySeconds = 5;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    // Coalesce freq_change emits so spinning the VFO doesn't flood the socket.
    private static readonly TimeSpan FreqEmitInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILogger<FreeDvReporterService> _log;
    private readonly ConcurrentDictionary<string, Station> _stations = new();
    private readonly SocketIoReporterClient _client;

    private readonly RadioService _radio;
    private readonly QrzService _qrz;
    private readonly FreeDvReporterSettingsStore _settingsStore;
    private readonly FreeDvService _freeDv;

    // Last values we actually emitted in report role, so a no-op change (e.g. a
    // StateChanged that didn't move the VFO or mode) doesn't re-emit.
    private long _emittedFreqHz;
    private string _emittedMode = "";
    private DateTime _lastFreqEmitUtc = DateTime.MinValue;
    // Latest PTT state, tracked off MoxChanged. StateDto carries no MOX flag
    // (TX state is session-only), so we keep our own copy to pair with a
    // mode-change tx_report.
    private volatile bool _transmitting;

    public FreeDvReporterService(
        ILogger<FreeDvReporterService> log,
        RadioService radio,
        QrzService qrz,
        FreeDvReporterSettingsStore settingsStore,
        FreeDvService freeDv)
    {
        _log = log;
        _radio = radio;
        _qrz = qrz;
        _settingsStore = settingsStore;
        _freeDv = freeDv;
        _client = new SocketIoReporterClient(HandleEvent, ResolveIdentity, log);
    }

    public override Task StartAsync(CancellationToken ct)
    {
        // Subscribe once; unsubscribed in Dispose. Handlers only ever emit to the
        // reporter socket (never call back into the radio) and swallow their own
        // errors, so the radio path is unaffected by reporter state.
        _radio.StateChanged += OnRadioStateChanged;
        _radio.MoxChanged += OnRadioMoxChanged;
        return base.StartAsync(ct);
    }

    public override void Dispose()
    {
        _radio.StateChanged -= OnRadioStateChanged;
        _radio.MoxChanged -= OnRadioMoxChanged;
        // _settingsStore is a DI singleton — its lifetime is owned by the
        // container, not this service; do not dispose it here.
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        bool reconnect = false;
        while (!ct.IsCancellationRequested)
        {
            // Drop any stale list so a fresh (re)connect starts clean — the
            // reporter resends the full roster via bulk_update on connect.
            _stations.Clear();

            // Seed the report-state cache from the live radio so the first
            // freq_change / tx_report we publish on connect reflects reality
            // rather than zeros (no-op in view role).
            SeedReportStateFromRadio();

            // Watch for the handshake to complete so we can publish the operator's
            // initial freq/mode/message once in report role. Runs alongside the
            // receive loop and exits when the loop returns.
            using var connectedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var republishTask = RepublishOnConnectAsync(connectedCts.Token);

            try
            {
                await _client.RunLoopAsync(ct, reconnect).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "FreeDV Reporter link failed; reconnecting in {Delay}s", ReconnectDelaySeconds);
            }
            finally
            {
                connectedCts.Cancel();
                try { await republishTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected on loop exit */ }
            }

            reconnect = true;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(ReconnectDelaySeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // Waits until the link reports Connected (or the token cancels) and, in
    // report role, publishes the operator's initial freq/mode/message once.
    private async Task RepublishOnConnectAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_client.Reporting)
                {
                    _client.RepublishState();
                    return;
                }
                if (_client.ConnectionState == "Connected")
                    return; // connected in view role — nothing to publish
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* loop exited */ }
    }

    // Resolves the CONNECT identity at handshake time from current settings,
    // falling back to the QRZ home station for blank callsign/grid. Returns a
    // "view" identity unless report mode is enabled AND a callsign + grid exist.
    private ReporterIdentity ResolveIdentity()
    {
        var settings = _settingsStore.Get();
        var (call, grid) = ResolveOperator(settings);

        if (!settings.ReportEnabled || string.IsNullOrWhiteSpace(call) || string.IsNullOrWhiteSpace(grid))
            return new ReporterIdentity("view", "", "", "", "");

        return new ReporterIdentity(
            Role: "report",
            Callsign: call,
            GridSquare: grid,
            Version: "Zeus",
            Os: OsLabel());
    }

    // Operator callsign/grid: settings first, QRZ home station as a fallback for
    // blank fields. Returns ("","") when neither source has them.
    private (string Call, string Grid) ResolveOperator(FreeDvReporterSettings settings)
    {
        var call = settings.Callsign;
        var grid = settings.GridSquare;
        if (string.IsNullOrWhiteSpace(call) || string.IsNullOrWhiteSpace(grid))
        {
            var home = _qrz.GetStatus().Home;
            if (home is not null)
            {
                if (string.IsNullOrWhiteSpace(call) && !string.IsNullOrWhiteSpace(home.Callsign))
                    call = home.Callsign.Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(grid) && !string.IsNullOrWhiteSpace(home.Grid))
                {
                    var g = home.Grid!.Trim();
                    grid = g.Length > 6 ? g[..6] : g;
                }
            }
        }
        return (call ?? "", grid ?? "");
    }

    private static string OsLabel()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macOS";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "Linux";
        return "Unknown";
    }

    // ----- public API for the endpoints -----

    /// <summary>Current report-mode settings (callsign/grid/message + enable flag).</summary>
    public FreeDvReporterSettings GetSettings() => _settingsStore.Get();

    /// <summary>
    /// Persist new report-mode settings and force a reconnect so the new role /
    /// identity takes effect on the next handshake. Returns the saved (normalized)
    /// settings.
    /// </summary>
    public FreeDvReporterSettings SaveSettings(FreeDvReporterSettings settings)
    {
        var saved = _settingsStore.Set(settings);
        // Push the (possibly changed) status message into the client cache so a
        // republish carries it; the force-reconnect re-handshakes with the new role.
        _client.EmitMessageUpdate(saved.Message);
        _client.ForceReconnect();
        return saved;
    }

    /// <summary>
    /// Ask the station identified by <paramref name="destSid"/> to QSY to the
    /// operator's current VFO frequency. No-op (returns false) unless we are
    /// connected in report role and the sid is known.
    /// </summary>
    public bool RequestQsy(string destSid)
    {
        if (!_client.Reporting) return false;
        if (string.IsNullOrWhiteSpace(destSid) || !_stations.ContainsKey(destSid)) return false;
        long freqHz = _radio.Snapshot().VfoHz;
        if (freqHz <= 0) return false;
        try
        {
            _client.EmitQsy(destSid, freqHz, GetSettings().Message);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "FreeDV Reporter: qsy_request to {Sid} failed", destSid);
            return false;
        }
    }

    // ----- radio seams (report role only; never throw, never touch the radio) -----

    private void OnRadioStateChanged(StateDto state)
    {
        try
        {
            if (!_client.Reporting) return;

            long freqHz = state.VfoHz;
            if (freqHz != _emittedFreqHz)
            {
                // Coalesce so spinning the VFO doesn't flood the socket: at most
                // one emit per FreqEmitInterval. The latest frequency wins.
                var now = DateTime.UtcNow;
                if (now - _lastFreqEmitUtc >= FreqEmitInterval)
                {
                    _emittedFreqHz = freqHz;
                    _lastFreqEmitUtc = now;
                    _client.EmitFreqChange(freqHz);
                }
            }

            string mode = ReportModeLabel(state.Mode);
            if (!string.Equals(mode, _emittedMode, StringComparison.Ordinal))
            {
                _emittedMode = mode;
                // transmitting tracked via MoxChanged; report current TX state.
                _client.EmitTxReport(mode, _transmitting);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "FreeDV Reporter: state-change emit failed");
        }
    }

    private void OnRadioMoxChanged(bool transmitting)
    {
        _transmitting = transmitting;
        try
        {
            if (!_client.Reporting) return;
            string mode = _emittedMode.Length > 0 ? _emittedMode : ReportModeLabel(_radio.Snapshot().Mode);
            _client.EmitTxReport(mode, transmitting);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "FreeDV Reporter: mox emit failed");
        }
    }

    // Seeds the client's cached report state from the live radio before connect.
    private void SeedReportStateFromRadio()
    {
        try
        {
            var snap = _radio.Snapshot();
            _emittedFreqHz = snap.VfoHz;
            _emittedMode = ReportModeLabel(snap.Mode);
            _client.EmitFreqChange(snap.VfoHz);                       // caches only (not Reporting yet)
            _client.EmitTxReport(_emittedMode, transmitting: false);  // caches only
            _client.EmitMessageUpdate(_settingsStore.Get().Message);  // caches only
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "FreeDV Reporter: seed report state failed");
        }
    }

    // Maps the radio's current mode to the FreeDV Reporter mode label. In FreeDV
    // mode we report the active submode (700D / RADEV1 / …); otherwise the plain
    // RxMode name (USB, LSB, …) so the operator still appears with a sensible mode.
    private string ReportModeLabel(RxMode mode)
    {
        if (mode != RxMode.FreeDv)
            return mode.ToString().ToUpperInvariant();
        return _freeDv.Status().Submode switch
        {
            FreeDvSubmode.Mode700C => "700C",
            FreeDvSubmode.Mode700D => "700D",
            FreeDvSubmode.Mode700E => "700E",
            FreeDvSubmode.Mode1600 => "1600",
            FreeDvSubmode.Mode800XA => "800XA",
            FreeDvSubmode.RadeV1 => "RADEV1",
            _ => "700D",
        };
    }

    /// <summary>
    /// Current stations snapshot: connection state, enabled flag, and the live
    /// station list sorted by frequency ascending (stations that never reported
    /// a frequency are omitted).
    /// </summary>
    public FreeDvStationsResponseDto GetSnapshot()
    {
        var cutoff = DateTime.UtcNow - StaleAfter;
        var list = new List<FreeDvStationDto>(_stations.Count);
        foreach (var s in _stations.Values)
        {
            // Defensive stale-prune — the server normally sends remove_connection,
            // but a dropped disconnect shouldn't leave a ghost forever.
            if (s.LastUpdate < cutoff)
            {
                _stations.TryRemove(s.Sid, out _);
                continue;
            }
            if (s.FreqHz <= 0) continue; // never reported a usable frequency

            list.Add(new FreeDvStationDto(
                Sid: s.Sid,
                Callsign: s.Callsign ?? "",
                GridSquare: s.GridSquare,
                FreqHz: s.FreqHz,
                Mode: s.Mode ?? "",
                Transmitting: s.Transmitting,
                RxOnly: s.RxOnly,
                Message: s.Message,
                Version: s.Version,
                LastRxSnr: s.LastRxSnr is double snr && !double.IsNaN(snr) ? snr : null,
                LastRxCallsign: s.LastRxCallsign,
                LastRxMode: s.LastRxMode,
                LastUpdate: s.LastUpdate.ToString("o", CultureInfo.InvariantCulture),
                ConnectTime: s.ConnectTime?.ToString("o", CultureInfo.InvariantCulture)));
        }

        list.Sort((a, b) => a.FreqHz.CompareTo(b.FreqHz));
        return new FreeDvStationsResponseDto(
            _client.ConnectionState,
            Enabled: true,
            list,
            Reporting: _client.Reporting,
            MySid: _client.MySid);
    }

    // ----- Socket.IO event handling -----

    // The single Action handed to SocketIoReporterClient. Dispatches by event
    // name; bulk_update recurses each contained [name, payload] pair through here.
    private void HandleEvent(string name, JsonElement payload)
    {
        switch (name)
        {
            case "bulk_update":
                // Initial snapshot: a JSON array of [eventName, payload] pairs.
                if (payload.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pair in payload.EnumerateArray())
                    {
                        if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() == 0)
                            continue;
                        string? innerName = pair[0].ValueKind == JsonValueKind.String ? pair[0].GetString() : null;
                        if (string.IsNullOrEmpty(innerName)) continue;
                        JsonElement innerPayload = pair.GetArrayLength() > 1 ? pair[1] : default;
                        HandleEvent(innerName!, innerPayload);
                    }
                }
                break;

            case "new_connection":
            case "freq_change":
            case "tx_report":
            case "message_update":
                UpsertStation(payload, isRxReport: false);
                break;

            case "rx_report":
                UpsertStation(payload, isRxReport: true);
                break;

            case "remove_connection":
                {
                    string? sid = GetString(payload, "sid");
                    if (!string.IsNullOrEmpty(sid))
                        _stations.TryRemove(sid!, out _);
                }
                break;

            case "connection_successful":
                _log.LogDebug("FreeDV Reporter: connection_successful");
                break;

            default:
                // qsy_request and any other events are not consumed here.
                break;
        }
    }

    private void UpsertStation(JsonElement p, bool isRxReport)
    {
        if (p.ValueKind != JsonValueKind.Object) return;
        string? sid = GetString(p, "sid");
        if (string.IsNullOrEmpty(sid)) return;

        var s = _stations.GetOrAdd(sid!, k => new Station { Sid = k });

        if (GetString(p, "callsign") is string call)
        {
            if (isRxReport) s.LastRxCallsign = call;
            else s.Callsign = call;
        }
        if (GetString(p, "grid_square") is string grid)
            s.GridSquare = grid.Length > 6 ? grid[..6] : grid;
        if (GetString(p, "version") is string ver)
            s.Version = ver;
        if (GetBool(p, "rx_only") is bool rxOnly)
            s.RxOnly = rxOnly;
        if (GetInt64(p, "freq") is long freq)
            s.FreqHz = freq;
        if (GetString(p, "mode") is string mode)
        {
            if (isRxReport) s.LastRxMode = mode;
            else s.Mode = mode;
        }
        if (GetBool(p, "transmitting") is bool tx)
            s.Transmitting = tx;
        if (GetString(p, "message") is string msg)
            s.Message = msg;
        if (isRxReport && GetDouble(p, "snr") is double snr)
            s.LastRxSnr = snr;
        if (GetDateTime(p, "connect_time") is DateTime connect)
            s.ConnectTime = connect;

        // Always re-stamp LastUpdate from the report (or now if absent), so the
        // stale-prune and any "last heard" UI reflect this event.
        s.LastUpdate = GetDateTime(p, "last_update") ?? DateTime.UtcNow;
    }

    // ----- tolerant JSON field readers (missing / null / string-or-number) -----

    private static string? GetString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            _ => null,
        };
    }

    private static bool? GetBool(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(el.GetString(), out var b) => b,
            JsonValueKind.Number when el.TryGetInt64(out var n) => n != 0,
            _ => null,
        };
    }

    private static long? GetInt64(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt64(out var n) => n,
            JsonValueKind.Number when el.TryGetDouble(out var d) => (long)Math.Round(d),
            JsonValueKind.String when long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sn) => sn,
            JsonValueKind.String when double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sd) => (long)Math.Round(sd),
            _ => null,
        };
    }

    private static double? GetDouble(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sd) => sd,
            _ => null,
        };
    }

    private static DateTime? GetDateTime(JsonElement obj, string name)
    {
        var s = GetString(obj, name);
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            return dt;
        return null;
    }

    // Internal mutable station record folded from the event stream. Mutated only
    // on the single Socket.IO receive loop; read (and stale-pruned) under the
    // ConcurrentDictionary in GetSnapshot.
    private sealed class Station
    {
        public string Sid = "";
        public string? Callsign;
        public string? GridSquare;
        public long FreqHz;
        public string? Mode;
        public bool Transmitting;
        public bool RxOnly;
        public string? Message;
        public string? Version;
        public double? LastRxSnr;
        public string? LastRxCallsign;
        public string? LastRxMode;
        public DateTime LastUpdate = DateTime.UtcNow;
        public DateTime? ConnectTime;
    }
}
