// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Server-side coordinator for FreeDV digital voice. Owns the single
// FreeDvModem instance, drives its active state from the operator's selected
// mode (RxMode.FreeDv), and exposes the /api/freedv status/config surface.
// The DSP pipeline taps ProcessRx (post-demod, RX0) and the TX mic-ingest
// taps ProcessTx (pre-WDSP) — both no-ops unless FreeDV is active. The radio
// itself runs a real SSB demod/mod underneath, on the FreeDV band-convention
// sideband — LSB below 10 MHz, USB at/above (RadioService.EffectiveEngineMode,
// applied in DspPipelineService) — so the OFDM carriers share one orientation
// with every other FreeDV station on the band. This service is sideband-agnostic:
// it processes whatever post-demod audio the pipeline hands it.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Dsp.FreeDv;

namespace Zeus.Server;

public sealed class FreeDvService : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<FreeDvService> _log;
    private FreeDvModem _modem;
    private RadeModem _rade;
    private volatile string? _txText;

    // Single volatile the audio hot path reads to pick the active modem, so it
    // never chains through (_modem.Submode == RadeV1 && _rade.Active). Set on the
    // control path (engage / submode change) under the same flow that toggles the
    // modems' active state.
    private volatile bool _radeActive;

    // Auto submode detection. The scanner is a pure state machine; this service
    // ticks it on a lightweight control loop and applies its submode decisions.
    // Scanning pauses while transmitting (ProcessTx stamps _lastTxActivityMs) so
    // a long over never bumps the operator off a mode mid-transmission.
    private volatile bool _autoDetect;
    private readonly FreeDvAutoScanner _scanner = new();
    private readonly CancellationTokenSource _scanCts = new();
    private readonly Task _scanLoop;
    private long _lastTxActivityMs = long.MinValue;
    private const int ScanTickMs = 250;
    private const long TxQuietMs = 400; // hold the scan within this window of the last TX block

    public FreeDvService(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<FreeDvService>();
        _modem = new FreeDvModem(loggerFactory.CreateLogger<FreeDvModem>());
        _rade = new RadeModem(loggerFactory.CreateLogger<RadeModem>());
        _scanLoop = ScanLoopAsync(_scanCts.Token);
    }

    /// <summary>
    /// True when the operator has selected the RADE V1 submode. The submode is
    /// tracked on <see cref="_modem"/> (the single source of truth for the
    /// selected submode, even for RADE), so RADE routing keys off it.
    /// </summary>
    private bool RadeSelected => _modem.Submode == FreeDvSubmode.RadeV1;

    /// <summary>True when FreeDV is engaged and the modem is processing audio.</summary>
    public bool Active => _modem.Active;

    /// <summary>True when the codec2 native library is loadable and FreeDV can run.</summary>
    public bool NativeAvailable => _modem.NativeAvailable;

    /// <summary>
    /// Re-evaluate codec2 availability after the in-app installer stages a new
    /// binary, swapping in a fresh modem so <see cref="NativeAvailable"/> can
    /// flip true without restarting the host. The operator's submode + squelch
    /// selection carries over. Safe against the DSP hot path: the swap is a
    /// single reference assignment, and the retired modem only ever passes audio
    /// through once disposed. Returns the post-reload availability.
    /// </summary>
    public bool ReloadNative()
    {
        // ResetProbe() clears BOTH the codec2 and zeus_rade probe caches, so a
        // freshly-installed lib of either kind goes live after this reload.
        FreeDvNativeLoader.ResetProbe();
        var fresh = new FreeDvModem(_loggerFactory.CreateLogger<FreeDvModem>());
        fresh.SetSubmode(_modem.Submode);
        fresh.SetSquelch(_modem.SquelchEnabled, _modem.SnrSquelchThreshDb);
        fresh.SetTxText(_txText); // carry the operator's TX callsign/text across the swap

        // Rebuild RADE too so a newly-installed zeus_rade lib re-probes and goes
        // live. Capture engaged state BEFORE disposing the old modems (Dispose
        // clears their active flags).
        bool wasEngaged = _modem.Active || _rade.Active;
        _radeActive = false;
        var freshRade = new RadeModem(_loggerFactory.CreateLogger<RadeModem>());

        var old = _modem;
        var oldRade = _rade;
        _modem = fresh;
        _rade = freshRade;
        old.Dispose();
        oldRade.Dispose();

        // Re-engage whichever modem the selected submode calls for, if FreeDV was
        // active before the reload.
        if (wasEngaged) ApplyEngagedRouting();

        _log.LogInformation(
            "FreeDV: native reload — codec2 {State}, RADE {RadeState}",
            fresh.NativeAvailable ? "available" : "still unavailable",
            freshRade.RadeAvailable ? "available" : "still unavailable");
        return fresh.NativeAvailable;
    }

    /// <summary>
    /// Reconcile the modem's active state with the live RX0 mode. Called from
    /// the DSP tick — cheap no-op when already in the right state. Activating
    /// opens the native modem at the current submode; deactivating releases it.
    /// </summary>
    public void SyncMode(RxMode mode)
    {
        bool want = mode == RxMode.FreeDv;
        bool engaged = _modem.Active || _rade.Active;
        if (want && !engaged)
        {
            _log.LogInformation("FreeDV: engaging (mode=FreeDv, submode={Submode})", _modem.Submode);
            ApplyEngagedRouting();
        }
        else if (!want && engaged)
        {
            _log.LogInformation("FreeDV: disengaging (mode={Mode})", mode);
            _modem.Deactivate();
            _rade.Deactivate();
            _radeActive = false;
        }
    }

    /// <summary>
    /// Activate whichever modem the selected submode calls for and deactivate the
    /// other, then publish the hot-path routing flag. Call only while FreeDV is
    /// (being) engaged. RADE selected -> _rade active, _modem idle; otherwise
    /// _modem active, _rade idle.
    /// </summary>
    private void ApplyEngagedRouting()
    {
        if (RadeSelected)
        {
            _modem.Deactivate();
            _rade.Activate();
            _radeActive = true;
        }
        else
        {
            _rade.Deactivate();
            _radeActive = false;
            _modem.Activate();
        }
    }

    /// <summary>RX0 post-demod insert: turn received modem audio into decoded speech, in place.</summary>
    public void ProcessRx(Span<float> block48k)
    {
        if (_radeActive) _rade.ProcessRxInPlace(block48k);
        else _modem.ProcessRxInPlace(block48k);
    }

    /// <summary>TX mic insert: turn mic speech into the transmitted modem signal, in place.</summary>
    public void ProcessTx(Span<float> block48k)
    {
        // Mark TX active so auto-detect pauses scanning until the over ends.
        Volatile.Write(ref _lastTxActivityMs, Environment.TickCount64);
        // RADE TX is not implemented yet — _rade.ProcessTxInPlace silences the
        // block when active rather than transmitting raw mic on a RADE frequency.
        if (_radeActive) _rade.ProcessTxInPlace(block48k);
        else _modem.ProcessTxInPlace(block48k);
    }

    /// <summary>Drop buffered TX modem audio on a MOX falling edge so the next over starts clean.</summary>
    public void FlushTx() => _modem.FlushTx();

    public FreeDvStatusDto Status()
    {
        bool rade = RadeSelected;
        return new(
            // codec2 availability stays the FreeDvModem flag; RADE availability is
            // the separate RadeAvailable flag below (a real probe now).
            NativeAvailable: _modem.NativeAvailable,
            // Sync / SNR / rates / active / version come from whichever modem the
            // selected submode routes to. RADE has no codec2 squelch, so the
            // squelch fields fall back to the FreeDvModem's stored selection.
            Active: rade ? _rade.Active : _modem.Active,
            Submode: _modem.Submode,
            Synced: rade ? _rade.Synced : _modem.Synced,
            SnrDb: Math.Round(rade ? _rade.SnrDb : _modem.SnrDb, 1),
            SquelchEnabled: _modem.SquelchEnabled,
            SnrSquelchThreshDb: _modem.SnrSquelchThreshDb,
            SpeechSampleRateHz: rade ? _rade.SpeechSampleRateHz : _modem.SpeechSampleRateHz,
            ModemSampleRateHz: rade ? _rade.ModemSampleRateHz : _modem.ModemSampleRateHz,
            // RX text sidechannel (callsign etc.). FreeDV decodes it from the txt
            // stream via freedv_set_callback_txt; RADE's EOO callsign is garbled
            // until freedv_text is wired, so RADE reports null.
            RxText: rade ? _rade.RxText : _modem.RxText,
            TxText: _txText,
            LibraryVersion: rade ? _rade.LibraryVersion : _modem.LibraryVersion,
            AutoDetect: _autoDetect,
            RadeAvailable: _rade.RadeAvailable);
    }

    public FreeDvStatusDto ApplyConfig(FreeDvConfigRequest req)
    {
        // A manual submode pick implicitly turns auto-detect off — the operator
        // is asserting a mode, so honour it rather than letting the scanner move
        // away from it on the next unsynced tick.
        if (req.Submode.HasValue)
        {
            if (_autoDetect && (!req.AutoDetect.HasValue || !req.AutoDetect.Value))
                _autoDetect = false;
            // _modem stays the record of the selected submode even for RADE V1,
            // mirroring today; RadeSelected then keys off it.
            _modem.SetSubmode(req.Submode.Value);
            // If FreeDV is engaged, swap the active modem to match the new submode
            // (into/out of RADE). No-op when the routing already matches.
            bool engaged = _modem.Active || _rade.Active;
            if (engaged) ApplyEngagedRouting();
        }
        if (req.SquelchEnabled.HasValue || req.SnrSquelchThreshDb.HasValue)
            _modem.SetSquelch(req.SquelchEnabled, req.SnrSquelchThreshDb);
        if (req.TxText is not null)
        {
            _txText = req.TxText;
            _modem.SetTxText(req.TxText); // push to the modem's TX varicode callback
        }
        if (req.AutoDetect.HasValue && req.AutoDetect.Value != _autoDetect)
        {
            _autoDetect = req.AutoDetect.Value;
            if (_autoDetect)
            {
                _scanner.Reset(Environment.TickCount64);
                _log.LogInformation("FreeDV: auto submode detection ENABLED (scanning {N} modes)", _scanner.Order.Count);
            }
            else
            {
                _log.LogInformation("FreeDV: auto submode detection DISABLED (holding {Submode})", _modem.Submode);
            }
        }
        return Status();
    }

    /// <summary>True when auto submode detection is engaged.</summary>
    public bool AutoDetect => _autoDetect;

    /// <summary>
    /// Whether the modem counts as transmitting "now" given the last TX-block
    /// timestamp. Auto-detect pauses scanning while this is true. The sentinel
    /// (long.MinValue = never transmitted) is handled explicitly so the
    /// <c>now - lastTx</c> subtraction can never overflow into a false positive
    /// that would gate scanning off forever.
    /// </summary>
    internal static bool IsTxActive(long nowMs, long lastTxMs, long quietMs) =>
        lastTxMs != long.MinValue && nowMs - lastTxMs < quietMs;

    // Control-loop: while auto-detect is on and the modem is receiving (active,
    // not transmitting), tick the scanner and apply any submode change it asks
    // for. Runs at a few Hz — far off the audio hot path — and is a no-op when
    // auto-detect is off, so it costs effectively nothing in the common case.
    private async Task ScanLoopAsync(CancellationToken ct)
    {
        bool wasScanning = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(ScanTickMs, ct).ConfigureAwait(false);

                var modem = _modem; // may be swapped by ReloadNative; read once per tick
                long now = Environment.TickCount64;
                bool transmitting = IsTxActive(now, Volatile.Read(ref _lastTxActivityMs), TxQuietMs);
                bool scanning = _autoDetect && modem.Active && modem.NativeAvailable && !transmitting;

                if (!scanning) { wasScanning = false; continue; }
                if (!wasScanning) { _scanner.Reset(now); wasScanning = true; }

                var next = _scanner.Tick(now, modem.Synced, modem.Submode);
                if (next is { } m && m != modem.Submode)
                {
                    _log.LogInformation("FreeDV: auto-detect — no lock, trying submode {Submode}", m);
                    modem.SetSubmode(m);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    public void Dispose()
    {
        _scanCts.Cancel();
        try { _scanLoop.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { /* OperationCanceled on shutdown */ }
        _scanCts.Dispose();
        _modem.Dispose();
        _rade.Dispose();
    }
}
