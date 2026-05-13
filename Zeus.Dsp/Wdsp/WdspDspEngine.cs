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

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;

namespace Zeus.Dsp.Wdsp;

public sealed class WdspDspEngine : IDspEngine
{
    // RXA: keep the 1024-sample window the panadapter / audio pipeline have
    // always used. Changing it broke RX audio entirely (regression observed
    // 2026-04-18). RXA OpenChannel uses RxaInSize / RxaDspSize.
    private const int RxaInSize = 1024;
    private const int RxaDspSize = 1024;

    // TXA profile varies by protocol — OpenTxChannel picks the right one:
    //   P1 (48 kHz DAC) : in=1024@48k, dsp=1024@48k, out=1024@48k, CFIR off
    //   P2 (192 kHz DAC): in=512@48k,  dsp=1024@96k, out=2048@192k, CFIR on
    // pihpsdr transmitter.c:954-997 (protocol switch → buffer_size / dsp_rate /
    // ratio) and Thetis audio.cs:1800-1809 (SampleRateTX + SetTXACFIRRun)
    // define these exactly. Zeus was previously hard-coded to the P1 profile
    // regardless of protocol, which on P2 left the G2 DUC starved (it runs
    // at 192 kHz but we fed 48 kHz) and generated 8-10 kHz close-in spurs
    // on TUN and MOX.
    private const int TxaInSizeP1 = 1024;
    private const int TxaDspSizeP1 = 1024;
    private const int TxaOutSizeP1 = 1024;
    private const int TxaInSizeP2 = 512;
    private const int TxaDspSizeP2 = 1024;
    private const int TxaOutSizeP2 = 2048;

    // Latched values chosen at OpenTxChannel time; ProcessTxBlock uses them
    // to size the mic / iq spans. Default to the P1 profile so tests and
    // bring-up code that open TXA without specifying a protocol still work.
    private int _txaInSize = TxaInSizeP1;
    private int _txaDspSize = TxaDspSizeP1;
    private int _txaOutSize = TxaOutSizeP1;
    private int _txaInputRateHz = 48_000;
    private int _txaDspRateHz = 48_000;
    private int _txaOutputRateHz = 48_000;
    private bool _txaCfirRun;

    // Leveler max-gain ceiling in dB applied at TXA init. Thetis ships
    // 15 dB (radio.cs:2981 tx_leveler_max_gain = 15.0). Zeus used to ship
    // 5 dB — which turned out to be the WDSP C-init default (TXA.c:169
    // 1.778 linear), not a considered choice. With Compressor off the
    // Leveler is the only makeup stage, so 5 dB left operators 10+ dB
    // below Thetis-equivalent modulation on the air.
    //
    // Operator (kb2uka) requested 8 dB: his external analog rack already
    // provides significant preamp and pre-DSP conditioning, so a smaller
    // Leveler ceiling sounds cleaner than the Thetis stock 15 dB on his
    // setup. Operators without an external rack can push it up to 15 via
    // POST /api/tx/leveler-max-gain.
    internal const double DefaultLevelerMaxGainDb = 8.0;

    // Legacy aliases — RXA-side code still references these. Kept = RxaInSize
    // / RxaDspSize so existing callsites (audio outSamples math, channel
    // structs, etc.) don't have to change.
    private const int InSize = RxaInSize;
    private const int DspSize = RxaDspSize;
    private const int DspRate = 48_000;
    private const int OutputRate = 48_000;
    private const int MaxFftSize = 262_144;
    private const int AnalyzerFftSize = 16_384;
    private const int AnalyzerFps = 30;
    private const int AnalyzerWindow = 2;
    private const double AnalyzerKaiserPi = 14.0;
    private const double AnalyzerKeepTime = 0.1;

    private enum RxaMode
    {
        LSB = 0, USB = 1, DSB = 2, CWL = 3, CWU = 4,
        FM = 5, AM = 6, DIGU = 7, SPEC = 8, DIGL = 9,
        SAM = 10, DRM = 11,
    }

    // Audio ring holds ~1 s of mono float32 @ 48 kHz (producer: worker thread after fexchange0,
    // consumer: ReadAudio caller on pipeline thread). Drops oldest when over capacity.
    private const int AudioRingCapacity = OutputRate;

    private sealed class ChannelState
    {
        public required int Id;
        public required int SampleRateHz;
        public required int PixelWidth;
        public required int OutDoubles;
        public required Thread Worker;
        public required BlockingCollection<double[]> InQueue;
        public readonly ConcurrentQueue<double[]> FreeFrames = new();
        public double[] PartialFrame = new double[2 * InSize];
        public int PartialFill;
        public readonly object FillGate = new();
        public volatile bool Stopped;
        public CancellationTokenSource Cts = new();
        public int SpectrumRun = 1;
        public readonly float[] AudioRing = new float[AudioRingCapacity];
        public int AudioHead;
        public int AudioCount;
        public readonly object AudioGate = new();
        // Bandpass tracked as unsigned magnitudes so SetMode can re-sign per mode
        // (WDSP wants negative f_low/f_high for LSB-family, positive for USB-family).
        public int FilterLowAbsHz = 150;
        public int FilterHighAbsHz = 2850;
        public RxaMode CurrentMode = RxaMode.USB;
        // Thetis "AGC Top" max-gain setting in dB. 80 matches the Thetis
        // AGC_MEDIUM default; the /api/agcGain endpoint can override at runtime.
        public double AgcTopDb = 80.0;
        // Read by RunWorker to gate xanbEXT/xnobEXT; writes only from SetNoiseReduction.
        // Single-writer on the pipeline thread + word-sized read on the worker = safe
        // without a lock (worst case: one extra frame at the old setting on toggle).
        public volatile NbMode CurrentNbMode = NbMode.Off;
        // Zoom level (1..32). Changing it re-calls SetAnalyzer with shifted
        // fscLin/fscHin; the worker's Spectrum0 and the pixel drain's GetPixels
        // take this lock so they never interleave with an in-flight reconfig.
        public int ZoomLevel = 1;
        public readonly object AnalyzerLock = new();
    }

    private readonly ConcurrentDictionary<int, ChannelState> _channels = new();
    private readonly ILogger _log;
    private int _disposed;

    // TXA lifecycle is disjoint from RXA's (no analyzer, no audio ring, no NB)
    // so we don't register it in _channels. _txaLock serializes OpenTxChannel
    // vs SetMox vs teardown — all three are rare, so a plain lock is fine.
    private readonly object _txaLock = new();
    // Counter throttles fexchange2-error logging so a persistent wire-protocol
    // mismatch doesn't flood the log. First 8 errors are visible then suppressed.
    private int _txFexchangeErrLogged;
    private int? _txaChannelId;
    // Tracked so SetTxMode can re-sign bandpass bounds (LSB family wants negative,
    // USB family positive) the same way RXA does through ApplyBandpassForMode.
    private RxaMode _txCurrentMode = RxaMode.USB;
    // TwoTone arm-state cache. SetTwoTone records the operator-supplied freqs
    // (positive Hz) here when arming; SetTxMode reads them back so a mid-test
    // mode change re-asserts the sideband-correct signed freqs onto PostGen.
    // gen.c xgen mode-1 emits e^(-jωt) — positive freq always lands LSB-side
    // of carrier, so USB-family modes need a sign flip to put the tones inside
    // the displayed bandpass. See gen.c:241-242 and Thetis setup.cs:11097-11101
    // (chkInvertTones, gated behind a checkbox there; we auto-sign per mode).
    private double _twoToneF1Hz;
    private double _twoToneF2Hz;
    private bool _twoToneArmed;
    // Latest per-stage TX peak meters, published atomically at the end of each
    // ProcessTxBlock. The reader (TxMetersService, 10 Hz during MOX) sees a
    // consistent snapshot without blocking the DSP thread. null until first TX
    // block runs or after TXA closes; GetTxStageMeters() returns
    // TxStageMeters.Silent in that case.
    private TxStageMeters? _latestTxStageMeters;
    private readonly object _txMeterPublishLock = new();

    // Latest per-stage RX meters, published atomically each time
    // GetRxStageMeters is called from the pipeline tick. The reader sees a
    // consistent snapshot across all 7 indices without racing against a
    // concurrent re-read. Mirrors the TX path's _latestTxStageMeters /
    // _txMeterPublishLock pattern. The lock is uncontended in steady state —
    // GetRxStageMeters runs from the pipeline tick at 5 Hz; if a future
    // caller polls from a second thread the snapshot field still gives them
    // a coherent set rather than a half-updated tuple.
    private RxStageMeters _latestRxStageMeters = RxStageMeters.Silent;
    private readonly object _rxMeterPublishLock = new();

    // TX panadapter analyzer. Separate WDSP `disp` slot from RXA's, fed with
    // the post-CFIR IQ from ProcessTxBlock so the operator can see the on-air
    // signal during MOX / TUN. The analyzer runs at the TXA output rate
    // (48 kHz on P1, 192 kHz on P2 post-CFIR) and uses fscLin/fscHin bin
    // clipping to display the same frequency span as the RXA analyzer —
    // matches pihpsdr transmitter.c:2323-2324. See issue #81.
    //
    // `_txDispLock` serializes Spectrum0 feed (from ProcessTxBlock), GetPixels
    // (from TryGetTxDisplayPixels), and SetAnalyzer reconfig (from SetZoom) —
    // same pattern as ChannelState.AnalyzerLock on the RX side.
    private readonly object _txDispLock = new();
    private int _txDispPixelWidth;
    private int _txDispZoomLevel = 1;
    private int _txDispRxSampleRateHz;
    private bool _txDispAlive;

    // PureSignal feedback display analyzer (issue #121). Optional second WDSP
    // disp slot fed from FeedPsFeedbackBlock's rxI/rxQ — i.e. the post-PA
    // signal observed via the radio's loopback ADC. When the operator turns on
    // the "Monitor PA output" toggle (StateDto.PsMonitorEnabled) AND PS is
    // armed AND calcc reports correcting=true, DspPipelineService.Tick reads
    // pixels from this analyzer instead of the post-CFIR TX analyzer so the
    // panadapter shows the actual on-air RF rather than the predistorted
    // baseband. Lifecycle is paired with SetPsEnabled(true/false): we open
    // the disp slot when PS arms and tear it down when PS disarms so the
    // WDSP analyzer table doesn't leak.
    //
    // Pixel width / zoom / matched RX sample rate are inherited from the TX
    // analyzer at arm time so display frames slot in with no resize when the
    // toggle flips. If the TX analyzer isn't alive (no RXA, or rate mismatch)
    // the PS-FB analyzer is also skipped — Tick will fall through to the TX
    // analyzer (or RX analyzer) the same as today.
    private readonly object _psFbDispLock = new();
    private int? _psFbDispId;
    private int _psFbDispPixelWidth;
    private int _psFbDispZoomLevel = 1;
    private int _psFbDispRxSampleRateHz;
    private bool _psFbDispAlive;
    private long _psFbFeedCount;

    // PureSignal state. _psLock serializes the WDSP PS setters (which mutate
    // shared state inside calcc.c) and FeedPsFeedbackBlock. _psInfoBuf is
    // pinned once and reused on every GetPSInfo call.
    private readonly object _psLock = new();
    private bool _psEnabled;
    private bool _psAuto = true;
    private bool _psSingle;
    private double _psHwPeak = 0.4072;   // P1 default; RadioService overrides at connect
    private int _psInts = 16;
    private int _psSpi = 256;
    private double _psMoxDelaySec = 0.2;
    private double _psLoopDelaySec = 0.0;
    private double _psAmpDelayNs = 150.0;
    private bool _psPtol;                // false = strict 0.8 ; true = relax 0.4 (matches pihpsdr/Thetis: ptol ? 0.4 : 0.8)
    private const int PsFeedbackBlockSize = 1024;
    // PS feedback IQ runs at 192 kHz on G2 / Saturn / ANAN-7000 (P2 paired
    // DDC0/DDC1 — see SetPSFeedbackRate(id, 192_000) in OpenTxChannel).
    // Used when configuring the PS-feedback display analyzer so its bin-clip
    // math matches the data rate it's receiving.
    private const int PsFeedbackSampleRateHz = 192_000;
    private readonly int[] _psInfoBuf = new int[16];
    // Edge-triggered state-transition log target. 255 is an out-of-range
    // sentinel so the first observed state always logs (LRESET..LTURNON
    // = 0..9 per calcc.c:543-552). Updated under _psLock.
    private byte _lastLoggedPsState = 255;
    // Pscc-call counter — incremented in FeedPsFeedbackBlock after psccF.
    // At 192 kHz / 1024-sample blocks we expect ~187 calls/sec while keyed.
    // Periodic log at every 100th call lets the operator confirm feedback is
    // arriving from the radio without flooding when PS is idle.
    private long _psFeedCount;
    private double _psMaxTxEnvelope;
    // Bring-up diagnostic — emit info[] every Nth GetPsStageMeters tick so the
    // calcc state machine is visible in the server log without flooding.
    // Drop alongside the wdsp.psSeed log once PS is confirmed stable.
    private int _psInfoLogCounter;

    // VST plugin-host seam. Wave 6a wires the out-of-process plugin sidecar
    // through Zeus.Server's PluginHostManager via the
    // <see cref="VstChainHandler"/> delegate below. ProcessRxVstChain /
    // ProcessTxMicVstChain read the volatile-bool flag first; if it is
    // false the seam short-circuits with a single boolean read — no
    // virtual dispatch into the host, no IPC.
    // Volatile so the host wiring can flip it without taking a lock on the
    // audio thread; no struct tearing concerns for a single bool.
    private volatile bool _vstChainEnabled;

    // The handler — wired by Zeus.Server at startup once PluginHostManager
    // is in DI. Never accessed under _txaLock so the audio thread doesn't
    // serialise with handler installation; null check is the trivial guard.
    // We deliberately do NOT take a hard reference to PluginHost from
    // Zeus.Dsp; the delegate keeps that boundary clean.
    private VstChainHandler? _vstChainHandler;

    // Wave 6a entry points — Zeus.Server's PluginHost wiring flips the flag
    // when a chain becomes available, and installs the handler delegate at
    // process start. `internal` so they stay out of the IDspEngine surface;
    // the host integration sits one layer up. Also exercised by
    // Zeus.Dsp.Tests via InternalsVisibleTo to assert the bypass path is
    // bit-identical to the seam-absent path.
    internal void SetVstChainEnabled(bool enabled) => _vstChainEnabled = enabled;
    internal bool VstChainEnabled => _vstChainEnabled;
    internal void SetVstChainHandler(VstChainHandler? handler) => _vstChainHandler = handler;
    internal VstChainHandler? VstChainHandler => _vstChainHandler;

    // TX Monitor — private RXA channel that demodulates the post-CFIR / post-
    // RSMPOUT TX IQ (the wire signal about to hit the radio) back to mono
    // baseband audio at 48 kHz, so the operator can audition the full TX chain
    // (mic → EQ → Leveler → VST → CFC → ALC → bandpass) at the actual TX
    // bandwidth profile, with or without keying. Equivalent to Thetis MON,
    // implemented as a parallel demod rather than a tap inside TXA so the
    // bandwidth filter shape is honoured exactly.
    //
    // The channel is opened lazily on first SetTxMonitorEnabled(true) once
    // OpenTxChannel has chosen the IQ rate (48 kHz P1 / 192 kHz P2). It stays
    // open for the engine lifetime; toggling monitor off just stops feeding
    // and stops draining. Mode + filter are synced from SetTxMode/SetTxFilter
    // so the audition matches the on-air bandwidth.
    //
    // _monitorRequested is the operator's intent (REST toggle); _monitorChannelId
    // becomes non-null once the channel is actually open. ProcessTxBlock feeds
    // IQ when both are set; ReadTxMonitorAudio drains regardless of the request
    // flag (the ring drains naturally when feed stops).
    private readonly object _monitorLock = new();
    private int? _monitorChannelId;
    private volatile bool _monitorRequested;
    private RxaMode _monitorMode = RxaMode.USB;
    private int _monitorFilterLow = 150;
    private int _monitorFilterHigh = 2850;

    // Tracked engine-side MOX so SetTxMonitorEnabled can decide whether to
    // flip TXA state independently. SetMox writes this under _txaLock; the
    // helpers below read it under _txaLock too. Without this the "monitor
    // on while MOX off" path leaves TXA quiescent (state=0) — fexchange2
    // returns without filling iout/qout and the monitor RXA hears silence
    // or stack garbage.
    private bool _moxOn;
    // Tracked engine-side TXA state-bit so the helper can flip idempotently
    // and avoid double-priming. TXA opens at state=0; SetChannelState walks
    // it through 1 / 0 transitions explicitly.
    private bool _txaRunning;

