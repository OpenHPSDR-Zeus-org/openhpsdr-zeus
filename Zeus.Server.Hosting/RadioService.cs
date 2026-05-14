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
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Protocol1;
using Zeus.Protocol1.Discovery;

namespace Zeus.Server;

public sealed class RadioService : IDisposable
{
    private const int DefaultHpsdrPort = 1024;

    private readonly object _sync = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RadioService> _log;
    private readonly DspSettingsStore _dspSettingsStore;
    private readonly PaSettingsStore _paStore;
    private readonly PreferredRadioStore? _preferredRadioStore;
    private readonly PsSettingsStore? _psStore;
    private readonly FilterPresetStore? _filterPresetStore;
    private readonly RadioStateStore? _radioStateStore;
    // Debounced state flush. Set to true in every Mutate(); a 1 Hz timer
    // calls FlushState() which writes to LiteDB and clears the flag.
    // Avoids hammering LiteDB during rapid VFO scroll or filter drags.
    private volatile bool _stateDirty;
    private readonly System.Threading.Timer? _stateFlushTimer;
    // Last-known preset name per mode, preserved across mode switches.
    // Accessed only from inside Mutate (under _sync) or at init.
    private readonly Dictionary<RxMode, string?> _lastPresetPerMode = new();
    // Last-commanded slider value in UI percent (0..100). Needed here because
    // the drive byte depends on three inputs — percent, per-band PA gain, and
    // global max-watts — any of which can change independently. When a band
    // edge is crossed or a PA setting is edited, we recompute without needing
    // to wait for the next SetDrive call.
    private int _drivePct;
    // Independent TUN drive %. When TUN is keyed, the recompute uses this in
    // place of _drivePct so the operator can pre-set a lower tune level (and
    // the same per-band PA gain gives equal watts at equal percentages). piHPSDR
    // default is 10 — a 0 default would be "press TUN, nothing happens".
    private int _tunePct = 10;
    // Which drive % the next frame uses. Latched via NotifyTunActive from
    // TxService whenever the MOX/TUN keying state changes so a drag on either
    // slider during a live TX picks the right source without polling.
    private bool _tunActive;

    private StateDto _state;

    // Latched MOX bit — populated via SetMox so the auto-ATT loop can pause
    // itself during TX without a service-locator pattern back to TxService.
    private bool _mox;

    private Protocol1Client? _activeClient;
    // True while DspPipelineService has a live Protocol2 client and no P1 is
    // active. Used to resolve the effective board kind for PA defaults when
    // the user is on a G2 MkII / Saturn (P2 discovery flow skips
    // Protocol1Client entirely).
    private bool _p2Active;
    // Discovered board kind for the active P2 connection. Set by
    // MarkProtocol2Connected from the connect-API request byte (issue #171 —
    // Brick2 is Hermes-on-P2, not OrionMkII). Unknown when the caller didn't
    // supply a byte, in which case ConnectedBoardKind falls back to OrionMkII
    // for backward compat.
    private HpsdrBoardKind _p2BoardKind = HpsdrBoardKind.Unknown;
    private bool _preampOn;
    // Auto-ATT defaults on; the user baseline starts at 0 dB and the control
    // loop ramps _attOffsetDb up to 31 dB on observed ADC overloads (Thetis
    // console.cs:22167-22181). The old hard-coded 15 dB masked clipping but
    // cost 15 dB of sensitivity on quiet bands.
    private HpsdrAtten _atten = new(0);

    // Auto-ATT control-loop state. Mutated only under _sync or on the RX-thread
    // overload-event path (which also takes _sync before touching state).
    private int _attOffsetDb;
    private int _adcOverloadLevel;          // 0..5, Thetis-style "red lamp" counter
    private bool _overloadSeenInWindow;     // any overload since last tick
    private long _lastTickMs = long.MinValue;
    private int _lastAppliedEffectiveDb = -1;   // so the first send always fires

    // Auto-AGC control-loop state. Adjusts AGC-T (the WDSP max-gain cap) so
    // the band noise floor lands near a fixed reference, mirroring Thetis'
    // noise-floor calibration. Closed-looping on the live S-meter (the prior
    // implementation) ramped to max gain on quiet bands and clipped when
    // signal returned.
    private double _agcOffsetDb;
    private long _lastAgcTickMs = long.MinValue;
    private readonly double[] _noiseFloorWindow = new double[10]; // 10 × 500 ms = 5 s
    private int _noiseFloorWindowIdx;
    private int _noiseFloorWindowFill;

    // 100 ms between 1-dB steps. Events arrive at ~1.2 kHz (192 kSps), so
    // without throttling the offset would saturate at 31 dB in ~30 ms. At 10 Hz
    // the full-range ramp takes ~3 s — matches Thetis' feel.
    private const int TickIntervalMs = 100;

    public event Action<StateDto>? StateChanged;
    public event Action<IProtocol1Client>? Connected;
    public event Action? Disconnected;
    // Protocol-2 lifecycle. Parallel to the P1-typed Connected/Disconnected
    // pair so subscribers (TxMetersService for hi-priority status, future
    // P2 consumers) can hook a freshly-opened Protocol2Client without
    // probing DspPipelineService. Issue #174 — needed so the meter service
    // can wire its OnTelemetry handler to client.TelemetryReceived.
    public event Action<Zeus.Protocol2.Protocol2Client>? P2Connected;
    public event Action? P2Disconnected;
    // Fires whenever the effective PA snapshot changes (store edit, VFO band
    // crossing, drive slider). DspPipelineService consumes this to forward the
    // same snapshot into any live Protocol2Client (byte 345 / byte 1401 /
    // CmdGeneral[58]). RadioService pushes to the P1 client directly because
    // it owns _activeClient.
    public event Action<PaRuntimeSnapshot>? PaSnapshotChanged;
    // Fires on every MOX / TUN edge. P1 side is pushed directly via
    // ActiveClient?.SetMox; these events give DspPipelineService the hook it
    // needs to forward the same bit into a live Protocol2Client, which owns
    // its own CmdHighPriority byte 4.
    public event Action<bool>? MoxChanged;
    public event Action<bool>? TunActiveChanged;
    // Fires when the operator toggles the Mercury preamp. P1 path is pushed
    // directly via ActiveClient?.SetPreamp inside SetPreamp; this event lets
    // DspPipelineService mirror the same change into a live Protocol2Client
    // (CmdHighPriority byte 1403, bit 0 = RX0 preamp). Issue #126 — the P2
    // forwarding is the missing link that left the PRE button non-functional
    // on Angelia / ANAN-100D.
    public event Action<bool>? PreampChanged;

    // Shared TX IQ source threaded through Protocol1Client. TxAudioIngest
    // writes into the same instance; this is the seam between "mic arrived
    // over WS" and "EP2 packet got real IQ". When null the client falls back
    // to its internal test-tone generator (dev / tests without a hub).
    private readonly Zeus.Protocol1.ITxIqSource? _txIqSource;

