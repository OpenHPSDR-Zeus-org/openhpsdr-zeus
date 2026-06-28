// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// WsjtxLiveEmitter — the live WSJT-X UDP stream GridTracker / JTAlert need for
// map / roster / alerts: Heartbeat (0) ~15 s, Status (1) on MOX change + a slow
// periodic tick, Decode (2) one per decoded FT8/FT4 line, WSPRDecode (10) one per
// WSPR spot.
//
// Same proven leaf-subscriber seam as PskReporterReporter / WsprnetReporter: the
// decode/spot handlers ONLY read existing events + a Snapshot() and ENQUEUE a
// UDP send — they never call back into the radio/DSP/TX path, and they swallow
// their own errors, so a reporter fault can never disturb decode or TX.
//
// SAFETY: SEND-ONLY (the broadcaster has no listener). Gated on
// cfg.Enabled && cfg.SendLiveDecodes — the disabled-default path never pays for a
// Snapshot() on the decode thread. PureSignal / drive / power are never touched.

using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Zeus.Contracts;
using Zeus.Dsp.Ft8;

namespace Zeus.Server.Wsjtx;

public sealed partial class WsjtxLiveEmitter : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(5);
    private static readonly string ZeusVersion = BuildVersion();

    private readonly ILogger<WsjtxLiveEmitter> _log;
    private readonly WsjtxManagementService _mgmt;
    private readonly Func<byte[], CancellationToken, Task> _send;
    private readonly Ft8Service? _ft8;
    private readonly WsprService? _wspr;
    private readonly RadioService? _radio;
    private readonly Ft8TxService? _tx;
    private readonly SpottingManagementService? _operator;

    public WsjtxLiveEmitter(
        ILogger<WsjtxLiveEmitter> log,
        WsjtxManagementService mgmt,
        WsjtxUdpBroadcaster broadcaster,
        Ft8Service ft8,
        WsprService wspr,
        RadioService radio,
        Ft8TxService tx,
        SpottingManagementService operatorIdentity)
    {
        _log = log;
        _mgmt = mgmt;
        _send = broadcaster.SendDatagramAsync;
        _ft8 = ft8;
        _wspr = wspr;
        _radio = radio;
        _tx = tx;
        _operator = operatorIdentity;

        _ft8.DecodesReady += OnDecodes;
        _wspr.SpotsReady += OnSpots;
        _radio.MoxChanged += OnMoxChanged;
    }

    // Test seam: no DSP/radio wiring. Drives the gate + field mapping via the
    // OnDecodes/OnSpots handlers and a captured send delegate.
    internal WsjtxLiveEmitter(
        ILogger<WsjtxLiveEmitter> log,
        WsjtxManagementService mgmt,
        Func<byte[], CancellationToken, Task> send)
    {
        _log = log;
        _mgmt = mgmt;
        _send = send;
        _ft8 = null;
        _wspr = null;
        _radio = null;
        _tx = null;
        _operator = null;
    }

    public override void Dispose()
    {
        if (_ft8 is not null) _ft8.DecodesReady -= OnDecodes;
        if (_wspr is not null) _wspr.SpotsReady -= OnSpots;
        if (_radio is not null) _radio.MoxChanged -= OnMoxChanged;
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var lastHeartbeat = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cfg = _mgmt.GetConfig();
                if (cfg.Enabled && cfg.SendLiveDecodes)
                {
                    var now = DateTime.UtcNow;
                    if (now - lastHeartbeat >= HeartbeatInterval)
                    {
                        lastHeartbeat = now;
                        await SendHeartbeatAsync(cfg, ct).ConfigureAwait(false);
                    }
                    await SendStatusAsync(cfg, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "wsjtx.live periodic tick failed");
            }

            try { await Task.Delay(StatusInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ---- event handlers (decode worker / state thread) -----------------------

    private void OnDecodes(Ft8DecodeBatch batch) => HandleDecodes(batch);

    private void OnSpots(WsprSpotBatch batch) => HandleSpots(batch);

    // Gate (enable + live flag) then build + send. Internal so the gating is
    // unit-testable without a DSP graph: disabled => nothing sent, live-flag off
    // => nothing sent, enabled+live => one datagram per decoded line.
    internal void HandleDecodes(Ft8DecodeBatch batch)
    {
        try
        {
            var cfg = _mgmt.GetConfig();
            if (!cfg.Enabled || !cfg.SendLiveDecodes) return; // zero cost on the default path
            foreach (var d in BuildDecodeDatagrams(cfg.InstanceId, batch))
                _ = _send(d, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "wsjtx.live decode emit failed");
        }
    }

    internal void HandleSpots(WsprSpotBatch batch)
    {
        try
        {
            var cfg = _mgmt.GetConfig();
            if (!cfg.Enabled || !cfg.SendLiveDecodes) return;
            foreach (var d in BuildWsprDatagrams(cfg.InstanceId, batch))
                _ = _send(d, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "wsjtx.live wspr emit failed");
        }
    }

    private void OnMoxChanged(bool mox)
    {
        try
        {
            var cfg = _mgmt.GetConfig();
            if (!cfg.Enabled || !cfg.SendLiveDecodes) return;
            _ = SendStatusAsync(cfg, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "wsjtx.live mox status failed");
        }
    }

    // ---- senders -------------------------------------------------------------

    private Task SendHeartbeatAsync(WsjtxRuntimeConfig cfg, CancellationToken ct)
    {
        var dg = WsjtxMessage.EncodeHeartbeat(cfg.InstanceId, maxSchema: 3, version: ZeusVersion, revision: "");
        return _send(dg, ct);
    }

    private Task SendStatusAsync(WsjtxRuntimeConfig cfg, CancellationToken ct)
    {
        if (_radio is null) return Task.CompletedTask;

        var snap = _radio.Snapshot();
        var (deCall, deGrid) = _operator?.ResolveOperator() ?? ("", "");

        string mode;
        uint trPeriodMs;
        if (_wspr?.IsEnabled == true)
        {
            mode = "WSPR";
            trPeriodMs = 120_000;
        }
        else if (_ft8?.IsEnabled == true)
        {
            mode = _ft8.ActiveProtocol == Ft8Protocol.Ft4 ? "FT4" : "FT8";
            trPeriodMs = _ft8.ActiveProtocol == Ft8Protocol.Ft4 ? 7_500u : 15_000u;
        }
        else
        {
            mode = snap.Mode.ToString().ToUpperInvariant();
            trPeriodMs = 0;
        }

        var txStatus = _tx?.Status();
        bool txEnabled = txStatus?.Armed ?? false;
        bool transmitting = txStatus?.Transmitting ?? false;
        string txMessage = txStatus?.Message ?? "";
        uint txDf = (uint)Math.Max(0, txStatus?.AudioHz ?? 0);
        string dxCall = ParseDxCall(txMessage, deCall);

        var dg = WsjtxMessage.EncodeStatus(
            instanceId: cfg.InstanceId,
            dialFrequencyHz: (ulong)Math.Max(0, snap.VfoHz),
            mode: mode,
            dxCall: dxCall,
            report: "",
            txMode: mode,
            txEnabled: txEnabled,
            transmitting: transmitting,
            decoding: (_ft8?.IsEnabled ?? false) || (_wspr?.IsEnabled ?? false),
            rxDf: 0,
            txDf: txDf,
            deCall: deCall,
            deGrid: deGrid,
            dxGrid: "",
            txWatchdog: false,
            subMode: "",
            fastMode: false,
            specialOperationMode: 0,
            frequencyTolerance: 0,
            trPeriod: trPeriodMs,
            configurationName: "Zeus",
            txMessage: txMessage);
        return _send(dg, ct);
    }

    // ---- pure field mappers (unit-tested directly) ---------------------------

    /// <summary>Map an FT8/FT4 decode batch to one Decode (type 2) datagram per line.</summary>
    internal static IReadOnlyList<byte[]> BuildDecodeDatagrams(string instanceId, Ft8DecodeBatch batch)
    {
        uint timeMs = MsSinceUtcMidnight(batch.SlotStartUtc);
        string mode = batch.Protocol == Ft8Protocol.Ft4 ? "FT4" : "FT8";
        var list = new List<byte[]>(batch.Decodes.Count);
        foreach (var d in batch.Decodes)
        {
            uint df = (uint)Math.Clamp((int)Math.Round(d.FreqHz), 0, int.MaxValue);
            list.Add(WsjtxMessage.EncodeDecode(
                instanceId,
                isNew: true,
                timeMsSinceMidnight: timeMs,
                snr: (int)Math.Round(d.SnrDb),
                deltaTimeSec: d.DtSec,
                deltaFrequencyHz: df,
                mode: mode,
                message: d.Text ?? "",
                lowConfidence: false,
                offAir: false));
        }
        return list;
    }

    /// <summary>Map a WSPR spot batch to one WSPRDecode (type 10) datagram per spot.</summary>
    internal static IReadOnlyList<byte[]> BuildWsprDatagrams(string instanceId, WsprSpotBatch batch)
    {
        uint timeMs = MsSinceUtcMidnight(batch.SlotStartUtc);
        var list = new List<byte[]>(batch.Spots.Count);
        foreach (var sp in batch.Spots)
        {
            ParseWsprMessage(sp.Message, out string call, out string grid, out int power);
            ulong freqHz = sp.FreqMhz > 0 ? (ulong)Math.Round(sp.FreqMhz * 1_000_000.0) : 0UL;
            list.Add(WsjtxMessage.EncodeWsprDecode(
                instanceId,
                isNew: true,
                timeMsSinceMidnight: timeMs,
                snr: (int)Math.Round(sp.SnrDb),
                deltaTimeSec: sp.DtSec,
                frequencyHz: freqHz,
                drift: sp.DriftHz,
                callsign: call,
                grid: grid,
                power: power,
                offAir: false));
        }
        return list;
    }

    internal static uint MsSinceUtcMidnight(DateTime slotStart)
    {
        var utc = slotStart.Kind switch
        {
            DateTimeKind.Utc => slotStart,
            DateTimeKind.Local => slotStart.ToUniversalTime(),
            _ => DateTime.SpecifyKind(slotStart, DateTimeKind.Utc),
        };
        return (uint)(((utc.Hour * 60 + utc.Minute) * 60 + utc.Second) * 1000 + utc.Millisecond);
    }

    [GeneratedRegex(@"^[A-R]{2}[0-9]{2}([A-X]{2})?$")]
    private static partial Regex GridRegex();

    // Tokenise a WSPR message ("CALL GRID DBM", "PFX/CALL DBM", "<CALL> GRID6 DBM").
    // call = first token (hash markers stripped), grid = a Maidenhead token if
    // present, power = the trailing integer (dBm) if present. Never throws.
    internal static void ParseWsprMessage(string? message, out string call, out string grid, out int power)
    {
        call = "";
        grid = "";
        power = 0;
        if (string.IsNullOrWhiteSpace(message)) return;

        var tokens = message.Trim().ToUpperInvariant()
            .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return;

        call = tokens[0].Trim('<', '>');

        if (tokens.Length >= 2 && int.TryParse(tokens[^1], out int p))
            power = p;

        foreach (var t in tokens)
        {
            if (GridRegex().IsMatch(t)) { grid = t; break; }
        }
    }

    // Extract the DX (target) call from OUR staged TX message: the first token
    // that looks like a callsign and is not our own. Returns "" when none.
    internal static string ParseDxCall(string? txMessage, string deCall)
    {
        if (string.IsNullOrWhiteSpace(txMessage)) return "";
        var tokens = txMessage.Trim().ToUpperInvariant()
            .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in tokens)
        {
            if (t == "CQ" || t == "DE") continue;
            var core = t.Trim('<', '>');
            if (LooksLikeCall(core) && !string.Equals(core, deCall, StringComparison.OrdinalIgnoreCase))
                return core;
        }
        return "";
    }

    private static bool LooksLikeCall(string s)
    {
        if (s.Length < 3) return false;
        bool hasLetter = false, hasDigit = false;
        foreach (var c in s)
        {
            if (c is >= 'A' and <= 'Z') hasLetter = true;
            else if (c is >= '0' and <= '9') hasDigit = true;
            else if (c != '/') return false;
        }
        if (!hasLetter || !hasDigit) return false;
        return !GridRegex().IsMatch(s); // a bare locator is not a call
    }

    private static string BuildVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "Zeus" : $"Zeus {v.Major}.{v.Minor}.{v.Build}";
    }
}