    public WdspDspEngine(ILogger<WdspDspEngine>? logger = null)
    {
        _log = logger ?? NullLogger<WdspDspEngine>.Instance;
        WdspNativeLoader.EnsureResolverRegistered();
        // WDSPwisdom is run by WdspWisdomInitializer at app startup before any
        // connect is allowed, so we trust it has completed by the time the
        // first OpenChannel lands here. Tests that construct the engine in
        // isolation can call WdspWisdomInitializer.EnsureInitializedAsync()
        // themselves, or accept slow first-open planning.
    }

    public int OpenChannel(int sampleRateHz, int pixelWidth)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (pixelWidth <= 0) throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));

        // Skip ids occupied by TXA (which is NOT registered in _channels but
        // still owns a slot in WDSP's global channel table). Wave 7 — when
        // the TX monitor opens AFTER TXA, this loop would otherwise hand
        // back the TXA's id and the two channels would alias inside WDSP,
        // double-freeing on disconnect.
        int id = 0;
        while (_channels.ContainsKey(id) || id == _txaChannelId) id++;

        int outSamples = (int)((long)InSize * OutputRate / sampleRateHz);
        int outDoubles = Math.Max(2, outSamples * 2);

        // Thetis pattern: open channel quiescent (state=0), apply all config,
        // then explicitly transition to state=1 with SetChannelState at the end.
        // Mirrors cmaster.c:80 (// initial state = 0) and rxa.cs:63
        // (WDSP.SetChannelState(chid + 0, 1, 0); // main rcvr ON). A fresh
        // channel opened at state=1 does set the exchange bit correctly
        // in-vitro, but runtime observation shows it can land clear — SAv/ADC
        // pin at -400 — suggesting the open→configure window allows the flag
        // to be stomped. Opening at 0 and flipping on last guarantees exchange
        // is set after all setters have run.
        NativeMethods.OpenChannel(
            channel: id,
            in_size: InSize,
            dsp_size: DspSize,
            input_samplerate: sampleRateHz,
            dsp_rate: DspRate,
            output_samplerate: OutputRate,
            type: 0,
            state: 0,
            tdelayup: 0.010,
            tslewup: 0.025,
            tdelaydown: 0.0,
            tslewdown: 0.010,
            bfo: 1);

        NativeMethods.SetRXABandpassWindow(id, 1);
        NativeMethods.SetRXABandpassRun(id, 1);
        NativeMethods.SetRXAAMDSBMode(id, 0);
        NativeMethods.SetRXAPanelRun(id, 1);
        // select=3 → route both I and Q into the panel. Without this WDSP
        // demodulates a single real-valued channel and can't separate sidebands
        // (LSB/USB become audibly identical mush).
        NativeMethods.SetRXAPanelSelect(id, 3);
        NativeMethods.SetRXAPanelBinaural(id, 0);
        NativeMethods.SetRXAPanelGain1(id, 1.0);
        NativeMethods.SetRXAMode(id, (int)RxaMode.USB);
        NativeMethods.SetRXABandpassFreqs(id, 150.0, 2850.0);
        NativeMethods.RXANBPSetFreqs(id, 150.0, 2850.0);
        NativeMethods.SetRXASNBAOutputBandwidth(id, 150.0, 2850.0);

        ApplyAgcDefaults(id);

        // Pre-RXA blankers: create run=0 so the setters / xanbEXT slots are
        // allocated before any SetNoiseReduction call touches them (EXT
        // setters deref panb[id]/pnob[id]). Create-time knob values are
        // passed through here too so the struct is self-consistent on return,
        // but the authoritative knob state comes from ApplyNbDefaults right
        // after — same approach a future advanced-NB panel will take.
        NativeMethods.CreateAnbEXT(
            id: id, run: 0, buffsize: InSize, samplerate: sampleRateHz,
            tau: NrDefaults.NbTau, hangtime: NrDefaults.NbHangtime,
            advtime: NrDefaults.NbAdvtime, backtau: NrDefaults.NbBacktau,
            threshold: NrDefaults.NbDefaultThresholdScaled);
        NativeMethods.CreateNobEXT(
            id: id, run: 0, mode: 0, buffsize: InSize, samplerate: sampleRateHz,
            slewtime: NrDefaults.NbTau, hangtime: NrDefaults.NbHangtime,
            advtime: NrDefaults.NbAdvtime, backtau: NrDefaults.NbBacktau,
            threshold: NrDefaults.NbDefaultThresholdScaled);
        ApplyNbDefaults(id);

        NativeMethods.XCreateAnalyzer(id, out int rc, MaxFftSize, 1, 1, null);
        if (rc != 0) throw new InvalidOperationException($"XCreateAnalyzer failed rc={rc}");

        ConfigureAnalyzer(id, sampleRateHz, InSize, pixelWidth, zoomLevel: 1);
        ConfigureDisplayAveraging(id);

        var state = new ChannelState
        {
            Id = id,
            SampleRateHz = sampleRateHz,
            PixelWidth = pixelWidth,
            OutDoubles = outDoubles,
            InQueue = new BlockingCollection<double[]>(boundedCapacity: 32),
            Worker = null!,
        };

        var worker = new Thread(() => RunWorker(state))
        {
            IsBackground = true,
            Name = $"WdspDsp-{id}",
            Priority = ThreadPriority.AboveNormal,
        };
        state.Worker = worker;

        _channels[id] = state;
        worker.Start();

        // Thetis rxa.cs:63 — "main rcvr ON". The OpenChannel call above used
        // state=0 so the slew.upflag / ch_upslew / exchange-bit initialisation
        // block in channel.c:94-99 did NOT run. SetChannelState(id, 1, 0) is
        // the canonical transition: it sets slew.upflag, ch_upslew, clears
        // exec_bypass, and sets exchange (channel.c:278-283). After this
        // returns, fexchange0's `if (_InterlockedAnd (&ch[channel].exchange, 1))`
        // guard (iobuffs.c:484) will be satisfied and xrxa → xmeter will run.
        NativeMethods.SetChannelState(id, 1, 0);

        return id;
    }

    public void CloseChannel(int channelId)
    {
        if (!_channels.TryRemove(channelId, out var state)) return;
        StopChannel(state);
    }

    public void FeedIq(int channelId, ReadOnlySpan<double> interleavedIqSamples)
    {
        if (!_channels.TryGetValue(channelId, out var state)) return;
        if (state.Stopped) return;

        int offset = 0;
        while (offset < interleavedIqSamples.Length)
        {
            lock (state.FillGate)
            {
                int need = state.PartialFrame.Length - state.PartialFill;
                int take = Math.Min(need, interleavedIqSamples.Length - offset);
                interleavedIqSamples.Slice(offset, take).CopyTo(state.PartialFrame.AsSpan(state.PartialFill));
                state.PartialFill += take;
                offset += take;

                if (state.PartialFill == state.PartialFrame.Length)
                {
                    double[] frame = state.PartialFrame;
                    if (!state.FreeFrames.TryDequeue(out var next))
                        next = new double[2 * InSize];
                    state.PartialFrame = next;
                    state.PartialFill = 0;
                    if (!state.InQueue.IsAddingCompleted)
                    {
                        try { state.InQueue.Add(frame); }
                        catch (InvalidOperationException) { state.FreeFrames.Enqueue(frame); }
                    }
                    else
                    {
                        state.FreeFrames.Enqueue(frame);
                    }
                }
            }
        }
    }

    public void SetMode(int channelId, RxMode mode)
    {
        if (!_channels.TryGetValue(channelId, out var state)) return;
        var mapped = MapMode(mode);
        NativeMethods.SetRXAMode(channelId, (int)mapped);
        state.CurrentMode = mapped;
        _log.LogInformation("wdsp.setMode channel={Id} mode={Mode}", channelId, mapped);
        ApplyBandpassForMode(state);
        // Drop up to ~1 s of already-demodulated audio queued with the old mode so
        // the user hears the new sideband immediately after clicking instead of
        // finishing the tail of the wrong one. AudioHead stays put; the read
        // position is derived from Head - Count, so zeroing Count is enough.
        lock (state.AudioGate) { state.AudioCount = 0; }
    }

    public void SetFilter(int channelId, int lowHz, int highHz)
    {
        if (!_channels.TryGetValue(channelId, out var state)) return;
        // Normalize to positive magnitudes; mode dictates the sign via ApplyBandpassForMode.
        int lo = Math.Abs(lowHz);
        int hi = Math.Abs(highHz);
        if (hi < lo) (lo, hi) = (hi, lo);
        state.FilterLowAbsHz = lo;
        state.FilterHighAbsHz = hi;
        ApplyBandpassForMode(state);
    }

    public void SetVfoHz(int channelId, long vfoHz)
    {
        // VFO lives in VfoService above Protocol1Client (doc 07 §1.5) — WDSP has no
        // tuner; frequency translation happens at the protocol seam.
    }

    public void SetAgcTop(int channelId, double topDb)
    {
        if (!_channels.TryGetValue(channelId, out var state)) return;
        state.AgcTopDb = topDb;
        NativeMethods.SetRXAAGCTop(channelId, topDb);
        _log.LogInformation("wdsp.setAgcTop channel={Id} topDb={TopDb:F1}", channelId, topDb);
    }

    public void SetRxAfGainDb(int channelId, double db)
    {
        if (!_channels.TryGetValue(channelId, out _)) return;
        // WDSP's SetRXAPanelGain1 takes a linear multiplier on the post-demod
        // audio panel (panel.c:66). 0 dB ≡ 1.0 linear, which is the value
        // OpenChannel installs at line 237 — so a fresh channel that never
        // sees this call behaves exactly as before. Thetis wires its master
        // AF slider the same way (audio.cs:218-224, `SetRXAPanelGain1(rxa,
        // Math.Pow(10.0, db/20.0))`).
        double linear = Math.Pow(10.0, db / 20.0);
        NativeMethods.SetRXAPanelGain1(channelId, linear);
        _log.LogInformation("wdsp.setRxAfGain channel={Id} db={Db:F1} linear={Linear:F4}", channelId, db, linear);
    }

    public void SetZoom(int channelId, int level)
    {
        SyntheticDspEngine.ValidateZoomLevel(level);
        if (!_channels.TryGetValue(channelId, out var state)) return;
        // Analyzer reconfig can race with Spectrum0 (worker) and GetPixels
        // (pipeline tick); the lock is the simpler option of the two team-lead
        // flagged. Briefly holds both producer and consumer while WDSP rebuilds
        // its bin mapping. Clients may still see one transient frame on the
        // wire — the averaging recovers in ~tau (≈100 ms) after the switch.
        lock (state.AnalyzerLock)
        {
            if (state.ZoomLevel == level) return;
            state.ZoomLevel = level;
            ConfigureAnalyzer(channelId, state.SampleRateHz, InSize, state.PixelWidth, level);
        }

        // Mirror zoom onto the TX analyzer so the TX panadapter span stays
        // lock-step with RX — otherwise keying mid-zoom would show a different
        // frequency window on MOX. No-op when TX analyzer is off.
        int? txaIdToReconfig = null;
        lock (_txDispLock)
        {
            if (_txDispAlive && _txaChannelId is int txa)
            {
                _txDispZoomLevel = level;
                txaIdToReconfig = txa;
                TryConfigureTxAnalyzer(txa, _txaDspRateHz, _txaDspSize, _txDispRxSampleRateHz, _txDispPixelWidth, level);
            }
        }

        // Mirror zoom onto the PS-FB analyzer when it's open, same reasoning
        // as the TX analyzer: keep the PA-output trace span lock-step with the
        // RX panadapter so toggling the PS-Monitor view doesn't shift the axis.
        lock (_psFbDispLock)
        {
            if (_psFbDispAlive && _psFbDispId is int psFb)
            {
                _psFbDispZoomLevel = level;
                TryConfigureTxAnalyzer(psFb, PsFeedbackSampleRateHz, PsFeedbackBlockSize, _psFbDispRxSampleRateHz, _psFbDispPixelWidth, level);
            }
        }

        _log.LogInformation("wdsp.setZoom channel={Id} level={Level} txDisp={TxDisp}",
            channelId, level, txaIdToReconfig?.ToString() ?? "off");
    }

    public void SetNoiseReduction(int channelId, NrConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (!_channels.TryGetValue(channelId, out var state)) return;

        // Mutually-exclusive NR button. When switching to a mode, re-apply its
        // Thetis defaults before toggling Run=1 — matches Thetis setup.cs order
        // (configure, then enable) and keeps "toggle off then back on" at parity
        // even if a future caller changes the knobs between toggles.
        switch (cfg.NrMode)
        {
            case NrMode.Anr:
                NativeMethods.SetRXAEMNRRun(channelId, 0);
                TrySetSbnrRun(channelId, 0);
                NativeMethods.SetRXAANRVals(channelId, NrDefaults.AnrTaps, NrDefaults.AnrDelay, NrDefaults.AnrGain, NrDefaults.AnrLeakage);
                NativeMethods.SetRXAANRPosition(channelId, NrDefaults.Position);
                NativeMethods.SetRXAANRRun(channelId, 1);
                break;
            case NrMode.Emnr:
                NativeMethods.SetRXAANRRun(channelId, 0);
                TrySetSbnrRun(channelId, 0);
                // Core EMNR algorithm selectors (gain method, NPE method, AE
                // filter) plus the optional Trained-method T1/T2 tuning. All
                // operator-tunable; null fields fall back to NrDefaults so the
                // engine state stays Thetis-equivalent when nothing's set yet.
                ApplyNr2Core(channelId, cfg);
                // post2 comfort-noise injection. emnr.c:981–1023 generates a
                // smoothed noise floor that masks residual EMNR warble — the
                // psychoacoustic mechanism behind Thetis's noticeably smoother
                // NR2 hiss.
                ApplyNr2Post2(channelId, cfg);
                NativeMethods.SetRXAEMNRRun(channelId, 1);
                break;
            case NrMode.Sbnr:
                // NR4 — libspecbleach spectral bleaching. Disable the other
                // post-RXA NR paths first (mutual exclusion), then push the
                // operator-tuned (or Thetis-default) parameters before flipping
                // Run=1. Wrapped in TrySetSbnr* so a libwdsp build that
                // pre-dates Phase 1 (no SBNR exports) leaves the channel in
                // NR-off rather than crashing the worker.
                NativeMethods.SetRXAANRRun(channelId, 0);
                TrySetEmnrPost2Run(channelId, 0);
                NativeMethods.SetRXAEMNRRun(channelId, 0);
                ApplyNr4Sbnr(channelId, cfg);
                break;
            default:
                NativeMethods.SetRXAANRRun(channelId, 0);
                TrySetEmnrPost2Run(channelId, 0);
                NativeMethods.SetRXAEMNRRun(channelId, 0);
                TrySetSbnrRun(channelId, 0);
                break;
        }

        if (cfg.AnfEnabled)
        {
            NativeMethods.SetRXAANFVals(channelId, NrDefaults.AnfTaps, NrDefaults.AnfDelay, NrDefaults.AnfGain, NrDefaults.AnfLeakage);
            NativeMethods.SetRXAANFPosition(channelId, NrDefaults.Position);
            NativeMethods.SetRXAANFRun(channelId, 1);
        }
        else
        {
            NativeMethods.SetRXAANFRun(channelId, 0);
        }

        NativeMethods.SetRXASNBARun(channelId, cfg.SnbEnabled ? 1 : 0);
        NativeMethods.RXANBPSetNotchesRun(channelId, cfg.NbpNotchesEnabled ? 1 : 0);

        // Mutually-exclusive pre-RXA blanker. Update threshold on whichever
        // path we're about to run (or both paths when switching off → on → the
        // dormant side keeps a stale value, harmless while its Run=0). UI slider
        // is 0..100; Thetis multiplies by 0.165 before passing to WDSP.
        double scaledThreshold = cfg.NbThreshold * NrDefaults.NbThresholdScale;
        switch (cfg.NbMode)
        {
            case NbMode.Nb1:
                NativeMethods.SetEXTNOBRun(channelId, 0);
                NativeMethods.SetEXTANBThreshold(channelId, scaledThreshold);
                NativeMethods.SetEXTANBRun(channelId, 1);
                break;
            case NbMode.Nb2:
                NativeMethods.SetEXTANBRun(channelId, 0);
                NativeMethods.SetEXTNOBThreshold(channelId, scaledThreshold);
                NativeMethods.SetEXTNOBRun(channelId, 1);
                break;
            default:
                NativeMethods.SetEXTANBRun(channelId, 0);
                NativeMethods.SetEXTNOBRun(channelId, 0);
                break;
        }

        // RunWorker gate. Toggled after the Run flags above so the worker
        // doesn't call xanbEXT/xnobEXT between "dispatch starts running NB1"
        // and "we remember we're NB1 mode" — same reason SetNoiseReduction
        // runs Run=0 on the other side before Run=1 on this side.
        state.CurrentNbMode = cfg.NbMode;

        _log.LogInformation(
            "wdsp.setNoiseReduction channel={Id} nr={Nr} anf={Anf} snb={Snb} notches={Notches} nb={Nb} thr={Thr:F2}",
            channelId, cfg.NrMode, cfg.AnfEnabled, cfg.SnbEnabled, cfg.NbpNotchesEnabled,
            cfg.NbMode, scaledThreshold);
    }

    // NR2 (EMNR) core algorithm selectors. Pushed on every NR config update
    // (not just at NrMode-switch time) so the operator can change Gain Method
    // / NPE Method / AE Filter from the inline panel and see effect without a
    // mode cycle. T1/T2 are unconditionally written when GainMethod=3 — WDSP
    // only consults them in the Trained-gain code path (emnr.c:1226–1276).
    private static void ApplyNr2Core(int channelId, NrConfig cfg)
    {
        int gainMethod = cfg.EmnrGainMethod ?? NrDefaults.EmnrGainMethod;
        int npeMethod = cfg.EmnrNpeMethod ?? NrDefaults.EmnrNpeMethod;
        bool aeRun = cfg.EmnrAeRun ?? (NrDefaults.EmnrAeRun != 0);

        NativeMethods.SetRXAEMNRgainMethod(channelId, gainMethod);
        NativeMethods.SetRXAEMNRnpeMethod(channelId, npeMethod);
        NativeMethods.SetRXAEMNRaeRun(channelId, aeRun ? 1 : 0);
        NativeMethods.SetRXAEMNRPosition(channelId, NrDefaults.Position);

        if (gainMethod == 3)
        {
            NativeMethods.SetRXAEMNRtrainZetaThresh(channelId, cfg.EmnrTrainT1 ?? NrDefaults.EmnrTrainT1);
            NativeMethods.SetRXAEMNRtrainT2(channelId, cfg.EmnrTrainT2 ?? NrDefaults.EmnrTrainT2);
        }
    }

    // NR2 (EMNR) post2 comfort-noise tunables. Configures all five params
    // before flipping post2Run so the post-processing stage starts coherent.
    // Null fields fall back to NrDefaults so the operator's "leave it default"
    // choice (cleared field) is honoured at write time without baking the
    // current default into the persisted config. Wrapped in try/catch so a
    // libwdsp.so built before the post2 exports landed (or a stale system
    // copy shadowing the bundled one) leaves NR2 running without comfort-noise
    // instead of crashing the worker.
    private void ApplyNr2Post2(int channelId, NrConfig cfg)
    {
        try
        {
            NativeMethods.SetRXAEMNRpost2Factor(channelId, cfg.EmnrPost2Factor ?? NrDefaults.EmnrPost2Factor);
            NativeMethods.SetRXAEMNRpost2Nlevel(channelId, cfg.EmnrPost2Nlevel ?? NrDefaults.EmnrPost2Nlevel);
            NativeMethods.SetRXAEMNRpost2Rate(channelId, cfg.EmnrPost2Rate ?? NrDefaults.EmnrPost2Rate);
            NativeMethods.SetRXAEMNRpost2Taper(channelId, cfg.EmnrPost2Taper ?? NrDefaults.EmnrPost2Taper);
            bool runOn = cfg.EmnrPost2Run ?? (NrDefaults.EmnrPost2Run != 0);
            NativeMethods.SetRXAEMNRpost2Run(channelId, runOn ? 1 : 0);
        }
        catch (EntryPointNotFoundException ex)
        {
            _log.LogWarning(
                "wdsp.emnr.post2.unavailable channel={Id} reason=\"libwdsp does not export SetRXAEMNRpost2* — bundled .so is being shadowed by an older system copy, or the build pre-dates post2 support\" detail={Msg}",
                channelId, ex.Message);
        }
    }

    // NR4 (SBNR / libspecbleach) parameter push + Run=1. Native setters take
    // float; we downcast at the seam. Wrapped in TrySet* because a libwdsp
    // built without Phase 1 of issue #79 will throw EntryPointNotFoundException
    // here — the operator gets NR-off behaviour instead of a worker crash.
    private void ApplyNr4Sbnr(int channelId, NrConfig cfg)
    {
        try
        {
            NativeMethods.SetRXASBNRPosition(channelId, cfg.Nr4Position ?? NrDefaults.Nr4Position);
            NativeMethods.SetRXASBNRreductionAmount(channelId, (float)(cfg.Nr4ReductionAmount ?? NrDefaults.Nr4ReductionAmount));
            NativeMethods.SetRXASBNRsmoothingFactor(channelId, (float)(cfg.Nr4SmoothingFactor ?? NrDefaults.Nr4SmoothingFactor));
            NativeMethods.SetRXASBNRwhiteningFactor(channelId, (float)(cfg.Nr4WhiteningFactor ?? NrDefaults.Nr4WhiteningFactor));
            NativeMethods.SetRXASBNRnoiseRescale(channelId, (float)(cfg.Nr4NoiseRescale ?? NrDefaults.Nr4NoiseRescale));
            NativeMethods.SetRXASBNRpostFilterThreshold(channelId, (float)(cfg.Nr4PostFilterThreshold ?? NrDefaults.Nr4PostFilterThreshold));
            NativeMethods.SetRXASBNRnoiseScalingType(channelId, cfg.Nr4NoiseScalingType ?? NrDefaults.Nr4NoiseScalingType);
            NativeMethods.SetRXASBNRRun(channelId, 1);
        }
        catch (EntryPointNotFoundException ex)
        {
            _log.LogWarning(
                "wdsp.sbnr.unavailable channel={Id} reason=\"libwdsp build does not export SBNR symbols (Phase 1 of issue #79 not yet shipped)\" detail={Msg}",
                channelId, ex.Message);
        }
    }

    // Pre-Phase-1-binary safe Run=0 — the only SBNR call we make outside the
    // Sbnr arm. EntryPointNotFoundException here just means "the library
    // doesn't have SBNR; nothing to turn off."
    private void TrySetSbnrRun(int channelId, int run)
    {
        try { NativeMethods.SetRXASBNRRun(channelId, run); }
        catch (EntryPointNotFoundException) { /* libwdsp pre-Phase-1; SBNR is a no-op */ }
    }

    // Same shape as TrySetSbnrRun for the post2 Run=0 calls we issue when
    // switching away from NR2. A stale libwdsp.so on the operator's machine
    // (e.g. an older copy in /usr/local/lib shadowing the bundled .so) would
    // otherwise throw EntryPointNotFoundException straight up the worker.
    private void TrySetEmnrPost2Run(int channelId, int run)
    {
        try { NativeMethods.SetRXAEMNRpost2Run(channelId, run); }
        catch (EntryPointNotFoundException) { /* libwdsp lacks post2; nothing to turn off */ }
    }

    // Post-RXA NR defaults — sourced from Thetis setup.designer.cs + radio.cs.
    // UI-space scaling (gain × 1e-6, leakage × 1e-3) is already resolved: these
    // are the post-scale values WDSP actually receives. See docs/prd/10-noise-reduction.md.
    private static class NrDefaults
    {
        public const int AnrTaps = 64;
        public const int AnrDelay = 16;
        public const double AnrGain = 1e-4;
        public const double AnrLeakage = 0.1;
        public const int AnfTaps = 64;
        public const int AnfDelay = 16;
        public const double AnfGain = 1e-4;
        public const double AnfLeakage = 0.1;
        public const int EmnrGainMethod = 2;
        public const int EmnrNpeMethod = 0;
        public const int EmnrAeRun = 1;
        public const int Position = 1;

        // Thetis Setup → DSP "Trained" T1/T2 NUDs (setup.designer.cs:43244,
        // 43276). Defaults match Thetis exactly; only consulted by WDSP when
        // EmnrGainMethod=3 (Trained gain method).
        public const double EmnrTrainT1 = -0.5;
        public const double EmnrTrainT2 = 2.0;

        // post2 defaults sourced from Thetis radio.cs:2103/2122/2160 (raw
        // NumericUpDown values 0..100, default 15/15/12). The /100 scaling
        // happens INSIDE WDSP at emnr.c:1035/1042/1050, so the wire value is
        // the Thetis slider raw — not the post-divide internal value WDSP
        // ends up storing. (post2Rate has no /100 in WDSP, so 5.0 is correct
        // as-is.) Earlier Zeus defaults of 0.15 were 100× too small once WDSP
        // divided again, leaving comfort-noise effectively silent.
        public const int EmnrPost2Run = 1;
        public const double EmnrPost2Factor = 15.0;
        public const double EmnrPost2Nlevel = 15.0;
        public const double EmnrPost2Rate = 5.0;
        public const int EmnrPost2Taper = 12;

        // NR4 (SBNR / libspecbleach) defaults — sourced from Thetis radio.cs
        // :2350-2462 (rx_nr4_* private fields). Native setters take float;
        // we keep them as double here and downcast at the P/Invoke seam to
        // match the rest of the contract surface.
        public const double Nr4ReductionAmount = 10.0;
        public const double Nr4SmoothingFactor = 0.0;
        public const double Nr4WhiteningFactor = 0.0;
        public const double Nr4NoiseRescale = 2.0;
        // Thetis Setup → DSP "SNRthresh" NUD default (setup.designer.cs:42132).
        // The radio.cs field-init is 0.0 but Setup pushes the NUD value (-10) at
        // first paint, so the operator's effective default is -10. WDSP's own
        // create_sbnr also seeds -10 (sbnr.c:84), so the ON-startup state in
        // Thetis is -10 across the board. Aligning here gives Zeus the same
        // first-run behaviour.
        public const double Nr4PostFilterThreshold = -10.0;
        public const int Nr4NoiseScalingType = 0;
        public const int Nr4Position = 1;

        // NB1/NB2 runtime-steady-state params — what Thetis actually runs with
        // once radio.cs's NB property setters have fired (tau=advtime=hangtime
        // = 5e-5, threshold = 0.165 × UI=20 = 3.3). backtau has no property
        // setter in Thetis, so it keeps cmaster.c's create-time value of 0.05.
        // Applied through Set* setters post-create (see ApplyNbDefaults) so
        // a future advanced-NB panel can reuse the same code path.
        public const double NbTau = 5e-5;
        public const double NbHangtime = 5e-5;
        public const double NbAdvtime = 5e-5;
        public const double NbBacktau = 0.05;
        public const double NbThresholdScale = 0.165;
        public const double NbDefaultThresholdScaled = 3.3;
    }

    public int ReadAudio(int channelId, Span<float> output)
    {
        if (!_channels.TryGetValue(channelId, out var state))
        {
            output.Clear();
            return 0;
        }

        lock (state.AudioGate)
        {
            int n = Math.Min(output.Length, state.AudioCount);
            if (n == 0) return 0;

            int tail = (state.AudioHead - state.AudioCount + AudioRingCapacity) % AudioRingCapacity;
            int firstChunk = Math.Min(n, AudioRingCapacity - tail);
            state.AudioRing.AsSpan(tail, firstChunk).CopyTo(output);
            int remainder = n - firstChunk;
            if (remainder > 0)
                state.AudioRing.AsSpan(0, remainder).CopyTo(output.Slice(firstChunk));

            state.AudioCount -= n;
            return n;
        }
    }

    private static void PushAudio(ChannelState state, ReadOnlySpan<double> interleavedStereo, int monoSampleCount)
    {
        lock (state.AudioGate)
        {
            for (int i = 0; i < monoSampleCount; i++)
            {
                // interleavedStereo is [L0, R0, L1, R1, ...]; take the left channel as mono.
                state.AudioRing[state.AudioHead] = (float)interleavedStereo[i * 2];
                state.AudioHead = (state.AudioHead + 1) % AudioRingCapacity;
                if (state.AudioCount < AudioRingCapacity)
                    state.AudioCount++;
                // Otherwise the oldest sample has been overwritten — head advance already did it.
            }
        }
    }

    // Thetis rxaMeterType.RXA_S_AV = 1 (console/dsp.cs:876-884) — average
    // signal strength in dBm, smoothed by WDSP's internal meter tau. Returns
    // a large negative (~−200) before any frame has been exchanged.
    private const int RxaMeterSAv = 1;

    // HL2 S-meter calibration offset in dB. Thetis
    // clsHardwareSpecific.cs:428 — RXMeterCalbrationOffsetDefaults default
    // branch returns 0.98f for non-ANAN models (HL2 falls here). Added to
    // GetRXAMeter output before exposing as dBm.
    private const double Hl2MeterCalOffsetDb = 0.98;

    private DateTime _lastRxMeterLogUtc;
    public double GetRxaSignalDbm(int channelId)
    {
        if (!_channels.ContainsKey(channelId)) return -200.0;
        double sAv = NativeMethods.GetRXAMeter(channelId, RxaMeterSAv);
        // Debug aid: if S_AV reads the "meter-didn't-run" sentinel (-400),
        // fall through to ADC_AV (index 3) which runs earlier in xrxa and
        // tells us whether the pipeline is exchanging at all. Pass the raw
        // value through on the sentinel path so the caller's `<= -399.0`
        // check still fires instead of being shifted by the cal.
        if (sAv <= -399.0)
        {
            double adcAv = NativeMethods.GetRXAMeter(channelId, 3);
            _log.LogInformation("wdsp.getRxaMeter sAv={SAv:F1} adcAv={AdcAv:F1} (sentinel)", sAv, adcAv);
            return sAv;
        }
        // Diagnostic 2026-04-18: log the live sAv at 1 Hz so we can see RX
        // signal level over time and pinpoint when it dies (e.g. after
        // MOX-on/off transition). Extended to read all four wcp-AGC indices —
        // smeter sits BEFORE the AGC stage in xrxa (RXA.c:645 vs 662), so if
        // sAv is -400 but agcAv/agcGain are real, the chain is alive through
        // AGC and the dead zone is between adcmeter and smeter (xbpsnbain or
        // xnbp). Conversely, if all are -400, xrxa itself is not running.
        var now = DateTime.UtcNow;
        if (now - _lastRxMeterLogUtc >= TimeSpan.FromSeconds(1))
        {
            _lastRxMeterLogUtc = now;
            // Indices per WDSP RXA.h:47-57 enum rxaMeterType.
            double adcAv = NativeMethods.GetRXAMeter(channelId, 3);   // RXA_ADC_AV
            double agcGain = NativeMethods.GetRXAMeter(channelId, 4); // RXA_AGC_GAIN
            double agcAv = NativeMethods.GetRXAMeter(channelId, 6);   // RXA_AGC_AV
            _log.LogInformation(
                "wdsp.rx.meter sAv={SAv:F1} adcAv={AdcAv:F1} agcGain={AgcGain:F1} agcAv={AgcAv:F1}",
                sAv, adcAv, agcGain, agcAv);
        }
        return sAv + Hl2MeterCalOffsetDb;
    }

    // Full RXA meter snapshot — fetches all 7 indices in one pass and
    // publishes under _rxMeterPublishLock so callers see a consistent set.
    // Indices per WDSP RXA.h:47-57 enum rxaMeterType:
    //   0  RXA_S_PK     1  RXA_S_AV     (signal peak / avg, dBm)
    //   2  RXA_ADC_PK   3  RXA_ADC_AV   (ADC input peak / avg, dBFS)
    //   4  RXA_AGC_GAIN              (AGC insertion gain, signed dB)
    //   5  RXA_AGC_PK   6  RXA_AGC_AV   (AGC envelope peak / avg, dBm)
    //
    // This helper is the canonical source for the 0x19 RxMetersV2Frame
    // broadcast in DspPipelineService. The pre-existing diagnostic reads
    // inside GetRxaSignalDbm (RXA_ADC_AV, RXA_AGC_GAIN, RXA_AGC_AV at 1 Hz)
    // are deliberately retained — they log a different cadence and tell us
    // whether the chain is alive when sAv is at sentinel.
    //
    // Cal offset is NOT applied here. The caller decides whether to add it
    // before serializing, so unit tests can assert raw WDSP output and a
    // future per-board calibration table can plug in at the broadcast seam
    // without re-touching this method. See plan §2.1 / §2.3.
    public RxStageMeters GetRxStageMeters(int channelId)
    {
        if (!_channels.ContainsKey(channelId)) return RxStageMeters.Silent;
        var snap = new RxStageMeters(
            SignalPk: (float)NativeMethods.GetRXAMeter(channelId, 0),
            SignalAv: (float)NativeMethods.GetRXAMeter(channelId, 1),
            AdcPk: (float)NativeMethods.GetRXAMeter(channelId, 2),
            AdcAv: (float)NativeMethods.GetRXAMeter(channelId, 3),
            AgcGain: (float)NativeMethods.GetRXAMeter(channelId, 4),
            AgcEnvPk: (float)NativeMethods.GetRXAMeter(channelId, 5),
            AgcEnvAv: (float)NativeMethods.GetRXAMeter(channelId, 6));
        lock (_rxMeterPublishLock) { _latestRxStageMeters = snap; }
        return snap;
    }

    public bool TryGetDisplayPixels(int channelId, DisplayPixout which, Span<float> dbOut)
    {
        if (!_channels.TryGetValue(channelId, out var state)) return false;
        if (dbOut.Length != state.PixelWidth)
            throw new ArgumentException($"expected span of {state.PixelWidth}", nameof(dbOut));

        lock (state.AnalyzerLock)
        {
            NativeMethods.GetPixels(channelId, (int)which, ref MemoryMarshal.GetReference(dbOut), out int flag);
            return flag == 1;
        }
    }

    public bool TryGetTxDisplayPixels(DisplayPixout which, Span<float> dbOut)
    {
        if (_disposed != 0) return false;
        int disp;
        int expectedWidth;
        lock (_txDispLock)
        {
            if (!_txDispAlive) return false;
            if (_txaChannelId is not int txa) return false;
            disp = txa;
            expectedWidth = _txDispPixelWidth;
            if (dbOut.Length != expectedWidth)
                throw new ArgumentException($"expected span of {expectedWidth}", nameof(dbOut));
            NativeMethods.GetPixels(disp, (int)which, ref MemoryMarshal.GetReference(dbOut), out int flag);
            return flag == 1;
        }
    }

    /// <summary>PureSignal feedback panadapter pixels — sourced from the
    /// post-PA loopback IQ pumped through FeedPsFeedbackBlock. Returns false
    /// when the PS-FB analyzer is not open (PS disarmed, TX analyzer inactive,
    /// or engine disposed). Caller is expected to gate this on
    /// <c>PsEnabled &amp;&amp; PsMonitorEnabled &amp;&amp; PsCorrecting</c> so a
    /// pre-correction transient doesn't briefly show splatter on the
    /// panadapter.</summary>
    public bool TryGetPsFeedbackDisplayPixels(DisplayPixout which, Span<float> dbOut)
    {
        if (_disposed != 0) return false;
        int disp;
        int expectedWidth;
        lock (_psFbDispLock)
        {
            if (!_psFbDispAlive) return false;
            if (_psFbDispId is not int id) return false;
            disp = id;
            expectedWidth = _psFbDispPixelWidth;
            if (dbOut.Length != expectedWidth)
                throw new ArgumentException($"expected span of {expectedWidth}", nameof(dbOut));
            NativeMethods.GetPixels(disp, (int)which, ref MemoryMarshal.GetReference(dbOut), out int flag);
            return flag == 1;
        }
    }

    public int OpenTxChannel(int outputRateHz = 48_000)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        int txaIdForReturn = OpenTxChannelInternal(outputRateHz);

        // If the operator had already toggled monitor on before TXA opened
        // (e.g. on a deferred protocol-2 connect path), open the channel now
        // that the IQ rate is known. EnsureMonitorChannelOpen short-circuits
        // when the channel is already open or the request flag is clear, so
        // calling it here is cheap on the common path. Done outside _txaLock
        // so the monitor lock-acquisition order stays one-way.
        if (_monitorRequested)
        {
            EnsureMonitorChannelOpen();
        }
        return txaIdForReturn;
    }

    private int OpenTxChannelInternal(int outputRateHz)
    {
        lock (_txaLock)
        {
            if (_txaChannelId is int existing) return existing;

            // Pick the TXA profile from the requested output rate. P1's DAC
            // runs at 48 kHz; the G2 on P2 expects 192 kHz. Any other value
            // falls back to the P1 profile (treated as "not a supported P2
            // rate" and keeps us off the air until the connect path specifies
            // one we know about).
            if (outputRateHz == 192_000)
            {
                _txaInSize = TxaInSizeP2;
                _txaDspSize = TxaDspSizeP2;
                _txaOutSize = TxaOutSizeP2;
                _txaInputRateHz = 48_000;
                _txaDspRateHz = 96_000;
                _txaOutputRateHz = 192_000;
                _txaCfirRun = true;
            }
            else
            {
                _txaInSize = TxaInSizeP1;
                _txaDspSize = TxaDspSizeP1;
                _txaOutSize = TxaOutSizeP1;
                _txaInputRateHz = 48_000;
                _txaDspRateHz = 48_000;
                _txaOutputRateHz = 48_000;
                _txaCfirRun = false;
            }

            // TXA id must not collide with any RXA id — pick the first free slot
            // past the current RXA allocation. WDSP doesn't care about id
            // ordering, it just uses the int as an index into its channel table.
            int id = 0;
            while (_channels.ContainsKey(id) || id == _txaChannelId) id++;

            // type: 1 (TX), state: 0 (stays quiescent until SetMox). Rates
            // chosen above so P1 keeps its 48/48/48 shape (rated power
            // confirmed on Hermes) and P2 matches pihpsdr transmitter.c's
            // 48/96/192 profile with ratio=4 so the G2 DUC sees samples at
            // its expected 192 kHz clock.
            NativeMethods.OpenChannel(
                channel: id,
                in_size: _txaInSize,
                dsp_size: _txaDspSize,
                input_samplerate: _txaInputRateHz,
                dsp_rate: _txaDspRateHz,
                output_samplerate: _txaOutputRateHz,
                type: 1,
                state: 0,
                tdelayup: 0.010,
                tslewup: 0.025,
                tdelaydown: 0.0,
                tslewdown: 0.010,
                bfo: 1);

            // SSB USB default + 150-2850 passband: wider than the classic SSB
            // 300-2700 to keep low-frequency voice energy through the chain
            // (task C.0 spec). Phase-C mic ingest drives fexchange2 once
            // SetMox(true) flips the TXA state to 1; until then the TXA sits
            // at state=0 and consumes nothing.
            NativeMethods.SetTXAMode(id, (int)RxaMode.USB);
            _txCurrentMode = RxaMode.USB;
            // Default passband matches the stock SSB TX width. DspPipelineService
            // re-asserts this from the live StateDto (TxFilterLowHz/HighHz)
            // immediately after OpenTxChannel so operator-edited widths survive
            // a protocol switch / engine reopen.
            NativeMethods.SetTXABandpassFreqs(id, 150.0, 2850.0);
            NativeMethods.SetTXABandpassWindow(id, 1);
            // Intentionally NOT calling SetTXABandpassRun(id, 1): despite the
            // name it sets bp1.run (the compressor-only aux bandpass), not bp0,
            // and bp1 ships with stale LSB-direction coefs that reject the USB
            // mic on first MOX — that's the "TX 0 W until mode toggle" symptom
            // the operator saw return after this branch first restored the call.
            // bp0 is always on from create_bandpass; nothing to enable here.
            NativeMethods.SetTXAPanelRun(id, 1);
            NativeMethods.SetTXAPanelGain1(id, 1.0);
            // pihpsdr transmitter.c:1298 routes mic to both I and Q via
            // PanelSelect=2 ("Mic I sample"). Without this, WDSP's default
            // may leave Q unassigned, allowing a secondary signal path to
            // leak into the TXA output.
            NativeMethods.SetTXAPanelSelect(id, 2);

            // Explicitly disable the PreGen stage and zero its state — pihpsdr
            // transmitter.c:1293-1296 does this on every TXA open. WDSP's
            // create_channel does not guarantee these defaults, and a residual
            // non-zero PreGen tone shows up alongside the PostGen tune carrier
            // as a second discrete frequency on the air (reported as
            // "2-tone-like output" during TUN on the G2 MkII).
            NativeMethods.SetTXAPreGenMode(id, 0);
            NativeMethods.SetTXAPreGenToneMag(id, 0.0);
            NativeMethods.SetTXAPreGenToneFreq(id, 0.0);
            NativeMethods.SetTXAPreGenRun(id, 0);

            // Clamp PostGen off at open time too — TUN will re-enable it via
            // SetTxTune. Same rationale: WDSP state from a previous channel
            // open can leak through if we don't zero it.
            NativeMethods.SetTXAPostGenRun(id, 0);

            // Explicit clean-slate TX chain state. WDSP initializes these
            // "off" at channel-create, but asserting them makes the baseline
            // deterministic and independent of the library build. Leveler is
            // Thetis-factory-ON (radio.cs:3018 tx_leveler_on = true) and is
            // enabled here to match that default — a disabled Leveler stage
            // also leaves GetTXAMeter(LVLR_PK) stuck at WDSP's -400 silence
            // sentinel, which made the frontend LVLR bar look broken. Other
            // stages (Compressor, CFC, PHROT, osctrl, EQ, AMSQ) remain OFF
            // until they're wired to operator UI and tuned — enabling them
            // with library-default parameters can mask or create distortion.
            // ALC stays on (see SetTXAALCSt below; never 0). AMSQ is the mic
            // noise gate and shouldn't shape SSB audio.
            NativeMethods.SetTXALevelerSt(id, 1);
            // Leveler max-gain default. WDSP's create_wcpagc ships with
            // max_gain = 1.778 linear (≈ +5 dB) at TXA.c:169; we assert the
            // value explicitly so the baseline stays deterministic and the
            // init log confirms what the Leveler's headroom is set to.
            // +5 dB matches the W1AEX / softerhardware community default
            // (milder than Thetis's +15 dB stock — see task #13 notes).
            // Operator-settable at runtime via POST /api/tx/leveler-max-gain.
            NativeMethods.SetTXALevelerTop(id, DefaultLevelerMaxGainDb);
            NativeMethods.SetTXACompressorRun(id, 0);
            NativeMethods.SetTXACFCOMPRun(id, 0);
            NativeMethods.SetTXAPHROTRun(id, 0);
            NativeMethods.SetTXAosctrlRun(id, 0);
            NativeMethods.SetTXAEQRun(id, 0);
            NativeMethods.SetTXAAMSQRun(id, 0);
            NativeMethods.SetTXAALCSt(id, 1);
            // ALC tuning — Zeus previously left these at WDSP library defaults
            // (MaxGain=0 dB linear/1.0). Thetis ships +3 dB max gain
            // (database.cs:4596). Attack 1 ms / Decay 10 ms matches both
            // pihpsdr (transmitter.c:1290-1291) and the WDSP factory that
            // Thetis inherits (TXA.c:319, tau_attack = 0.001). A slower 2 ms
            // attack missed plosive onset and the follow-up ALC chop sounded
            // crunchy — operator described it as "brittle."
            NativeMethods.SetTXAALCMaxGain(id, 3.0);
            NativeMethods.SetTXAALCAttack(id, 1);
            NativeMethods.SetTXAALCDecay(id, 10);

            // CFIR compensates the sinc droop introduced by the TXA upsample
            // to the output rate. Thetis (audio.cs:1808) turns it ON for P2,
            // OFF for P1; pihpsdr (transmitter.c:1288) does the same. Wiring
            // this on P1 would over-correct the flat 48k chain and tilt the
            // passband, so it's conditional on the P2 profile.
            if (_txaCfirRun)
            {
                NativeMethods.SetTXACFIRRun(id, 1);
            }

            _txaChannelId = id;

            // PureSignal seed. The TXA channel already owns `calcc.p` and
            // `iqc.p0/p1` as a side effect of create_txa() (TXA.c:405,424);
            // these setters tune the WDSP state machine to safe defaults so
            // arming PS later just needs SetPSRunCal(1) + SetPSControl mode-on.
            //
            // HW-peak is *not* set here — RadioService.SetPsHwPeak runs after
            // discovery so the right value (P1=0.4072 / G2=0.6121 / ANAN-7000
            // =0.2899) is applied per actual connected radio. The 0.4072 in
            // `_psHwPeak` is just a neutral default.
            //
            // See `docs/lessons/wdsp-init-gotchas.md`: setters before state-
            // flip is the load-bearing pattern. PS setters are independent of
            // `SetChannelState`, so they're safe to run unconditionally at
            // TXA open time.
            NativeMethods.SetPSFeedbackRate(id, 192_000);
            // pihpsdr semantic (transmitter.c:2517): ps_ptol=0 (default) → 0.8
            // strict; ps_ptol=1 → 0.4 relaxed. Same convention in Thetis
            // PSForm.designer.cs.
            NativeMethods.SetPSPtol(id, _psPtol ? 0.4 : 0.8);
            // pihpsdr/Thetis defaults: PinMode=1, MapMode=1 (transmitter.c:
            // 1041-1042 ps_map=1 / ps_pin=1; PSForm.designer.cs chkPSPin /
            // chkPSMap = Checked = true).
            NativeMethods.SetPSPinMode(id, 1);
            NativeMethods.SetPSMapMode(id, 1);
            NativeMethods.SetPSStabilize(id, 0);
            NativeMethods.SetPSIntsAndSpi(id, _psInts, _psSpi);
            NativeMethods.SetPSMoxDelay(id, _psMoxDelaySec);
            NativeMethods.SetPSLoopDelay(id, _psLoopDelaySec);
            _ = NativeMethods.SetPSTXDelay(id, _psAmpDelayNs * 1e-9);
            NativeMethods.SetPSHWPeak(id, _psHwPeak);
            NativeMethods.SetPSControl(id, 1, 0, 0, 0);   // RESET state
            // SetPSRunCal stays 0 until the operator arms PS.
            // Bring-up diagnostic — drop once PS is confirmed stable on rack.
            _log.LogInformation(
                "wdsp.psSeed pinMode=1 mapMode=1 ptol={Ptol} hwPeak={Peak:F4} feedbackRate=192000",
                _psPtol ? 0.4 : 0.8, _psHwPeak);

            // TX panadapter analyzer — issue #81. Match the first RXA's pixel
            // width and zoom so the TX trace renders into the same widget
            // without a span change on MOX. If no RXA exists yet (shouldn't
            // happen in practice — RadioService opens RX before TX), skip
            // analyzer creation and leave _txDispAlive false so the server
            // falls back to the RX pixels during MOX.
            int rxPixelWidth = 0;
            int rxSampleRateHz = 0;
            int rxZoom = 1;
            foreach (var st in _channels.Values)
            {
                rxPixelWidth = st.PixelWidth;
                rxSampleRateHz = st.SampleRateHz;
                rxZoom = Math.Max(1, st.ZoomLevel);
                break;
            }
            if (rxPixelWidth > 0)
            {
                // Analyzer disp index reuses the TXA channel id — WDSP keeps
                // channels and analyzers in separate arrays so the collision
                // between RXA's channel=0 / disp=0 and TXA's channel=id /
                // disp=id is purely in our bookkeeping, not in the library.
                NativeMethods.XCreateAnalyzer(id, out int txRc, MaxFftSize, 1, 1, null);
                if (txRc == 0)
                {
                    bool configured;
                    lock (_txDispLock)
                    {
                        _txDispPixelWidth = rxPixelWidth;
                        _txDispZoomLevel = rxZoom;
                        _txDispRxSampleRateHz = rxSampleRateHz;
                        // Configure for the SIPHON tap point (xsiphon position
                        // in xtxa — BEFORE iqc/cfir/rsmpout). dsp_rate / dsp_size
                        // describe the IQ at that stage. Pulling pre-iqc samples
                        // gives the operator's clean voice spectrum on the
                        // panadapter, matching Thetis (cmaster.cs:544-545,
                        // TXA.c:586). Pre-fix the analyzer was configured at
                        // the OUTPUT (post-cfir/rsmpout) rate and got fed the
                        // predistorted IQ — cosmetically dirty by design.
                        configured = TryConfigureTxAnalyzer(id, _txaDspRateHz, _txaDspSize, rxSampleRateHz, rxPixelWidth, rxZoom);
                        if (configured)
                        {
                            ConfigureDisplayAveragingTau(id, TxAvgTauSec);
                            _txDispAlive = true;
                        }
                    }
                    if (!configured)
                    {
                        // Rate relationship doesn't support bin-clip (e.g. TX narrower
                        // than RX, or non-integer ratio). Destroy the unused analyzer
                        // slot and leave _txDispAlive false so the panadapter falls
                        // back to the RX analyzer on MOX.
                        NativeMethods.DestroyAnalyzer(id);
                        _log.LogWarning(
                            "wdsp.openTxChannel tx-analyzer skipped — rx={RxRate} tx={TxRate} not an integer multiple; panadapter will fall back to RX trace",
                            rxSampleRateHz, _txaOutputRateHz);
                    }
                }
                else
                {
                    _log.LogWarning(
                        "wdsp.openTxChannel tx-analyzer XCreateAnalyzer rc={Rc} — TX panadapter will fall back to RX trace",
                        txRc);
                }
            }

            _log.LogInformation(
                "wdsp.openTxChannel id={Id} rates={InRate}/{DspRate}/{OutRate} sizes={InSz}/{OutSz} cfir={Cfir} chain=[alc=1 lvlr=1 lvlrMax={LvlrMax:F1}dB cpdr=0 cfc=0 phrot=0 osctrl=0 eq=0 amsq=0] bp=150..2850 panelGain=1.0 txDisp={TxDisp}(pix={Pix} rxRate={RxRate} txRate={TxRate} zoom={Zoom})",
                id, _txaInputRateHz, _txaDspRateHz, _txaOutputRateHz,
                _txaInSize, _txaOutSize, _txaCfirRun ? 1 : 0, DefaultLevelerMaxGainDb,
                _txDispAlive ? "on" : "off", _txDispPixelWidth, _txDispRxSampleRateHz, _txaOutputRateHz, _txDispZoomLevel);
            return id;
        }
    }

    public void SetMox(bool moxOn)
    {
        if (_disposed != 0) return;

        int txaId;
        int rxaId;
        lock (_txaLock)
        {
            if (_txaChannelId is not int txa) return;
            txaId = txa;

            // v0.1 always has exactly one RXA open; take the first key. If
            // there's no RXA (shouldn't happen in practice) the SetMox call is
            // meaningless — bail without touching TXA so we don't desync state.
            int? rxa = null;
            foreach (var key in _channels.Keys) { rxa = key; break; }
            if (rxa is not int r) return;
            rxaId = r;
        }

        // Thetis console.cs:31375/31387/31409 orders the transitions so the
        // outgoing side is damped (dmp=1) before the incoming side comes up
        // clean (dmp=0) — avoids a pop from the demuted side catching an
        // in-flight buffer.
        //
        // TX-monitor wrinkle: when MOX falls but monitor is on, TXA must
        // stay running so fexchange2 keeps producing IQ for the monitor
        // demod path. We re-derive TXA target = (MOX || monitor) so the
        // monitor path doesn't go silent when the operator releases MOX.
        int rxaPrior, txaPrior = -1;
        bool wantTxa = moxOn || _monitorRequested;
        if (moxOn)
        {
            _moxOn = true;
            rxaPrior = NativeMethods.SetChannelState(rxaId, 0, 1);
            if (!_txaRunning)
            {
                txaPrior = NativeMethods.SetChannelState(txaId, 1, 0);
                _txaRunning = true;
            }
            // No priming: Thetis (console.cs:31375) does not prime — bfo=1
            // semantics already make the first fexchange wait for real output.
            // Tell PureSignal calcc that MOX is now true so the LCOLLECT phase
            // can advance once feedback IQ starts flowing. Safe to call even
            // when PS isn't armed — it just toggles a flag inside calcc.
            NativeMethods.SetPSMox(txaId, 1);
        }
        else
        {
            _moxOn = false;
            // Drop the PS MOX flag *before* the TXA state-flip so the iqc
            // stage sees "no longer transmitting" while the chain is still
            // alive — same ordering pihpsdr uses (transmitter.c:2422-2444).
            NativeMethods.SetPSMox(txaId, 0);
            // Only damp TXA if the audition path doesn't need it. When
            // monitor is on, TXA stays at state=1 so the chain keeps
            // producing IQ to be demodulated by the monitor RXA channel.
            if (_txaRunning && !wantTxa)
            {
                txaPrior = NativeMethods.SetChannelState(txaId, 0, 1);
                _txaRunning = false;
            }
            rxaPrior = NativeMethods.SetChannelState(rxaId, 1, 0);
            // PERF_PASS_3_DEBUG: t2 — WDSP RXA brought back up. Uncommitted.
            _log.LogInformation("wdsp.rxa.up ts={Ts}",
                System.Diagnostics.Stopwatch.GetTimestamp());
            // Unkeying: clear the stage-meter snapshot so UI doesn't latch the
            // last-during-TX reading while idle. The next MOX-on will publish
            // fresh data on its first ProcessTxBlock.
            lock (_txMeterPublishLock) { _latestTxStageMeters = null; }
        }
        // Diagnostic 2026-04-18: capture the prior-state return of every
        // SetChannelState call so we can detect cases where the requested
        // transition was a no-op (prior == new) — that's the failure mode that
        // looks like "RX audio doesn't come back after MOX-off".
        _log.LogInformation(
            "wdsp.setMox on={Mox} rxa={Rxa} (prior {RxaPrior}) txa={Txa} (prior {TxaPrior})",
            moxOn, rxaId, rxaPrior, txaId, txaPrior);
    }

    public TxStageMeters GetTxStageMeters()
    {
        lock (_txMeterPublishLock)
        {
            return _latestTxStageMeters ?? TxStageMeters.Silent;
        }
    }

    public int TxBlockSamples => _txaInSize;
    public int TxOutputSamples => _txaOutSize;

    public void SetTxPanelGain(double linearGain)
    {
        if (_disposed != 0) return;
        lock (_txaLock)
        {
            if (_txaChannelId is not int txa) return;
            NativeMethods.SetTXAPanelGain1(txa, linearGain);
        }
        _log.LogInformation("wdsp.setTxPanelGain linear={Gain:F3}", linearGain);
    }

    public void SetTxLevelerMaxGain(double maxGainDb)
    {
        if (_disposed != 0) return;
        lock (_txaLock)
        {
            if (_txaChannelId is not int txa) return;
            NativeMethods.SetTXALevelerTop(txa, maxGainDb);
        }
        _log.LogInformation("wdsp.setTxLevelerMaxGain dB={Db:F1}", maxGainDb);
    }

    public void SetTxTune(bool on)
    {
        if (_disposed != 0) return;
        lock (_txaLock)
        {
            if (_txaChannelId is not int txa) return;
            // Thetis console.cs:31806-31829 (chkTUN_CheckedChanged, non-pulse
            // branch): mode=0 single tone at ±cw_pitch offset, mag=MAX_TONE_MAG.
            // wdsp/gen.c:221-241: mode 0 = tone, mode 1 = two-tone (summed).
            // Two-tone produces a difference-frequency beat envelope, which
            // shows up on the forward-power meter as jitter — that's why the
            // old mode=1 reading "jumped like Parkinson's". The tone is offset
            // by cw_pitch so it lands in the TXA sideband passband set by
            // ApplyTxBandpassForMode (a 0 Hz tone sits on the suppressed-carrier
            // null for SSB). Sign mirrors Thetis's sideband rule.
            if (on)
            {
                // pihpsdr radio.c:2716/2743 tunes at freq=0.0 — a true
                // zero-beat carrier right on the VFO, which is the correct
                // signal for tuning an external antenna tuner. Thetis uses
                // ±cw_pitch so the tone survives the SSB bandpass, but for
                // an ATU we want the carrier on-frequency, not 600 Hz off.
                // Mode 0 = single tone (pihpsdr transmitter.c:2808, Thetis
                // console.cs:30089 — both use mode 0).
                const double toneFreq = 0.0;
                const double toneMag = 0.99999;
                NativeMethods.SetTXAPostGenMode(txa, 0);
                NativeMethods.SetTXAPostGenToneFreq(txa, toneFreq);
                NativeMethods.SetTXAPostGenToneMag(txa, toneMag);
                NativeMethods.SetTXAPostGenRun(txa, 1);
                // Disable Leveler while TUN is keyed. pihpsdr sidesteps the
                // AGC-pumping AM envelope by keeping Leveler off
                // (transmitter.c:2612 — state = compressor||cfc, both off
                // on tune). We restore Leveler on TUN-off so mic MOX keeps
                // its current Thetis-matching behavior.
                NativeMethods.SetTXALevelerSt(txa, 0);
                _log.LogInformation("wdsp.setTxTune on=true mode=singletone freq={Freq:F0} mag={Mag:F5} leveler=off", toneFreq, toneMag);
            }
            else
            {
                NativeMethods.SetTXAPostGenRun(txa, 0);
                NativeMethods.SetTXALevelerSt(txa, 1);
                _log.LogInformation("wdsp.setTxTune on=false leveler=on");
            }
        }
    }

    public void SetTxMode(RxMode mode)
    {
        if (_disposed != 0) return;
        var mapped = MapMode(mode);
        lock (_txaLock)
        {
            if (_txaChannelId is not int txa) return;
            NativeMethods.SetTXAMode(txa, (int)mapped);
            _txCurrentMode = mapped;
            // TXA bandpass is now operator-controlled — DspPipelineService
            // asserts SetTxFilter after SetTxMode using the per-mode-family
            // memory in RadioService. No auto-apply here.
            //
            // TwoTone is sideband-sensitive (gen.c xgen mode-1 emits e^(-jωt)
            // which always lands LSB-side of carrier). If a TwoTone test is
            // mid-flight when the operator changes mode, re-assert PostGen
            // freqs with the new sign so the tones stay inside the displayed
            // bandpass. Mag and run flag stay as last set.
            if (_twoToneArmed)
            {
                bool usbFamily = mapped == RxaMode.USB
                              || mapped == RxaMode.CWU
                              || mapped == RxaMode.DIGU;
                double signedF1 = usbFamily ? -_twoToneF1Hz : _twoToneF1Hz;
                double signedF2 = usbFamily ? -_twoToneF2Hz : _twoToneF2Hz;
                NativeMethods.SetTXAPostGenTTFreq(txa, signedF1, signedF2);
                _log.LogInformation(
                    "wdsp.setTxMode twoTone re-signed f1={F1} f2={F2} signedF1={SF1} signedF2={SF2} mode={Mode}",
                    _twoToneF1Hz, _twoToneF2Hz, signedF1, signedF2, mapped);
            }
        }
        // Mirror the mode onto the monitor channel so the audition demodulates
        // with the same sideband / modulation as the on-air signal.
        lock (_monitorLock)
        {
            _monitorMode = mapped;
            if (_monitorChannelId is int monId)
            {
                SetMode(monId, mode);
            }
        }
        _log.LogInformation("wdsp.setTxMode mode={Mode}", mapped);
    }

    public void SetTxFilter(int lowHz, int highHz)
    {
        if (_disposed != 0) return;
        lock (_txaLock)
        {
            if (_txaChannelId is not int txa) return;
            NativeMethods.SetTXABandpassFreqs(txa, lowHz, highHz);
        }
        // Mirror the filter onto the monitor channel so the audition stays at
        // the same bandwidth as the on-air signal. Stash the values regardless
        // of whether the monitor channel is open yet — EnsureMonitorChannelOpen
        // reads them at lazy-open time.
        lock (_monitorLock)
        {
            _monitorFilterLow = lowHz;
            _monitorFilterHigh = highHz;
            if (_monitorChannelId is int monId)
            {
                SetFilter(monId, lowHz, highHz);
            }
        }
        _log.LogInformation("wdsp.setTxFilter low={Low} high={High}", lowHz, highHz);
    }

    /// <summary>Operator-facing TX-monitor toggle. When true, the engine opens
    /// (or reuses) a private RXA channel and feeds it the post-CFIR/RSMPOUT TX
    /// IQ produced inside <see cref="ProcessTxBlock"/>; the demodulated mono
    /// audio is available via <see cref="ReadTxMonitorAudio"/>. The channel is
    /// only opened once TXA exists; calling this before <see cref="OpenTxChannel"/>
    /// stores the request and the channel opens on the next OpenTxChannel
    /// (or the first ProcessTxBlock with a valid TXA, whichever runs first).
    /// Toggling off keeps the channel allocated but stops feeding it, so the
    /// next on-toggle is instant.</summary>
    public void SetTxMonitorEnabled(bool enabled)
    {
        if (_disposed != 0) return;
        _monitorRequested = enabled;
        if (enabled)
        {
            EnsureMonitorChannelOpen();
        }

        // Flip TXA's run state if the audition path's requirement diverges
        // from MOX's. Without this the chain stays quiescent (state=0) when
        // the operator hits monitor with MOX off, fexchange2 returns without
        // filling iout/qout, and the monitor RXA gets silence (or stack
        // garbage from the uninitialised IQ buffer). RXA stays put — the
        // operator still wants to NOT hear the band when monitor is on, but
        // we don't damp RXA either, since the AudioFrame substitution in
        // DspPipelineService.Tick handles the "RX muted" UX cleanly.
        int? txaPrior = null;
        bool nowRunning;
        lock (_txaLock)
        {
            if (_txaChannelId is int txa && !_moxOn)
            {
                bool wantTxa = enabled;  // MOX off; TXA target derived from monitor
                if (_txaRunning != wantTxa)
                {
                    txaPrior = NativeMethods.SetChannelState(txa, wantTxa ? 1 : 0, wantTxa ? 0 : 1);
                    _txaRunning = wantTxa;
                }
            }
            nowRunning = _txaRunning;
        }
        _log.LogInformation(
            "wdsp.setTxMonitor requested={Enabled} channelId={Id} txaRunning={Running}{Prior}",
            enabled, _monitorChannelId, nowRunning,
            txaPrior is int p ? $" (txa prior={p})" : "");
    }

    /// <summary>Drain demodulated TX-monitor audio into <paramref name="output"/>.
    /// Returns the number of mono float32 samples written, 0 when monitor is
    /// off or the channel hasn't been opened yet. Same shape as
    /// <see cref="ReadAudio"/> but routes to the private monitor channel.</summary>
    public int ReadTxMonitorAudio(Span<float> output)
    {
        if (_disposed != 0) return 0;
        if (!_monitorRequested) return 0;
        int? id = _monitorChannelId;
        if (id is null) return 0;
        return ReadAudio(id.Value, output);
    }

    /// <summary>Volatile-read so callers can gate the audio-broadcast path
    /// without taking _monitorLock. Reflects the operator's request, not
    /// whether the channel is fully open.</summary>
    public bool IsTxMonitorOn => _monitorRequested;

    // Open the monitor RXA channel matched to the current TXA output rate. The
    // channel uses the standard OpenChannel lifecycle (state=0 → configure →
    // worker → SetChannelState(id,1,0)) so the wdsp-init-gotchas.md ordering
    // is honoured. Mode + filter are synced from the latched TX values so the
    // audition starts at the right bandwidth profile from the first sample.
    //
    // No-op if the monitor channel is already open. No-op (with a deferred
    // open) if TXA isn't open yet — first OpenTxChannel will retry.
    private void EnsureMonitorChannelOpen()
    {
        lock (_monitorLock)
        {
            if (_monitorChannelId is not null) return;
            int iqRate;
            lock (_txaLock)
            {
                if (_txaChannelId is null) return;
                iqRate = _txaOutputRateHz;
            }
            // PixelWidth=1024 is plenty for the analyzer that OpenChannel
            // creates. The analyzer output is never read for the monitor
            // channel; we keep it allocated so RunWorker's Spectrum0 call
            // doesn't crash on an unallocated slot. Cost: a few KB of FFT
            // state per engine — negligible.
            int id;
            try
            {
                id = OpenChannel(iqRate, pixelWidth: 1024);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "wdsp.openMonitorChannel failed iqRate={Rate}", iqRate);
                return;
            }
            // Sync mode + filter to current TX state. SetMode also clears the
            // audio ring so the first audition block starts from silence.
            SetMode(id, MapRxaToRxMode(_txCurrentMode));
            SetFilter(id, _monitorFilterLow, _monitorFilterHigh);
            _monitorMode = _txCurrentMode;
            _monitorChannelId = id;
            _log.LogInformation(
                "wdsp.openMonitorChannel id={Id} iqRate={Rate} mode={Mode} filter=[{Lo},{Hi}]",
                id, iqRate, _txCurrentMode, _monitorFilterLow, _monitorFilterHigh);
        }
    }

    // RxaMode → RxMode reverse lookup so the monitor channel can be configured
    // via the public SetMode API (which takes the contract enum). The forward
    // mapping lives at MapMode(RxMode); kept inline here since the inverse is
    // only needed by the monitor seam.
    private static RxMode MapRxaToRxMode(RxaMode m) => m switch
    {
        RxaMode.LSB => RxMode.LSB,
        RxaMode.USB => RxMode.USB,
        RxaMode.CWL => RxMode.CWL,
        RxaMode.CWU => RxMode.CWU,
        RxaMode.AM => RxMode.AM,
        RxaMode.FM => RxMode.FM,
        RxaMode.SAM => RxMode.SAM,
        RxaMode.DSB => RxMode.DSB,
        RxaMode.DIGL => RxMode.DIGL,
        RxaMode.DIGU => RxMode.DIGU,
        _ => RxMode.USB,
    };

    public void SetTwoTone(bool on, double freq1, double freq2, double mag)
    {
        if (_disposed != 0) return;
        // Clamp to safe ranges. Audio passband 50..5000; mag 0..1 linear.
        if (freq1 < 50.0) freq1 = 50.0;
        if (freq1 > 5000.0) freq1 = 5000.0;
        if (freq2 < 50.0) freq2 = 50.0;
        if (freq2 > 5000.0) freq2 = 5000.0;
        if (mag < 0.0) mag = 0.0;
        if (mag > 1.0) mag = 1.0;
        lock (_txaLock)
        {
            if (_txaChannelId is not int txa) return;
            if (on)
            {
                // gen.c xgen mode-1 emits e^(-jωt): positive freq lands on
                // the LSB-side of carrier in any TX mode. USB-family modes
                // need a sign flip so the tones appear above carrier inside
                // the displayed bandpass. Cache the operator-supplied
                // (unsigned) freqs so SetTxMode can re-assert with the
                // correct sign on a mid-test mode change.
                bool usbFamily = _txCurrentMode == RxaMode.USB
                              || _txCurrentMode == RxaMode.CWU
                              || _txCurrentMode == RxaMode.DIGU;
                double signedF1 = usbFamily ? -freq1 : freq1;
                double signedF2 = usbFamily ? -freq2 : freq2;
                _twoToneF1Hz = freq1;
                _twoToneF2Hz = freq2;
                _twoToneArmed = true;
                // PostGen mode=1 = two-tone summed (gen.c:221-241).
                NativeMethods.SetTXAPostGenMode(txa, 1);
                NativeMethods.SetTXAPostGenTTFreq(txa, signedF1, signedF2);
                NativeMethods.SetTXAPostGenTTMag(txa, mag, mag);
                NativeMethods.SetTXAPostGenRun(txa, 1);
                // Same Leveler-off pattern SetTxTune uses; the test signal
                // doesn't need voice-energy AGC and Leveler can pump on the
                // discrete tones.
                NativeMethods.SetTXALevelerSt(txa, 0);
                _log.LogInformation(
                    "wdsp.setTwoTone on=true f1={F1} f2={F2} signedF1={SF1} signedF2={SF2} mag={Mag:F3} mode={Mode}",
                    freq1, freq2, signedF1, signedF2, mag, _txCurrentMode);
            }
            else
            {
                _twoToneArmed = false;
                NativeMethods.SetTXAPostGenRun(txa, 0);
                NativeMethods.SetTXALevelerSt(txa, 1);
                _log.LogInformation(
                    "wdsp.setTwoTone on=false f1={F1} f2={F2} mag={Mag:F3}",
                    freq1, freq2, mag);
            }
        }
    }

    public void SetPsHwPeak(double hwPeak)
    {
        if (_disposed != 0) return;
        if (hwPeak <= 0.0 || hwPeak > 2.0) return;   // bogus value, ignore
        lock (_psLock)
        {
            _psHwPeak = hwPeak;
            int? txa;
            lock (_txaLock) txa = _txaChannelId;
            if (txa is int id)
            {
                NativeMethods.SetPSHWPeak(id, hwPeak);
            }
        }
        _log.LogInformation("wdsp.setPsHwPeak peak={Peak:F4}", hwPeak);
    }

    public void SetPsControl(bool autoCal, bool singleCal)
    {
        if (_disposed != 0) return;
        lock (_psLock)
        {
            _psAuto = autoCal;
            _psSingle = singleCal;
            int? txa;
            lock (_txaLock) txa = _txaChannelId;
            if (txa is not int id) return;
            // (reset, mancal, automode, turnon) — see Thetis PSForm.cs.
            // Single takes precedence over auto when both true.
            int reset = 0;
            int mancal = singleCal ? 1 : 0;
            int automode = (autoCal && !singleCal) ? 1 : 0;
            int turnon = 0;
            if (!autoCal && !singleCal)
            {
                // Both off → reset / idle.
                reset = 1;
            }
            NativeMethods.SetPSControl(id, reset, mancal, automode, turnon);
        }
        _log.LogInformation("wdsp.setPsControl auto={Auto} single={Single}", autoCal, singleCal);
    }

    public void SetPsAdvanced(bool ptol, double moxDelaySec, double loopDelaySec,
                              double ampDelayNs, double hwPeak, int ints, int spi)
    {
        if (_disposed != 0) return;
        lock (_psLock)
        {
            int? txa;
            lock (_txaLock) txa = _txaChannelId;
            int id = txa ?? -1;

            if (ptol != _psPtol)
            {
                _psPtol = ptol;
                // ptol=true → relax 0.4; ptol=false → strict 0.8
                // (pihpsdr transmitter.c:2517 / Thetis PSForm.cs).
                if (id >= 0) NativeMethods.SetPSPtol(id, ptol ? 0.4 : 0.8);
            }
            if (moxDelaySec != _psMoxDelaySec)
            {
                _psMoxDelaySec = moxDelaySec;
                if (id >= 0) NativeMethods.SetPSMoxDelay(id, moxDelaySec);
            }
            if (loopDelaySec != _psLoopDelaySec)
            {
                _psLoopDelaySec = loopDelaySec;
                if (id >= 0) NativeMethods.SetPSLoopDelay(id, loopDelaySec);
            }
            if (ampDelayNs != _psAmpDelayNs)
            {
                _psAmpDelayNs = ampDelayNs;
                if (id >= 0) _ = NativeMethods.SetPSTXDelay(id, ampDelayNs * 1e-9);
            }
            // mi0bot: PSForm.cs PSpeak_TextChanged calls
            // puresignal.SetPSHWPeak(_txachannel, _PShwpeak) unconditionally on
            // every TextChanged, with no equality guard against the prior
            // value. Mirror that here so the operator can re-push the same
            // value to clear an info[6]=0x0044 fault state without typing a
            // different value first. Range check stays (mi0bot stops the
            // operator at the WinForms NUD min/max).
            if (hwPeak > 0.0 && hwPeak <= 2.0)
            {
                _psHwPeak = hwPeak;
                if (id >= 0) NativeMethods.SetPSHWPeak(id, hwPeak);
            }
            // Only call SetPSIntsAndSpi when the values actually changed —
            // it's a heavy restart inside calcc.c (allocates new buffers).
            if (ints > 0 && spi > 0 && (ints != _psInts || spi != _psSpi))
            {
                _psInts = ints;
                _psSpi = spi;
                if (id >= 0) NativeMethods.SetPSIntsAndSpi(id, ints, spi);
            }
        }
        _log.LogInformation(
            "wdsp.setPsAdvanced ptol={Ptol} mox={Mox:F3}s loop={Loop:F3}s amp={Amp:F1}ns peak={Peak:F4} ints={Ints} spi={Spi}",
            ptol, moxDelaySec, loopDelaySec, ampDelayNs, hwPeak, ints, spi);
    }

    public void SetPsEnabled(bool enabled)
    {
        if (_disposed != 0) return;
        lock (_psLock)
        {
            int? txa;
            lock (_txaLock) txa = _txaChannelId;
            if (txa is not int id)
            {
                _psEnabled = false;
                return;
            }

            if (enabled)
            {
                _psEnabled = true;
                // Reset diagnostic counters so the first state transition
                // and the first 100 pscc blocks log on every fresh arm.
                _lastLoggedPsState = 255;
                Interlocked.Exchange(ref _psFeedCount, 0);
                NativeMethods.SetPSRunCal(id, 1);
                int mancal = _psSingle ? 1 : 0;
                int automode = (_psAuto && !_psSingle) ? 1 : 0;
                // reset=1 forces a clean LRESET transit so a re-arm after a
                // single-cal cycle (which can leave the SM in LSTAYON) starts
                // a fresh fit (Thetis PSForm.cs:645,661).
                NativeMethods.SetPSControl(id, 1, mancal, automode, 0);
                // Open the PS-feedback display analyzer (issue #121). Inherits
                // pixel width / zoom / matched RX rate from the TX analyzer so
                // the PS-Monitor pan/wf frames slot into the same widget the
                // TX analyzer is rendering into. Skipped when TX analyzer is
                // off (no RXA, or P1 P2 rate-ratio mismatch) — the toggle
                // becomes a no-op in that case and Tick keeps falling through
                // to the existing TX/RX trace.
                OpenPsFeedbackAnalyzer(id);
            }
            else
            {
                _psEnabled = false;
                // Tear down the PS-FB analyzer first so a stale GetPixels
                // call from Tick doesn't race with WDSP cleaning up the slot.
                ClosePsFeedbackAnalyzer();
                // pihpsdr shutdown gotcha (transmitter.c:2422-2444): when
                // disabling PS while NOT keyed, push 7 zero-IQ blocks through
                // psccF so the calcc state machine advances to LRESET cleanly
                // and doesn't latch a stale curve in iqc on re-arm.
                var zeros = new float[PsFeedbackBlockSize];
                for (int i = 0; i < 7; i++)
                {
                    NativeMethods.psccF(id, PsFeedbackBlockSize, zeros, zeros, zeros, zeros, 0, 0);
                }
                NativeMethods.SetPSRunCal(id, 0);
                NativeMethods.SetPSControl(id, 1, 0, 0, 0);
            }
        }
        _log.LogInformation("wdsp.setPsEnabled enabled={Enabled}", enabled);
    }

    // Open / configure the PS-feedback display analyzer. Caller holds
    // _psLock. Mirrors the TX analyzer's pixel width / zoom / matched RX
    // sample rate so DspPipelineService.Tick can pick between TX-pixels and
    // PS-FB-pixels per tick without a buffer resize.
    private void OpenPsFeedbackAnalyzer(int txaId)
    {
        // Snapshot TX-display geometry under its own lock — we need it whether
        // or not _txDispAlive is true, but the values are only meaningful when
        // it is.
        bool txAlive;
        int pixelWidth;
        int rxRate;
        int zoom;
        lock (_txDispLock)
        {
            txAlive = _txDispAlive;
            pixelWidth = _txDispPixelWidth;
            rxRate = _txDispRxSampleRateHz;
            zoom = _txDispZoomLevel;
        }
        if (!txAlive || pixelWidth <= 0)
        {
            _log.LogInformation("wdsp.psFb.open skip — txDisp not alive (toggle will fall through to TX pixels)");
            return;
        }

        lock (_psFbDispLock)
        {
            if (_psFbDispAlive) return;

            // Pick a disp slot that doesn't collide with any RX channel id or
            // the TXA channel id. WDSP's analyzer table is indexed
            // independently of channel ids (see comment at OpenTxChannel
            // analyzer creation), but our own bookkeeping needs the int to be
            // unique so SetZoom / Spectrum0 / GetPixels reach the right slot.
            int psFbId = 0;
            while (_channels.ContainsKey(psFbId) || psFbId == txaId) psFbId++;

            NativeMethods.XCreateAnalyzer(psFbId, out int rc, MaxFftSize, 1, 1, null);
            if (rc != 0)
            {
                _log.LogWarning("wdsp.psFb.open XCreateAnalyzer rc={Rc} — PS-Monitor will fall back to TX trace", rc);
                return;
            }
            bool configured = TryConfigureTxAnalyzer(psFbId, PsFeedbackSampleRateHz, PsFeedbackBlockSize, rxRate, pixelWidth, zoom);
            if (!configured)
            {
                NativeMethods.DestroyAnalyzer(psFbId);
                _log.LogWarning(
                    "wdsp.psFb.open skipped — rx={RxRate} psFb={PsFbRate} not an integer multiple; PS-Monitor will fall back to TX trace",
                    rxRate, PsFeedbackSampleRateHz);
                return;
            }
            ConfigureDisplayAveragingTau(psFbId, TxAvgTauSec);
            _psFbDispId = psFbId;
            _psFbDispPixelWidth = pixelWidth;
            _psFbDispRxSampleRateHz = rxRate;
            _psFbDispZoomLevel = zoom;
            _psFbDispAlive = true;
            _log.LogInformation(
                "wdsp.psFb.open id={Id} pix={Pix} rxRate={RxRate} psFbRate={PsFbRate} zoom={Zoom}",
                psFbId, pixelWidth, rxRate, PsFeedbackSampleRateHz, zoom);
        }
    }

    // Tear down the PS-feedback display analyzer. Caller holds _psLock so
    // FeedPsFeedbackBlock can't race in mid-Spectrum0; combined with
    // _psFbDispLock around GetPixels / Spectrum0 this keeps the analyzer slot
    // safe to destroy.
    private void ClosePsFeedbackAnalyzer()
    {
        lock (_psFbDispLock)
        {
            if (!_psFbDispAlive) return;
            if (_psFbDispId is int id)
            {
                NativeMethods.DestroyAnalyzer(id);
                _log.LogInformation("wdsp.psFb.close id={Id}", id);
            }
            _psFbDispId = null;
            _psFbDispAlive = false;
            _psFbDispPixelWidth = 0;
            _psFbDispRxSampleRateHz = 0;
            _psFbDispZoomLevel = 1;
        }
    }

    public void FeedPsFeedbackBlock(ReadOnlySpan<float> txI, ReadOnlySpan<float> txQ,
                                    ReadOnlySpan<float> rxI, ReadOnlySpan<float> rxQ)
    {
        if (_disposed != 0) return;
        if (txI.Length != PsFeedbackBlockSize ||
            txQ.Length != PsFeedbackBlockSize ||
            rxI.Length != PsFeedbackBlockSize ||
            rxQ.Length != PsFeedbackBlockSize)
        {
            // Don't throw — log once and drop, so a transient sizing mismatch
            // upstream (DDC re-config edge) doesn't crash the pipeline.
            _log.LogWarning("wdsp.feedPsFeedback block sizes mismatch; expected {Expected}", PsFeedbackBlockSize);
            return;
        }
        int? txa;
        lock (_txaLock) txa = _txaChannelId;
        if (txa is not int id) return;

        // psccF takes float[] (not Span). Allocate fresh — caller may reuse
        // its buffers immediately after this returns.
        var bufTxI = txI.ToArray();
        var bufTxQ = txQ.ToArray();
        var bufRxI = rxI.ToArray();
        var bufRxQ = rxQ.ToArray();

        lock (_psLock)
        {
            // mox/solidmox args are ignored by psccF (calcc.c:846); SetPSMox
            // is the source of truth and is driven from SetMox above.
            NativeMethods.psccF(id, PsFeedbackBlockSize, bufTxI, bufTxQ, bufRxI, bufRxQ, 0, 0);
            long n = Interlocked.Increment(ref _psFeedCount);
            if (n % 100 == 1)
            {
                // Confirms paired packets are reaching the engine. If this
                // line never appears while keyed + PS armed, the wire path
                // (Protocol2Client paired-packet decode) isn't running.
                _log.LogInformation("wdsp.pscc fed {N} blocks", n);
            }
        }

        // Feed the PS-feedback display analyzer with the same rxI/rxQ block
        // (post-PA loopback IQ). DspPipelineService.Tick reads from this
        // analyzer when PsMonitorEnabled is on, surfacing the actual on-air
        // signal instead of the predistorted TX-modulator IQ. Q is negated
        // for the same WDSP analyzer convention used on the RX and TX paths
        // (see ProcessTxBlock: `txSpectrumIq[2*i + 1] = -qout[i]`); without
        // it the PS-Monitor view would render with sidebands flipped about
        // the carrier.
        if (_psFbDispAlive)
        {
            int? psFbId = null;
            lock (_psFbDispLock)
            {
                if (_psFbDispAlive) psFbId = _psFbDispId;
            }
            if (psFbId is int fbDisp)
            {
                Span<double> psSpectrumIq = stackalloc double[2 * PsFeedbackBlockSize];
                for (int i = 0; i < PsFeedbackBlockSize; i++)
                {
                    psSpectrumIq[2 * i] = bufRxI[i];
                    psSpectrumIq[2 * i + 1] = -bufRxQ[i];
                }
                lock (_psFbDispLock)
                {
                    if (_psFbDispAlive && _psFbDispId == fbDisp)
                    {
                        NativeMethods.Spectrum0(1, fbDisp, 0, 0, ref psSpectrumIq[0]);
                        long n = ++_psFbFeedCount;
                        if (n == 1 || n % 200 == 0)
                        {
                            _log.LogInformation("wdsp.psFb.fed n={N} blocks", n);
                        }
                    }
                }
            }
        }
    }

    public PsStageMeters GetPsStageMeters()
    {
        if (_disposed != 0) return PsStageMeters.Silent;
        int? txa;
        lock (_txaLock) txa = _txaChannelId;
        if (txa is not int id) return PsStageMeters.Silent;
        // Skip the GetPSInfo P/Invoke when PS isn't armed — saves a per-tick
        // jaunt into the native side and matches the wire-quiet contract for
        // the PsMeters frame.
        if (!_psEnabled) return PsStageMeters.Silent;

        // Pin the int[16] buffer for the duration of the GetPSInfo call so
        // WDSP can write into it. Re-using the same buffer between calls is
        // fine because GetPSInfo writes synchronously.
        int feedbackRaw;
        byte calState;
        bool correcting;
        double maxTx;
        int calibrationAttempts;
        lock (_psLock)
        {
            unsafe
            {
                fixed (int* p = _psInfoBuf)
                {
                    NativeMethods.GetPSInfo(id, (IntPtr)p);
                }
            }
            feedbackRaw = _psInfoBuf[4];
            correcting = _psInfoBuf[14] != 0;
            calState = (byte)Math.Clamp(_psInfoBuf[15], 0, 255);
            calibrationAttempts = _psInfoBuf[5];
            NativeMethods.GetPSMaxTX(id, out maxTx);
            _psMaxTxEnvelope = maxTx;
        }

        // CorrectionDb: until we tap GetPSDisp's curve, derive a coarse
        // proxy as 20*log10(feedbackLevel/256+eps). Replace with a real RMS
        // when we wire GetPSDisp. Safe: callers treat <=−200 as "bypassed".
        float feedback = feedbackRaw;
        float depthDb = correcting
            ? (float)(20.0 * Math.Log10(Math.Max(feedback, 1e-3) / 256.0))
            : 0f;

        // Edge-triggered state-transition log. calcc.c:543-552 LRESET=0,
        // LWAIT=1, LMOXDELAY=2, LSETUP=3, LCOLLECT=4, MOXCHECK=5, LCALC=6,
        // LDELAY=7, LSTAYON=8, LTURNON=9. info[14]=1 means corrections live;
        // info[6] bitmask flags scheck rejections (see r3-correct.md §B1).
        // The 5-sec periodic log below is too sparse to catch the
        // LCOLLECT↔LRESET bounce that happens every ~50 ms when scheck fails;
        // edge-triggered surfaces every transition without flooding when
        // PS is parked (e.g. stuck at LRESET while idle).
        if (_lastLoggedPsState != calState)
        {
            _log.LogInformation(
                "wdsp.psState {Prev}->{Cur} info4={Fb} info6=0x{Sc:X4} info13={Dog} info14={Cor}",
                _lastLoggedPsState, calState,
                _psInfoBuf[4], _psInfoBuf[6], _psInfoBuf[13], _psInfoBuf[14]);
            _lastLoggedPsState = calState;
        }

        // Bring-up diagnostic — log info[0..7] + correcting/state every Nth
        // call so the calcc state machine progression is visible during a
        // rack run. With TxMetersService running at ~10 Hz, N=50 ≈ 5 s.
        // Drop once PS is confirmed working.
        if (++_psInfoLogCounter % 50 == 0)
        {
            _log.LogDebug(
                "wdsp.psInfo binfo=[{B0},{B1},{B2},{B3},{B4},{B5},{B6},{B7}] correcting={C} state={S}",
                _psInfoBuf[0], _psInfoBuf[1], _psInfoBuf[2], _psInfoBuf[3],
                _psInfoBuf[4], _psInfoBuf[5], _psInfoBuf[6], _psInfoBuf[7],
                _psInfoBuf[14], _psInfoBuf[15]);
        }

        return new PsStageMeters(
            FeedbackLevel: feedback,
            CalState: calState,
            Correcting: correcting,
            CorrectionDb: depthDb,
            MaxTxEnvelope: (float)maxTx,
            CalibrationAttempts: calibrationAttempts);
    }

    public void ResetPs()
    {
        if (_disposed != 0) return;
        int? txa;
        lock (_txaLock) txa = _txaChannelId;
        if (txa is not int id) return;
        lock (_psLock)
        {
            // Two-phase reset+restore — matches Thetis PSForm.cs:760-783
            // (timer2code Monitor → SetNewValues → RestoreOperation) and
            // pihpsdr's tx_ps_reset → tx_ps_resume pattern (transmitter.c
            // :2478-2502). Phase 1 clears calcc to LRESET with mancal/
            // automode zeroed (drops any in-flight fit). Phase 2 restores
            // the saved Auto/Single mode so calcc autorestarts. Without
            // phase 2, automode stays 0 and calcc parks at LRESET forever
            // — which on a Patch-A-gated AutoAttenuate loop means info[5]
            // never increments past 1 and the loop stalls after one step.
            NativeMethods.SetPSControl(id, 1, 0, 0, 0);
            int mancal = _psSingle ? 1 : 0;
            int automode = (_psAuto && !_psSingle) ? 1 : 0;
            NativeMethods.SetPSControl(id, 0, mancal, automode, 0);
        }
        _log.LogInformation("wdsp.resetPs auto={Auto} single={Single}", _psAuto, _psSingle);
    }

    public void SavePsCorrection(string path)
    {
        if (_disposed != 0) return;
        if (string.IsNullOrWhiteSpace(path)) return;
        int? txa;
        lock (_txaLock) txa = _txaChannelId;
        if (txa is not int id) return;
        lock (_psLock)
        {
            NativeMethods.PSSaveCorr(id, path);
        }
        _log.LogInformation("wdsp.savePsCorrection path={Path}", path);
    }

    public void RestorePsCorrection(string path)
    {
        if (_disposed != 0) return;
        if (string.IsNullOrWhiteSpace(path)) return;
        int? txa;
        lock (_txaLock) txa = _txaChannelId;
        if (txa is not int id) return;
        lock (_psLock)
        {
            NativeMethods.PSRestoreCorr(id, path);
            // Restore-and-go pattern from Thetis PSForm.cs:982-1162: turnon=1
            NativeMethods.SetPSControl(id, 0, 0, 0, 1);
        }
        _log.LogInformation("wdsp.restorePsCorrection path={Path}", path);
    }

    // CFC (Continuous Frequency Compressor) — issue #123. xcfcomp already
    // sits in xtxa between xeqp and xbandpass; this method just pushes the
    // operator-tuned profile + scalar params and toggles the run flag.
    //
    // Param push order matters: profile + scalars + post-EQ-run all happen
    // BEFORE the master CFCOMPRun flip. That way when the master toggles on,
    // the audio pipeline starts processing with a fully-configured stage —
    // mirrors the same "configure, then enable" Thetis pattern we use for
    // every other WDSP stage (see SetNoiseReduction NR2/NR4 ordering).
    //
    // Pass IntPtr.Zero for Qg/Qe — selects classic non-parametric mode
    // (cfcomp.c:122-123 falls back to linear interpolation). Matches
    // pihpsdr's cfc_menu.c, which is the canonical reference per issue #123.
    public unsafe void SetCfcConfig(CfcConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (cfg.Bands is null) throw new ArgumentException("Bands must not be null", nameof(cfg));
        if (cfg.Bands.Length != 10)
            throw new ArgumentException($"Bands must have exactly 10 entries; got {cfg.Bands.Length}", nameof(cfg));

        if (_disposed != 0) return;

        int txa;
        lock (_txaLock)
        {
            if (_txaChannelId is not int id) return;
            txa = id;
        }

        // Build parallel arrays for SetTXACFCOMPprofile. WDSP sorts internally
        // (cfcomp.c:147) and clamps to [0, Nyquist], so we don't pre-validate
        // monotonicity — operators are free to type frequencies in any order.
        const int nfreqs = 10;
        double[] f = new double[nfreqs];
        double[] g = new double[nfreqs];
        double[] e = new double[nfreqs];
        for (int i = 0; i < nfreqs; i++)
        {
            var band = cfg.Bands[i];
            f[i] = band.FreqHz;
            g[i] = band.CompLevelDb;
            e[i] = band.PostGainDb;
        }

        lock (_txaLock)
        {
            // Re-check inside the lock — TXA could have closed between the
            // outer lookup and here on a teardown race. Same pattern other
            // _txaLock callers use.
            if (_txaChannelId is not int id) return;

            fixed (double* pF = f, pG = g, pE = e)
            {
                NativeMethods.SetTXACFCOMPprofile(
                    id, nfreqs,
                    ref *pF, ref *pG, ref *pE,
                    IntPtr.Zero, IntPtr.Zero);
            }
            NativeMethods.SetTXACFCOMPPrecomp(id, cfg.PreCompDb);
            NativeMethods.SetTXACFCOMPPrePeq(id, cfg.PrePeqDb);
            NativeMethods.SetTXACFCOMPPeqRun(id, cfg.PostEqEnabled ? 1 : 0);
            NativeMethods.SetTXACFCOMPRun(id, cfg.Enabled ? 1 : 0);
        }

        _log.LogInformation(
            "wdsp.setCfc enabled={Enabled} peq={Peq} precomp={Pre:F1}dB prepeq={PrePeq:F1}dB",
            cfg.Enabled, cfg.PostEqEnabled, cfg.PreCompDb, cfg.PrePeqDb);
    }

    // VST plugin-host seam. Both methods short-circuit on the disabled flag
    // with a single volatile read — no allocation, no copy, no syscall — so
    // the bypass path adds a virtual call and a boolean check to ProcessTxBlock
    // / DspPipelineService.Tick. When the chain is enabled and a handler is
    // installed, dispatch through the delegate (Zeus.Server's PluginHost
    // wiring lives there). We deliberately do NOT take a dependency on
    // Zeus.PluginHost here; the host integration sits one layer up so
    // Zeus.Dsp stays free of IPC / sandboxing concerns.
    public bool ProcessRxVstChain(Span<float> audio, int frames, int sampleRateHz)
    {
        if (!_vstChainEnabled) return false;
        var handler = _vstChainHandler;
        if (handler is null) return false;
        return handler(audio, frames, sampleRateHz);
    }

    public bool ProcessTxMicVstChain(Span<float> audio, int frames, int sampleRateHz)
    {
        if (!_vstChainEnabled) return false;
        var handler = _vstChainHandler;
        if (handler is null) return false;
        return handler(audio, frames, sampleRateHz);
    }

    private DateTime _lastTxMeterLogUtc;

    public int ProcessTxBlock(ReadOnlySpan<float> micMono, Span<float> iqInterleaved)
    {
        if (_disposed != 0) return 0;
        int inSize = _txaInSize;
        int outSize = _txaOutSize;
        if (micMono.Length != inSize)
            throw new ArgumentException($"expected mic span of {inSize}", nameof(micMono));
        if (iqInterleaved.Length != 2 * outSize)
            throw new ArgumentException($"expected iq span of {2 * outSize}", nameof(iqInterleaved));

        int txa;
        lock (_txaLock)
        {
            if (_txaChannelId is not int id) return 0;
            txa = id;
        }

        // fexchange2 wants mutable refs to the first float of each buffer.
        // For P2, in != out (inSize=512 mic, outSize=2048 IQ). Stack-allocate
        // both — max combined footprint is 512 + 512 + 2048 + 2048 = 5120 floats
        // ≈ 20 KiB, well inside the default stack budget.
        Span<float> iin = stackalloc float[inSize];
        micMono.CopyTo(iin);
        Span<float> qin = stackalloc float[inSize];
        qin.Clear();
        Span<float> iout = stackalloc float[outSize];
        Span<float> qout = stackalloc float[outSize];
        // stackalloc spans are NOT guaranteed zero-initialised across all
        // .NET configurations (SkipLocalsInit can elide the zeroing). When
        // TXA is at state=0, fexchange2 returns without writing iout/qout,
        // and we'd otherwise propagate stack garbage downstream — both into
        // the wire IQ ring and (worse) into the TX-monitor RXA channel, where
        // garbage demodulates as audible noise. Clear them so the no-process
        // path is deterministic silence.
        iout.Clear();
        qout.Clear();

        // VST plugin-host seam — TX side, on the 48 kHz mono mic buffer
        // BEFORE WDSP's fexchange2 chain. This placement keeps WDSP CFC last
        // (after the plugin chain) so the operator's preferred final-stage
        // compressor remains the gain-limiting authority. See
        // docs/proposals/vst-host-phase2-wire.md "TX seam location".
        // When the chain is disabled or no handler is installed, the seam
        // short-circuits on a volatile-bool read and the buffer flows through
        // bit-identical to the seam-absent code path. When the chain is
        // enabled, the handler may mutate `iin` in place; downstream
        // fexchange2 sees the post-plugin audio.
        if (_vstChainEnabled)
        {
            // _txaInputRateHz is always 48 kHz (mic side) for both P1 and P2
            // profiles; use it explicitly for clarity.
            ProcessTxMicVstChain(iin, inSize, _txaInputRateHz);
        }

        NativeMethods.fexchange2(txa, ref iin[0], ref qin[0], ref iout[0], ref qout[0], out int err);
        if (err != 0 && ++_txFexchangeErrLogged <= 8)
        {
            _log.LogWarning("wdsp.fexchange2 tx err={Err} (suppressed after 8 occurrences)", err);
        }

        for (int i = 0; i < outSize; i++)
        {
            iqInterleaved[2 * i] = iout[i];
            iqInterleaved[2 * i + 1] = qout[i];
        }

        // TX Monitor — feed the post-CFIR / post-RSMPOUT IQ (the wire signal
        // about to hit the radio) into the private monitor RXA channel so the
        // operator can hear the actual on-air audio at the TX bandwidth
        // profile. Volatile-bool short-circuit when monitor is off; matches
        // the VST seam pattern above. Float→double conversion is required by
        // the FeedIq contract; stack-allocate to avoid GC pressure on the
        // mic-ingest hot path. Worst case is P2: outSize=2048 → 2 × 2048 ×
        // 8 bytes = 32 KiB on the stack, comfortable under the default budget.
        if (_monitorRequested && _monitorChannelId is int monId)
        {
            Span<double> monIqDouble = stackalloc double[2 * outSize];
            for (int i = 0; i < outSize; i++)
            {
                monIqDouble[2 * i] = iout[i];
                monIqDouble[2 * i + 1] = qout[i];
            }
            FeedIq(monId, monIqDouble);
        }

        // Feed the TX analyzer with the post-CFIR IQ so TryGetTxDisplayPixels
        // Feed the TX analyzer from the WDSP TXA SIPHON (xsiphon position in
        // xtxa, BEFORE iqc/cfir/rsmpout — see siphon.c, TXA.c:586) so the
        // panadapter trace shows the operator's pre-distortion voice spectrum.
        // Pre-fix this used the post-cfir iout/qout output buffer, which is
        // intentionally shaped with anti-IMD content while PS is correcting
        // and renders as visible "splatter" even when the antenna is clean
        // (issue #121). Thetis takes the same tap (cmaster.cs:544-545,
        // TXASetSipMode + TXASetSipDisplay). Sample rate / size match the
        // analyzer config: dsp_rate / dsp_size. Q is still negated to match
        // the WDSP analyzer's sideband convention (same fix as before — the
        // siphon hands back complex IQ in the same orientation as the post-
        // CFIR buffer did).
        if (_txDispAlive)
        {
            int sipSize = _txaDspSize;
            Span<float> sipBuf = stackalloc float[2 * sipSize];
            NativeMethods.TXAGetaSipF1(txa, ref sipBuf[0], sipSize);
            Span<double> txSpectrumIq = stackalloc double[2 * sipSize];
            for (int i = 0; i < sipSize; i++)
            {
                txSpectrumIq[2 * i] = sipBuf[2 * i];
                txSpectrumIq[2 * i + 1] = -sipBuf[2 * i + 1];
            }
            lock (_txDispLock)
            {
                NativeMethods.Spectrum0(1, txa, 0, 0, ref txSpectrumIq[0]);
            }
        }

        // Per-stage TXA peak + average meters. Peak surfaces clipping-induced
        // crackle that averages smooth away; the average is what the operator
        // reads to judge level. Both are published so the frontend can show
        // a Thetis-style dual-needle per row. Indices per native/wdsp/TXA.h:49-66
        // txaMeterType:
        //   0  MIC_PK    1  MIC_AV
        //   2  EQ_PK     3  EQ_AV
        //   4  LVLR_PK   5  LVLR_AV   6  LVLR_GAIN
        //   7  CFC_PK    8  CFC_AV    9  CFC_GAIN
        //  10  COMP_PK  11  COMP_AV
        //  12  ALC_PK   13  ALC_AV   14  ALC_GAIN
        //  15  OUT_PK   16  OUT_AV
        double micPk = NativeMethods.GetTXAMeter(txa, 0);
        double micAv = NativeMethods.GetTXAMeter(txa, 1);
        double eqPk = NativeMethods.GetTXAMeter(txa, 2);
        double eqAv = NativeMethods.GetTXAMeter(txa, 3);
        double lvlrPk = NativeMethods.GetTXAMeter(txa, 4);
        double lvlrAv = NativeMethods.GetTXAMeter(txa, 5);
        double lvlrGain = NativeMethods.GetTXAMeter(txa, 6);
        double cfcPk = NativeMethods.GetTXAMeter(txa, 7);
        double cfcAv = NativeMethods.GetTXAMeter(txa, 8);
        double cfcGain = NativeMethods.GetTXAMeter(txa, 9);
        double compPk = NativeMethods.GetTXAMeter(txa, 10);
        double compAv = NativeMethods.GetTXAMeter(txa, 11);
        double alcPk = NativeMethods.GetTXAMeter(txa, 12);
        double alcAv = NativeMethods.GetTXAMeter(txa, 13);
        double alcGain = NativeMethods.GetTXAMeter(txa, 14);
        double outPk = NativeMethods.GetTXAMeter(txa, 15);
        double outAv = NativeMethods.GetTXAMeter(txa, 16);

        // Publish the snapshot before returning so pollers don't see a
        // partially-written set. Lock is uncontended in steady state —
        // ProcessTxBlock runs from the TX ingest thread and GetTxStageMeters
        // only from TxMetersService (10 Hz).
        // *Gain readings from WDSP are 20*log10(linear_gain) ≤ 0 when
        // reducing. Store as positive "gain reduction" dB per TxStageMeters
        // convention.
        var snap = new TxStageMeters(
            MicPk: (float)micPk,
            MicAv: (float)micAv,
            EqPk: (float)eqPk,
            EqAv: (float)eqAv,
            LvlrPk: (float)lvlrPk,
            LvlrAv: (float)lvlrAv,
            LvlrGr: (float)-lvlrGain,
            CfcPk: (float)cfcPk,
            CfcAv: (float)cfcAv,
            CfcGr: (float)-cfcGain,
            CompPk: (float)compPk,
            CompAv: (float)compAv,
            AlcPk: (float)alcPk,
            AlcAv: (float)alcAv,
            AlcGr: (float)-alcGain,
            OutPk: (float)outPk,
            OutAv: (float)outAv);
        lock (_txMeterPublishLock) { _latestTxStageMeters = snap; }

        var now = DateTime.UtcNow;
        if (now - _lastTxMeterLogUtc >= TimeSpan.FromSeconds(1))
        {
            _lastTxMeterLogUtc = now;
            double micBlockPeak = 0, ioutPeak = 0;
            for (int i = 0; i < inSize; i++)
            {
                double m = Math.Abs(iin[i]); if (m > micBlockPeak) micBlockPeak = m;
            }
            for (int i = 0; i < outSize; i++)
            {
                double oi = Math.Abs(iout[i]); double oq = Math.Abs(qout[i]);
                double ma = Math.Max(oi, oq); if (ma > ioutPeak) ioutPeak = ma;
            }
            _log.LogInformation(
                "wdsp.tx.stage micBlockPeak={MP:F3} iqBlockPeak={IP:F4} | mic pk={MicPk:F1} av={MicAv:F1} | eq pk={EqPk:F1} av={EqAv:F1} | lvlr pk={LvlrPk:F1} av={LvlrAv:F1} gr={LvlrGr:F1} | cfc pk={CfcPk:F1} av={CfcAv:F1} gr={CfcGr:F1} | alc pk={AlcPk:F1} av={AlcAv:F1} gr={AlcGr:F1} | out pk={OutPk:F1} av={OutAv:F1}",
                micBlockPeak, ioutPeak,
                micPk, micAv, eqPk, eqAv,
                lvlrPk, lvlrAv, -lvlrGain,
                cfcPk, cfcAv, -cfcGain,
                alcPk, alcAv, -alcGain,
                outPk, outAv);
        }
        return outSize;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var key in _channels.Keys.ToArray())
        {
            if (_channels.TryRemove(key, out var state))
                StopChannel(state);
        }
        lock (_psLock)
        {
            ClosePsFeedbackAnalyzer();
        }
        lock (_txaLock)
        {
            if (_txaChannelId is int txa)
            {
                lock (_txDispLock)
                {
                    if (_txDispAlive)
                    {
                        NativeMethods.DestroyAnalyzer(txa);
                        _txDispAlive = false;
                    }
                }
                NativeMethods.CloseChannel(txa);
                _txaChannelId = null;
            }
        }
    }

    // Thetis-style log-recursive EMA on both pan and wf outputs. `tauSec` is
    // the visual smoothing time constant; with PipelineFps ticks/s the per-tick
    // retention is `exp(-1 / (fps * tau))`. Default 100 ms reads as "smooth
    // but still alive" — heavy enough to kill the per-frame jumpiness the
    // user called out, light enough that signals still pop.
    private const int PipelineFps = 30;
    private const double DefaultAvgTauSec = 0.100;
    // Heavier smoothing on TX-side traces. Voice modulation through the
    // operator's leveler/compressor/ALC has natural envelope dynamics that
    // a 100 ms tau renders as visible "splatter spreading"; 0.5 s gives the
    // Thetis-style smoothed envelope so the operator sees signal shape, not
    // every voiced/unvoiced transition.
    private const double TxAvgTauSec = 0.175;
    private const int LogRecursiveMode = 3;

    private static void ConfigureDisplayAveraging(int disp)
        => ConfigureDisplayAveragingTau(disp, DefaultAvgTauSec);

    private static void ConfigureDisplayAveragingTau(int disp, double tauSec)
    {
        double backmult = Math.Exp(-1.0 / (PipelineFps * tauSec));
        for (int pixout = 0; pixout < 2; pixout++)
        {
            NativeMethods.SetDisplayAverageMode(disp, pixout, LogRecursiveMode);
            NativeMethods.SetDisplayAvBackmult(disp, pixout, backmult);
            NativeMethods.SetDisplayNumAverage(disp, pixout, 2);
        }
    }

    // TX analyzer wrapper: reuses ConfigureAnalyzer's bin-clip math after
    // folding the TX/RX rate ratio into an "effective zoom" so the TX trace
    // displays the same frequency span as RX. Example: P2 TX at 192 kHz vs
    // RX at 48 kHz is a 4× rate ratio; at RX zoom=1 the effective TX zoom is
    // 4, which clips 3/8 × fft_size bins off each side — i.e. keeps the
    // centre 25% of the full-span FFT, which is exactly pihpsdr's fixed
    // 24 kHz-wide TX panadapter (transmitter.c:2323-2324). On P1 the ratio
    // is 1 and this degenerates back to RX-zoom behaviour.
    // Returns true when the TX/RX rate relationship supports the bin-clip span
    // match (txRate is a positive integer multiple of rxRate). Callers that get
    // false skip TX analyzer creation and fall back to the RX analyzer during
    // MOX — matches the pre-issue-#81 behaviour for that codepath.
    private static bool TryConfigureTxAnalyzer(int disp, int txSampleRateHz, int txBlockSize, int rxSampleRateHz, int pixelWidth, int rxZoomLevel)
    {
        if (rxSampleRateHz <= 0 || txSampleRateHz < rxSampleRateHz || txSampleRateHz % rxSampleRateHz != 0)
            return false;
        int effectiveZoom = rxZoomLevel * (txSampleRateHz / rxSampleRateHz);
        // bf_sz must match the per-Spectrum0 block size fed from ProcessTxBlock
        // (_txaOutSize: 1024 on P1, 2048 on P2). Hardcoding InSize left WDSP
        // reading only the first 1024 of each 2048-sample P2 block, aliasing at
        // (192000/1024) ≈ 188 Hz and producing a spur comb on the TUN carrier.
        ConfigureAnalyzer(disp, txSampleRateHz, txBlockSize, pixelWidth, effectiveZoom);
        return true;
    }

    private static void ConfigureAnalyzer(int disp, int sampleRateHz, int bfSize, int pixelWidth, int zoomLevel)
    {
        int overlap = (int)Math.Max(0, Math.Ceiling(AnalyzerFftSize - (double)sampleRateHz / AnalyzerFps));
        int maxW = AnalyzerFftSize + (int)Math.Min(
            AnalyzerKeepTime * sampleRateHz,
            AnalyzerKeepTime * AnalyzerFftSize * AnalyzerFps);
        int flp = 0;

        // fscLin/fscHin are integer bin counts to clip from the LOW and HIGH
        // ends of the full-span FFT output (analyzer.c:1253-1254, PanDisplay.cs
        // :4720-4726 in Thetis). For a centred zoom by factor L, keep
        // fft_size/L bins in the middle and clip (fft_size - fft_size/L)/2
        // from each side. At L=1 both clips are 0 (full span).
        double fscLin = 0.0, fscHin = 0.0;
        if (zoomLevel > 1)
        {
            int clippedPerSide = AnalyzerFftSize * (zoomLevel - 1) / (2 * zoomLevel);
            fscLin = clippedPerSide;
            fscHin = clippedPerSide;
        }

        NativeMethods.SetAnalyzer(
            disp: disp,
            n_pixout: 2,
            n_fft: 1,
            typ: 1,
            flp: ref flp,
            sz: AnalyzerFftSize,
            bf_sz: bfSize,
            win_type: AnalyzerWindow,
            pi_alpha: AnalyzerKaiserPi,
            ovrlp: overlap,
            clp: 0,
            fscLin: fscLin,
            fscHin: fscHin,
            n_pix: pixelWidth,
            n_stch: 1,
            calset: 0,
            fmin: 0.0,
            fmax: 0.0,
            max_w: maxW);
    }

    private void StopChannel(ChannelState state)
    {
        state.Stopped = true;
        state.InQueue.CompleteAdding();
        state.Cts.Cancel();
        if (!state.Worker.Join(TimeSpan.FromSeconds(2)))
        {
            // Worker did not exit in time; fall through to teardown anyway.
        }
        state.InQueue.Dispose();
        state.Cts.Dispose();
        NativeMethods.DestroyAnalyzer(state.Id);
        // Tear down EXT blankers before CloseChannel — they reference our id
        // slot in panb[]/pnob[] and outlive CloseChannel unless destroyed here.
        NativeMethods.DestroyAnbEXT(state.Id);
        NativeMethods.DestroyNobEXT(state.Id);
        NativeMethods.CloseChannel(state.Id);
    }

    private static void RunWorker(ChannelState state)
    {
        double[] audio = new double[state.OutDoubles];
        double[] spectrumIq = new double[2 * InSize];
        int monoSamples = state.OutDoubles / 2;
        try
        {
            foreach (var frame in state.InQueue.GetConsumingEnumerable(state.Cts.Token))
            {
                // Pre-RXA blanker. In-place is safe: xanb/xnob read a->in[i]
                // before writing a->out[i] within each iteration, so same-buffer
                // aliasing doesn't clobber unread samples. Skipped entirely when
                // both NBs are off so there's no WDSP call overhead in the common
                // path. Non-enabled side stays at Run=0, so even if the mode
                // changes mid-frame its xanb/xnob is a no-op pass-through.
                switch (state.CurrentNbMode)
                {
                    case NbMode.Nb1:
                        NativeMethods.XanbEXT(state.Id, ref frame[0], ref frame[0]);
                        break;
                    case NbMode.Nb2:
                        NativeMethods.XnobEXT(state.Id, ref frame[0], ref frame[0]);
                        break;
                }

                NativeMethods.fexchange0(
                    state.Id,
                    ref frame[0],
                    ref audio[0],
                    out _);
                // Empirical fix for HL2 panadapter sideband mirror: conjugate the
                // IQ stream fed to the analyzer (I unchanged, Q negated). Audio
                // path keeps the original IQ so demod stays correct. Without this
                // the displayed spectrum appears flipped about the carrier (USB
                // energy shows left of carrier, LSB shows right) despite audio
                // and the synthetic-IQ orientation test both being correct.
                for (int i = 0; i < frame.Length; i += 2)
                {
                    spectrumIq[i] = frame[i];
                    spectrumIq[i + 1] = -frame[i + 1];
                }
                // Analyzer input side: paired with GetPixels under the same
                // lock, so SetZoom can rebuild bin mapping without a half-
                // written state being observed.
                lock (state.AnalyzerLock)
                {
                    NativeMethods.Spectrum0(state.SpectrumRun, state.Id, 0, 0, ref spectrumIq[0]);
                }
                PushAudio(state, audio, monoSamples);
                state.FreeFrames.Enqueue(frame);
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
    }

    // Mirror Thetis radio.cs's NB property-setter behaviour: after create_*EXT
    // seeds the struct, the setters immediately overwrite the knob state and
    // run initBlanker / init_nob once. Keeping this as a discrete config block
    // means an advanced-NB panel can reuse the same setter path with
    // user-supplied values rather than introducing a second code path.
    private static void ApplyNbDefaults(int id)
    {
        NativeMethods.SetEXTANBTau(id, NrDefaults.NbTau);
        NativeMethods.SetEXTANBHangtime(id, NrDefaults.NbHangtime);
        NativeMethods.SetEXTANBAdvtime(id, NrDefaults.NbAdvtime);
        NativeMethods.SetEXTANBBacktau(id, NrDefaults.NbBacktau);
        NativeMethods.SetEXTANBThreshold(id, NrDefaults.NbDefaultThresholdScaled);

        NativeMethods.SetEXTNOBMode(id, 0);
        NativeMethods.SetEXTNOBTau(id, NrDefaults.NbTau);
        NativeMethods.SetEXTNOBHangtime(id, NrDefaults.NbHangtime);
        NativeMethods.SetEXTNOBAdvtime(id, NrDefaults.NbAdvtime);
        NativeMethods.SetEXTNOBBacktau(id, NrDefaults.NbBacktau);
        NativeMethods.SetEXTNOBThreshold(id, NrDefaults.NbDefaultThresholdScaled);
    }

    // Applies Thetis AGC_MEDIUM defaults — the mode all HL2 users start on.
    // Without this, WDSP's AGC is off and the audio path has effectively
    // unity gain on signals with peak ~2e-5, which is inaudible.
    private static void ApplyAgcDefaults(int id)
    {
        NativeMethods.SetRXAAGCMode(id, 3);              // MED
        NativeMethods.SetRXAAGCSlope(id, 35);
        NativeMethods.SetRXAAGCTop(id, 80.0);            // max gain, dB
        NativeMethods.SetRXAAGCAttack(id, 2);
        NativeMethods.SetRXAAGCHang(id, 0);
        NativeMethods.SetRXAAGCDecay(id, 250);
        NativeMethods.SetRXAAGCHangThreshold(id, 100);
    }

    // WDSP bandpass takes signed frequencies: LSB-family modes live in negative
    // baseband (low=-high, high=-low), USB-family in positive. CW follows the
    // USB/LSB convention per its suffix. Other modes keep unsigned bounds since
    // their passbands span zero.
    private static void ApplyBandpassForMode(ChannelState state)
    {
        int lo = state.FilterLowAbsHz;
        int hi = state.FilterHighAbsHz;
        double low, high;
        switch (state.CurrentMode)
        {
            case RxaMode.LSB:
            case RxaMode.CWL:
            case RxaMode.DIGL:
                low = -hi; high = -lo; break;
            case RxaMode.USB:
            case RxaMode.CWU:
            case RxaMode.DIGU:
                low = lo; high = hi; break;
            default:
                // AM/SAM/DSB/FM/DRM/SPEC: symmetric around 0.
                low = -hi; high = hi; break;
        }
        // Thetis rxa.cs:110-124: every filter change updates all three stages.
        // SetRXABandpassFreqs alone only affects bp1, which is bypassed for SSB.
        // nbp0 (RXANBPSetFreqs) is what actually carries the SSB passband.
        NativeMethods.SetRXABandpassFreqs(state.Id, low, high);
        NativeMethods.RXANBPSetFreqs(state.Id, low, high);
        NativeMethods.SetRXASNBAOutputBandwidth(state.Id, low, high);
    }

    private static RxaMode MapMode(RxMode mode) => mode switch
    {
        RxMode.LSB => RxaMode.LSB,
        RxMode.USB => RxaMode.USB,
        RxMode.CWL => RxaMode.CWL,
        RxMode.CWU => RxaMode.CWU,
        RxMode.AM => RxaMode.AM,
        RxMode.FM => RxaMode.FM,
        RxMode.SAM => RxaMode.SAM,
        RxMode.DSB => RxaMode.DSB,
        RxMode.DIGL => RxaMode.DIGL,
        RxMode.DIGU => RxaMode.DIGU,
        _ => RxaMode.USB,
    };
}