    public RadioService(ILoggerFactory loggerFactory, DspSettingsStore dspSettingsStore, PaSettingsStore paStore, FilterPresetStore? filterPresetStore = null, Zeus.Protocol1.ITxIqSource? txIqSource = null, PreferredRadioStore? preferredRadioStore = null, PsSettingsStore? psStore = null, RadioStateStore? radioStateStore = null)
    {
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<RadioService>();
        _dspSettingsStore = dspSettingsStore;
        _paStore = paStore;
        _preferredRadioStore = preferredRadioStore;
        _psStore = psStore;
        _filterPresetStore = filterPresetStore;
        _radioStateStore = radioStateStore;
        _paStore.Changed += RecomputePaAndPush;
        if (_preferredRadioStore is not null)
            _preferredRadioStore.Changed += RecomputePaAndPush;
        _txIqSource = txIqSource;

        // Load persisted DSP settings from the store, or use defaults if not found
        var persistedNr = _dspSettingsStore.Get() ?? new NrConfig();
        // CFC — issue #123. Persisted globally; null on a fresh install or
        // legacy DB row falls back to the default-OFF baseline so the operator
        // sees no behaviour change unless they enable.
        var persistedCfc = _dspSettingsStore.GetCfc() ?? CfcConfig.Default;

        // Seed the last-preset cache from persisted store for all modes so
        // the first mode-switch in a session recalls the correct slot.
        if (filterPresetStore != null)
        {
            foreach (RxMode m in Enum.GetValues<RxMode>())
                _lastPresetPerMode[m] = filterPresetStore.GetLastSelectedPreset(m);
        }

        // Load persisted PS settings — operator's calibration tuning. Master
        // arm and cal-mode are deliberately NOT persisted (parity with MOX);
        // only the timing/preset/auto-att tuning is. PsHwPeak is left at the
        // P1 default; ConnectAsync / ConnectP2Async overrides per-radio.
        var ps = _psStore?.Get();

        // RadioStateStore snapshot — hydrates active mode/VFO/filter/volume/zoom
        // and the per-mode-family filter memory. Null on first run; falls through
        // to the hardcoded defaults below. Snapshot wins for the fields it knows
        // about; existing domain stores (DspSettings, PsSettings, etc.) still
        // hydrate the wider config they own.
        var rsSnap = _radioStateStore?.Get();

        // Restore per-mode-family filter memory from snapshot if available so
        // an AM→USB mode-switch at startup recalls the last SSB width, not the
        // compile-time default.
        if (rsSnap is not null)
        {
            _ssbFilter = new(rsSnap.SsbFilterLoAbs, rsSnap.SsbFilterHiAbs);
            _amFilter = new(rsSnap.AmFilterLoAbs, rsSnap.AmFilterHiAbs);
            _fmFilter = new(rsSnap.FmFilterLoAbs, rsSnap.FmFilterHiAbs);
            _cwFilter = new(rsSnap.CwFilterLoAbs, rsSnap.CwFilterHiAbs);
            _ssbTxFilter = new(rsSnap.SsbTxFilterLoAbs, rsSnap.SsbTxFilterHiAbs);
            _amTxFilter = new(rsSnap.AmTxFilterLoAbs, rsSnap.AmTxFilterHiAbs);
            _fmTxFilter = new(rsSnap.FmTxFilterLoAbs, rsSnap.FmTxFilterHiAbs);
            _cwTxFilter = new(rsSnap.CwTxFilterLoAbs, rsSnap.CwTxFilterHiAbs);
        }

        _state = new(
            Status: ConnectionStatus.Disconnected,
            Endpoint: null,
            VfoHz: rsSnap?.VfoHz ?? 14_200_000,
            Mode: rsSnap?.Mode ?? RxMode.USB,
            FilterLowHz: rsSnap?.FilterLowHz ?? 100,
            FilterHighHz: rsSnap?.FilterHighHz ?? 2850,
            SampleRate: 192_000,    // set at connect time; not in global snapshot
            // Maintainer-chosen baseline of 45 dB (Thetis "AGC slow / fast"
            // sits in this range and reads less aggressive than the WDSP
            // 80 dB internal default on quiet bands). Operator overrides
            // persist via DspSettingsStore.SetAgcTopDb so the value sticks
            // across restarts; null on first run gets the 45 dB seed.
            AgcTopDb: _dspSettingsStore.GetAgcTopDb() ?? 45.0,
            AttenDb: rsSnap?.AttenDb ?? 0,
            Nr: persistedNr,
            ZoomLevel: rsSnap?.ZoomLevel ?? 1,
            AutoAttEnabled: rsSnap?.AutoAttEnabled ?? true,
            AttOffsetDb: 0,         // always reset — control-loop accumulator
            AdcOverloadWarning: false,
            FilterPresetName: rsSnap?.FilterPresetName ?? "VAR1",
            FilterAdvancedPaneOpen: filterPresetStore?.GetAdvancedPaneOpen() ?? false,
            TxFilterLowHz: rsSnap?.TxFilterLowHz ?? 150,
            TxFilterHighHz: rsSnap?.TxFilterHighHz ?? 2850,
            RxAfGainDb: rsSnap?.RxAfGainDb ?? 0.0,
            AutoAgcEnabled: rsSnap?.AutoAgcEnabled ?? false,
            AgcOffsetDb: 0.0,       // always reset — control-loop accumulator
            // PS persisted fields (or DTO defaults when not persisted yet).
            // PsEnabled NOT persisted — always starts off each session.
            PsAuto: ps?.Auto ?? true,
            PsPtol: ps?.Ptol ?? false,
            PsAutoAttenuate: ps?.AutoAttenuate ?? true,
            PsMoxDelaySec: ps?.MoxDelaySec ?? 0.2,
            PsLoopDelaySec: ps?.LoopDelaySec ?? 0.0,
            PsAmpDelayNs: ps?.AmpDelayNs ?? 150.0,
            PsFeedbackSource: ps?.Source ?? PsFeedbackSource.Internal,
            PsIntsSpiPreset: ps?.IntsSpiPreset ?? "16/256",
            // Two-tone test generator dial-in. Defaults match pihpsdr / Thetis
            // (700/1900 Hz, 0.49 each — peak ~0.98 just under WDSP IQ clip).
            TwoToneFreq1: ps?.TwoToneFreq1 ?? 700.0,
            TwoToneFreq2: ps?.TwoToneFreq2 ?? 1900.0,
            TwoToneMag: ps?.TwoToneMag ?? 0.49,
            Cfc: persistedCfc);

        // Kick off the debounce flush timer. Fires every 1 s; only writes to
        // LiteDB when _stateDirty is set (i.e., at least one Mutate() has fired
        // since the last flush). Keeps RadioService latency unaffected by disk IO
        // during rapid VFO scroll or filter drags.
        if (_radioStateStore is not null)
            _stateFlushTimer = new System.Threading.Timer(_ => FlushState(), null, 1_000, 1_000);
    }

    /// <summary>
    /// Single-source-of-truth Upsert helper for the PS settings store. Reads
    /// the current StateDto snapshot and writes the full PsSettingsEntry so
    /// callers don't drop fields by writing only what they touched. Called
    /// from SetPs, SetPsAdvanced, SetPsFeedbackSource, and SetTwoTone.
    ///
    /// PsEnabled / TwoToneEnabled (master arm flags) and PsHwPeak (per-radio
    /// derived) are intentionally NOT in the entry — same operator-action
    /// discipline as MOX/TUN.
    /// </summary>
    private void PersistPsState()
    {
        if (_psStore is null) return;
        var snap = Snapshot();
        _psStore.Upsert(new PsSettingsEntry
        {
            Auto = snap.PsAuto,
            Ptol = snap.PsPtol,
            AutoAttenuate = snap.PsAutoAttenuate,
            MoxDelaySec = snap.PsMoxDelaySec,
            LoopDelaySec = snap.PsLoopDelaySec,
            AmpDelayNs = snap.PsAmpDelayNs,
            IntsSpiPreset = snap.PsIntsSpiPreset,
            Source = snap.PsFeedbackSource,
            TwoToneFreq1 = snap.TwoToneFreq1,
            TwoToneFreq2 = snap.TwoToneFreq2,
            TwoToneMag = snap.TwoToneMag,
        });
    }

    // Ribbon-visibility setter — frontend toggles via REST, server broadcasts
    // a StateDto so other browser tabs stay in sync.
    public StateDto SetFilterAdvancedPaneOpen(bool open)
    {
        _filterPresetStore?.SetAdvancedPaneOpen(open);
        Mutate(s => s with { FilterAdvancedPaneOpen = open });
        return Snapshot();
    }

    public IProtocol1Client? ActiveClient
    {
        get { lock (_sync) return _activeClient; }
    }

    /// <summary>
    /// True when any backend (P1 or P2) has a live connection. Needed by
    /// TxService's MOX / TUN interlock — a G2 on P2 has no ActiveClient
    /// (Protocol1Client is null) but still wants to accept TX requests.
    /// </summary>
    public bool IsConnected
    {
        get { lock (_sync) return _activeClient is not null || _p2Active; }
    }

    public StateDto Snapshot() { lock (_sync) return _state; }

    /// <summary>Current operator preamp toggle. PreampOn isn't on the
    /// StateDto wire format, so DspPipelineService reads it directly when
    /// it needs to push the value into a freshly-opened Protocol2Client
    /// (issue #126). Lock-safe so a connect-time read can't tear against
    /// a concurrent SetPreamp.</summary>
    public bool PreampOn { get { lock (_sync) return _preampOn; } }

    /// <summary>Effective RX step attenuator in dB — operator baseline
    /// (<see cref="StateDto.AttenDb"/>) plus any auto-ATT overload offset
    /// (<see cref="StateDto.AttOffsetDb"/>), clamped to 0..31. This is the
    /// value that lands on the wire (CmdHighPriority byte 1443 on P2;
    /// CC0=0x14 on P1). Exposed for DspPipelineService.ConnectP2Async so a
    /// fresh P2 client is initialised with the operator's current effective
    /// atten before its first CmdHighPriority emission.</summary>
    public int EffectiveAttenDb
    {
        get
        {
            lock (_sync)
                return Math.Clamp(_atten.ClampedDb + _attOffsetDb, HpsdrAtten.MinDb, HpsdrAtten.MaxDb);
        }
    }

