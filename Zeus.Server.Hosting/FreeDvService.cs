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

    // Serialises the routing-state mutators (SyncMode / ApplySubmode /
    // ApplyEngagedRouting / ReloadNative). Without it the DSP tick (SyncMode), the
    // auto-detect scan loop and the API thread (ApplyConfig) interleave and leave
    // _radeActive out of sync with _rade.Active — routing RX to the inactive
    // classic modem so RADE audio passes through undecoded ("sounds like USB").
    // Hot-path ProcessRx/ProcessTx read _radeActive (volatile) without this lock.
    private readonly object _routingGate = new();

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

    // ---- Parallel speculative submode detection ----
    // The sequential scanner dwells on one candidate at a time (RADE 6 s + each
    // classic 3 s = ~15 s worst case, reopening the modem on every hop). To make
    // AUTO acquisition near-instant, while unsynced we run EVERY candidate decoder
    // concurrently against the SAME received audio and detect the first that syncs.
    // The candidates are advanced only for their sync state — their decoded speech is
    // discarded; the real playout still comes from the normally-selected decoder, so
    // nothing regresses when the selected mode already happens to match. After a
    // speculative winner is applied the live decoder re-acquires at that submode;
    // first-audio follows re-acquisition, not the instant of detection.
    //
    // The whole pool lifecycle (build / feed-eligibility / winner-apply / teardown)
    // lives on the control thread (ScanLoopAsync). The audio hot path only ever
    // does a SINGLE volatile read of the published snapshot and, if non-null, feeds
    // each entry a copy of the block in that entry's own pre-allocated scratch —
    // no lock, no allocation. Publishing the array reference is a single atomic
    // store; the entries and their scratch are fully built before the store, so a
    // hot-path reader either sees null (byte-identical to today's path) or a
    // completely-initialised pool.
    //
    // Continuous-sync confirmation before camping (SpecLockConfirmMs) ensures a
    // brief false-sync on the wrong candidate can never latch AUTO onto it.
    //
    // Two hysteresis mechanisms keep pool churn under control on Pi-class hardware:
    //   1. SpecTeardownDebounceMs — when ShouldSpeculate goes false because the
    //      live decoder is acquiring (liveSynced/Locked), delay the teardown so a
    //      marginal flickering signal doesn't open/close the pool up to 4×/s.
    //      The !scanning path (TX / disengage) bypasses the debounce and tears down
    //      immediately to avoid feeding pool decoders stale TX audio.
    //   2. SpecReacquireGraceMs — after a winner is applied, suppress the immediate
    //      pool rebuild so the live decoder can re-acquire at the new submode without
    //      a redundant race. If it hasn't acquired by the end of the grace window,
    //      speculation resumes normally (handles a false detection gracefully).
    private const int SpecScratchLen = 8192;              // >= any 48 kHz audio block we hand the pool
    private const long SpecLockConfirmMs = 1500;          // continuous candidate sync before we camp (mirrors the scanner)
    private const long SpecTeardownDebounceMs = 750;      // hold ShouldSpeculate==false before tearing the pool down
    private const long SpecReacquireGraceMs = 2500;       // suppress pool rebuild after a winner is applied
    private volatile bool _speculating;
    private volatile SpecEntry[]? _specPool;              // published snapshot the audio thread reads
    private long _specSyncSinceMs = long.MinValue;        // when the current winner first held sync (control thread)
    private FreeDvSubmode _specWinner;                    // the candidate that is currently holding sync
    private long _specStopSinceMs = long.MinValue;        // when ShouldSpeculate last went false (debounce, control thread)
    private long _specSuppressUntilMs = long.MinValue;    // suppress pool rebuild until this timestamp (control thread)

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

    // Classic codec2 candidates the speculative pool spins up (each on its own
    // FreeDvModem). RADE V1 is the fourth candidate but reuses the existing _rade
    // context rather than a fresh modem — the model is heavy and one RX context is
    // enough. This mirrors the scanner's Order minus RADE (which is fed separately).
    private static readonly FreeDvSubmode[] SpecClassicCandidates =
    {
        FreeDvSubmode.Mode700D,
        FreeDvSubmode.Mode700E,
        FreeDvSubmode.Mode1600,
    };

    // One speculative candidate: a decoder context, the submode it is tuned to,
    // whether the audio thread should feed it a scratch copy (false for a context
    // that is ALREADY the live real decoder — it advances via the real routing, so
    // double-driving it would corrupt its input stream), and the entry's own
    // pre-allocated scratch buffer so the hot path never allocates. Sync state is
    // read through the Synced property, which reads each modem's volatile flag.
    // Dedicated classic modems are owned by the pool and disposed on teardown;
    // the shared _rade context is not (OwnsModem=false).
    private sealed class SpecEntry
    {
        public required FreeDvSubmode Submode;
        public required bool FeedScratch;   // audio thread copies+decodes into Scratch
        public required bool OwnsModem;     // dispose Modem on teardown (false for shared _rade)
        public FreeDvModem? Modem;          // classic codec2 candidate (null when this is the RADE entry)
        public RadeModem? Rade;             // RADE candidate (the shared _rade; null for classic entries)
        public required float[] Scratch;    // per-entry hot-path scratch (never shared)

        // Reads the modem's volatile Synced flag (no lock, no alloc — hot-path safe).
        public bool Synced => Modem is not null ? Modem.Synced : Rade!.Synced;

        // Decode a copy of the block only to advance this candidate's sync state.
        // The decoded output in Scratch is discarded. Hot path: no lock, no alloc.
        // SpecScratchLen (8192) is >= any real 48 kHz audio block; the assert below
        // catches mismatches in Debug builds; the clamp is the Release safety net.
        public void FeedRx(ReadOnlySpan<float> block48k)
        {
            System.Diagnostics.Debug.Assert(
                block48k.Length <= Scratch.Length,
                "spec scratch smaller than audio block — increase SpecScratchLen");
            int n = block48k.Length;
            if (n > Scratch.Length) n = Scratch.Length;
            var span = Scratch.AsSpan(0, n);
            block48k[..n].CopyTo(span);
            if (Modem is not null) Modem.ProcessRxInPlace(span);
            else Rade!.ProcessRxInPlace(span);
        }
    }

    /// <summary>
    /// True when FreeDV is engaged and a decoder is processing audio. Must check
    /// BOTH modems: when RADE V1 is selected the classic <see cref="_modem"/> is
    /// deactivated and only <see cref="_rade"/> is active, so checking _modem alone
    /// made the DSP pipeline skip ProcessRx for RADE — RX passed through as plain
    /// SSB ("sounds like USB") and the RADE decoder never ran.
    /// </summary>
    public bool Active => _modem.Active || _rade.Active;

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
        var freshRade = new RadeModem(_loggerFactory.CreateLogger<RadeModem>());
        freshRade.SetTxText(_txText); // carry the EOO callsign across the swap

        // Swap + re-route under the routing lock so a concurrent SyncMode /
        // ApplySubmode can't observe a half-swapped pair or desynced _radeActive.
        lock (_routingGate)
        {
            // Retire any live speculative pool FIRST: it references the OLD _rade
            // (about to be disposed) and owns classic candidate modems. Tearing it
            // down here unpublishes the snapshot and disposes those candidates
            // before the swap, so the hot path can't feed a disposed context.
            TearDownSpeculativePool();

            // Capture engaged state BEFORE disposing the old modems (Dispose
            // clears their active flags).
            bool wasEngaged = _modem.Active || _rade.Active;
            _radeActive = false;

            var old = _modem;
            var oldRade = _rade;
            _modem = fresh;
            _rade = freshRade;
            old.Dispose();
            oldRade.Dispose();

            // Re-engage whichever modem the selected submode calls for, if FreeDV
            // was active before the reload.
            if (wasEngaged) ApplyEngagedRouting();
        }

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
        lock (_routingGate)
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
                TearDownSpeculativePool(); // FreeDV is leaving — no candidates to run
                _modem.Deactivate();
                _rade.Deactivate();
                _radeActive = false;
            }
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

    /// <summary>
    /// Set the selected submode and, when FreeDV is engaged, route audio to the
    /// decoder that submode calls for (RADE V1 -> _rade, otherwise -> _modem).
    /// _modem remains the single source of truth for the selected submode even
    /// when RADE is the active decoder. Used by both the operator's manual pick
    /// (ApplyConfig) and the auto-detect scan loop, so AUTO can move into and out
    /// of RADE exactly like a manual pick.
    /// </summary>
    private void ApplySubmode(FreeDvSubmode submode)
    {
        lock (_routingGate)
        {
            // Any routing/submode change retires a live speculative pool: it may
            // own the shared _rade activation and classic candidate modems whose
            // state this change would invalidate. Safe no-op when no pool is live
            // (the scan-loop winner path already tore it down before calling here).
            TearDownSpeculativePool();
            _modem.SetSubmode(submode);
            if (_modem.Active || _rade.Active) ApplyEngagedRouting();
        }
    }

    /// <summary>RX0 post-demod insert: turn received modem audio into decoded speech, in place.</summary>
    public void ProcessRx(Span<float> block48k)
    {
        // Speculative parallel detection: while AUTO is unsynced, advance every
        // candidate decoder against a COPY of this block so the control loop can
        // lock onto the first that syncs — near-instant vs. the sequential dwell.
        // A single volatile read; null (the common/steady state and the whole
        // AUTO-off path) makes this a no-op, leaving the hot path byte-identical to
        // before. Each entry decodes into its own pre-allocated scratch, so this is
        // allocation-free; the decoded speech is discarded (detection only).
        var pool = _specPool;
        if (pool is not null)
        {
            for (int i = 0; i < pool.Length; i++)
            {
                var e = pool[i];
                if (e.FeedScratch) e.FeedRx(block48k);
            }
        }

        // Real playout still runs through the normally-selected decoder, exactly as
        // before — so nothing regresses when the selected mode already matches.
        if (_radeActive) _rade.ProcessRxInPlace(block48k);
        else _modem.ProcessRxInPlace(block48k);
    }

    /// <summary>TX mic insert: turn mic speech into the transmitted modem signal, in place.</summary>
    public void ProcessTx(Span<float> block48k)
    {
        // Mark TX active so auto-detect pauses scanning until the over ends.
        Volatile.Write(ref _lastTxActivityMs, Environment.TickCount64);
        // RADE encodes mic speech to the RADE waveform (LPCNet analyzer + rade_tx);
        // classic codec2 modes use freedv_tx. The RADE EOO callsign is set via
        // SetTxText and decoded on RX, both proven; auto-emitting the EOO frame on
        // un-key needs the TX tail to drain (bench-verify) so it is not auto-fired.
        if (_radeActive) _rade.ProcessTxInPlace(block48k);
        else _modem.ProcessTxInPlace(block48k);
    }

    /// <summary>Drop buffered TX modem audio on a MOX falling edge so the next over starts clean.</summary>
    public void FlushTx()
    {
        if (_radeActive) _rade.FlushTx();
        else _modem.FlushTx();
    }

    /// <summary>
    /// Reset the FreeDV RECEIVER's decode state across a MOX transition so it
    /// resumes empty and unsynced. Without this the modem keeps decoding the RXA
    /// stream while keyed (WDSP RX is drained every tick regardless of MOX) and,
    /// at un-key, the resuming RX dumps that self-decoded backlog — an
    /// end-of-over garble in Zeus's own audio, on both RADE and codec2. Flushes
    /// BOTH modems so a submode change across the edge can't strand a stale
    /// backlog in the now-inactive one (the inactive modem is already empty, so
    /// its flush is a cheap no-op).
    /// </summary>
    public void FlushRx()
    {
        _rade.FlushRx();
        _modem.FlushRx();
    }

    /// <summary>
    /// End-of-over flush on the active modem: complete the final frame (and, for
    /// RADE, append the EOO callsign) so a whole frame can be clocked out on the
    /// TX tail. Returns the 48 kHz output samples now pending so the tail drain
    /// knows how long to hold PTT. No-op (0) unless FreeDV is engaged.
    /// </summary>
    public int FinishTx()
    {
        if (!Active) return 0;
        return _radeActive ? _rade.FinishTx() : _modem.FinishTx();
    }

    /// <summary>
    /// Drain queued modem output (no new encoding) into the block for the TX tail
    /// drain; returns the real (non-pad) sample count, 0 when empty. Must only be
    /// called while the TX path is owned by the tail drain.
    /// </summary>
    public int DrainTx(Span<float> block48k)
    {
        if (!Active) { block48k.Clear(); return 0; }
        return _radeActive ? _rade.DrainTo(block48k) : _modem.DrainTo(block48k);
    }

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
            // _modem stays the record of the selected submode even for RADE V1;
            // ApplySubmode swaps the active decoder to match (into/out of RADE).
            ApplySubmode(req.Submode.Value);
        }
        if (req.SquelchEnabled.HasValue || req.SnrSquelchThreshDb.HasValue)
            _modem.SetSquelch(req.SquelchEnabled, req.SnrSquelchThreshDb);
        if (req.TxText is not null)
        {
            _txText = req.TxText;
            _modem.SetTxText(req.TxText); // codec2 TX varicode callback
            _rade.SetTxText(req.TxText);  // RADE EOO callsign (LDPC reliable-text)
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

    /// <summary>
    /// Pure guard for whether the speculative parallel-detection pool should be
    /// running this tick. Speculate only while AUTO is actively scanning, the
    /// scanner has NOT camped a mode, and the live decoder is NOT already synced —
    /// i.e. exactly the "unsynced, unlocked, receiving" window where a parallel
    /// race beats the sequential dwell. Any other combination retracts the pool.
    /// Kept static + side-effect-free so the engage/disengage logic is unit-testable
    /// without native modems or real time (mirrors <see cref="IsTxActive"/>).
    /// </summary>
    internal static bool ShouldSpeculate(bool scanning, bool scannerLocked, bool liveSynced) =>
        scanning && !scannerLocked && !liveSynced;

    // Control-loop: while auto-detect is on and the modem is receiving (active,
    // not transmitting), drive detection and apply any submode change. Runs at a
    // few Hz — far off the audio hot path — and is a no-op when auto-detect is off,
    // so it costs effectively nothing in the common case.
    //
    // Detection strategy: while unsynced and unlocked, run the SPECULATIVE PARALLEL
    // pool — every candidate decoder advances against the same received audio and
    // we lock onto the first that holds sync for SpecLockConfirmMs. The scanner
    // remains the lock/unlock timing authority for the CAMPED mode: once a winner
    // is applied, the scanner locks it (its own debounce) and, on a sustained loss
    // (> unlockMs), releases it so speculation rebuilds and re-acquires instantly.
    private async Task ScanLoopAsync(CancellationToken ct)
    {
        bool wasScanning = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(ScanTickMs, ct).ConfigureAwait(false);

                var modem = _modem; // may be swapped by ReloadNative; read once per tick
                var rade = _rade;
                long now = Environment.TickCount64;
                bool transmitting = IsTxActive(now, Volatile.Read(ref _lastTxActivityMs), TxQuietMs);
                // FreeDV is engaged when EITHER decoder is active (the selected
                // submode decides which). Scan whenever a decoder we can run is
                // engaged — RADE counts, so AUTO works while parked on RADE V1.
                bool engaged = modem.Active || rade.Active;
                bool canDecode = modem.NativeAvailable || rade.RadeAvailable;
                bool scanning = _autoDetect && engaged && canDecode && !transmitting;

                if (!scanning)
                {
                    // Stop speculating whenever AUTO pauses (disabled / disengaged /
                    // transmitting) so we never feed pool decoders stale/own-TX audio.
                    if (_speculating) lock (_routingGate) TearDownSpeculativePool();
                    wasScanning = false;
                    continue;
                }
                if (!wasScanning) { _scanner.Reset(now); wasScanning = true; }

                // Sync comes from whichever decoder the current submode routes to.
                bool synced = _radeActive ? rade.Synced : modem.Synced;
                var current = modem.Submode;

                // Let the scanner run its lock/unlock timing on the CURRENT mode.
                // While the pool is running the scanner won't advance modes (it only
                // camps/releases); we drive candidate switching ourselves below.
                var next = _scanner.Tick(now, synced, current);

                if (ShouldSpeculate(scanning, _scanner.Locked, synced))
                {
                    // ShouldSpeculate is true — reset the teardown debounce timer so
                    // any prior stop-condition is forgotten the instant speculation is
                    // needed again.
                    _specStopSinceMs = long.MinValue;

                    // Unsynced, unlocked, receiving: run the speculative pool. Build
                    // it once (respecting the post-winner re-acquire grace window),
                    // then poll the candidates for a confirmed winner.
                    if (!_speculating)
                    {
                        // Suppress the rebuild for SpecReacquireGraceMs after a winner
                        // was applied, giving the live decoder time to re-acquire at
                        // the new submode before we spin up a redundant parallel race.
                        // If the grace window expires without re-acquisition, speculation
                        // resumes normally (a false detection just restarts the search).
                        if (now >= _specSuppressUntilMs)
                            lock (_routingGate) BuildSpeculativePool();
                    }
                    else if (TryPickSpeculativeWinner(now, out var winner))
                    {
                        _log.LogInformation(
                            "FreeDV: parallel detection — detected submode {Submode}; " +
                            "live decoder re-acquires before first audio", winner);
                        // ApplySubmode tears the pool down + routes RADE <-> classic
                        // under the routing gate. The live decoder now re-acquires at
                        // the winner submode; first decoded audio follows that re-sync.
                        ApplySubmode(winner);
                        // Start the grace window: suppress a pool rebuild until the
                        // live decoder has had time to re-acquire or the grace expires.
                        _specSuppressUntilMs = now + SpecReacquireGraceMs;
                        // Seed the scanner on the winner so its lock timing takes
                        // over from a clean dwell; the winner is synced, so the
                        // scanner's next ticks confirm and camp it.
                        _scanner.Reset(now);
                    }
                }
                else if (_speculating)
                {
                    // ShouldSpeculate is false (camped or live decoder is acquiring).
                    // Debounce the teardown: only tear down after the stop condition
                    // has held continuously for SpecTeardownDebounceMs, so a briefly
                    // flickering marginal signal can't open/close 3 codec2 + 1 RADE
                    // (heavy neural) contexts at 4 Hz on Pi-class hardware.
                    if (_specStopSinceMs == long.MinValue) _specStopSinceMs = now;
                    if (now - _specStopSinceMs >= SpecTeardownDebounceMs)
                        lock (_routingGate) TearDownSpeculativePool();
                }

                // Preserve the sequential fallback: only meaningful when NOT
                // speculating (e.g. no candidate ever wins because native is
                // unavailable for the pool). Then the scanner's dwell-advance still
                // walks the modes exactly as before.
                if (!_speculating && next is { } m && m != current)
                {
                    _log.LogInformation("FreeDV: auto-detect — no lock, trying submode {Submode}", m);
                    ApplySubmode(m); // routes RADE <-> classic, not just _modem.SetSubmode
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            if (_speculating) lock (_routingGate) TearDownSpeculativePool();
        }
    }

    /// <summary>
    /// Poll the speculative pool for a candidate that has held sync continuously
    /// for <see cref="SpecLockConfirmMs"/>. Returns true (with the winning submode)
    /// only after the debounce, so a brief false-sync on the wrong candidate never
    /// camps AUTO onto it — the same guard the sequential scanner used. Control
    /// thread only.
    /// </summary>
    private bool TryPickSpeculativeWinner(long nowMs, out FreeDvSubmode winner)
    {
        winner = default;
        var pool = _specPool;
        if (pool is null) return false;

        // First synced candidate wins (pool order = classic modes then RADE). We
        // require the SAME candidate to stay synced across the confirm window.
        FreeDvSubmode? syncedNow = null;
        foreach (var e in pool)
        {
            if (e.Synced) { syncedNow = e.Submode; break; }
        }

        if (syncedNow is not { } m)
        {
            // Nothing synced — reset the confirm timer so a future sync must hold
            // the full window afresh.
            _specSyncSinceMs = long.MinValue;
            return false;
        }

        if (_specSyncSinceMs == long.MinValue || m != _specWinner)
        {
            // A (new) candidate just started syncing — begin the confirm window.
            _specWinner = m;
            _specSyncSinceMs = nowMs;
            return false;
        }

        if (nowMs - _specSyncSinceMs >= SpecLockConfirmMs)
        {
            winner = m;
            return true;
        }
        return false;
    }

    // -------- speculative pool lifecycle (control thread: ScanLoopAsync only) --------

    /// <summary>
    /// Build the speculative detection pool and publish it to the audio thread.
    /// Runs under <see cref="_routingGate"/> (called from the scan loop) so it can
    /// safely toggle _rade active state alongside the live routing. One entry per
    /// scanner candidate (RADE V1 + the classic codec2 modes):
    ///  - the currently-live decoder's submode is referenced directly with
    ///    FeedScratch=false (it advances through the real ProcessRx routing);
    ///  - every other classic candidate gets a fresh FreeDvModem activated at that
    ///    submode, fed a scratch copy on the hot path;
    ///  - the RADE candidate reuses the shared _rade context (activated here if it
    ///    is not already the live decoder), fed a scratch copy unless it is live.
    /// The whole array is constructed before the single volatile publish, so the
    /// hot path only ever sees null or a fully-initialised pool.
    /// </summary>
    private void BuildSpeculativePool()
    {
        FreeDvSubmode liveSubmode = _modem.Submode; // the operator/scan's current pick

        var entries = new List<SpecEntry>(SpecClassicCandidates.Length + 1);

        // Classic candidates.
        foreach (var m in SpecClassicCandidates)
        {
            if (!_radeActive && m == liveSubmode)
            {
                // This classic mode IS the live decoder — reference it, don't
                // duplicate. It advances via the real routing (FeedScratch=false).
                entries.Add(new SpecEntry
                {
                    Submode = m,
                    FeedScratch = false,
                    OwnsModem = false,
                    Modem = _modem,
                    Scratch = Array.Empty<float>(), // never fed
                });
            }
            else
            {
                // A speculative classic candidate: its own modem, opened at this
                // submode, fed a scratch copy on the hot path.
                var spec = new FreeDvModem(_loggerFactory.CreateLogger<FreeDvModem>());
                spec.SetSubmode(m);
                spec.SetSquelch(enabled: false, threshDb: null); // detection cares about sync, not squelch
                spec.Activate();
                entries.Add(new SpecEntry
                {
                    Submode = m,
                    FeedScratch = true,
                    OwnsModem = true,
                    Modem = spec,
                    Scratch = new float[SpecScratchLen],
                });
            }
        }

        // RADE candidate — always the shared _rade context.
        if (_radeActive)
        {
            // RADE is the live decoder: it advances through the real routing.
            entries.Add(new SpecEntry
            {
                Submode = FreeDvSubmode.RadeV1,
                FeedScratch = false,
                OwnsModem = false,
                Rade = _rade,
                Scratch = Array.Empty<float>(),
            });
        }
        else
        {
            // Not live: activate the shared context for speculation and feed it a
            // scratch copy. Teardown deactivates it again (it is not owned/disposed).
            _rade.Activate();
            entries.Add(new SpecEntry
            {
                Submode = FreeDvSubmode.RadeV1,
                FeedScratch = true,
                OwnsModem = false,
                Rade = _rade,
                Scratch = new float[SpecScratchLen],
            });
        }

        _specSyncSinceMs = long.MinValue;
        // Publish last: entries + scratch are fully built above, so the hot path
        // sees a complete pool the instant _specPool becomes non-null.
        _specPool = entries.ToArray();
        _speculating = true;
        _log.LogInformation("FreeDV: speculative parallel detection ENGAGED ({N} candidates)", entries.Count);
    }

    /// <summary>
    /// Retract the speculative pool: unpublish it from the audio thread FIRST (so
    /// no further scratch feeds run), then dispose the owned classic candidate
    /// modems and, if the shared RADE context was activated only for speculation,
    /// deactivate it. Runs under <see cref="_routingGate"/> (scan-loop caller).
    /// </summary>
    private void TearDownSpeculativePool()
    {
        var pool = _specPool;
        _speculating = false;
        _specPool = null; // hot path stops feeding after this store
        if (pool is null) return;

        // The hot path may be mid-block when we null the snapshot. Each owned modem
        // is deactivated before disposal, and its own Deactivate/Dispose gates the
        // decoder's hot path out via the seqlock, so a concurrent FeedRx already in
        // flight completes safely.
        foreach (var e in pool)
        {
            if (e.OwnsModem && e.Modem is not null)
            {
                e.Modem.Deactivate();
                e.Modem.Dispose();
            }
        }

        // If we activated the shared RADE context purely for speculation (i.e. it
        // is not the live decoder), return it to idle. _radeActive stays the
        // authority on whether RADE is the live decoder.
        if (!_radeActive) _rade.Deactivate();
        _specSyncSinceMs = long.MinValue;
        _specStopSinceMs = long.MinValue; // reset debounce so next speculation cycle starts clean
    }

    public void Dispose()
    {
        _scanCts.Cancel();
        try { _scanLoop.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { /* OperationCanceled on shutdown */ }
        _scanCts.Dispose();
        lock (_routingGate) TearDownSpeculativePool();
        _modem.Dispose();
        _rade.Dispose();
    }
}
