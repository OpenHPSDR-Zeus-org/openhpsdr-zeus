// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Dsp.Wdsp;
using Zeus.Protocol1;

namespace Zeus.Server;

public class DspPipelineService : BackgroundService
{
    private const int Width = 2048;
    private const int SyntheticSampleRateHz = 192_000;
    private const int AudioOutputRateHz = 48_000;
    private const int AudioDrainCapacity = 2048;
    private static readonly TimeSpan TickPeriod = TimeSpan.FromMilliseconds(1000.0 / 30.0);

    private readonly RadioService _radio;
    private readonly StreamingHub _hub;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DspPipelineService> _log;

    /// <summary>
    /// Raised when an RX S-meter reading is available (approximately 5 Hz).
    /// Arguments: (channelId, dBm)
    /// </summary>
    public event Action<int, double>? RxMeterUpdated;

    /// <summary>
    /// Raised on every decoded RX IQ frame, after it has been fed to WDSP.
    /// Arguments: (receiver, sampleRateHz, interleavedIQ).
    /// The memory references a pooled buffer and is only valid for the
    /// duration of the synchronous handler — copy if retention is needed.
    /// </summary>
    public event Action<int, int, ReadOnlyMemory<double>>? RxIqAvailable;

    /// <summary>
    /// Raised when demodulated RX audio samples are available (~30 Hz ticks,
    /// 48 kHz mono FLOAT32). Arguments: (receiver, sampleRateHz, samples).
    /// The memory references a local buffer and is only valid for the
    /// duration of the synchronous handler — copy if retention is needed.
    /// </summary>
    public event Action<int, int, ReadOnlyMemory<float>>? RxAudioAvailable;

    private readonly object _engineLock = new();
    private IDspEngine? _engine;
    private int _channelId;
    private int _sampleRateHz;

    private Task? _iqPumpTask;
    private CancellationTokenSource? _iqPumpCts;

    // PureSignal feedback pump. Reads paired DDC0+DDC1 IQ from the active
    // protocol client and feeds the WDSP psccF entry once per 1024-sample
    // block. Lifecycle is tied to the connection (started on connect,
    // stopped on disconnect) — not to PsEnabled, because the radio sends
    // paired frames whenever the PS wire bit is set even before the WDSP
    // calcc state machine is armed.
    private Task? _psFeedbackPumpTask;
    private CancellationTokenSource? _psFeedbackPumpCts;

    // Protocol 2 path (parallel to the RadioService-owned P1 path). Held
    // directly here because RadioService is Protocol1Client-shaped and
    // growing a P2 variant there would require a larger refactor; for now
    // keeping it isolated avoids touching any P1 behavior.
    private Zeus.Protocol2.Protocol2Client? _p2Client;

    private RxMode _appliedMode = RxMode.USB;
    private int _appliedLowHz;
    private int _appliedHighHz;
    private int _appliedTxLowHz;
    private int _appliedTxHighHz;
    private double _appliedAgcTopDb;
    private double _appliedAgcOffsetDb;
    private double _appliedRxAfGainDb;
    private NrConfig _appliedNr = new();
    private int _appliedZoomLevel = 1;
    // PureSignal latched values — same change-detect pattern as the others
    // so OnRadioStateChanged only fires the (possibly heavy)
    // SetPsIntsAndSpi / SetPsRunCal calls when the value actually moves.
    private bool _appliedPsEnabled;
    private bool _appliedPsAuto = true;
    private bool _appliedPsSingle;
    private bool _appliedPsPtol;
    private double _appliedPsMoxDelaySec = 0.2;
    private double _appliedPsLoopDelaySec;
    private double _appliedPsAmpDelayNs = 150.0;
    private double _appliedPsHwPeak = 0.4072;
    private string _appliedPsIntsSpiPreset = "16/256";
    private PsFeedbackSource _appliedPsFeedbackSource = PsFeedbackSource.Internal;
    // PS-Monitor toggle (issue #121). Pure source-routing flag — Tick reads
    // it on each tick to choose between the TX analyzer (predistorted IQ)
    // and the PS-feedback analyzer (post-PA loopback IQ). volatile because
    // OnRadioStateChanged writes from the state-handler thread and Tick
    // reads from the pipeline thread — no compound mutation, just a bool.
    private volatile bool _psMonitorEnabled;
    private long _psMonitorTickCount;
    // TX Monitor latch (issue #106 follow-up). Same change-detect pattern as
    // _psMonitorEnabled — UpdateState writes when StateDto.TxMonitorEnabled
    // flips, and the latch fires engine.SetTxMonitorEnabled exactly once per
    // edge so we don't spam the engine on every tick with the same value.
    private bool _appliedTxMonitorEnabled;
    // Set by DisconnectP2Async so the next OnRadioStateChanged after a
    // fresh ConnectP2Async re-pushes every PS field regardless of equality
    // — necessary because the new WdspDspEngine instance starts with field
    // defaults that don't match the cached `_appliedPs*` state.
    private bool _psResyncRequired;
    // TwoTone latched fields (protocol-agnostic, drives PostGen mode=1).
    private bool _appliedTwoToneEnabled;
    private double _appliedTwoToneFreq1 = 700.0;
    private double _appliedTwoToneFreq2 = 1900.0;
    private double _appliedTwoToneMag = 0.49;
    // CFC (Continuous Frequency Compressor) — issue #123. Default-OFF so a
    // fresh state-change push (no Cfc field on the wire) doesn't flip the
    // engine into a partial config. _psResyncRequired piggybacks: when a P2
    // reconnect tears down the engine, we re-push the CFC profile too so the
    // new WdspDspEngine instance picks up the operator's persisted config.
    private CfcConfig _appliedCfc = CfcConfig.Default;

    // RX front-end (step attenuator + Mercury preamp). Mirrored to a live
    // Protocol2Client when the value moves; on P1 these go through
    // RadioService.ActiveClient directly. Issue #126 — without this
    // forwarding the S-ATT slider and PRE button were inert on Angelia /
    // ANAN-100D. Effective atten = StateDto.AttenDb + AttOffsetDb (auto-ATT
    // offset), so the existing overload control loop continues to drive the
    // radio on P2. Sentinel -1 forces the first push regardless of value.
    private int _appliedEffectiveAttDb = -1;
    private bool _appliedPreampOn;

    private uint _seq;
    private uint _audioSeq;
    // Latched from MoxChanged so Tick can route the panadapter to the TX
    // analyzer during keying without snapshotting RadioService. TUN also flips
    // MOX on (TxService.cs:153-155), so this single flag covers both paths —
    // see issue #81. volatile because MoxChanged fires on the caller's thread
    // and Tick reads from the pipeline thread.
    private volatile bool _keyed;
    // RX S-meter broadcast throttle. Pipeline ticks at 30 Hz; broadcasting
    // every 6 ticks = 5 Hz gives a smoother meter than Thetis's 4 Hz baseline
    // without spamming the WS (30 Hz dBm readouts add nothing a UI can use).
    private int _rxMeterTickMod;
    private const int RxMeterTickModulus = 6;