    public async Task<StateDto> ConnectAsync(string endpoint, int sampleRate, CancellationToken ct = default)
    {
        if (!TryParseEndpoint(endpoint, out var ipEndpoint))
            throw new ArgumentException($"Invalid endpoint '{endpoint}'.", nameof(endpoint));

        var hpsdrRate = MapSampleRate(sampleRate);

        Protocol1Client? client;
        lock (_sync)
        {
            if (_activeClient is not null)
                throw new InvalidOperationException("Already connected. Disconnect first.");

            client = new Protocol1Client(
                _loggerFactory.CreateLogger<Protocol1Client>(),
                _txIqSource);
            client.AdcOverloadObserved += OnAdcOverload;
            _activeClient = client;
            _state = _state with
            {
                Status = ConnectionStatus.Connecting,
                Endpoint = endpoint,
                SampleRate = hpsdrRate.SampleRateHz(),
            };
            // Fresh connection — reset per-session auto-ATT state so a sticky
            // offset from a previous session doesn't leak onto new hardware.
            _attOffsetDb = 0;
            _adcOverloadLevel = 0;
            _overloadSeenInWindow = false;
            _lastTickMs = long.MinValue;
            _lastAppliedEffectiveDb = -1;
        }
        StateChanged?.Invoke(Snapshot());

        try
        {
            await client.ConnectAsync(ipEndpoint, ct).ConfigureAwait(false);
            // Board fingerprint is known after ConnectAsync. Restore the last
            // sample rate the operator used on this board, if one is stored.
            // Falls back to the ConnectRequest rate on first connect (or after
            // /run fresh), so behaviour is identical to pre-#287 builds until
            // the operator explicitly changes the rate on this board.
            if (_radioStateStore is not null)
            {
                var storedHz = _radioStateStore.GetBoardSampleRate(client.BoardKind, EffectiveOrionMkIIVariant);
                if (storedHz.HasValue)
                {
                    hpsdrRate = MapSampleRate(storedHz.Value);
                    lock (_sync)
                        _state = _state with { SampleRate = hpsdrRate.SampleRateHz() };
                }
            }
            await client.StartAsync(new StreamConfig(hpsdrRate, _preampOn, _atten), ct).ConfigureAwait(false);
            client.SetVfoAHz(CwOffset.EffectiveLoHz(Snapshot()));

            // Default-on the N2ADR 7-relay filter board for HL2 — mirrors
            // Thetis's HERCULES preset (setup.cs:14642). Most HL2 deployments
            // ship with N2ADR; without this the OC pins stay 0 and the LPF
            // relays never click. Operators on bare HL2 (no filter board) can
            // override via PA Settings once that knob is exposed.
            if (client.BoardKind == HpsdrBoardKind.HermesLite2)
                client.SetHasN2adr(true);

            // HL2 Band Volts PWM enable (issue #279) — rehydrate the
            // persisted operator preference into the fresh client so the
            // very first outgoing Config frame carries the correct bit.
            // Honoured on HL2 only; on every other board the flag is set
            // but the wire effect (legacy LT2208 DITHER) is not requested
            // here, since Zeus only flips it from the HL2 settings panel.
            if (client.BoardKind == HpsdrBoardKind.HermesLite2
                && _preferredRadioStore is not null)
            {
                client.EnableHl2BandVolts = _preferredRadioStore.GetEnableHl2BandVolts();
            }

            Mutate(s => s with { Status = ConnectionStatus.Connected });
            _log.LogInformation("radio.connected endpoint={Ep} rate={Rate}", ipEndpoint, hpsdrRate);
            Connected?.Invoke(client);
            // N2ADR 7-relay low-pass filter board is standard equipment on HL2.
            // Enable it unconditionally on connect so band changes immediately
            // drive the relay coils. Future work: make this a user toggle IFF a
            // compelling reason to ship bare HL2 without N2ADR emerges.
            if (ConnectedBoardKind == HpsdrBoardKind.HermesLite2)
                client.SetHasN2adr(true);
            // Replay PA settings into the fresh client — drive byte, OC masks,
            // and (for P2 downstream) PA-enable. Without this the client sits
            // at the protocol defaults (drive=0, OC=0) until something else
            // moves.
            RecomputePaAndPush();
            return Snapshot();
        }
        catch
        {
            lock (_sync) { _activeClient = null; }
            await TearDownClientAsync(client).ConfigureAwait(false);
            Mutate(s => s with { Status = ConnectionStatus.Error, Endpoint = null });
            throw;
        }
    }

    public async Task<StateDto> DisconnectAsync(CancellationToken ct = default)
    {
        Protocol1Client? client;
        lock (_sync)
        {
            client = _activeClient;
            _activeClient = null;
        }

        if (client is not null)
        {
            client.AdcOverloadObserved -= OnAdcOverload;
            Disconnected?.Invoke();
            await TearDownClientAsync(client, ct).ConfigureAwait(false);
            _log.LogInformation("radio.disconnected");
        }

        Mutate(s => s with
        {
            Status = ConnectionStatus.Disconnected,
            Endpoint = null,
            AttOffsetDb = 0,
            AdcOverloadWarning = false,
        });
        return Snapshot();
    }

    public StateDto SetVfo(long hz)
    {
        long clamped = Math.Clamp(hz, 0L, 60_000_000L);
        long previous;
        RxMode currentMode;
        lock (_sync) { previous = _state.VfoHz; currentMode = _state.Mode; }
        Mutate(s => s with { VfoHz = clamped });
        // CW retunes the LO cw_pitch below/above the dial so the listening
        // freq lands on the +cw_pitch / -cw_pitch audio passband. In SSB /
        // AM / FM / DIG modes EffectiveLoHz returns the dial value
        // unchanged, preserving prior behaviour.
        ActiveClient?.SetVfoAHz(CwOffset.EffectiveLoHz(currentMode, clamped));
        // Band edge crossed? Per-band PA gain / OC bits may have swapped — push
        // the new snapshot before the next TX frame ships. Cheap when no
        // crossing occurred (same bytes re-pushed).
        if (BandUtils.FreqToBand(previous) != BandUtils.FreqToBand(clamped))
        {
            RecomputePaAndPush();
        }
        return Snapshot();
    }

    // Per-mode-family remembered filter magnitudes. Mode switching snapshots
    // the current abs-filter into the departing family's slot and restores the
    // target family's slot on entry — so FM→USB brings back the SSB width
    // the user was using, not the 5500-Hz FM stomp the old SignedFilterForMode
    // left behind (FM overrode f_low/f_high to ±5500, and on return to USB
    // the min-abs/max-abs recomputation collapsed the passband to (5500,5500),
    // killing audio).
    private sealed record FamilyFilter(int LoAbs, int HiAbs);
    private FamilyFilter _ssbFilter = new(150, 2850);
    private FamilyFilter _amFilter = new(0, 4000);
    private FamilyFilter _fmFilter = new(0, 5500);
    // CW abs values include the cw_pitch offset (Thetis F6 250 Hz preset:
    // pitch=600, half=125 → 475..725). SignedFilterForMode keeps them as
    // (+475,+725) for CWU and mirrors to (-725,-475) for CWL.
    private FamilyFilter _cwFilter = new(475, 725);

    // TX-side per-family filter memory. Thetis stores a single TX filter Lo/Hi
    // (setup.cs:5029-5066); pihpsdr uses hardcoded per-mode shapes
    // (transmitter.c:2108-2211). Zeus mirrors the RX per-family model so the
    // operator's USB TX width survives an AM round-trip, and LSB/USB share
    // absolute values with sign flipped at apply time. Defaults track Thetis
    // stock: SSB 150-2850, AM/DSB 0-4000, FM 0-3000 (Thetis narrowest FM TX
    // is 3 kHz half-width), CW 475-725 (250 Hz around cw_pitch=600).
    private FamilyFilter _ssbTxFilter = new(150, 2850);
    private FamilyFilter _amTxFilter = new(0, 4000);
    private FamilyFilter _fmTxFilter = new(0, 3000);
    private FamilyFilter _cwTxFilter = new(475, 725);

    public StateDto SetMode(RxMode mode)
    {
        RxMode departingMode = default;
        string? departingPreset = null;
        long newVfoHz = 0;
        Mutate(s =>
        {
            departingMode = s.Mode;
            departingPreset = s.FilterPresetName;

            // Save departing mode's preset name to the in-memory cache.
            _lastPresetPerMode[s.Mode] = s.FilterPresetName;

            // 1) Save current abs-filter into the mode we are LEAVING (RX + TX).
            int curLoAbs = Math.Min(Math.Abs(s.FilterLowHz), Math.Abs(s.FilterHighHz));
            int curHiAbs = Math.Max(Math.Abs(s.FilterLowHz), Math.Abs(s.FilterHighHz));
            StoreFamilyFilter(s.Mode, curLoAbs, curHiAbs);
            int curTxLoAbs = Math.Min(Math.Abs(s.TxFilterLowHz), Math.Abs(s.TxFilterHighHz));
            int curTxHiAbs = Math.Max(Math.Abs(s.TxFilterLowHz), Math.Abs(s.TxFilterHighHz));
            StoreTxFamilyFilter(s.Mode, curTxLoAbs, curTxHiAbs);

            // 2) Look up the target family's remembered filter (RX + TX).
            var fam = FamilyFilterFor(mode);
            var txFam = TxFamilyFilterFor(mode);

            // 3) Re-sign per target mode's sideband convention.
            var (lo, hi) = SignedFilterForMode(mode, fam.LoAbs, fam.HiAbs);
            var (txLo, txHi) = SignedFilterForMode(mode, txFam.LoAbs, txFam.HiAbs);

            // 4) Restore the last-known preset name for the incoming mode.
            _lastPresetPerMode.TryGetValue(mode, out var restoredPreset);

            // 5) Thetis-style dial bump on SSB↔CW transitions so the
            //    effective LO doesn't jump under the operator's feet — the
            //    dial absorbs the ±cw_pitch step and the radio stays on the
            //    same physical signal. Within CWU↔CWL the dial stays put
            //    (Thetis console.cs:34037-34052, 34203-34298 mirrored here).
            //    Non-CW↔non-CW transitions return 0, so SSB/AM/FM/DIG
            //    behaviour is unchanged.
            long bump = CwOffset.DialBumpForModeTransition(s.Mode, mode);
            newVfoHz = Math.Clamp(s.VfoHz + bump, 0L, 60_000_000L);

            return s with
            {
                Mode = mode,
                VfoHz = newVfoHz,
                FilterLowHz = lo, FilterHighHz = hi,
                TxFilterLowHz = txLo, TxFilterHighHz = txHi,
                FilterPresetName = restoredPreset,
            };
        });

        // Persist the departing mode's last preset outside the lock.
        if (departingPreset != null)
            _filterPresetStore?.UpsertLastSelectedPreset(departingMode, departingPreset);

        // Push the new effective LO. Even with no dial bump, switching
        // into/out of CW changes EffectiveLoHz by ±cw_pitch and the radio
        // needs the new tuning before the next IQ block arrives. P2 is
        // pushed via DspPipelineService.OnRadioStateChanged.
        ActiveClient?.SetVfoAHz(CwOffset.EffectiveLoHz(mode, newVfoHz));

        return Snapshot();
    }

    public StateDto SetFilter(int lowHz, int highHz, string? presetName = null)
    {
        if (highHz < lowHz) (lowHz, highHz) = (highHz, lowHz);
        RxMode modeAtSet = RxMode.USB;
        string? resolvedName = presetName;
        Mutate(s =>
        {
            modeAtSet = s.Mode;
            // Normalize the slot name: if (low,high) exactly matches a non-VAR
            // preset for this mode, use that slot's name regardless of what the
            // caller passed. Prevents dual selection where a stored VAR happens
            // to equal a standard preset width and edges.
            var match = FilterPresets.DefaultsForMode(s.Mode)
                .FirstOrDefault(e => !e.IsVar && e.LowHz == lowHz && e.HighHz == highHz);
            if (match is not null) resolvedName = match.SlotName;
            if (resolvedName != null) _lastPresetPerMode[s.Mode] = resolvedName;
            return s with { FilterLowHz = lowHz, FilterHighHz = highHz, FilterPresetName = resolvedName };
        });
        if (resolvedName != null)
            _filterPresetStore?.UpsertLastSelectedPreset(modeAtSet, resolvedName);
        return Snapshot();
    }

    // TX bandpass filter setter. Signed pair like SetFilter — caller is
    // expected to have already re-signed positive (abs) values per the current
    // mode's sideband convention. DspPipelineService picks up the state-change
    // and forwards to the engine via IDspEngine.SetTxFilter.
    public StateDto SetTxFilter(int lowHz, int highHz)
    {
        if (highHz < lowHz) (lowHz, highHz) = (highHz, lowHz);
        Mutate(s => s with { TxFilterLowHz = lowHz, TxFilterHighHz = highHz });
        return Snapshot();
    }

    public IReadOnlyList<FilterPresetDto> GetFilterPresets(RxMode mode)
    {
        var defaults = FilterPresets.DefaultsForMode(mode);
        return defaults.Select(e =>
        {
            if (e.IsVar && _filterPresetStore != null)
            {
                var stored = _filterPresetStore.GetVarOverride(mode, e.SlotName);
                if (stored.HasValue)
                    return new FilterPresetDto(e.SlotName, e.Label, stored.Value.LowHz, stored.Value.HighHz, true);
            }
            return new FilterPresetDto(e.SlotName, e.Label, e.LowHz, e.HighHz, e.IsVar);
        }).ToList();
    }

    public StateDto SetFilterPresetOverride(RxMode mode, string slotName, int loHz, int hiHz)
    {
        if (slotName is not ("VAR1" or "VAR2"))
            throw new InvalidOperationException("Only VAR1 and VAR2 slots can be overridden.");
        _filterPresetStore?.UpsertVarOverride(mode, slotName, loHz, hiHz);
        return Snapshot();
    }

    public string[] GetFavoriteFilterSlots(RxMode mode)
    {
        return _filterPresetStore?.GetFavoriteSlots(mode) ?? new[] { "F6", "F5", "F4" };
    }

    public StateDto SetFavoriteFilterSlots(RxMode mode, string[] slotNames)
    {
        if (slotNames.Length > 3)
            throw new ArgumentException("Maximum 3 favorite slots allowed", nameof(slotNames));
        _filterPresetStore?.SetFavoriteSlots(mode, slotNames);
        return Snapshot();
    }

    private void StoreFamilyFilter(RxMode mode, int loAbs, int hiAbs)
    {
        var slot = new FamilyFilter(loAbs, hiAbs);
        switch (mode)
        {
            case RxMode.USB: case RxMode.LSB: case RxMode.DIGU: case RxMode.DIGL:
                _ssbFilter = slot; break;
            case RxMode.AM: case RxMode.SAM: case RxMode.DSB:
                _amFilter = slot; break;
            case RxMode.FM:
                _fmFilter = slot; break;
            case RxMode.CWL: case RxMode.CWU:
                _cwFilter = slot; break;
        }
    }

    private void StoreTxFamilyFilter(RxMode mode, int loAbs, int hiAbs)
    {
        var slot = new FamilyFilter(loAbs, hiAbs);
        switch (mode)
        {
            case RxMode.USB: case RxMode.LSB: case RxMode.DIGU: case RxMode.DIGL:
                _ssbTxFilter = slot; break;
            case RxMode.AM: case RxMode.SAM: case RxMode.DSB:
                _amTxFilter = slot; break;
            case RxMode.FM:
                _fmTxFilter = slot; break;
            case RxMode.CWL: case RxMode.CWU:
                _cwTxFilter = slot; break;
        }
    }

    private FamilyFilter TxFamilyFilterFor(RxMode mode) => mode switch
    {
        RxMode.USB or RxMode.LSB or RxMode.DIGU or RxMode.DIGL => _ssbTxFilter,
        RxMode.AM or RxMode.SAM or RxMode.DSB => _amTxFilter,
        RxMode.FM => _fmTxFilter,
        RxMode.CWL or RxMode.CWU => _cwTxFilter,
        _ => _ssbTxFilter,
    };

    private FamilyFilter FamilyFilterFor(RxMode mode) => mode switch
    {
        RxMode.USB or RxMode.LSB or RxMode.DIGU or RxMode.DIGL => _ssbFilter,
        RxMode.AM or RxMode.SAM or RxMode.DSB => _amFilter,
        RxMode.FM => _fmFilter,
        RxMode.CWL or RxMode.CWU => _cwFilter,
        _ => _ssbFilter,
    };

    private static (int low, int high) SignedFilterForMode(RxMode mode, int loAbs, int hiAbs)
    {
        return mode switch
        {
            RxMode.USB => (+loAbs, +hiAbs),
            RxMode.DIGU => (0, +hiAbs),
            RxMode.LSB => (-hiAbs, -loAbs),
            RxMode.DIGL => (-hiAbs, 0),
            RxMode.AM or RxMode.SAM or RxMode.DSB => (-hiAbs, +hiAbs),
            RxMode.FM => (-hiAbs, +hiAbs),
            // CW is sideband-keyed: CWU sits in the positive baseband around
            // +cw_pitch, CWL in the negative around -cw_pitch. WDSP groups
            // CWU with USB and CWL with LSB inside ApplyBandpassForMode, so
            // the absolute family-filter values already include the cw_pitch
            // offset (see FilterPresets.Cwu/Cwl: low/high = ±(pitch ± half)).
            // A symmetric (-hi,+hi) signing here would collapse the passband
            // to (hi,hi) after WDSP's abs-and-sort, killing CW audio.
            RxMode.CWU => (+loAbs, +hiAbs),
            RxMode.CWL => (-hiAbs, -loAbs),
            _ => (+loAbs, +hiAbs),
        };
    }

    public StateDto SetSampleRate(HpsdrSampleRate rate)
    {
        int hz = rate.SampleRateHz();
        Mutate(s => s with { SampleRate = hz });
        ActiveClient?.SetSampleRate(rate);
        var board = ConnectedBoardKind;
        if (board != HpsdrBoardKind.Unknown)
            _radioStateStore?.SetBoardSampleRate(board, hz, EffectiveOrionMkIIVariant);
        return Snapshot();
    }

    public StateDto SetPreamp(bool on)
    {
        lock (_sync) _preampOn = on;
        // P1 path: Protocol1Client owns the bit; SetPreamp pushes the
        // updated CcState on the next outgoing frame. ActiveClient is
        // null on a P2 connection, so the PreampChanged event below is
        // what carries the bit into Protocol2Client (issue #126).
        ActiveClient?.SetPreamp(on);
        PreampChanged?.Invoke(on);
        return Snapshot();
    }

    public StateDto SetAttenuator(HpsdrAtten atten)
    {
        _atten = atten;
        Mutate(s => s with { AttenDb = atten.ClampedDb });
        // Honour any active auto-ATT offset when the user adjusts the baseline.
        // _lastAppliedEffectiveDb is invalidated so the new sum reaches the radio
        // even if it happens to equal the previous effective value.
        int effective;
        lock (_sync)
        {
            effective = Math.Clamp(_atten.ClampedDb + _attOffsetDb, HpsdrAtten.MinDb, HpsdrAtten.MaxDb);
            _lastAppliedEffectiveDb = effective;
        }
        ActiveClient?.SetAttenuator(new HpsdrAtten(effective));
        return Snapshot();
    }

    public StateDto SetAutoAtt(bool enabled)
    {
        lock (_sync)
        {
            if (_state.AutoAttEnabled == enabled) return _state;
            _state = _state with { AutoAttEnabled = enabled };
            if (!enabled)
            {
                // Turning auto off: stop accumulating overload counters so the
                // warning lamp doesn't linger and reset the offset to zero so
                // the hardware comes back to the user's baseline immediately.
                _attOffsetDb = 0;
                _adcOverloadLevel = 0;
                _overloadSeenInWindow = false;
                _state = _state with { AttOffsetDb = 0, AdcOverloadWarning = false };
                int baseline = _atten.ClampedDb;
                if (_lastAppliedEffectiveDb != baseline)
                {
                    _lastAppliedEffectiveDb = baseline;
                    ActiveClient?.SetAttenuator(_atten);
                }
            }
        }
        var snap = Snapshot();
        StateChanged?.Invoke(snap);
        return snap;
    }