    public DspPipelineService(RadioService radio, StreamingHub hub, ILoggerFactory loggerFactory)
    {
        _radio = radio;
        _hub = hub;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<DspPipelineService>();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        OpenSynthetic();
        _radio.Connected += OnRadioConnected;
        _radio.Disconnected += OnRadioDisconnected;
        _radio.StateChanged += OnRadioStateChanged;
        _radio.PaSnapshotChanged += OnPaSnapshotChanged;
        _radio.MoxChanged += OnRadioMoxChanged;
        _radio.TunActiveChanged += OnRadioTunActiveChanged;
        _radio.PreampChanged += OnRadioPreampChanged;
        // Wire up Auto-AGC: feed RX meter readings to RadioService control loop
        RxMeterUpdated += (channelId, dbm) => _radio.HandleRxMeterForAutoAgc(dbm, Environment.TickCount64);

        var panBuf = new float[Width];
        var wfBuf = new float[Width];
        var audioBuf = new float[AudioDrainCapacity];
        using var timer = new PeriodicTimer(TickPeriod);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                Tick(panBuf, wfBuf, audioBuf);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _radio.Connected -= OnRadioConnected;
            _radio.Disconnected -= OnRadioDisconnected;
            _radio.StateChanged -= OnRadioStateChanged;
            _radio.PaSnapshotChanged -= OnPaSnapshotChanged;
            _radio.MoxChanged -= OnRadioMoxChanged;
            _radio.TunActiveChanged -= OnRadioTunActiveChanged;
            _radio.PreampChanged -= OnRadioPreampChanged;
            await StopIqPumpAsync().ConfigureAwait(false);
            CloseCurrentEngine();
        }
    }

    public void SetMox(bool on)
    {
        IDspEngine? engine;
        lock (_engineLock) { engine = _engine; }
        // SyntheticDspEngine.SetMox is a no-op per the interface contract;
        // we still forward so the engine type stays opaque to TxService.
        engine?.SetMox(on);
    }

    public void SetTxTune(bool on)
    {
        IDspEngine? engine;
        lock (_engineLock) { engine = _engine; }
        engine?.SetTxTune(on);
    }

    /// <summary>Current engine snapshot (may be <see cref="SyntheticDspEngine"/>
    /// while disconnected). TxAudioIngest calls ProcessTxBlock on this; the
    /// engine handles a disposed-during-call race internally by returning 0.
    /// Virtual so tests can subclass this service and substitute a stub engine
    /// without running the full Synthetic/WDSP lifecycle.</summary>
    public virtual IDspEngine? CurrentEngine
    {
        get { lock (_engineLock) return _engine; }
    }

    /// <summary>Raised after the engine instance is swapped (Synthetic ↔ WDSP).
    /// VstHostHostedService subscribes and re-installs its chain handler on
    /// the new engine. Subscribers receive the new <see cref="IDspEngine"/>
    /// (never null).</summary>
    public event Action<IDspEngine>? EngineChanged;

    private void RaiseEngineChanged(IDspEngine engine)
    {
        try { EngineChanged?.Invoke(engine); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "dsp.pipeline EngineChanged subscriber threw");
        }
    }

    /// <summary>Snapshot of the active Protocol2 client, or null on P1 / no
    /// connection. Exposed for the PS auto-attenuate service which needs to
    /// call <c>SetTxAttenuationDb</c> on the same client this pipeline is
    /// driving. Non-virtual — auto-attenuate is hard-gated on a P2 connection
    /// and tests don't exercise it.</summary>
    public Zeus.Protocol2.Protocol2Client? CurrentP2Client => _p2Client;

    private void OpenSynthetic()
    {
        var engine = new SyntheticDspEngine();
        int channelId = engine.OpenChannel(SyntheticSampleRateHz, Width);
        ApplyStateToNewChannel(engine, channelId);
        lock (_engineLock)
        {
            _engine = engine;
            _channelId = channelId;
            _sampleRateHz = SyntheticSampleRateHz;
        }
        _log.LogInformation("dsp.pipeline engine=synthetic channel={Id}", channelId);
        RaiseEngineChanged(engine);
    }

    private void OnRadioConnected(IProtocol1Client client)
    {
        var state = _radio.Snapshot();
        int rate = state.SampleRate;

        var wdsp = new WdspDspEngine(_loggerFactory.CreateLogger<WdspDspEngine>());
        int channelId = wdsp.OpenChannel(rate, Width);
        // P1 DAC runs at 48 kHz; keep TXA at the 48/48/48 profile Hermes is
        // calibrated against.
        wdsp.OpenTxChannel(outputRateHz: 48_000);
        ApplyStateToNewChannel(wdsp, channelId);

        IDspEngine? old;
        int oldChannel;
        lock (_engineLock)
        {
            old = _engine;
            oldChannel = _channelId;
            _engine = wdsp;
            _channelId = channelId;
            _sampleRateHz = rate;
        }

        TeardownEngine(old, oldChannel);
        _log.LogInformation("dsp.pipeline engine=wdsp channel={Id} rate={Rate}", channelId, rate);
        RaiseEngineChanged(wdsp);

        StartIqPump(client);
        StartPsFeedbackPumpP1(client);
        // Force the next OnRadioStateChanged to re-push every PS field into
        // the freshly-opened WdspDspEngine instance — same rationale as the
        // P2 reconnect path. Without this, a P1 reconnect leaves the engine
        // sitting at field defaults (hwPeak=0.4072) and calcc never sees
        // the operator's HL2 0.233 / hardware-correct numbers.
        _psResyncRequired = true;
        _appliedTxMonitorEnabled = false;
        // Apply the per-board PS HW peak default so the engine sees the
        // right curve scale before the operator arms PS. Mirrors P2's
        // ApplyPsHwPeakForConnection call. ConnectedBoardKind returns the
        // currently-active board (HL2, Hermes, ANAN-class…) — the value
        // is per-board (HL2 → 0.233, others → 0.4072) and only fires a
        // StateChanged when the value actually changes.
        _radio.ApplyPsHwPeakForConnection(isProtocol2: false, _radio.ConnectedBoardKind);
    }

    private void OnRadioDisconnected()
    {
        StopIqPumpAsync().GetAwaiter().GetResult();

        var synth = new SyntheticDspEngine();
        int channelId = synth.OpenChannel(SyntheticSampleRateHz, Width);
        ApplyStateToNewChannel(synth, channelId);

        IDspEngine? old;
        int oldChannel;
        lock (_engineLock)
        {
            old = _engine;
            oldChannel = _channelId;
            _engine = synth;
            _channelId = channelId;
            _sampleRateHz = SyntheticSampleRateHz;
        }

        TeardownEngine(old, oldChannel);
        _log.LogInformation("dsp.pipeline engine=synthetic channel={Id}", channelId);
        RaiseEngineChanged(synth);
    }

    private void OnRadioStateChanged(StateDto s)
    {
        // Forward VFO changes to the P2 client when it's active. RadioService
        // does this for P1 via ActiveClient?.SetVfoAHz() inside SetVfo, but
        // ActiveClient is null for P2 connections, so the radio never learns
        // about tune changes without this forward. Sample rate / mode follow
        // here too when P2-side support is added.
        var p2 = _p2Client;
        p2?.SetVfoAHz(CwOffset.EffectiveLoHz(s));

        IDspEngine? engine;
        int channel;
        lock (_engineLock) { engine = _engine; channel = _channelId; }
        if (engine is null) return;

        if (s.Mode != _appliedMode)
        {
            engine.SetMode(channel, s.Mode);
            // Keep TXA modulator mode in sync with the RX side. On Synthetic
            // and before OpenTxChannel has run this is a no-op.
            engine.SetTxMode(s.Mode);
            _appliedMode = s.Mode;
        }
        if (s.FilterLowHz != _appliedLowHz || s.FilterHighHz != _appliedHighHz)
        {
            engine.SetFilter(channel, s.FilterLowHz, s.FilterHighHz);
            _appliedLowHz = s.FilterLowHz;
            _appliedHighHz = s.FilterHighHz;
        }
        if (s.TxFilterLowHz != _appliedTxLowHz || s.TxFilterHighHz != _appliedTxHighHz)
        {
            engine.SetTxFilter(s.TxFilterLowHz, s.TxFilterHighHz);
            _appliedTxLowHz = s.TxFilterLowHz;
            _appliedTxHighHz = s.TxFilterHighHz;
        }
        if (s.AgcTopDb != _appliedAgcTopDb || s.AgcOffsetDb != _appliedAgcOffsetDb)
        {
            double effectiveAgc = s.AgcTopDb + s.AgcOffsetDb;
            engine.SetAgcTop(channel, effectiveAgc);
            _appliedAgcTopDb = s.AgcTopDb;
            _appliedAgcOffsetDb = s.AgcOffsetDb;
        }
        if (s.RxAfGainDb != _appliedRxAfGainDb)
        {
            engine.SetRxAfGainDb(channel, s.RxAfGainDb);
            _appliedRxAfGainDb = s.RxAfGainDb;
        }
        var nr = s.Nr ?? new NrConfig();
        if (!nr.Equals(_appliedNr))
        {
            engine.SetNoiseReduction(channel, nr);
            _appliedNr = nr;
        }
        if (s.ZoomLevel != _appliedZoomLevel)
        {
            engine.SetZoom(channel, s.ZoomLevel);
            _appliedZoomLevel = s.ZoomLevel;
        }

        // ---- TwoTone (protocol-agnostic; PostGen mode=1 inside TXA) ----
        // TwoTone is safe on P1 even though PS itself is P2-only in v1
        // because it touches only the TXA stage, not the wire format.
        if (s.TwoToneEnabled != _appliedTwoToneEnabled
            || s.TwoToneFreq1 != _appliedTwoToneFreq1
            || s.TwoToneFreq2 != _appliedTwoToneFreq2
            || s.TwoToneMag != _appliedTwoToneMag)
        {
            engine.SetTwoTone(s.TwoToneEnabled, s.TwoToneFreq1, s.TwoToneFreq2, s.TwoToneMag);
            _appliedTwoToneEnabled = s.TwoToneEnabled;
            _appliedTwoToneFreq1 = s.TwoToneFreq1;
            _appliedTwoToneFreq2 = s.TwoToneFreq2;
            _appliedTwoToneMag = s.TwoToneMag;
        }

        // ---- PureSignal ----
        // Apply HW-peak first because SetPsAdvanced may also touch it; then
        // advanced timing/preset; then control mode; then master arm last so
        // the engine is fully configured before the cal state machine starts.
        // _psResyncRequired (set by DisconnectP2Async) forces every push on
        // the first state-change after a P2 reconnect so the new engine
        // instance picks up the canonical state instead of running on its
        // field defaults.
        bool resync = _psResyncRequired;
        if (resync || s.PsHwPeak != _appliedPsHwPeak)
        {
            engine.SetPsHwPeak(s.PsHwPeak);
            _appliedPsHwPeak = s.PsHwPeak;
        }
        if (resync
            || s.PsPtol != _appliedPsPtol
            || s.PsMoxDelaySec != _appliedPsMoxDelaySec
            || s.PsLoopDelaySec != _appliedPsLoopDelaySec
            || s.PsAmpDelayNs != _appliedPsAmpDelayNs
            || s.PsIntsSpiPreset != _appliedPsIntsSpiPreset)
        {
            (int ints, int spi) = ParseIntsSpi(s.PsIntsSpiPreset);
            engine.SetPsAdvanced(
                s.PsPtol,
                s.PsMoxDelaySec,
                s.PsLoopDelaySec,
                s.PsAmpDelayNs,
                s.PsHwPeak,
                ints,
                spi);
            _appliedPsPtol = s.PsPtol;
            _appliedPsMoxDelaySec = s.PsMoxDelaySec;
            _appliedPsLoopDelaySec = s.PsLoopDelaySec;
            _appliedPsAmpDelayNs = s.PsAmpDelayNs;
            _appliedPsIntsSpiPreset = s.PsIntsSpiPreset;
        }
        if (resync || s.PsAuto != _appliedPsAuto || s.PsSingle != _appliedPsSingle)
        {
            engine.SetPsControl(s.PsAuto, s.PsSingle);
            _appliedPsAuto = s.PsAuto;
            _appliedPsSingle = s.PsSingle;
        }
        if (resync || s.PsEnabled != _appliedPsEnabled)
        {
            // pihpsdr transmitter.c:2467-2473 inverts the order: write the
            // wire (RxSpec / HighPriority with PS bits set) FIRST, then sleep
            // 100 ms to let the radio firmware spin up DDC0/DDC1 sync, then
            // arm the engine. Without the settle window, the first 5-20
            // pscc calls receive partial / glitched samples, scheck flags
            // binfo[6], bs_count climbs to 2, calcc resets to LRESET — and
            // the loop sometimes thrashes instead of converging.
            //
            // Disarm path stays engine-first: drop the engine run flag, then
            // close the wire, then drain any in-flight paired frames so they
            // don't arrive after PS has shut down.
            //
            // Task.Delay(100).Wait() is acceptable here — OnRadioStateChanged
            // runs on a state-change handler thread, not the request path.
            //
            // P1 sibling (issue #172): the active P1 client gets the same
            // arm/disarm sequencing — flip the wire bit (which also
            // bumps NumReceiversMinusOne in the next Config frame so the
            // gateware switches to the 2-DDC paired layout), wait the
            // same 100 ms settle window, then arm the engine. On a
            // non-HL2 P1 board this is harmless: SetPsEnabled stores the
            // flag locally and the C0=0x14 wire byte is unaffected
            // (board-gated in WriteAttenuatorPayload).
            var p1Active = _radio.ActiveClient;
            if (s.PsEnabled)
            {
                _p2Client?.SetPsFeedbackEnabled(true);
                p1Active?.SetPsEnabled(true);
                try { Task.Delay(100).Wait(); } catch { /* ignore */ }
                engine.SetPsEnabled(true);
            }
            else
            {
                engine.SetPsEnabled(false);
                _p2Client?.SetPsFeedbackEnabled(false);
                p1Active?.SetPsEnabled(false);
                DrainPsFeedback();
            }
            _appliedPsEnabled = s.PsEnabled;
        }
        if (resync || s.PsFeedbackSource != _appliedPsFeedbackSource)
        {
            // Wire-only change — flips ALEX_RX_ANTENNA_BYPASS in alex0 on
            // the next CmdHighPriority emission. WDSP is unaffected.
            _p2Client?.SetPsFeedbackSource(s.PsFeedbackSource == PsFeedbackSource.External);
            _appliedPsFeedbackSource = s.PsFeedbackSource;
        }

        // ---- CFC (Continuous Frequency Compressor) ---------------------
        // issue #123. Same resync rule as PS: a P2 disconnect tears down the
        // engine, so the next state-change push has to re-assert the operator
        // CFC config even when the StateDto value hasn't changed. Equality
        // check uses CfcConfig record value semantics (the Bands array length
        // is fixed at 10, contents compared element-wise via the auto-record
        // Equals — but `record` only does reference equality on arrays, so
        // value-compare manually). null on the wire (legacy state frame)
        // falls back to CfcConfig.Default → engine sees a clean OFF profile.
        var cfc = s.Cfc ?? CfcConfig.Default;
        if (resync || !CfcConfigsEqual(cfc, _appliedCfc))
        {
            engine.SetCfcConfig(cfc);
            _appliedCfc = cfc;
        }

        // ---- RX step attenuator (operator + auto-ATT offset) -----------
        // Issue #126. Mirror RadioService's effective-atten composition
        // (operator baseline AttenDb + auto-ATT overload offset AttOffsetDb,
        // clamped 0..31) onto a live Protocol2Client. RadioService already
        // pushes the same value to the P1 client directly via
        // ActiveClient?.SetAttenuator on every operator change AND every
        // auto-ATT tick — but on a P2 connection ActiveClient is null, so
        // without this forward the S-ATT slider and the auto-ATT overload
        // ramp both fail silently on Angelia / ANAN-100D. RadioService
        // raises StateChanged whenever AttOffsetDb moves, so the auto-ATT
        // control loop reaches the wire through this block too.
        int effectiveAttDb = Math.Clamp(s.AttenDb + s.AttOffsetDb, 0, 31);
        if (resync || effectiveAttDb != _appliedEffectiveAttDb)
        {
            _p2Client?.SetAttenuator(effectiveAttDb);
            _appliedEffectiveAttDb = effectiveAttDb;
        }

        // PS-Monitor (issue #121) — pure UI source routing. No engine call,
        // no wire write; Tick reads _psMonitorEnabled and prefers the
        // PS-feedback analyzer when on + PS armed + correcting. Latched
        // here so the volatile read in Tick stays cheap.
        if (_psMonitorEnabled != s.PsMonitorEnabled)
        {
            _log.LogInformation("psMonitor.latch enabled={Enabled}", s.PsMonitorEnabled);
            _psMonitorEnabled = s.PsMonitorEnabled;
        }

        // TX Monitor (issue #106 follow-up) — engages the engine's parallel
        // demod path on the post-CFIR TX IQ. Edge-triggered call to the
        // engine so a re-tick with the same flag is a no-op. The engine
        // tolerates being called before TXA is open (lazy-open inside) so
        // ordering vs SetTxMode/SetTxFilter above doesn't matter.
        if (_appliedTxMonitorEnabled != s.TxMonitorEnabled)
        {
            _log.LogInformation("txMonitor.latch enabled={Enabled}", s.TxMonitorEnabled);
            engine.SetTxMonitorEnabled(s.TxMonitorEnabled);
            _appliedTxMonitorEnabled = s.TxMonitorEnabled;
        }

        // Resync done — clear the flag so subsequent state changes use
        // normal change-detect (no spurious wire writes on each tick).
        _psResyncRequired = false;
    }

    // CfcConfig auto-generated record Equals does reference equality on the
    // Bands array, which would always trigger a re-push on every tick where
    // the panel rebuilt the array. Explicit element-wise compare so a no-op
    // POST round-trip stays cheap.
    private static bool CfcConfigsEqual(CfcConfig a, CfcConfig b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Enabled != b.Enabled) return false;
        if (a.PostEqEnabled != b.PostEqEnabled) return false;
        if (a.PreCompDb != b.PreCompDb) return false;
        if (a.PrePeqDb != b.PrePeqDb) return false;
        if (a.Bands is null || b.Bands is null) return ReferenceEquals(a.Bands, b.Bands);
        if (a.Bands.Length != b.Bands.Length) return false;
        for (int i = 0; i < a.Bands.Length; i++)
        {
            if (a.Bands[i].FreqHz != b.Bands[i].FreqHz) return false;
            if (a.Bands[i].CompLevelDb != b.Bands[i].CompLevelDb) return false;
            if (a.Bands[i].PostGainDb != b.Bands[i].PostGainDb) return false;
        }
        return true;
    }

    // "16/256" → (16, 256). Falls back to (16, 256) on any parse failure
    // because that's the only ints/spi pair WDSP allows save/restore on
    // (Thetis PSForm.cs:865) — a safe default.
    private static (int Ints, int Spi) ParseIntsSpi(string preset)
    {
        if (string.IsNullOrWhiteSpace(preset)) return (16, 256);
        var slash = preset.IndexOf('/');
        if (slash <= 0) return (16, 256);
        if (!int.TryParse(preset.AsSpan(0, slash), out int ints)) return (16, 256);
        if (!int.TryParse(preset.AsSpan(slash + 1), out int spi)) return (16, 256);
        if (ints <= 0 || spi <= 0) return (16, 256);
        return (ints, spi);
    }

    private void ApplyStateToNewChannel(IDspEngine engine, int channelId)
    {
        var s = _radio.Snapshot();
        var nr = s.Nr ?? new NrConfig();
        engine.SetMode(channelId, s.Mode);
        // Sync TXA modulator with RX mode at engine-open time so the first
        // key-down lands with the correct sideband (no-op on Synthetic / pre-
        // OpenTxChannel).
        engine.SetTxMode(s.Mode);
        engine.SetFilter(channelId, s.FilterLowHz, s.FilterHighHz);
        engine.SetTxFilter(s.TxFilterLowHz, s.TxFilterHighHz);
        engine.SetVfoHz(channelId, s.VfoHz);
        double effectiveAgc = s.AgcTopDb + s.AgcOffsetDb;
        engine.SetAgcTop(channelId, effectiveAgc);
        engine.SetRxAfGainDb(channelId, s.RxAfGainDb);
        engine.SetNoiseReduction(channelId, nr);
        engine.SetZoom(channelId, s.ZoomLevel);
        _appliedMode = s.Mode;
        _appliedLowHz = s.FilterLowHz;
        _appliedHighHz = s.FilterHighHz;
        _appliedTxLowHz = s.TxFilterLowHz;
        _appliedTxHighHz = s.TxFilterHighHz;
        _appliedAgcTopDb = s.AgcTopDb;
        _appliedAgcOffsetDb = s.AgcOffsetDb;
        _appliedRxAfGainDb = s.RxAfGainDb;
        _appliedNr = nr;
        _appliedZoomLevel = s.ZoomLevel;
    }

    private void StartIqPump(IProtocol1Client client)
    {
        var cts = new CancellationTokenSource();
        _iqPumpCts = cts;
        _iqPumpTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in client.IqFrames.ReadAllAsync(cts.Token).ConfigureAwait(false))
                {
                    IDspEngine? engine;
                    int channel;
                    lock (_engineLock) { engine = _engine; channel = _channelId; }
                    engine?.FeedIq(channel, frame.InterleavedSamples.Span);
                    RxIqAvailable?.Invoke(0, frame.SampleRateHz, frame.InterleavedSamples);
                    // Return the underlying double[] to ArrayPool now that the
                    // frame has been consumed. Protocol1Client.RxLoop rents
                    // ~2 KB per packet from ArrayPool<double>.Shared and the
                    // contract on RxIqAvailable says the memory is only valid
                    // for the duration of the synchronous handler — so once
                    // FeedIq + the event have returned, the buffer is dead.
                    // Without this return the rented arrays drop straight to
                    // GC at ~381/s on HL2 (≈750 KB/s of gen0 garbage),
                    // forcing the pool to allocate fresh on the next Rent.
                    if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(
                            frame.InterleavedSamples, out var seg) && seg.Array is { } arr)
                    {
                        System.Buffers.ArrayPool<double>.Shared.Return(arr);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "dsp.pipeline iq-pump exited with error");
            }
        }, cts.Token);
    }

    private void StartIqPumpP2(Zeus.Protocol2.Protocol2Client client)
    {
        var cts = new CancellationTokenSource();
        _iqPumpCts = cts;
        _iqPumpTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in client.IqFrames.ReadAllAsync(cts.Token).ConfigureAwait(false))
                {
                    IDspEngine? engine;
                    int channel;
                    lock (_engineLock) { engine = _engine; channel = _channelId; }
                    engine?.FeedIq(channel, frame.InterleavedSamples.Span);
                    RxIqAvailable?.Invoke(0, frame.SampleRateHz, frame.InterleavedSamples);
                }
            }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "dsp.pipeline p2 iq-pump exited with error");
            }
        }, cts.Token);
    }

    // PureSignal feedback pump (P2). Reads 1024-sample paired blocks from the
    // Protocol2Client and hands them to the WDSP `psccF` entry. Runs whether
    // or not PS is armed — the engine drops blocks internally when SetPsRunCal
    // is 0, so steady-state cost is one P/Invoke per 5.3 ms (1024 / 192 kHz).
    private void StartPsFeedbackPumpP2(Zeus.Protocol2.Protocol2Client client)
    {
        var cts = new CancellationTokenSource();
        _psFeedbackPumpCts = cts;
        _psFeedbackPumpTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in client.PsFeedbackFrames.ReadAllAsync(cts.Token).ConfigureAwait(false))
                {
                    IDspEngine? engine;
                    lock (_engineLock) { engine = _engine; }
                    engine?.FeedPsFeedbackBlock(frame.TxI, frame.TxQ, frame.RxI, frame.RxQ);
                }
            }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "dsp.pipeline p2 ps-feedback-pump exited with error");
            }
        }, cts.Token);
    }

    /// <summary>
    /// HL2 / Protocol-1 sibling of <see cref="StartPsFeedbackPumpP2"/>.
    /// Reads 1024-sample paired blocks emitted by the
    /// <see cref="IProtocol1Client.PsFeedbackFrames"/> channel and pushes
    /// them into WDSP's <c>psccF</c> via the engine's
    /// <c>FeedPsFeedbackBlock</c>. Same lifecycle as the P2 pump:
    /// started on connect, stopped on disconnect — NOT gated on PsEnabled,
    /// because the radio sends paired frames whenever the wire bit is
    /// set and the engine drops blocks internally when SetPsRunCal is 0
    /// (see lessons_puresignal_convergence_g2_mkii.md). Issue #172.
    /// </summary>
    private void StartPsFeedbackPumpP1(IProtocol1Client client)
    {
        var cts = new CancellationTokenSource();
        _psFeedbackPumpCts = cts;
        _psFeedbackPumpTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in client.PsFeedbackFrames.ReadAllAsync(cts.Token).ConfigureAwait(false))
                {
                    IDspEngine? engine;
                    lock (_engineLock) { engine = _engine; }
                    engine?.FeedPsFeedbackBlock(frame.TxI, frame.TxQ, frame.RxI, frame.RxQ);
                }
            }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "dsp.pipeline p1 ps-feedback-pump exited with error");
            }
        }, cts.Token);
    }

    private async Task StopPsFeedbackPumpAsync()
    {
        var cts = _psFeedbackPumpCts;
        var task = _psFeedbackPumpTask;
        _psFeedbackPumpCts = null;
        _psFeedbackPumpTask = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        cts.Dispose();
    }

    // Best-effort drain of in-flight paired frames after PS disarm. Called
    // synchronously from OnRadioStateChanged so by the time the next state
    // change tries to re-arm, the channel is empty. The pump task itself is
    // not stopped — only the buffered backlog is drained. Drains either
    // active client (P1 or P2 — only one is non-null at a time).
    private void DrainPsFeedback()
    {
        var p2 = _p2Client;
        if (p2 is not null)
        {
            var reader = p2.PsFeedbackFrames;
            while (reader.TryRead(out _)) { }
            return;
        }
        var p1 = _radio.ActiveClient;
        if (p1 is not null)
        {
            var reader = p1.PsFeedbackFrames;
            while (reader.TryRead(out _)) { }
        }
    }

    /// <summary>
    /// Connect to a Protocol 2 radio and start streaming RX IQ into the DSP
    /// engine. Parallel path to RadioService.ConnectAsync (which is Protocol 1
    /// only); both swap the engine to WDSP and start a pump. Only one client
    /// at a time.
    /// </summary>
    public async Task ConnectP2Async(
        IPEndPoint radioEndpoint,
        int sampleRateKhz,
        byte numAdc,
        CancellationToken ct,
        HpsdrBoardKind boardKind = HpsdrBoardKind.Unknown)
    {
        if (_p2Client is not null)
            throw new InvalidOperationException("Already connected (P2).");
        if (_radio.ActiveClient is not null)
            throw new InvalidOperationException("Already connected (P1). Disconnect first.");

        var client = new Zeus.Protocol2.Protocol2Client(
            _loggerFactory.CreateLogger<Zeus.Protocol2.Protocol2Client>());
        client.SetNumAdc(numAdc);
        await client.ConnectAsync(radioEndpoint, ct).ConfigureAwait(false);
        // Seed the operator's RX front-end (preamp + step attenuator) BEFORE
        // StartAsync so the very first CmdHighPriority emitted inside the
        // start sequence carries the correct values. SetPreamp/SetAttenuator
        // pre-StartAsync only stash into private fields (the early-return on
        // _rxTask==null path), so no wire packets fly here — they ride the
        // CmdHighPriority(run=1) inside StartAsync below. Without this seed
        // a P2 reconnect would leave the radio at preamp=off / atten=0
        // until the operator nudged either control. Issue #126.
        bool initialPreamp = _radio.PreampOn;
        int initialAttDb = _radio.EffectiveAttenDb;
        client.SetPreamp(initialPreamp);
        client.SetAttenuator(initialAttDb);
        await client.StartAsync(sampleRateKhz, ct).ConfigureAwait(false);

        int rateHz = sampleRateKhz * 1000;
        IDspEngine newEngine;
        int newChannelId;
        try
        {
            var wdsp = new WdspDspEngine(_loggerFactory.CreateLogger<WdspDspEngine>());
            newChannelId = wdsp.OpenChannel(rateHz, Width);
            // G2 MkII DUC on P2 expects 192 kHz TX IQ. WDSP upsamples internally
            // (48k mic → 96k DSP → 192k out) and CFIR compensates the sinc
            // droop. Feeding 48 kHz IQ to a 192 kHz DUC as we did before
            // produced 8-10 kHz close-in spurs around the carrier.
            wdsp.OpenTxChannel(outputRateHz: 192_000);
            // Best-effort apply. Some local WDSP builds are missing newer
            // entry points (e.g. SetRXAEMNRpost2Run); the channel itself is
            // open and capable of spectrum work even if a noise-reduction
            // toggle can't be set. Narrow catch so a genuinely broken engine
            // still surfaces via the outer handler.
            try { ApplyStateToNewChannel(wdsp, newChannelId); }
            catch (EntryPointNotFoundException ex)
            {
                _log.LogWarning(ex, "dsp.pipeline p2 wdsp missing entry point — partial config applied");
            }
            newEngine = wdsp;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "dsp.pipeline p2 wdsp open failed, falling back to synthetic engine");
            var synth = new SyntheticDspEngine();
            newChannelId = synth.OpenChannel(rateHz, Width);
            try { ApplyStateToNewChannel(synth, newChannelId); }
            catch (EntryPointNotFoundException) { }
            newEngine = synth;
        }

        IDspEngine? old;
        int oldChannel;
        lock (_engineLock)
        {
            old = _engine;
            oldChannel = _channelId;
            _engine = newEngine;
            _channelId = newChannelId;
            _sampleRateHz = rateHz;
        }
        TeardownEngine(old, oldChannel);
        _log.LogInformation("dsp.pipeline p2 engine={Engine} rate={Rate}", newEngine.GetType().Name, rateHz);
        RaiseEngineChanged(newEngine);

        _p2Client = client;
        // Sync the change-detect cache with the values we just seeded so the
        // first OnRadioStateChanged after connect doesn't redundantly re-push
        // (which would emit a duplicate CmdHighPriority). Re-read in case the
        // operator changed either control during the connect window — the
        // PreampChanged / StateChanged handlers would have early-returned on
        // _p2Client==null. Comparing here recovers any drift before the cache
        // settles.
        _appliedPreampOn = initialPreamp;
        _appliedEffectiveAttDb = initialAttDb;
        bool nowPreamp = _radio.PreampOn;
        int nowAttDb = _radio.EffectiveAttenDb;
        if (nowPreamp != initialPreamp)
        {
            client.SetPreamp(nowPreamp);
            _appliedPreampOn = nowPreamp;
        }
        if (nowAttDb != initialAttDb)
        {
            client.SetAttenuator(nowAttDb);
            _appliedEffectiveAttDb = nowAttDb;
        }
        StartIqPumpP2(client);
        StartPsFeedbackPumpP2(client);
        // Force the next OnRadioStateChanged to re-push every PS field into
        // the freshly-opened WdspDspEngine instance, regardless of whether
        // the canonical state in StateDto has changed since the prior
        // session. The new engine starts with field defaults (hwPeak=0.4072,
        // ptol=0.8, etc.) and the change-detect cache `_appliedPs*` doesn't
        // know that — without this flag the engine never gets the operator's
        // settings back, calcc runs on wrong hw_scale, and PS doesn't
        // converge after a reconnect. See `project_ps_reconnect_state_loss.md`.
        _psResyncRequired = true;
        // TX-monitor: same re-push problem as PS — the new engine starts at
        // monitor=off, so if the operator had it on the latch's change-detect
        // would skip the push. Reset the latch so the next UpdateState fires.
        _appliedTxMonitorEnabled = false;
        // Pass the live client so RadioService can fire P2Connected with a
        // reference to the freshly-opened Protocol2Client. TxMetersService
        // subscribes through that event to hook hi-priority status (#174).
        _radio.MarkProtocol2Connected(radioEndpoint.ToString(), rateHz, client, boardKind);
        // P2 G2/MkII default HW peak = 0.6121; ANAN-7000/8000 = 0.2899. The
        // RadioService switch covers both so we don't bake a value in here.
        // ConnectedBoardKind now returns the discovered board kind when the
        // caller plumbed it through (issue #171); falls back to OrionMkII when
        // the byte wasn't supplied.
        _radio.ApplyPsHwPeakForConnection(isProtocol2: true, _radio.ConnectedBoardKind);
        // Push current PA snapshot into the brand-new client so byte 345 /
        // byte 1401 / CmdGeneral[58] reflect PaSettingsStore from frame 1.
        _radio.ReplayPaSnapshot();
    }

    private void OnPaSnapshotChanged(PaRuntimeSnapshot snap)
    {
        var p2 = _p2Client;
        if (p2 is null) return;
        p2.SetDriveByte(snap.DriveByte);
        p2.SetOcMasks(snap.OcTxMask, snap.OcRxMask);
        p2.SetPaEnabled(snap.PaEnabled);
    }

    private void OnRadioMoxChanged(bool on)
    {
        _keyed = on;
        _p2Client?.SetMox(on);
    }

    private void OnRadioTunActiveChanged(bool on)
    {
        _p2Client?.SetTune(on);
    }

    // Mirror operator preamp toggles into a live Protocol2Client. P1 is
    // pushed by RadioService.SetPreamp directly via ActiveClient. PreampOn
    // isn't on the StateDto wire format, so this event-driven path is the
    // only way the bit reaches CmdHighPriority byte 1403 on P2 (issue #126).
    private void OnRadioPreampChanged(bool on)
    {
        var p2 = _p2Client;
        if (p2 is null) return;
        if (on == _appliedPreampOn) return;
        p2.SetPreamp(on);
        _appliedPreampOn = on;
    }

    /// <summary>
    /// Forward a WDSP TXA block of interleaved float IQ to the live P2 client.
    /// No-op when P2 isn't connected; safe to call from TxTuneDriver / future
    /// mic-MOX feeders without branching on protocol.
    /// </summary>
    public void ForwardTxIqToP2(ReadOnlySpan<float> iqInterleaved)
    {
        _p2Client?.SendTxIq(iqInterleaved);
    }

    public async Task DisconnectP2Async(CancellationToken ct)
    {
        var client = _p2Client;
        _p2Client = null;
        if (client is null) return;

        await StopIqPumpAsync().ConfigureAwait(false);
        await StopPsFeedbackPumpAsync().ConfigureAwait(false);
        try { await client.StopAsync(ct).ConfigureAwait(false); } catch { }
        await client.DisposeAsync().ConfigureAwait(false);

        var synth = new SyntheticDspEngine();
        int channelId = synth.OpenChannel(SyntheticSampleRateHz, Width);
        ApplyStateToNewChannel(synth, channelId);

        IDspEngine? old;
        int oldChannel;
        lock (_engineLock)
        {
            old = _engine;
            oldChannel = _channelId;
            _engine = synth;
            _channelId = channelId;
            _sampleRateHz = SyntheticSampleRateHz;
        }
        TeardownEngine(old, oldChannel);
        RaiseEngineChanged(synth);
        // Mark PS state for forced re-push on the next ConnectP2Async. The
        // change-detect cache (`_appliedPs*`) is preserved across disconnect
        // — by design, so a reconnect with unchanged operator state doesn't
        // generate spurious wire writes — but a fresh WdspDspEngine starts
        // with field defaults (hwPeak=0.4072, ptol=0.8, etc.) that don't
        // match the canonical state. Without this flag, OnRadioStateChanged
        // skips every PS push because s.PsX == _appliedPsX, and the new
        // engine never gets the operator's settings. See
        // `project_ps_reconnect_state_loss.md` for the rack reproduction.
        _psResyncRequired = true;
        _appliedTxMonitorEnabled = false;
        _radio.MarkProtocol2Disconnected();
        _log.LogInformation("dsp.pipeline p2 disconnected, engine=synthetic");
    }

    public Zeus.Protocol2.Protocol2Client? ActiveP2Client => _p2Client;

    private async Task StopIqPumpAsync()
    {
        var cts = _iqPumpCts;
        var task = _iqPumpTask;
        _iqPumpCts = null;
        _iqPumpTask = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { /* logged at the source */ }
        }
        cts.Dispose();
    }

    private void CloseCurrentEngine()
    {
        IDspEngine? engine;
        int channel;
        lock (_engineLock)
        {
            engine = _engine;
            channel = _channelId;
            _engine = null;
            _channelId = 0;
        }
        TeardownEngine(engine, channel);
    }

    private static void TeardownEngine(IDspEngine? engine, int channelId)
    {
        if (engine is null) return;
        try { engine.CloseChannel(channelId); } catch { /* best-effort */ }
        engine.Dispose();
    }

    private void Tick(float[] panBuf, float[] wfBuf, float[] audioBuf)
    {
        IDspEngine? engine;
        int channel;
        int sampleRate;
        lock (_engineLock)
        {
            engine = _engine;
            channel = _channelId;
            sampleRate = _sampleRateHz;
        }
        if (engine is null) return;

        var state = _radio.Snapshot();
        // Synthetic engine stays open while disconnected so SetMode/SetFilter
        // etc. have somewhere to land, but its sweep+static placeholder used
        // to render a misleading "fake spectrum" before any radio existed.
        // Gate on the engine type rather than the connection status: status
        // flips to Connected before OnRadioConnected swaps the engine, and a
        // status-only check let one or two synthetic frames leak through that
        // race window — visible as a brief flash of the fake waterfall right
        // when the user clicked Connect. The synthetic engine never produces
        // real-radio data, so suppressing it unconditionally is correct.
        if (engine is SyntheticDspEngine) return;

        engine.SetVfoHz(channel, state.VfoHz);

        // While keyed (MOX or TUN — see _keyed comment) pull from the TX
        // analyzer so the panadapter shows the transmitted signal instead of
        // the RX front end's TX bleed (issue #81). If the TX analyzer isn't
        // ready (not yet produced an FFT, or engine doesn't have a TX
        // analyzer — e.g. Synthetic), TryGetTxDisplayPixels returns false and
        // we fall through to the RX analyzer, matching the pre-issue-#81
        // behaviour. This fallback also covers the first ~1 tick after
        // keying before the analyzer averaging has settled.
        //
        // Issue #121 layered on top: if the operator has the "Monitor PA
        // output" toggle on AND PS is armed AND PS has converged
        // (info[14]==1, surfaced via GetPsStageMeters().Correcting), prefer
        // the PS-feedback analyzer (post-PA loopback IQ). Falls back to the
        // TX analyzer if the PS-FB analyzer hasn't produced a fresh FFT yet
        // — same shape as the existing TX → RX fallback. Default-off
        // toggle: when off the codepath is identical to pre-#121, byte for
        // byte, on every board.
        bool pan = false, wf = false;
        bool psFbPanUsed = false, psFbWfUsed = false;
        if (_keyed)
        {
            if (_appliedPsEnabled && _psMonitorEnabled
                && engine.GetPsStageMeters().Correcting)
            {
                pan = engine.TryGetPsFeedbackDisplayPixels(DisplayPixout.Panadapter, panBuf);
                wf = engine.TryGetPsFeedbackDisplayPixels(DisplayPixout.Waterfall, wfBuf);
                psFbPanUsed = pan;
                psFbWfUsed = wf;
            }
            if (!pan) pan = engine.TryGetTxDisplayPixels(DisplayPixout.Panadapter, panBuf);
            if (!wf) wf = engine.TryGetTxDisplayPixels(DisplayPixout.Waterfall, wfBuf);
        }
        if (_keyed && _psMonitorEnabled)
        {
            _psMonitorTickCount++;
            if (_psMonitorTickCount % 30 == 0)
            {
                var m = engine.GetPsStageMeters();
                _log.LogInformation(
                    "psMonitor.gate keyed=1 psEn={PsEn} mon=1 corr={Corr} psFbPan={Pan} psFbWf={Wf}",
                    _appliedPsEnabled, m.Correcting, psFbPanUsed, psFbWfUsed);
            }
        }
        else
        {
            _psMonitorTickCount = 0;
        }
        if (!pan) pan = engine.TryGetDisplayPixels(channel, DisplayPixout.Panadapter, panBuf);
        if (!wf) wf = engine.TryGetDisplayPixels(channel, DisplayPixout.Waterfall, wfBuf);

        // Flip to display order (low freq left, high freq right). WDSP emits
        // pixel 0 = highest positive frequency — see doc 03 §10 and
        // doc 08 §3 "Pixel axis reversal". SyntheticDspEngine already emits
        // in WDSP order so this reversal applies to both engines. Guarded by
        // the freshness flag: TryGetDisplayPixels leaves the buffer untouched
        // when no new FFT is ready, so an unconditional reverse would alternate
        // the orientation on every stale tick and broadcast mirrored garbage
        // (still flagged invalid, but bandwidth wasted and timing-sensitive).
        if (pan) Array.Reverse(panBuf);
        if (wf) Array.Reverse(wfBuf);

        var flags = DisplayBodyFlags.None;
        if (pan) flags |= DisplayBodyFlags.PanValid;
        if (wf) flags |= DisplayBodyFlags.WfValid;

        // Zoom narrows the analyzer's display span to sampleRate/level around
        // the VFO, so hzPerPixel shrinks by the same factor. Client re-uses
        // this for axis labels and planWaterfallUpdate horizontal shift — no
        // extra contract field needed, per task #7 scope note.
        int zoomLevel = Math.Max(1, state.ZoomLevel);
        float hzPerPixel = (float)((double)sampleRate / zoomLevel / Width);
        double nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var frame = new DisplayFrame(
            Seq: ++_seq,
            TsUnixMs: nowMs,
            RxId: 0,
            BodyFlags: flags,
            Width: Width,
            // Panadapter centres on the radio's actual LO, which equals
            // VfoHz outside CW and VfoHz ∓ cw_pitch in CWU/CWL. The CW filter
            // (audio passband centred on cw_pitch) then renders on top of
            // the dial line via PassbandOverlay's `centerHz + filterLow..high`.
            CenterHz: CwOffset.EffectiveLoHz(state),
            HzPerPixel: hzPerPixel,
            PanDb: panBuf,
            WfDb: wfBuf);

        _hub.Broadcast(frame);

        // Audio broadcast — when TX monitor is on, replace RX audio with the
        // monitor channel's demodulated TX audio so the operator hears the
        // chain output (post-bandpass / post-CFIR, demodulated back to mono)
        // instead of band RX. This unifies "monitor while keyed" (Thetis MON
        // semantics) and "audition without keying" (audio passes through the
        // chain so VST plugins receive samples and their meters animate). RX
        // is drained anyway so the WDSP audio ring doesn't back up — we just
        // don't broadcast it. The VST RX seam still fires on the drained RX
        // so RX-side plugins keep running even while monitor is on.
        bool txMonitorOn = engine.IsTxMonitorOn;
        int audioSampleCount = engine.ReadAudio(channel, audioBuf);
        if (audioSampleCount > 0)
        {
            // VST plugin host is TX-only by design (operator decision
            // 2026-04-30). The chain is configured for the TX bandwidth,
            // tuned for voice processing, and shares one set of plugin
            // instances with the TX seam — routing RX through it would
            // (a) apply TX-tuned effects to band audio (sounds wrong),
            // (b) inherit IIR state from the most recent TX block, and
            // (c) waste CPU on RX when the operator only wants chain
            // processing on transmit. The RX-side seam method on
            // IDspEngine remains in place for any future "RX insert"
            // feature, but the audio pipeline does not call it.

            if (!txMonitorOn)
            {
                var audioFrame = new AudioFrame(
                    Seq: ++_audioSeq,
                    TsUnixMs: nowMs,
                    RxId: 0,
                    Channels: 1,
                    SampleRateHz: (uint)AudioOutputRateHz,
                    SampleCount: (ushort)audioSampleCount,
                    Samples: new ReadOnlyMemory<float>(audioBuf, 0, audioSampleCount));
                _hub.Broadcast(audioFrame);
                RxAudioAvailable?.Invoke(0, AudioOutputRateHz, new ReadOnlyMemory<float>(audioBuf, 0, audioSampleCount));
            }
        }
        if (txMonitorOn)
        {
            // Drain whatever the monitor RXA produced this tick. The buffer
            // shape matches the RX path (mono float32 @ 48 kHz) so it slots
            // into the same AudioFrame format with no front-end change. When
            // the chain is idle (no MOX, no mic) the monitor channel produces
            // silence, which is the correct behaviour for "audition mode but
            // operator isn't talking".
            int monCount = engine.ReadTxMonitorAudio(audioBuf.AsSpan());
            if (monCount > 0)
            {
                var monFrame = new AudioFrame(
                    Seq: ++_audioSeq,
                    TsUnixMs: nowMs,
                    RxId: 0,
                    Channels: 1,
                    SampleRateHz: (uint)AudioOutputRateHz,
                    SampleCount: (ushort)monCount,
                    Samples: new ReadOnlyMemory<float>(audioBuf, 0, monCount));
                _hub.Broadcast(monFrame);
            }
        }

        if (++_rxMeterTickMod >= RxMeterTickModulus)
        {
            _rxMeterTickMod = 0;
            // Prefer WDSP's calibrated S-meter when it's ticking. In this
            // integration the meter tap reads -400 ("didn't run") — needs
            // deeper WDSP state debugging to chase down. Until then, fall
            // back to RMS of the already-flowing post-demod audio ring, which
            // gives a "proof of life" meter that moves with band activity.
            double dbm = engine.GetRxaSignalDbm(channel);
            if (!double.IsFinite(dbm) || dbm <= -399.0)
            {
                // 0 dBFS audio ~= S9+ signal; calibrate against ambient band
                // noise later. Empirical offset of -50 dBm puts typical 20m
                // band noise near S2/S3 instead of pinning at S0.
                double rms = 0.0;
                if (audioSampleCount > 0)
                {
                    for (int i = 0; i < audioSampleCount; i++)
                    {
                        double v = audioBuf[i];
                        rms += v * v;
                    }
                    rms = Math.Sqrt(rms / audioSampleCount);
                }
                double dbfs = 20.0 * Math.Log10(Math.Max(rms, 1e-10));
                dbm = dbfs - 50.0; // rough uncalibrated conversion
            }
            if (!double.IsFinite(dbm)) dbm = -160.0;
            _hub.Broadcast(new RxMeterFrame((float)dbm));
            RxMeterUpdated?.Invoke(channel, dbm);

            // Additive 0x19 broadcast (RxMetersV2Frame). Carries the full
            // set of WDSP RXA stage readings so the configurable Meters
            // Panel can render any of them; older clients that only know
            // 0x14 ignore this frame. Same 5 Hz cadence as 0x14 above.
            //
            // Cal offset: HL2 / non-ANAN default per Thetis
            // clsHardwareSpecific.cs:428. TODO(meters/cal-offset): replace
            // with a per-board lookup (e.g.
            // RadioMeterCalibration.RxOffsetDb(HpsdrBoardKind)) and remove
            // the duplicate constant inside WdspDspEngine.GetRxaSignalDbm
            // once the abstraction exists. Tracked as PR-1 follow-up per
            // plan §7 Q3 and CLAUDE.md "use per-board abstractions".
            const double rxCalOffsetDb = 0.98;
            var rx = engine.GetRxStageMeters(channel);
            var v2 = BuildRxMetersV2(rx, rxCalOffsetDb);
            _hub.Broadcast(v2);
            RxMetersV2Updated?.Invoke(channel, v2);
        }
    }

    /// <summary>
    /// Raised when an RXA stage-meter snapshot is broadcast (approximately
    /// 5 Hz, alongside <see cref="RxMeterUpdated"/>). Arguments:
    /// (channelId, frame). Test seam — the broadcast itself is a no-op
    /// when no clients are attached, so this event lets unit tests
    /// observe the encoded frame without instantiating a WebSocket.
    /// </summary>
    public event Action<int, RxMetersV2Frame>? RxMetersV2Updated;

    /// <summary>
    /// Build the wire frame from a raw <see cref="RxStageMeters"/>
    /// snapshot, applying <paramref name="calOffsetDb"/> only to the
    /// dBm-scale fields (Signal*, AgcEnv*). ADC* is dBFS (raw ADC,
    /// board-independent) and AgcGain is dB of insertion gain — both get
    /// the raw value. Exposed for unit tests so the encoding rule can be
    /// asserted without spinning up a hub or pipeline tick.
    /// </summary>
    public static RxMetersV2Frame BuildRxMetersV2(in RxStageMeters rx, double calOffsetDb)
    {
        float cal = (float)calOffsetDb;
        return new RxMetersV2Frame(
            SignalPk: rx.SignalPk + cal,
            SignalAv: rx.SignalAv + cal,
            AdcPk: rx.AdcPk,
            AdcAv: rx.AdcAv,
            AgcGain: rx.AgcGain,
            AgcEnvPk: rx.AgcEnvPk + cal,
            AgcEnvAv: rx.AgcEnvAv + cal);
    }
}