    public StateDto SetAutoAgc(bool enabled)
    {
        lock (_sync)
        {
            if (_state.AutoAgcEnabled == enabled) return _state;
            _state = _state with { AutoAgcEnabled = enabled };
            if (!enabled)
            {
                // Turning auto off: reset the offset to zero so AGC-T returns
                // to the user's baseline immediately.
                _agcOffsetDb = 0.0;
                _lastAgcTickMs = long.MinValue;
                _noiseFloorWindowFill = 0;
                _noiseFloorWindowIdx = 0;
                _state = _state with { AgcOffsetDb = 0.0 };
            }
            else
            {
                // Turning auto on: reset timer + window so we recalibrate.
                _lastAgcTickMs = long.MinValue;
                _noiseFloorWindowFill = 0;
                _noiseFloorWindowIdx = 0;
            }
        }
        var snap = Snapshot();
        StateChanged?.Invoke(snap);
        return snap;
    }

    /// <summary>
    /// Auto-AGC control loop. Estimates the band noise floor from a sliding
    /// window of S-meter samples and chooses an absolute effective AGC-T that
    /// places that floor near TargetAudioDb at the WDSP output, then derives
    /// AgcOffsetDb = target − AgcTopDb so the same noise floor converges to
    /// the same effective value regardless of where the user parked the
    /// slider baseline. Slew-limited so adjustments are inaudibly gradual.
    /// </summary>
    internal void HandleRxMeterForAutoAgc(double signalDbm, long nowMs)
    {
        bool changedOffset = false;
        double newOffset = 0.0;
        double noiseFloor = double.NaN;

        lock (_sync)
        {
            if (!_state.AutoAgcEnabled) return;
            if (_mox) return;   // Pause during TX
            if (!double.IsFinite(signalDbm) || signalDbm <= -250.0) return;

            // If we paused for >5 s (TX, just-toggled-on, RX dropout) the
            // window may hold stale samples — clear before re-accumulating.
            if (_lastAgcTickMs != long.MinValue && nowMs - _lastAgcTickMs > 5000)
            {
                _noiseFloorWindowFill = 0;
                _noiseFloorWindowIdx = 0;
            }

            if (_lastAgcTickMs != long.MinValue && nowMs - _lastAgcTickMs < 500)
                return;
            _lastAgcTickMs = nowMs;

            _noiseFloorWindow[_noiseFloorWindowIdx] = signalDbm;
            _noiseFloorWindowIdx = (_noiseFloorWindowIdx + 1) % _noiseFloorWindow.Length;
            if (_noiseFloorWindowFill < _noiseFloorWindow.Length) _noiseFloorWindowFill++;

            // Wait for ~3 s of samples before adjusting so the minimum is meaningful.
            if (_noiseFloorWindowFill < 6) return;

            noiseFloor = double.PositiveInfinity;
            for (int i = 0; i < _noiseFloorWindowFill; i++)
            {
                double v = _noiseFloorWindow[i];
                if (v < noiseFloor) noiseFloor = v;
            }

            // Auto-AGC chooses an *absolute* effective AGC-T from the noise
            // floor: place the band noise at TargetAudioDb after WDSP's
            // max-gain stage, so a quiet band lands at ~80 dB and a noisy
            // band lands at ~50 dB regardless of where the user parked the
            // slider baseline. The offset is whatever it takes to reach that
            // absolute target on top of the current AgcTopDb baseline.
            const double TargetAudioDb = -40.0;     // desired audio-output noise level
            const double MinEffectiveAgcT = 20.0;
            const double MaxEffectiveAgcT = 100.0;
            double targetEffective = Math.Clamp(
                TargetAudioDb - noiseFloor, MinEffectiveAgcT, MaxEffectiveAgcT);
            double desiredOffset = targetEffective - _state.AgcTopDb;

            double delta = desiredOffset - _agcOffsetDb;
            if (Math.Abs(delta) < 0.5) return;

            const double SlewPerTickDb = 1.0;
            _agcOffsetDb = delta > 0
                ? Math.Min(desiredOffset, _agcOffsetDb + SlewPerTickDb)
                : Math.Max(desiredOffset, _agcOffsetDb - SlewPerTickDb);

            _state = _state with { AgcOffsetDb = _agcOffsetDb };
            newOffset = _agcOffsetDb;
            changedOffset = true;
        }

        if (changedOffset)
        {
            StateChanged?.Invoke(Snapshot());
            _log.LogDebug("auto-agc offset={Offset}dB noisefloor={Floor}dBm", newOffset, noiseFloor);
        }
    }

    // MOX is transient — it belongs on the wire (CcState.Mox → C0 LSB), not in
    // the persisted RX StateDto. TxService owns the latched bool that the UI
    // reads back; this method is the P1-side fan-out only. We also stash the
    // bit locally so the auto-ATT loop can pause itself during TX (Thetis
    // console.cs:22188 — TX uses its own TxAttenData path, not the RX ramp).
    public void SetMox(bool on)
    {
        lock (_sync) _mox = on;
        ActiveClient?.SetMox(on);
        MoxChanged?.Invoke(on);
    }

    // Drive is transient like MOX — latched on the Protocol1Client so the
    // DriveFilter register on the next outgoing frame carries it. We clamp
    // here rather than at the endpoint so every entry point (REST, future
    // CAT bridge, tests) gets the same range guarantee.
    public void SetDrive(int percent)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        Interlocked.Exchange(ref _drivePct, clamped);
        RecomputePaAndPush();
    }

    // Independent TUN drive %. Applies on the very next frame if TUN is already
    // keyed; otherwise it sits until TxService flips _tunActive.
    public void SetTuneDrive(int percent)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        Interlocked.Exchange(ref _tunePct, clamped);
        RecomputePaAndPush();
    }

    // TxService calls this on every MOX/TUN edge. Runs the same recompute the
    // drive-slider path uses so the drive byte on the wire always reflects the
    // just-applied keying state (Thetis PreviousPWR swap, `console.cs:30094`).
    public void NotifyTunActive(bool on)
    {
        lock (_sync) _tunActive = on;
        RecomputePaAndPush();
        TunActiveChanged?.Invoke(on);
    }

    // DspPipelineService calls this right after a P2 client is created so the
    // fresh connection sees the current PA snapshot without waiting for the
    // next state change.
    public void ReplayPaSnapshot() => RecomputePaAndPush();

    /// <summary>
    /// HL2 Band Volts PWM enable (issue #279). Updates the persisted
    /// per-radio preference AND any live Protocol-1 client so the next
    /// outgoing Config frame carries the new bit. Honoured on HL2 only;
    /// non-HL2 boards never see this bit on the wire because Zeus' UI gate
    /// (<c>HasHl2OptionalToggles</c>) hides the control there. Returns the
    /// effective value (echoes the input — present for symmetry with other
    /// state setters that may sanitize).
    /// </summary>
    public bool SetHl2BandVolts(bool enabled)
    {
        // Write-through to persistent storage first so a crash between the
        // store write and the live-client push can't lose the preference.
        _preferredRadioStore?.SetEnableHl2BandVolts(enabled);
        // Then push to the live client (if any) so the bit lands on the wire
        // immediately. Safe on non-HL2 boards: SnapshotState's C3-bit-3
        // encoding fires regardless of board, but the gate at the UI /
        // capability level keeps non-HL2 operators from ever flipping it.
        if (_activeClient is not null)
        {
            _activeClient.EnableHl2BandVolts = enabled;
        }
        return enabled;
    }

    /// <summary>
    /// Reads the persisted HL2 Band Volts preference. Surfaced for the
    /// <c>/api/radio/hl2-options</c> GET endpoint. Returns <c>false</c> when
    /// no preferences store is wired (test factories) or no row exists yet.
    /// </summary>
    public bool GetHl2BandVolts() =>
        _preferredRadioStore?.GetEnableHl2BandVolts() ?? false;

    // Compute the current drive byte + OC masks + PA enable from _drivePct,
    // PaSettingsStore, and the current VFO band. Push to the active P1 client
    // and fire PaSnapshotChanged for the P2 forwarder. Called on:
    //   - SetDrive (slider moved)
    //   - SetVfo when the band changes
    //   - PaSettingsStore.Changed (user edited PA Settings)
    //   - Connected (push current snapshot to fresh client)
    private void RecomputePaAndPush()
    {
        var stateSnap = Snapshot();
        // PA config uses the effective board so the operator can pre-stage
        // PA Settings for a radio not yet connected; once a radio IS on the
        // wire, EffectiveBoardKind == ConnectedBoardKind (discovery wins).
        var cfg = _paStore.GetAll(EffectiveBoardKind, EffectiveOrionMkIIVariant);
        var bandName = BandUtils.FreqToBand(stateSnap.VfoHz);
        var bandCfg = bandName is not null
            ? cfg.Bands.FirstOrDefault(b => b.Band == bandName) ?? new PaBandSettingsDto(bandName)
            : new PaBandSettingsDto("unknown");

        bool tunActive;
        lock (_sync) tunActive = _tunActive;
        int activePct = tunActive
            ? Volatile.Read(ref _tunePct)
            : Volatile.Read(ref _drivePct);
        // Route through the per-board drive-profile so HL2's 4-bit drive
        // register is respected (bottom nibble ignored by gateware). See
        // Zeus.Server.RadioDriveProfile + docs/lessons/hl2-drive-byte-
        // quantization.md. Non-HL2 boards get the straight 8-bit math via
        // FullByteDriveProfile.
        var driveProfile = RadioDriveProfiles.For(ConnectedBoardKind);
        byte driveByte = driveProfile.EncodeDriveByte(activePct, bandCfg.PaGainDb, cfg.Global.PaMaxPowerWatts);
        bool paEnabled = cfg.Global.PaEnabled && !bandCfg.DisablePa;

        _log.LogInformation(
            "pa.recompute tunActive={Tun} pct={Pct} band={Band} gainDb={Gain:F2} maxW={Max} profile={Profile} -> byte={Byte} paEn={PaEn}",
            tunActive, activePct, bandName ?? "?", bandCfg.PaGainDb, cfg.Global.PaMaxPowerWatts, driveProfile.BoardLabel, driveByte, paEnabled);

        ActiveClient?.SetDriveByte(driveByte);
        ActiveClient?.SetOcMasks(bandCfg.OcTx, bandCfg.OcRx);

        PaSnapshotChanged?.Invoke(new PaRuntimeSnapshot(
            DriveByte: driveByte,
            OcTxMask: bandCfg.OcTx,
            OcRxMask: bandCfg.OcRx,
            PaEnabled: paEnabled));
    }

    // Back-compat shim for callers/tests that predate IRadioDriveProfile.
    // Runtime RecomputePaAndPush no longer goes through here — it uses the
    // per-board RadioDriveProfiles.For(board) dispatch so HL2's 4-bit drive
    // is quantised correctly. Keep this method as the 8-bit/full-byte math
    // for tests and anything else that wants the raw value.
    internal static byte ComputeDriveByte(int drivePct, double paGainDb, int maxWatts)
        => DriveByteMath.ComputeFullByte(drivePct, paGainDb, maxWatts);

    // Thetis "AGC Top" slider — max post-AGC gain in dB. Clamped to the
    // Thetis UI range (−20..120). DspPipelineService picks this up through the
    // StateChanged event and forwards it to the active engine.
    public StateDto SetAgcTop(double topDb)
    {
        double clamped = Math.Clamp(topDb, -20.0, 120.0);
        Mutate(s => s with { AgcTopDb = clamped });
        // Persist so the operator's choice survives a server restart. Only
        // the user-baseline (AgcTopDb) is persisted — the auto-AGC offset
        // is recomputed live and isn't worth saving.
        _dspSettingsStore.SetAgcTopDb(clamped);
        return Snapshot();
    }

    // Master RX AF gain in dB. −50 dB is effectively silent (0.003 linear),
    // 0 dB matches the fresh-open default, +20 dB is a 10× linear boost for
    // quiet signals. Range mirrors Thetis's ptbAF (console.cs:4312-4313:
    // tbAF.Minimum = -50, Maximum = 20).
    public StateDto SetRxAfGain(double db)
    {
        double clamped = Math.Clamp(db, -50.0, 20.0);
        Mutate(s => s with { RxAfGainDb = clamped });
        return Snapshot();
    }

    public StateDto SetNr(NrConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        Mutate(s => s with { Nr = cfg });

        // Persist the new DSP settings to the store
        _dspSettingsStore.Upsert(cfg);

        return Snapshot();
    }

    // Right-click popover save for NR2 (EMNR) post2 tunables. Merges only
    // the non-null fields onto the current NrConfig so the operator can edit
    // a single knob without disturbing siblings, then re-pushes the whole
    // block through SetNr to keep persistence and engine state in lock-step.
    public StateDto SetNr2Post2(Nr2Post2ConfigSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        var current = Snapshot().Nr ?? new NrConfig();
        var merged = current with
        {
            EmnrPost2Run = req.Post2Run ?? current.EmnrPost2Run,
            EmnrPost2Factor = req.Post2Factor ?? current.EmnrPost2Factor,
            EmnrPost2Nlevel = req.Post2Nlevel ?? current.EmnrPost2Nlevel,
            EmnrPost2Rate = req.Post2Rate ?? current.EmnrPost2Rate,
            EmnrPost2Taper = req.Post2Taper ?? current.EmnrPost2Taper,
        };
        return SetNr(merged);
    }

    // NR2 (EMNR) core algorithm selectors + Trained-method T1/T2. Same
    // null-merge pattern as SetNr2Post2: each absent field leaves the
    // persisted value untouched. Range-checks the enum-shaped fields so
    // an out-of-range value can't push WDSP into an undefined branch.
    public StateDto SetNr2Core(Nr2CoreConfigSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.GainMethod is int gm && (gm < 0 || gm > 3))
            throw new ArgumentException($"GainMethod must be 0..3, got {gm}", nameof(req));
        if (req.NpeMethod is int npm && (npm < 0 || npm > 2))
            throw new ArgumentException($"NpeMethod must be 0..2, got {npm}", nameof(req));

        var current = Snapshot().Nr ?? new NrConfig();
        var merged = current with
        {
            EmnrGainMethod = req.GainMethod ?? current.EmnrGainMethod,
            EmnrNpeMethod = req.NpeMethod ?? current.EmnrNpeMethod,
            EmnrAeRun = req.AeRun ?? current.EmnrAeRun,
            EmnrTrainT1 = req.TrainT1 ?? current.EmnrTrainT1,
            EmnrTrainT2 = req.TrainT2 ?? current.EmnrTrainT2,
        };
        return SetNr(merged);
    }

    // Right-click popover save for NR4 (SBNR) tunables — same merge-and-
    // re-push pattern as SetNr2Post2.
    public StateDto SetNr4(Nr4ConfigSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        var current = Snapshot().Nr ?? new NrConfig();
        var merged = current with
        {
            Nr4ReductionAmount = req.ReductionAmount ?? current.Nr4ReductionAmount,
            Nr4SmoothingFactor = req.SmoothingFactor ?? current.Nr4SmoothingFactor,
            Nr4WhiteningFactor = req.WhiteningFactor ?? current.Nr4WhiteningFactor,
            Nr4NoiseRescale = req.NoiseRescale ?? current.Nr4NoiseRescale,
            Nr4PostFilterThreshold = req.PostFilterThreshold ?? current.Nr4PostFilterThreshold,
            Nr4NoiseScalingType = req.NoiseScalingType ?? current.Nr4NoiseScalingType,
            Nr4Position = req.Position ?? current.Nr4Position,
        };
        return SetNr(merged);
    }

    // CFC (Continuous Frequency Compressor) — issue #123. The whole 10-band
    // config travels in one POST because the operator edits the panel as a
    // single table; the engine then re-pushes the whole profile to WDSP.
    // Mirrors the SetNr shape: validate, mutate state, persist, return
    // snapshot. DspPipelineService picks up the change-detect on the next
    // OnRadioStateChanged tick and pushes through to the engine.
    public StateDto SetCfc(CfcSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        var cfg = req.Config ?? throw new ArgumentException("Config required", nameof(req));
        if (cfg.Bands is null || cfg.Bands.Length != 10)
            throw new ArgumentException($"Bands must have exactly 10 entries; got {cfg.Bands?.Length ?? 0}", nameof(req));

        Mutate(s => s with { Cfc = cfg });
        _dspSettingsStore.Upsert(cfg);
        _log.LogInformation(
            "radio.setCfc enabled={Enabled} peq={Peq} preComp={Pre:F1}dB prePeq={PrePeq:F1}dB",
            cfg.Enabled, cfg.PostEqEnabled, cfg.PreCompDb, cfg.PrePeqDb);
        return Snapshot();
    }

    // ---------------- PureSignal ----------------
    // SetPs flips master arm and cal-mode in a single mutate so the engine
    // sees a consistent state when DspPipelineService.OnRadioStateChanged
    // fires.
    public StateDto SetPs(PsControlSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        Mutate(s => s with
        {
            PsEnabled = req.Enabled,
            PsAuto = req.Auto,
            PsSingle = req.Single,
        });
        // Persist Auto/Single change so the operator's cal-mode preference
        // sticks across restarts. (PsEnabled is the master arm — not
        // persisted; same discipline as MOX/TUN.)
        PersistPsState();
        return Snapshot();
    }

    public StateDto SetPsAdvanced(PsAdvancedSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        Mutate(s => s with
        {
            PsPtol = req.Ptol ?? s.PsPtol,
            PsAutoAttenuate = req.AutoAttenuate ?? s.PsAutoAttenuate,
            PsMoxDelaySec = req.MoxDelaySec ?? s.PsMoxDelaySec,
            PsLoopDelaySec = req.LoopDelaySec ?? s.PsLoopDelaySec,
            PsAmpDelayNs = req.AmpDelayNs ?? s.PsAmpDelayNs,
            PsHwPeak = req.HwPeak ?? s.PsHwPeak,
            PsIntsSpiPreset = req.IntsSpiPreset ?? s.PsIntsSpiPreset,
        });
        PersistPsState();
        return Snapshot();
    }

    /// <summary>
    /// Choose Internal vs External feedback antenna for PureSignal.
    /// Mutates StateDto; DspPipelineService.OnRadioStateChanged forwards
    /// the bool into the active Protocol2Client where it flips one alex0
    /// bit on the next CmdHighPriority. WDSP cal/iqc are unaffected — the
    /// HW-Peak slider stays shared across sources (matches pihpsdr/Thetis).
    /// </summary>
    public StateDto SetPsFeedbackSource(PsFeedbackSourceSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        Mutate(s => s with { PsFeedbackSource = req.Source });
        PersistPsState();
        return Snapshot();
    }

    /// <summary>
    /// Toggle the "Monitor PA output" view (issue #121). When on, AND PS is
    /// armed, AND PS has converged, DspPipelineService.Tick reads pixels
    /// from the PS-feedback analyzer instead of the post-CFIR TX analyzer
    /// so the operator sees the actual on-air RF rather than the
    /// predistorted baseband. Operator viewing preference — NOT persisted
    /// across sessions (same discipline as PsEnabled / MOX).
    /// </summary>
    public StateDto SetPsMonitor(PsMonitorSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        _log.LogInformation("setPsMonitor enabled={Enabled}", req.Enabled);
        Mutate(s => s with { PsMonitorEnabled = req.Enabled });
        return Snapshot();
    }

    /// <summary>TX-monitor toggle (audition path). Mutates StateDto so the
    /// next DspPipelineService.UpdateState tick latches the value into
    /// engine.SetTxMonitorEnabled. Mirrors PsMonitor's lifecycle — operator
    /// preference, not persisted across sessions; resets to off on each new
    /// connect so the radio doesn't come up auditioning unintentionally.</summary>
    public StateDto SetTxMonitor(TxMonitorSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        _log.LogInformation("setTxMonitor enabled={Enabled}", req.Enabled);
        Mutate(s => s with { TxMonitorEnabled = req.Enabled });
        return Snapshot();
    }

    public StateDto SetTwoTone(TwoToneSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        Mutate(s => s with
        {
            TwoToneEnabled = req.Enabled,
            TwoToneFreq1 = req.Freq1 ?? s.TwoToneFreq1,
            TwoToneFreq2 = req.Freq2 ?? s.TwoToneFreq2,
            TwoToneMag = req.Mag ?? s.TwoToneMag,
        });
        // Persist freq1/freq2/mag — operator tunings survive restart.
        // TwoToneEnabled (master arm) is NOT persisted; same operator-action
        // discipline as MOX/TUN.
        PersistPsState();
        return Snapshot();
    }

    // Update the live state to track PS read-back from the engine. Called by
    // TxMetersService at 10 Hz while PS is armed.
    public void UpdatePsLiveReadout(double feedbackLevel, byte calState, bool correcting)
    {
        Mutate(s => s with
        {
            PsFeedbackLevel = feedbackLevel,
            PsCalState = calState,
            PsCorrecting = correcting,
        });
    }

    /// <summary>
    /// Resolves the operator-correct PS hardware-peak default for the given
    /// protocol + board kind. Sources:
    ///   - P1 Hermes / ANAN-10/100/100D/200D / Hermes-II / 10E / 100B → 0.4072
    ///   - P2 OrionMkII (G2 / Saturn) → 0.6121
    ///   - P2 ANAN-7000 / 8000 (default P2) → 0.2899
    ///   - HermesLite2 (either protocol) → 0.233 (MI0BOT special, but only if
    ///     someone connects an HL2 — Brian's Hermes is original, not HL2)
    /// Source authority: Thetis clsHardwareSpecific.cs:295-318 +
    /// pihpsdr transmitter.c:1166-1179 NEW_DEVICE_SATURN.
    /// </summary>
    public static double ResolvePsHwPeak(bool isProtocol2, HpsdrBoardKind board) =>
        ResolvePsHwPeak(isProtocol2, board, OrionMkIIVariant.G2);

    /// <summary>
    /// Variant-aware overload (issue #218 Phase 6). When
    /// <paramref name="board"/> is <see cref="HpsdrBoardKind.OrionMkII"/>
    /// on Protocol 2, the variant disambiguates the Saturn-FPGA family
    /// (G2 / G2-1K → 0.6121) from the OrionMkII-class family (7000DLE /
    /// 8000DLE / Apache OrionMkII original / ANVELINA-PRO3 / Red Pitaya
    /// → 0.2899). Pre-#218 the dispatch returned 0.6121 for every 0x0A
    /// board, which over-scaled the PS curve on non-Saturn variants.
    /// </summary>
    public static double ResolvePsHwPeak(bool isProtocol2, HpsdrBoardKind board, OrionMkIIVariant variant) =>
        // Per-protocol switch shaped so the P1 follow-up (separately
        // tracked) can wire HW-peak per-board too. P1 today is gated off
        // in the frontend but the engine still receives the right number
        // on connect — keeps Synthetic + tests deterministic.
        (isProtocol2, board) switch
        {
            (false, HpsdrBoardKind.HermesLite2)              => 0.233,
            (false, _)                                        => 0.4072,
            // 0x0A wire byte: Saturn FPGA (G2 / G2-1K) reports the high
            // peak per Thetis clsHardwareSpecific.cs:313; everything else
            // sharing the byte (7000DLE / 8000DLE / Apache OrionMkII /
            // ANVELINA-PRO3 / Red Pitaya) takes the default. Default
            // variant G2 preserves Zeus' pre-#218 P2 behaviour.
            (true,  HpsdrBoardKind.OrionMkII)                 => variant switch
            {
                OrionMkIIVariant.G2     => 0.6121,
                OrionMkIIVariant.G2_1K  => 0.6121,
                _                        => 0.2899,
            },
            (true,  HpsdrBoardKind.HermesLite2)               => 0.233,
            (true,  _)                                        => 0.2899,
        };

    /// <summary>
    /// Apply a per-radio PS hardware-peak default to the StateDto. Called by
    /// DspPipelineService after a successful connect (P1 or P2) so the
    /// engine sees the correct curve scale before the operator arms PS.
    /// Doesn't fire StateChanged unless the value actually moves.
    /// </summary>
    public void ApplyPsHwPeakForConnection(bool isProtocol2, HpsdrBoardKind board)
    {
        double peak = ResolvePsHwPeak(isProtocol2, board, EffectiveOrionMkIIVariant);
        // Snap PsHwPeakDefault to the resolved per-board value alongside
        // PsHwPeak so the UI can compare them for a "differs from default"
        // hint. mi0bot ref: PSForm.cs:830 reads HardwareSpecific.PSDefaultPeak
        // (clsHardwareSpecific.cs:303-328) at the same boundary — radio
        // change → re-resolve default.
        Mutate(s =>
            s.PsHwPeak == peak && s.PsHwPeakDefault == peak
                ? s
                : s with { PsHwPeak = peak, PsHwPeakDefault = peak });
        _log.LogInformation(
            "radio.applyPsHwPeak proto={Proto} board={Board} peak={Peak:F4}",
            isProtocol2 ? "P2" : "P1", board, peak);
    }

    public StateDto SetZoom(int level)
    {
        // Accepts the full DSP range (1..16); Program.cs already range-checks
        // the HTTP payload against these same bounds. A prior powers-of-two
        // guard here silently rejected 3/5/6/7 with a 500, causing the
        // frontend slider (step=1, 1..8) to appear stuck after valid steps.
        if (level < SyntheticDspEngine.MinZoomLevel || level > SyntheticDspEngine.MaxZoomLevel)
            throw new ArgumentException(
                $"zoom level must be in [{SyntheticDspEngine.MinZoomLevel},{SyntheticDspEngine.MaxZoomLevel}]; got {level}",
                nameof(level));
        Mutate(s => s with { ZoomLevel = level });
        return Snapshot();
    }

    public void Dispose()
    {
        _paStore.Changed -= RecomputePaAndPush;
        try { DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch { /* best-effort */ }
        _stateFlushTimer?.Dispose();
        // Final flush so the last operator actions survive a clean shutdown.
        _stateDirty = true;
        FlushState();
    }

    private void Mutate(Func<StateDto, StateDto> fn)
    {
        StateDto next;
        lock (_sync)
        {
            next = fn(_state);
            _state = next;
        }
        _stateDirty = true;
        StateChanged?.Invoke(next);
    }

    // Debounce flush: called by _stateFlushTimer every 1 s.
    // Captures the latest StateDto + family-filter memory under _sync and
    // writes to LiteDB. No-op when nothing has mutated since the last flush.
    private void FlushState()
    {
        if (!_stateDirty || _radioStateStore is null) return;
        _stateDirty = false;

        StateDto snap;
        FamilyFilter ssb, am, fm, cw, ssbTx, amTx, fmTx, cwTx;
        lock (_sync)
        {
            snap = _state;
            ssb = _ssbFilter; am = _amFilter; fm = _fmFilter; cw = _cwFilter;
            ssbTx = _ssbTxFilter; amTx = _amTxFilter; fmTx = _fmTxFilter; cwTx = _cwTxFilter;
        }

        try
        {
            _radioStateStore.Save(new RadioStateEntry
            {
                VfoHz = snap.VfoHz,
                Mode = snap.Mode,
                FilterLowHz = snap.FilterLowHz,
                FilterHighHz = snap.FilterHighHz,
                TxFilterLowHz = snap.TxFilterLowHz,
                TxFilterHighHz = snap.TxFilterHighHz,
                FilterPresetName = snap.FilterPresetName,
                AutoAttEnabled = snap.AutoAttEnabled,
                AttenDb = snap.AttenDb,
                AutoAgcEnabled = snap.AutoAgcEnabled,
                RxAfGainDb = snap.RxAfGainDb,
                ZoomLevel = snap.ZoomLevel,
                SsbFilterLoAbs = ssb.LoAbs,   SsbFilterHiAbs = ssb.HiAbs,
                AmFilterLoAbs = am.LoAbs,     AmFilterHiAbs = am.HiAbs,
                FmFilterLoAbs = fm.LoAbs,     FmFilterHiAbs = fm.HiAbs,
                CwFilterLoAbs = cw.LoAbs,     CwFilterHiAbs = cw.HiAbs,
                SsbTxFilterLoAbs = ssbTx.LoAbs, SsbTxFilterHiAbs = ssbTx.HiAbs,
                AmTxFilterLoAbs = amTx.LoAbs,   AmTxFilterHiAbs = amTx.HiAbs,
                FmTxFilterLoAbs = fmTx.LoAbs,   FmTxFilterHiAbs = fmTx.HiAbs,
                CwTxFilterLoAbs = cwTx.LoAbs,   CwTxFilterHiAbs = cwTx.HiAbs,
                UpdatedUtc = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "radio.state.flush failed");
        }
    }

    // Used by DspPipelineService when a Protocol 2 radio connects or
    // disconnects. RadioService's _activeClient is P1-only; this is how
    // the shared state (Status, Endpoint, SampleRate) stays coherent for
    // the UI without growing a P2 client slot here.
    //
    // The optional <paramref name="client"/> wires the freshly-opened
    // Protocol2Client to subscribers of <see cref="P2Connected"/>; passing
    // null keeps the signature backward-compatible for tests that don't
    // need the telemetry surface (issue #174).
    public void MarkProtocol2Connected(
        string endpoint,
        int sampleRateHz,
        Zeus.Protocol2.Protocol2Client? client = null,
        HpsdrBoardKind boardKind = HpsdrBoardKind.Unknown)
    {
        lock (_sync)
        {
            _p2Active = true;
            _p2BoardKind = boardKind;
        }
        Mutate(s => s with
        {
            Status = ConnectionStatus.Connected,
            Endpoint = endpoint,
            SampleRate = sampleRateHz,
        });
        // P2 is alive — PA defaults should reflect G2 / Orion class so the
        // operator sees realistic numbers when they open the PA panel.
        RecomputePaAndPush();
        // Fire AFTER the state mutation + PA recompute so subscribers see a
        // fully-coherent RadioService when they read board kind / snapshot.
        if (client is not null) P2Connected?.Invoke(client);
    }

    public void MarkProtocol2Disconnected()
    {
        lock (_sync)
        {
            _p2Active = false;
            _p2BoardKind = HpsdrBoardKind.Unknown;
        }
        Mutate(s => s with
        {
            Status = ConnectionStatus.Disconnected,
            Endpoint = null,
        });
        P2Disconnected?.Invoke();
    }

    // Resolves the board class for ALL board-specific behavior: PA settings,
    // drive-byte encoding, ATT behavior, filter switching. Normally returns
    // the board ID from discovery (P1) or infers OrionMkII for P2. When the
    // operator has enabled "Override Detection" in PreferredRadioStore, returns
    // the preferred board instead — use this for hardware combinations that
    // report incorrect board IDs or need different behavior (e.g., Anvelina SDR
    // + ANAN 200D PA detected as OrionMkII but needs Orion behavior).
    public HpsdrBoardKind ConnectedBoardKind
    {
        get
        {
            lock (_sync)
            {
                // Check if operator has explicitly enabled board override.
                // This allows forcing specific board behavior when auto-detection
                // is wrong or incomplete (different hardware with same board ID).
                if (_preferredRadioStore?.GetOverrideDetection() == true)
                {
                    var preferred = _preferredRadioStore.Get();
                    if (preferred.HasValue && preferred.Value != HpsdrBoardKind.Unknown)
                    {
                        return preferred.Value;
                    }
                }

                // Normal path: use discovery result.
                if (_activeClient is not null) return _activeClient.BoardKind;
                if (_p2Active)
                {
                    // Brick2 announces as Hermes (0x01) on P2; older Zeus
                    // assumed every P2 radio was OrionMkII because the connect
                    // API didn't carry the discovered byte (issue #171). The
                    // byte is now plumbed through MarkProtocol2Connected — fall
                    // back to OrionMkII only when the caller didn't supply it
                    // (legacy tests, older frontends).
                    return _p2BoardKind != HpsdrBoardKind.Unknown
                        ? _p2BoardKind
                        : HpsdrBoardKind.OrionMkII;
                }
                return HpsdrBoardKind.Unknown;
            }
        }
    }

    // Board used to seed PA defaults / power-math tables. When a radio is
    // connected, ConnectedBoardKind wins (which may be overridden by the
    // operator). Before first connect, the stored preference takes over so
    // the PA panel shows sane values for the radio the operator is about to
    // plug in.
    public HpsdrBoardKind EffectiveBoardKind
    {
        get
        {
            var connected = ConnectedBoardKind;
            if (connected != HpsdrBoardKind.Unknown) return connected;
            return _preferredRadioStore?.Get() ?? HpsdrBoardKind.Unknown;
        }
    }

    // Variant override for the 0x0A wire-byte alias family (issue #218).
    // Read by dispatch helpers (RadioCalibrations.For / PaDefaults.* /
    // BoardCapabilitiesTable.For) when EffectiveBoardKind == OrionMkII;
    // ignored for every other board. Default OrionMkIIVariant.G2 preserves
    // Zeus' pre-#218 behaviour for operators who never touch this setting.
    public OrionMkIIVariant EffectiveOrionMkIIVariant =>
        _preferredRadioStore?.GetOrionMkIIVariant() ?? OrionMkIIVariant.G2;

    // Protocol1 → RadioService bridge. Runs on the RX thread at ~1.2 kHz;
    // hands off to HandleAdcOverload for the logic the tests can drive.
    private void OnAdcOverload(AdcOverloadStatus status) =>
        HandleAdcOverload(status, Environment.TickCount64);

    /// <summary>
    /// Port of Thetis' handleOverload (console.cs:22167) plus the
    /// <c>_adc_overload_level</c> counter (console.cs:22093-22113). Runs every
    /// incoming EP6 packet; applies at most one +1/-1 dB step per
    /// <see cref="TickIntervalMs"/> window so the ramp is bounded and matches
    /// Thetis' perceived rate.
    /// </summary>
    internal void HandleAdcOverload(AdcOverloadStatus status, long nowMs)
    {
        bool changedWarning = false;
        int? effectiveToApply = null;
        bool newWarning = false;
        int newOffset = 0;

        lock (_sync)
        {
            if (!_state.AutoAttEnabled) return;
            if (_mox) return;   // TX-side ATT is owned by a different code path

            if (status.AnyOverload) _overloadSeenInWindow = true;

            if (_lastTickMs == long.MinValue)
            {
                _lastTickMs = nowMs;
                return;
            }

            if (nowMs - _lastTickMs < TickIntervalMs) return;
            _lastTickMs = nowMs;

            bool seen = _overloadSeenInWindow;
            _overloadSeenInWindow = false;

            if (seen)
            {
                if (_attOffsetDb < 31) _attOffsetDb++;
                _adcOverloadLevel = Math.Min(5, _adcOverloadLevel + 2);
            }
            else
            {
                if (_attOffsetDb > 0) _attOffsetDb--;
                if (_adcOverloadLevel > 0) _adcOverloadLevel--;
            }

            int effective = Math.Clamp(_atten.ClampedDb + _attOffsetDb, HpsdrAtten.MinDb, HpsdrAtten.MaxDb);
            if (effective != _lastAppliedEffectiveDb)
            {
                _lastAppliedEffectiveDb = effective;
                effectiveToApply = effective;
            }

            bool warn = _adcOverloadLevel > 3;
            if (warn != _state.AdcOverloadWarning || _attOffsetDb != _state.AttOffsetDb)
            {
                _state = _state with { AttOffsetDb = _attOffsetDb, AdcOverloadWarning = warn };
                changedWarning = true;
                newWarning = warn;
                newOffset = _attOffsetDb;
            }
        }

        if (effectiveToApply is int eff)
        {
            ActiveClient?.SetAttenuator(new HpsdrAtten(eff));
        }
        if (changedWarning)
        {
            StateChanged?.Invoke(Snapshot());
            // Debug-level — at 10 Hz this would flood logs if promoted.
            _log.LogDebug("auto-att offset={Offset}dB warn={Warn}", newOffset, newWarning);
        }
    }

    private static async Task TearDownClientAsync(Protocol1Client client, CancellationToken ct = default)
    {
        try { await client.StopAsync(ct).ConfigureAwait(false); } catch { /* best-effort */ }
        try { await client.DisconnectAsync(ct).ConfigureAwait(false); } catch { /* best-effort */ }
        client.Dispose();
    }

    internal static bool TryParseEndpoint(string endpoint, out IPEndPoint result)
    {
        result = null!;
        if (string.IsNullOrWhiteSpace(endpoint)) return false;

        if (IPEndPoint.TryParse(endpoint, out var parsed))
        {
            result = parsed.Port == 0
                ? new IPEndPoint(parsed.Address, DefaultHpsdrPort)
                : parsed;
            return true;
        }

        if (IPAddress.TryParse(endpoint, out var addr))
        {
            result = new IPEndPoint(addr, DefaultHpsdrPort);
            return true;
        }

        return false;
    }

    private static HpsdrSampleRate MapSampleRate(int hz) => hz switch
    {
        48_000 => HpsdrSampleRate.Rate48k,
        96_000 => HpsdrSampleRate.Rate96k,
        192_000 => HpsdrSampleRate.Rate192k,
        384_000 => HpsdrSampleRate.Rate384k,
        _ => throw new ArgumentException($"Unsupported sample rate {hz}.", nameof(hz)),
    };
}

internal static class HpsdrSampleRateExtensions
{
    public static int SampleRateHz(this HpsdrSampleRate rate) => rate switch
    {
        HpsdrSampleRate.Rate48k => 48_000,
        HpsdrSampleRate.Rate96k => 96_000,
        HpsdrSampleRate.Rate192k => 192_000,
        HpsdrSampleRate.Rate384k => 384_000,
        _ => throw new ArgumentOutOfRangeException(nameof(rate), rate, null),
    };
}
