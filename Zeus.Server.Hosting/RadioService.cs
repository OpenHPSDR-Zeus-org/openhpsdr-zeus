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
using Zeus.Protocol2;

namespace Zeus.Server;

public sealed class RadioService : IDisposable
{
    private const int DefaultHpsdrPort = 1024;
    internal const double DefaultAgcTopDb = 80.0;

    private readonly object _sync = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RadioService> _log;
    private readonly DspSettingsStore _dspSettingsStore;
    private readonly PaSettingsStore _paStore;
    private readonly PreferredRadioStore? _preferredRadioStore;
    private readonly PsSettingsStore? _psStore;
    private readonly FilterPresetStore? _filterPresetStore;
    private readonly RadioStateStore? _radioStateStore;
    // Cached PS board key for the currently-connected radio. Set by
    // ApplyPsHwPeakForConnection (P1 or P2 connect path) and read by
    // PersistPsState to route HW Peak writes to the correct per-board slot.
    // Empty when nothing is connected — PersistPsState skips HW Peak persistence
    // in that case (no board → no slot to write).
    private string _currentPsBoardKey = string.Empty;
    // Mirror of the persisted PS TX feedback attenuation (dB) for the
    // currently-connected board. Loaded from TxAttnByBoard on connect
    // (GetPersistedPsTxAttnDb), updated by SetPsTxAttenuationDb when the
    // auto-attenuate dance (or a manual control) settles on a value, and
    // written back by PersistPsState. -1 = "no value for this board yet" →
    // PersistPsState leaves the slot untouched so we never clobber a good
    // saved value with a default.
    private int _currentPsTxAttnDb = -1;
    // Debounced state flush. Set to true in every Mutate(); a 1 Hz timer
    // calls FlushState() which writes to LiteDB and clears the flag.
    // Avoids hammering LiteDB during rapid VFO scroll or filter drags.
    private volatile bool _stateDirty;
    private readonly System.Threading.Timer? _stateFlushTimer;
    // Last-known preset name per mode, preserved across mode switches.
    // RX2 keeps its own cache so VFO B top-bar edits do not affect what VFO A
    // restores on its next mode change.
    // Accessed only from inside Mutate (under _sync) or at init.
    private readonly Dictionary<RxMode, string?> _lastPresetPerMode = new();
    private readonly Dictionary<RxMode, string?> _lastPresetPerModeB = new();
    // Last-commanded slider value in UI percent (0..100). Needed here because
    // the drive byte depends on three inputs — percent, per-band PA gain, and
    // global max-watts — any of which can change independently. When a band
    // edge is crossed or a PA setting is edited, we recompute without needing
    // to wait for the next SetDrive call.
    private int _drivePct;
    // On-board CW keyer config (C&C 0x0B), forwarded to the connected P1
    // client and re-pushed on reconnect. Seeded from CwSettingsStore in the
    // ctor; updated at runtime via SetCwKeyerConfig from the CW settings
    // endpoint. Default mode 0 (straight) is safe — see zeus-bks.
    private int _cwKeyerWpm;
    private int _cwKeyerMode;
    // Independent TUN drive %. When TUN is keyed, the recompute uses this in
    // place of _drivePct so the operator can pre-set a lower tune level (and
    // the same per-band PA gain gives equal watts at equal percentages). piHPSDR
    // default is 10 — a 0 default would be "press TUN, nothing happens".
    private int _tunePct = 10;
    // TX pre-key (MOX) delay ms (0..500). Authoritative copy read by TxService
    // on the MOX rising edge to arm the IQ-mute window; the StateDto mirror is
    // for the frontend. Always kept strictly below the PS MOX hold-off so PS
    // never calibrates on muted RF — see SetTxMoxPreKeyDelayMs / ClampPreKeyToPs.
    // Issue #630.
    private int _txMoxPreKeyDelayMs;
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
    private Protocol2Client? _p2Client;
    // Discovered board kind for the active P2 connection. Set by
    // MarkProtocol2Connected from the connect-API request byte (issue #171 —
    // Brick2 is Hermes-on-P2, not OrionMkII). Unknown when the caller didn't
    // supply a byte, in which case ConnectedBoardKind falls back to OrionMkII
    // for backward compat.
    private HpsdrBoardKind _p2BoardKind = HpsdrBoardKind.Unknown;
    private bool _preampOn;
    // Manual notch filters (MNF). Authoritative on the server so notches
    // survive reconnects and backend restarts (a fresh engine starts with an empty WDSP notch DB);
    // not on the StateDto wire format — DspPipelineService reads Notches and
    // listens to NotchesChanged to push them to the engine. Guarded by _sync.
    private List<NotchDto> _notches = new();
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
    private byte _lastAdcOverloadBits;
    private ushort? _lastAdc0MaxMagnitude;
    private ushort? _lastAdc1MaxMagnitude;
    private ushort _adc0MaxMagnitudeAtOverload;
    private ushort _adc1MaxMagnitudeAtOverload;
    private DateTimeOffset? _lastAdcTelemetryUtc;
    private AdcProtectionConfig _adcProtection = new();
    private long _lastTickMs = long.MinValue;
    private int _lastAppliedEffectiveDb = -1;   // so the first send always fires

    // Auto-AGC control-loop state. Adjusts AGC-T (the WDSP max-gain cap) so
    // the band noise floor lands near a fixed reference, mirroring Thetis'
    // noise-floor calibration. Closed-looping on the live S-meter (the prior
    // implementation) ramped to max gain on quiet bands and clipped when
    // signal returned.
    private const int AgcNoiseFloorWindowSamples = 12; // 12 × 500 ms = 6 s
    private const double AgcNoiseFloorPercentile = 0.20;
    private const double AgcDeadbandDb = 1.5;
    private const double AgcSlewPerTickDb = 0.5;
    private const double AgcMinEffectiveAgcT = 20.0;
    private const double AgcMaxEffectiveAgcT = 100.0;
    private const double AgcCutStressThresholdDb = -10.0;
    private const double AgcCutTargetDb = -8.0;
    private const double AgcAdcPressureThresholdDbfs = -6.0;
    private double _agcOffsetDb;
    private long _lastAgcTickMs = long.MinValue;
    private readonly double[] _noiseFloorWindow = new double[AgcNoiseFloorWindowSamples];
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

    /// <summary>Fires when the DDC sample rate (display bandwidth) changes. P1
    /// is pushed directly via ActiveClient?.SetSampleRate inside SetSampleRate;
    /// this event is the only path that reaches a live Protocol2Client (whose
    /// RX-spec carries the rate) AND lets DspPipelineService re-open the WDSP RX
    /// channel at the new input rate so demod + panadapter axis follow. Without
    /// it, a P2 bandwidth change updated state but never reached the radio
    /// (ActiveClient is P1-only / null on P2). Carries the new rate in Hz.</summary>
    public event Action<int>? SampleRateChanged;

    /// <summary>Raised when the manual-notch list changes. DspPipelineService
    /// forwards the new set to the live DSP engine (WDSP notch database).</summary>
    public event Action<IReadOnlyList<NotchDto>>? NotchesChanged;

    /// <summary>Current manual-notch set. DspPipelineService reads this on a
    /// fresh-engine connect to re-apply notches the new WDSP channel lost.</summary>
    public IReadOnlyList<NotchDto> Notches { get { lock (_sync) return _notches.ToArray(); } }

    // Shared TX IQ source threaded through Protocol1Client. TxAudioIngest
    // writes into the same instance; this is the seam between "mic arrived
    // over WS" and "EP2 packet got real IQ". When null the client falls back
    // to its internal test-tone generator (dev / tests without a hub).
    private readonly Zeus.Protocol1.ITxIqSource? _txIqSource;

    public RadioService(ILoggerFactory loggerFactory, DspSettingsStore dspSettingsStore, PaSettingsStore paStore, FilterPresetStore? filterPresetStore = null, Zeus.Protocol1.ITxIqSource? txIqSource = null, PreferredRadioStore? preferredRadioStore = null, PsSettingsStore? psStore = null, RadioStateStore? radioStateStore = null, CwSettingsStore? cwSettingsStore = null, TxAudioProfileStore? txAudioProfileStore = null)
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
        // Seed the on-board CW keyer config from persisted settings so a
        // reconnect after restart re-applies the operator's mode/speed
        // before they touch the panel — otherwise a paddle op who saved
        // iambic would key as straight (default) on first connect. See
        // zeus-bks.
        if (cwSettingsStore is not null)
        {
            var cw = cwSettingsStore.Get();
            Volatile.Write(ref _cwKeyerWpm, cw.Wpm);
            Volatile.Write(ref _cwKeyerMode, (int)cw.KeyerMode);
        }

        // Load persisted DSP settings from the store, or use defaults if not found
        var persistedNr = NormalizeNrConfig(_dspSettingsStore.Get() ?? new NrConfig());
        // CFC — issue #123. Persisted globally; null on a fresh install or
        // legacy DB row falls back to the default-OFF baseline so the operator
        // sees no behaviour change unless they enable.
        var persistedCfc = _dspSettingsStore.GetCfc() ?? CfcConfig.Default;
        // AGC mode + custom params. Null on a fresh install / legacy DB row
        // falls back to the Med default so first-connect behaviour is unchanged.
        var persistedAgc = _dspSettingsStore.GetAgc() ?? new AgcConfig(AgcMode.Med);
        // RX squelch. Null on a fresh install / legacy DB row falls back to the
        // off default so first-connect behaviour is unchanged (Thetis §5).
        var persistedSquelch = _dspSettingsStore.GetSquelch() ?? new SquelchConfig();
        // TX leveling. Null on a fresh install / legacy DB row falls back to the
        // TxLevelingConfig defaults so first-connect behaviour is unchanged
        // (Thetis §6.1-6.3). The Leveler max-gain stays on LevelerMaxGainDb.
        var persistedTxLeveling = _dspSettingsStore.GetTxLeveling() ?? new TxLevelingConfig();

        // TX Audio Profile startup overlay. If the operator has a "last loaded"
        // unified TX Audio Profile, its scalar/config values overlay the
        // per-setting stores BEFORE _state is built, so the radio comes up on
        // that profile rather than the last ad-hoc live values. The heavier
        // chain/plugin-state replay runs later via TxAudioProfileService.
        // StartAsync. When there is NO last-loaded id (fresh install / never
        // used profiles) nothing is overlaid — byte-identical to current
        // defaults. PureSignal and every excluded field are untouched.
        int? overlayMicGain = null;
        double? overlayLevelerMaxGain = null;
        int? overlayTxFilterLow = null, overlayTxFilterHigh = null;
        if (txAudioProfileStore is not null)
        {
            var lastId = txAudioProfileStore.GetLastLoadedId();
            var lastProfile = string.IsNullOrWhiteSpace(lastId) ? null : txAudioProfileStore.Get(lastId);
            if (lastProfile is not null)
            {
                persistedCfc = lastProfile.CfcConfig ?? persistedCfc;
                persistedTxLeveling = lastProfile.TxLeveling ?? persistedTxLeveling;
                overlayMicGain = Math.Clamp(lastProfile.MicGainDb, -40, 10);
                overlayLevelerMaxGain = Math.Clamp(lastProfile.LevelerMaxGainDb, 0.0, 20.0);
                // Re-sign the operator-typed positive magnitudes for the startup
                // mode so the TX bandpass comes up correctly.
                int loAbs = Math.Min(Math.Abs(lastProfile.LowCutHz), Math.Abs(lastProfile.HighCutHz));
                int hiAbs = Math.Max(Math.Abs(lastProfile.LowCutHz), Math.Abs(lastProfile.HighCutHz));
                var startupMode = radioStateStore?.Get()?.Mode ?? RxMode.USB;
                var (sLo, sHi) = SignedFilterForMode(startupMode, loAbs, hiAbs);
                overlayTxFilterLow = sLo;
                overlayTxFilterHigh = sHi;
            }
        }

        // Seed the last-preset cache from persisted store for all modes so
        // the first mode-switch in a session recalls the correct slot.
        if (filterPresetStore != null)
        {
            foreach (RxMode m in Enum.GetValues<RxMode>())
            {
                _lastPresetPerMode[m] = filterPresetStore.GetLastSelectedPreset(m);
                _lastPresetPerModeB[m] = _lastPresetPerMode[m];
            }
        }

        // Load persisted PS settings — operator's calibration tuning and
        // standing master-arm preference. Actual transmit/keying actions
        // (MOX/TUN/TwoToneEnabled) still reset each session. PsHwPeak is resolved
        // per-radio in ApplyPsHwPeakForConnection (called from
        // ConnectAsync / ConnectP2Async), which prefers the persisted
        // per-board value when present and falls back to the factory
        // default otherwise.
        var ps = _psStore?.Get();

        // RadioStateStore snapshot — hydrates active mode/VFO/filter/volume/zoom
        // and the per-mode-family filter memory. Null on first run; falls through
        // to the hardcoded defaults below. Snapshot wins for the fields it knows
        // about; existing domain stores (DspSettings, PsSettings, etc.) still
        // hydrate the wider config they own.
        var rsSnap = _radioStateStore?.Get();
        _adcProtection = NormalizeAdcProtection(new AdcProtectionConfig(
            Enabled: rsSnap?.AutoAttEnabled ?? true,
            AttackMs: rsSnap?.AdcProtectionAttackMs ?? 100,
            ReleaseMs: rsSnap?.AdcProtectionReleaseMs ?? 100,
            AttackStepDb: rsSnap?.AdcProtectionAttackStepDb ?? 1,
            ReleaseStepDb: rsSnap?.AdcProtectionReleaseStepDb ?? 1,
            MaxOffsetDb: rsSnap?.AdcProtectionMaxOffsetDb ?? 31,
            WarningThreshold: rsSnap?.AdcProtectionWarningThreshold ?? 3,
            MagnitudeSoftLimit: rsSnap?.AdcProtectionMagnitudeSoftLimit ?? 0));

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
            _preampOn = rsSnap.PreampOn;
            _notches = rsSnap.Notches
                .Select(n => new NotchDto(n.CenterHz, n.WidthHz, n.Active, NormalizeNotchSource(n.Source)))
                .ToList();
            _drivePct = Math.Clamp(rsSnap.DrivePct, 0, 100);
            _tunePct = Math.Clamp(rsSnap.TunePct, 0, 100);
            // Hydrate the TX pre-key delay, then clamp it below the persisted PS
            // MOX hold-off so a hand-edited DB row can't break the invariant.
            _txMoxPreKeyDelayMs = ClampPreKeyToPs(
                Math.Clamp(rsSnap.TxMoxPreKeyDelayMs, 0, MaxPreKeyDelayMs),
                ps?.MoxDelaySec ?? 0.2);
        }

        _state = new(
            Status: ConnectionStatus.Disconnected,
            Endpoint: null,
            VfoHz: rsSnap?.VfoHz ?? 14_200_000,
            Mode: rsSnap?.Mode ?? RxMode.USB,
            FilterLowHz: rsSnap?.FilterLowHz ?? 100,
            FilterHighHz: rsSnap?.FilterHighHz ?? 2850,
            SampleRate: 192_000,    // set at connect time; not in global snapshot
            // Thetis / WDSP AGC_MEDIUM baseline. This gives the RX AGC enough
            // headroom to normalize weak post-demod audio immediately after a
            // fresh start. Operator overrides persist via
            // DspSettingsStore.SetAgcTopDb so deliberate lower AGC-T settings
            // still stick across restarts.
            AgcTopDb: _dspSettingsStore.GetAgcTopDb() ?? DefaultAgcTopDb,
            Agc: persistedAgc,
            Squelch: persistedSquelch,
            TxLeveling: persistedTxLeveling,
            AttenDb: rsSnap?.AttenDb ?? 0,
            Nr: persistedNr,
            ZoomLevel: rsSnap?.ZoomLevel ?? 1,
            AutoAttEnabled: _adcProtection.Enabled,
            AttOffsetDb: 0,         // always reset — control-loop accumulator
            AdcOverloadWarning: false,
            FilterPresetName: rsSnap?.FilterPresetName ?? "VAR1",
            FilterAdvancedPaneOpen: filterPresetStore?.GetAdvancedPaneOpen() ?? false,
            TxFilterLowHz: overlayTxFilterLow ?? rsSnap?.TxFilterLowHz ?? 150,
            TxFilterHighHz: overlayTxFilterHigh ?? rsSnap?.TxFilterHighHz ?? 2850,
            RxAfGainDb: rsSnap?.RxAfGainDb ?? 0.0,
            // 0 dB unity matches the engine's TXA fresh-open default; legacy
            // rows missing the field hydrate to that same default. A last-loaded
            // TX Audio Profile overlays its mic gain ahead of the snapshot.
            MicGainDb: overlayMicGain ?? Math.Clamp(rsSnap?.MicGainDb ?? 0, -40, 10),
            // 8.0 dB matches WdspDspEngine.DefaultLevelerMaxGainDb. Clamp range
            // widened to 0..20 for Thetis parity (radio.cs leveler top 0..20).
            LevelerMaxGainDb: overlayLevelerMaxGain ?? Math.Clamp(rsSnap?.LevelerMaxGainDb ?? 8.0, 0.0, 20.0),
            AutoAgcEnabled: rsSnap?.AutoAgcEnabled ?? false,
            AgcOffsetDb: 0.0,       // always reset — control-loop accumulator
            // PS persisted fields (or DTO defaults when not persisted yet).
            PsEnabled: ps?.Enabled ?? false,
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
            Cfc: persistedCfc,
            // Hydrate drive sliders from RadioStateStore so a fresh frontend
            // connect lands on the operator's last-set values. The private
            // fields above (_drivePct / _tunePct) were already hydrated in the
            // rsSnap block; mirror them into the StateDto so SetDrive doesn't
            // become the only path that puts these into the broadcast.
            DrivePct: Volatile.Read(ref _drivePct),
            TunePct: Volatile.Read(ref _tunePct),
            TxMoxPreKeyDelayMs: Volatile.Read(ref _txMoxPreKeyDelayMs),
            // Hardware NCO — persisted in RadioStateStore so a restart resumes
            // on the same physical centre. RadioLoHz snaps to VfoHz on legacy
            // rows (RadioLoHz==0 — e.g. rows written by the old CTUN-off
            // branch) so the panadapter centre is never zero on a fresh
            // hydration. CTUN behaviour (frozen NCO, dial roams) is now
            // unconditional — see docs/prd/panfall_behavior.md.
            RadioLoHz: (rsSnap?.RadioLoHz ?? 0L) != 0L
                ? rsSnap!.RadioLoHz
                : (rsSnap?.VfoHz ?? 14_200_000),
            Rx2Enabled: rsSnap?.Rx2Enabled ?? false,
            VfoBHz: (rsSnap?.VfoBHz ?? 0L) != 0L
                ? rsSnap!.VfoBHz
                : (rsSnap?.VfoHz ?? 14_200_000),
            ModeB: rsSnap?.ModeB ?? rsSnap?.Mode ?? RxMode.USB,
            FilterLowHzB: rsSnap?.FilterLowHzB ?? rsSnap?.FilterLowHz ?? 100,
            FilterHighHzB: rsSnap?.FilterHighHzB ?? rsSnap?.FilterHighHz ?? 2850,
            FilterPresetNameB: rsSnap?.FilterPresetNameB ?? rsSnap?.FilterPresetName ?? "VAR1",
            Rx2AudioMode: rsSnap?.Rx2AudioMode ?? Zeus.Contracts.Rx2AudioMode.Both,
            Rx2AfGainDb: Math.Clamp(rsSnap?.Rx2AfGainDb ?? 0.0, -50.0, 20.0),
            TxVfo: rsSnap?.TxVfo ?? TxVfo.A,
            CwPitchHz: CwOffset.CwPitchHz,
            CtunEnabled: rsSnap?.CtunEnabled ?? false,
            PreampOn: rsSnap?.PreampOn ?? false);

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
    /// PsEnabled is persisted as the operator's standing PS preference.
    /// TwoToneEnabled remains session-only because it can key the transmitter.
    /// PsHwPeak IS persisted per-connected-board via the HwPeakByBoard dictionary;
    /// when no board is currently connected the HwPeak portion of the write
    /// is skipped (existing per-board entries are preserved untouched).
    /// </summary>
    private void PersistPsState()
    {
        if (_psStore is null) return;
        var snap = Snapshot();
        // Preserve any existing per-board HW Peak map and only mutate the
        // slot owned by the currently-connected radio. Reset / disconnect
        // paths set _currentPsBoardKey back to empty, which skips the HW
        // Peak write entirely — operators don't lose other-board entries.
        var existing = _psStore.Get();
        var hwPeakByBoard = existing?.HwPeakByBoard is { } map
            ? new Dictionary<string, double>(map)
            : new Dictionary<string, double>();
        if (!string.IsNullOrEmpty(_currentPsBoardKey))
        {
            hwPeakByBoard[_currentPsBoardKey] = snap.PsHwPeak;
        }
        // Same per-board preserve-then-mutate as HW Peak. Only write the TX
        // attenuation slot when we actually have a value for the connected
        // board (>= 0); otherwise carry the existing map through untouched so
        // a HW-Peak-triggered persist never wipes a saved attenuation.
        var txAttnByBoard = existing?.TxAttnByBoard is { } amap
            ? new Dictionary<string, int>(amap)
            : new Dictionary<string, int>();
        if (!string.IsNullOrEmpty(_currentPsBoardKey) && _currentPsTxAttnDb >= 0)
        {
            txAttnByBoard[_currentPsBoardKey] = _currentPsTxAttnDb;
        }
        _psStore.Upsert(new PsSettingsEntry
        {
            Enabled = snap.PsEnabled,
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
            HwPeakByBoard = hwPeakByBoard,
            TxAttnByBoard = txAttnByBoard,
        });
    }

    /// <summary>
    /// Record + persist the PS TX feedback attenuation the auto-attenuate
    /// dance (or a manual operator control) settled on, for the currently-
    /// connected board. Restored to the radio on the next connect by
    /// DspPipelineService so a hot external-tap feedback chain doesn't boot
    /// at 0 dB and re-saturate the feedback ADC. No-op persistence when no
    /// board is connected (no slot to write).
    /// </summary>
    public void SetPsTxAttenuationDb(int db)
    {
        _currentPsTxAttnDb = db;
        PersistPsState();
        // Surface the live value so the PURESIGNAL panel's manual control and
        // the "differs" hint track what's actually applied.
        Mutate(s => s.PsTxFeedbackAttenuationDb == db ? s : s with { PsTxFeedbackAttenuationDb = db });
    }

    /// <summary>
    /// Persisted PS TX feedback attenuation (dB) for the currently-connected
    /// board, or null if none has been saved yet. Called on connect to
    /// restore the radio's feedback attenuation before the operator arms PS.
    /// Side effect: seeds <see cref="_currentPsTxAttnDb"/> so a later
    /// PersistPsState preserves the slot rather than treating it as unset.
    /// </summary>
    public int? GetPersistedPsTxAttnDb()
    {
        if (string.IsNullOrEmpty(_currentPsBoardKey)) return null;
        var persisted = _psStore?.Get();
        if (persisted?.TxAttnByBoard is { } map
            && map.TryGetValue(_currentPsBoardKey, out int db))
        {
            _currentPsTxAttnDb = db;
            return db;
        }
        return null;
    }

    /// <summary>
    /// Build the per-board PS settings key used by PsSettingsEntry.HwPeakByBoard.
    /// Format: `{p1|p2}:{board}[:variant]` where the variant suffix is only
    /// present when board is `OrionMkII` and we're on P2 (the 0x0A wire-byte
    /// alias family — G2, G2_1K, Anan7000DLE, Anan8000DLE, OrionMkII original,
    /// AnvelinaPro3, RedPitaya — each has a distinct feedback chain).
    /// </summary>
    internal static string GetPsBoardKey(bool isProtocol2, HpsdrBoardKind board, OrionMkIIVariant variant)
    {
        string proto = isProtocol2 ? "p2" : "p1";
        if (isProtocol2 && board == HpsdrBoardKind.OrionMkII)
            return $"{proto}:{board}:{variant}";
        return $"{proto}:{board}";
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

    internal int ResolveConnectSampleRateHz(HpsdrBoardKind discoveredKind, int requestedHz, bool protocol2)
    {
        int requested = MapSampleRate(requestedHz).SampleRateHz();
        var board = ResolveBoardKindForPreferences(
            discoveredKind,
            protocol2 ? HpsdrBoardKind.OrionMkII : HpsdrBoardKind.Unknown);
        int maxHz = MaxAllowedSampleRateHz(board, protocol2);
        if (requested > maxHz)
        {
            _log.LogWarning(
                "radio.connect requested sample-rate {Requested} exceeds board={Board} max={Max}; clamping",
                requested, board, maxHz);
            requested = maxHz;
        }

        if (_radioStateStore is null || board == HpsdrBoardKind.Unknown)
            return requested;

        var storedHz = _radioStateStore.GetBoardSampleRate(board, EffectiveOrionMkIIVariant);
        if (!storedHz.HasValue)
            return requested;

        var stored = MapSampleRate(storedHz.Value).SampleRateHz();
        if (stored > maxHz)
        {
            _log.LogWarning(
                "radio.connect sample-rate store has rate={Rate} above board={Board} max={Max}; using requested rate={Requested}",
                stored, board, maxHz, requested);
            return requested;
        }

        return stored;
    }

    private int MaxAllowedSampleRateHz(HpsdrBoardKind board, bool protocol2)
    {
        var boardMax = BoardCapabilitiesTable
            .For(board, EffectiveOrionMkIIVariant)
            .MaxRxSampleRateHz;
        return protocol2
            ? boardMax
            : Math.Min(boardMax, 384_000);
    }

    private HpsdrBoardKind ResolveBoardKindForPreferences(HpsdrBoardKind discoveredKind, HpsdrBoardKind fallbackKind)
    {
        if (_preferredRadioStore?.GetOverrideDetection() == true)
        {
            var preferred = _preferredRadioStore.Get();
            if (preferred.HasValue && preferred.Value != HpsdrBoardKind.Unknown)
                return preferred.Value;
        }

        return discoveredKind != HpsdrBoardKind.Unknown
            ? discoveredKind
            : fallbackKind;
    }

    public async Task<StateDto> ConnectAsync(string endpoint, int sampleRate, CancellationToken ct = default,
        HpsdrBoardKind discoveredKind = HpsdrBoardKind.Unknown)
    {
        if (!TryParseEndpoint(endpoint, out var ipEndpoint))
            throw new ArgumentException($"Invalid endpoint '{endpoint}'.", nameof(endpoint));

        // This is the Protocol 1 connect path (it constructs a Protocol1Client).
        // P1's 2-bit rate field caps at 384 kHz; 768/1536 kHz are Protocol 2 only,
        // so reject rather than silently wrap on the wire.
        if (sampleRate > 384_000)
            throw new ArgumentException(
                $"Protocol 1 supports up to 384 kHz; {sampleRate} Hz requires Protocol 2.", nameof(sampleRate));

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
            // Plumb the discovered board byte so ConnectedBoardKind returns
            // the real board rather than the Protocol1Client default
            // (HermesLite2). Without this, an ANAN-10E (Hermes, 0x01) is
            // treated as HL2 for PA calibration / drive profile — issue #294.
            if (discoveredKind != HpsdrBoardKind.Unknown)
                client.SetBoardKind(discoveredKind);
            int restoredHz = ResolveConnectSampleRateHz(client.BoardKind, hpsdrRate.SampleRateHz(), protocol2: false);
            if (restoredHz != hpsdrRate.SampleRateHz())
            {
                hpsdrRate = MapSampleRate(restoredHz);
                lock (_sync)
                    _state = _state with { SampleRate = hpsdrRate.SampleRateHz() };
            }
            await client.StartAsync(new StreamConfig(hpsdrRate, _preampOn, _atten), ct).ConfigureAwait(false);
            // Retune the radio to the persisted hardware NCO (RadioLoHz). The
            // dial (VfoHz) may sit elsewhere; WDSP's shift stage covers the
            // gap. Hydration above already guarantees RadioLoHz != 0 by
            // snapping to VfoHz on legacy rows, so a plain SetVfoAHz here is
            // always valid. See docs/prd/panfall_behavior.md.
            var connectSnap = Snapshot();
            client.SetVfoAHz(connectSnap.RadioLoHz);

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

            // Frequency-correction factor (issue #325) — rehydrate so the
            // first tune-write on the fresh client carries the operator's
            // calibrated correction. Cheap default when no calibration has
            // run (factor = 1.0).
            if (_preferredRadioStore is not null)
            {
                client.SetFrequencyCorrectionFactor(_preferredRadioStore.GetFrequencyCorrectionFactor());
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
            // Push the persisted CW keyer config into the fresh client so the
            // on-board iambic keyer matches the operator's panel before the
            // first key-down. Default mode straight makes this a no-op until
            // iambic is opted into. See zeus-bks.
            client.SetCwKeyerConfig(Volatile.Read(ref _cwKeyerWpm), (CwKeyerMode)Volatile.Read(ref _cwKeyerMode));
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
        // Drop the PS board key — any SetPsAdvanced call between now and the
        // next connect (e.g. operator dialling in the panel while
        // disconnected) should NOT write into the previous radio's slot.
        // ApplyPsHwPeakForConnection sets it again on next connect.
        _currentPsBoardKey = string.Empty;
        // Same for the TX-attn mirror — next connect re-seeds it from the
        // persisted slot via GetPersistedPsTxAttnDb.
        _currentPsTxAttnDb = -1;
        return Snapshot();
    }

    public StateDto SetVfo(long hz) => SetVfo(hz, fromExternal: false);

    public StateDto SetVfoB(long hz)
    {
        long clamped = Math.Clamp(hz, 0L, 60_000_000L);
        long previousTx;
        lock (_sync) previousTx = TxFrequencyHz(_state);
        Mutate(s => s with { VfoBHz = clamped });
        if (BandUtils.FreqToBand(previousTx) != BandUtils.FreqToBand(TxFrequencyHz(Snapshot())))
        {
            RecomputePaAndPush();
        }
        return Snapshot();
    }

    public StateDto SetRx2(Rx2SetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        long previousTx;
        lock (_sync) previousTx = TxFrequencyHz(_state);
        Mutate(s =>
        {
            long nextVfoB = req.VfoBHz.HasValue
                ? Math.Clamp(req.VfoBHz.Value, 0L, 60_000_000L)
                : s.VfoBHz > 0
                    ? s.VfoBHz
                    : s.VfoHz;
            var nextMode = req.AudioMode ?? s.Rx2AudioMode;
            double nextGain = req.AfGainDb.HasValue
                ? Math.Clamp(req.AfGainDb.Value, -50.0, 20.0)
                : s.Rx2AfGainDb;
            bool nextEnabled = req.Enabled ?? s.Rx2Enabled;
            return s with
            {
                Rx2Enabled = nextEnabled,
                VfoBHz = nextVfoB,
                Rx2AudioMode = nextMode,
                Rx2AfGainDb = nextGain,
            };
        });
        if (BandUtils.FreqToBand(previousTx) != BandUtils.FreqToBand(TxFrequencyHz(Snapshot())))
        {
            RecomputePaAndPush();
        }
        return Snapshot();
    }

    public StateDto SetTxVfo(TxVfo txVfo)
    {
        if (!Enum.IsDefined(txVfo))
            throw new ArgumentOutOfRangeException(nameof(txVfo), txVfo, "Unknown TX VFO");
        long previousTx;
        lock (_sync) previousTx = TxFrequencyHz(_state);
        Mutate(s => s.TxVfo == txVfo ? s : s with { TxVfo = txVfo });
        var snap = Snapshot();
        if (BandUtils.FreqToBand(previousTx) != BandUtils.FreqToBand(TxFrequencyHz(snap)))
        {
            RecomputePaAndPush();
        }
        return snap;
    }

    public static long TxFrequencyHz(StateDto state) =>
        state.TxVfo == TxVfo.B ? state.VfoBHz : state.VfoHz;

    public static long TxEffectiveLoHz(StateDto state) =>
        CwOffset.EffectiveLoHz(state.Mode, TxFrequencyHz(state));

    public StateDto SwapVfos()
    {
        long previousTx = 0;
        long newA = 0;
        RxMode mode = RxMode.USB;
        Mutate(s =>
        {
            previousTx = TxFrequencyHz(s);
            newA = Math.Clamp(s.VfoBHz, 0L, 60_000_000L);
            mode = s.Mode;
            return s with
            {
                VfoHz = newA,
                VfoBHz = Math.Clamp(s.VfoHz, 0L, 60_000_000L),
                RadioLoHz = CwOffset.EffectiveLoHz(mode, newA),
            };
        });
        ActiveClient?.SetVfoAHz(CwOffset.EffectiveLoHz(mode, newA));
        if (BandUtils.FreqToBand(previousTx) != BandUtils.FreqToBand(TxFrequencyHz(Snapshot())))
        {
            RecomputePaAndPush();
        }
        return Snapshot();
    }

    /// <summary>
    /// Set the VFO (dial) frequency.
    ///
    /// <para><b>CTUN on</b> (<see cref="StateDto.CtunEnabled"/>): the operator
    /// click-tunes off the panadapter centre. We move only the dial and leave
    /// the hardware NCO (<c>RadioLoHz</c>) frozen — DspPipelineService
    /// recomputes the WDSP shift stage (= EffectiveLoHz(mode, vfo) − RadioLoHz)
    /// off the StateChanged event so the tuned signal still lands at baseband
    /// for RX. TX retunes the shared VFO register to the dial on key-down
    /// (<see cref="SetMox"/> → <see cref="AlignLoForTx"/>) and restores the
    /// frozen centre on un-key, so the radio transmits on the dial — the fix
    /// for the #470 revert. The frozen NCO is kept only while the dial stays
    /// inside the captured IQ window; once the requested shift would exceed the
    /// IF capacity (≈ ±0.45×sample_rate) the signal is no longer in the
    /// sampled spectrum, so we fall through to a classic recenter.</para>
    ///
    /// <para><b>CTUN off</b>: classic "radio follows the dial" — every tune
    /// retunes the hardware NCO so the clicked frequency becomes the new
    /// centre (RadioLoHz = dial's effective LO, WDSP shift = 0).</para>
    ///
    /// External sources (CAT/TCI/calibration, <paramref name="fromExternal"/>
    /// =true) always recenter regardless of CTUN — they expect "radio follows
    /// the dial" (Thetis <c>CATChangesCenterFreq=true</c>). Mirrors Thetis
    /// <c>ClickTuneDisplay</c> (console.cs:43143).
    /// </summary>
    public StateDto SetVfo(long hz, bool fromExternal)
    {
        long clamped = Math.Clamp(hz, 0L, 60_000_000L);
        long previous;
        RxMode currentMode;
        bool ctun;
        long currentLo;
        int sampleRate;
        lock (_sync)
        {
            previous = _state.VfoHz;
            currentMode = _state.Mode;
            ctun = _state.CtunEnabled;
            currentLo = _state.RadioLoHz;
            sampleRate = _state.SampleRate;
        }

        // CTUN: dial roams, NCO frozen — as long as the tuned signal stays
        // inside the captured IQ window (±IF capacity). A panadapter click
        // always resolves to an on-screen frequency, and the visible span is
        // ⊆ the sample window, so clicks never trip the guard; only wheel /
        // keyboard / typed tuning can push the dial out, and that recenters.
        if (ctun && !fromExternal)
        {
            long shiftHz = CwOffset.EffectiveLoHz(currentMode, clamped) - currentLo;
            long ifCapHz = (long)(sampleRate * 0.45);
            if (Math.Abs(shiftHz) <= ifCapHz)
            {
                Mutate(s => s with { VfoHz = clamped });
                if (BandUtils.FreqToBand(previous) != BandUtils.FreqToBand(clamped))
                {
                    RecomputePaAndPush();
                }
                return Snapshot();
            }
            // Dial left the IQ window — fall through to recenter so the radio
            // keeps demodulating (Thetis snaps the display when the click
            // leaves the span). RadioLoHz follows the dial below.
        }

        // Classic recenter (CTUN off, CTUN out-of-window, or external source):
        // retune the hardware NCO to the dial's effective LO (CW: dial ∓
        // pitch), which leaves the WDSP CTUN-shift stage at zero.
        long radioLoNew = CwOffset.EffectiveLoHz(currentMode, clamped);
        Mutate(s => s with { VfoHz = clamped, RadioLoHz = radioLoNew });
        ActiveClient?.SetVfoAHz(radioLoNew);
        // Band edge crossed? Per-band PA gain / OC bits may have swapped — push
        // the new snapshot before the next TX frame ships. Cheap when no
        // crossing occurred (same bytes re-pushed).
        if (BandUtils.FreqToBand(previous) != BandUtils.FreqToBand(clamped))
        {
            RecomputePaAndPush();
        }
        return Snapshot();
    }

    /// <summary>
    /// Set the radio's hardware NCO (LO) centre frequency in Hz, leaving
    /// VfoHz untouched. Returns the updated <see cref="StateDto"/>.
    /// Out-of-range values are clamped to [0, 60_000_000]; callers wanting
    /// strict rejection should validate before calling. Triggers a P1 client
    /// SetVfoAHz (and the P2 path via DspPipelineService.OnRadioStateChanged
    /// reading the new RadioLoHz), and a PA recompute if the LO crossed a
    /// band edge. WDSP's shift stage is updated by DspPipelineService so the
    /// dial-relative demodulation remains correct.
    /// </summary>
    public StateDto SetRadioLo(long hz)
    {
        long clamped = Math.Clamp(hz, 0L, 60_000_000L);
        long previous;
        lock (_sync) { previous = _state.RadioLoHz; }
        Mutate(s => s with { RadioLoHz = clamped });
        ActiveClient?.SetVfoAHz(clamped);
        if (BandUtils.FreqToBand(previous) != BandUtils.FreqToBand(clamped))
        {
            RecomputePaAndPush();
        }
        return Snapshot();
    }

    /// <summary>
    /// Force the hardware LO to the canonical CW-mode offset of the
    /// displayed VFO (LO = VFO − pitch for CWU, LO = VFO + pitch for CWL)
    /// so a CW transmission lands the carrier on the dial. No-op for non-CW
    /// modes and when the LO is already aligned.
    ///
    /// Background: CTUN (centred tuning) lets the operator click-tune the
    /// panadapter without moving the hardware LO; <see cref="SetVfo"/>
    /// updates only the displayed VFO and lets WDSP's shift stage move the
    /// signal to baseband. That trick works fine for RX, but TX shares the
    /// same physical NCO — if we keyed the radio while CTUN was active, the
    /// carrier would land at <c>LO ± pitch</c> in real RF, not at the dial.
    /// The host-side CW engine calls this before each transmission to
    /// guarantee the operator-tuned freq is what reaches the antenna; the
    /// pattern is the TCI-equivalent of "external retune" — see issue
    /// <c>zeus-drf</c> bench notes (2026-05-24).
    ///
    /// Returns true when the LO was actually moved (caller may want to
    /// log it for diagnostics), false on no-op.
    /// </summary>
    // Remembered RX centre while keyed under CTUN, so RestoreLoAfterTx() can
    // put the frozen NCO back on un-key. long.MinValue == "not in a TX cycle"
    // (or CTUN off — only CTUN records). Guarded by _sync.
    private long _ctunPreTxLoHz = long.MinValue;

    // Capture the receive centre exactly once per key-down when TX must move
    // the shared radio LO away from the current RX view. That is CTUN and TX B.
    // Caller must hold _sync.
    private void RememberFrozenLoUnderLock()
    {
        if ((_state.CtunEnabled || _state.TxVfo == TxVfo.B) && _ctunPreTxLoHz == long.MinValue)
            _ctunPreTxLoHz = _state.RadioLoHz;
    }

    public bool AlignLoForCwTx()
    {
        long vfo;
        RxMode mode;
        long currentLo;
        lock (_sync)
        {
            vfo = TxFrequencyHz(_state);
            mode = _state.Mode;
            currentLo = _state.RadioLoHz;
            RememberFrozenLoUnderLock();
        }
        if (mode != RxMode.CWU && mode != RxMode.CWL) return false;
        long targetLo = CwOffset.EffectiveLoHz(mode, vfo);
        if (targetLo == currentLo) return false;
        SetRadioLo(targetLo);
        return true;
    }

    /// <summary>
    /// CTUN TX alignment for all modes (the phone/digi analogue of
    /// <see cref="AlignLoForCwTx"/>). When CTUN froze the hardware NCO off the
    /// dial for RX, the shared P1/P2 VFO register would otherwise transmit on
    /// the frozen centre — the #470 bug. Called from <see cref="SetMox"/> on
    /// the key-down edge: snap the hardware LO to the dial's effective LO so
    /// the carrier lands on frequency, remembering the frozen centre for
    /// <see cref="RestoreLoAfterTx"/> to put back on un-key. No-op when CTUN is
    /// off (classic tuning already keeps LO == dial). Mirrors Thetis, which
    /// writes VFOAFreq to the NCO on MOX and restores CentreFrequency on RX
    /// (console.cs UpdateTXDDSFreq / HdwMOXChanged). Returns true if the LO
    /// moved.
    /// </summary>
    public bool AlignLoForTx()
    {
        long vfo;
        RxMode mode;
        long currentLo;
        lock (_sync)
        {
            if (!_state.CtunEnabled && _state.TxVfo != TxVfo.B) return false;
            vfo = TxFrequencyHz(_state);
            mode = _state.Mode;
            currentLo = _state.RadioLoHz;
            RememberFrozenLoUnderLock();
        }
        long targetLo = CwOffset.EffectiveLoHz(mode, vfo);
        if (targetLo == currentLo) return false;
        SetRadioLo(targetLo);
        return true;
    }

    /// <summary>
    /// Restore the frozen RX centre remembered by <see cref="AlignLoForTx"/> /
    /// <see cref="AlignLoForCwTx"/>. Called from <see cref="SetMox"/> on the
    /// un-key edge so the panadapter returns to the same off-centre CTUN view
    /// the operator had before transmitting. No-op when nothing was recorded
    /// (CTUN off, or the LO was already on the dial). Returns true if the LO
    /// moved.
    /// </summary>
    public bool RestoreLoAfterTx()
    {
        long restore;
        lock (_sync)
        {
            if (_ctunPreTxLoHz == long.MinValue) return false;
            restore = _ctunPreTxLoHz;
            _ctunPreTxLoHz = long.MinValue;
        }
        SetRadioLo(restore);
        return true;
    }

    /// <summary>
    /// Enable or disable CTUN (click-tune / centred tuning). Enabling simply
    /// freezes the hardware NCO at its current value (which already equals the
    /// dial's effective LO, so nothing moves) — subsequent <see cref="SetVfo"/>
    /// calls leave it put. Disabling snaps the NCO back to the dial so the
    /// panadapter recentres and classic "radio follows the dial" resumes.
    /// Persisted via FlushState.
    /// </summary>
    public StateDto SetCtunEnabled(bool enabled)
    {
        long vfo;
        RxMode mode;
        bool changed;
        lock (_sync)
        {
            changed = _state.CtunEnabled != enabled;
            vfo = _state.VfoHz;
            mode = _state.Mode;
        }
        if (!changed) return Snapshot();
        // Mutate marks the state dirty (so FlushState persists the toggle) and
        // fires StateChanged.
        Mutate(s => s with { CtunEnabled = enabled });
        if (!enabled)
        {
            // Turning CTUN off: recentre the NCO on the dial (mirrors a classic
            // SetVfo). SetRadioLo fires StateChanged so the WDSP shift drops to
            // zero and the frontend frames recentre.
            SetRadioLo(CwOffset.EffectiveLoHz(mode, vfo));
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
    private FamilyFilter _ssbFilterB = new(150, 2850);
    private FamilyFilter _amFilterB = new(0, 4000);
    private FamilyFilter _fmFilterB = new(0, 5500);
    private FamilyFilter _cwFilterB = new(475, 725);

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

    public StateDto SetMode(RxMode mode) => SetMode(mode, TxVfo.A);

    public StateDto SetMode(RxMode mode, TxVfo receiver)
    {
        if (!Enum.IsDefined(receiver))
            throw new ArgumentOutOfRangeException(nameof(receiver), receiver, "Unknown VFO receiver");

        RxMode departingMode = default;
        string? departingPreset = null;
        long newVfoAHz = 0;
        bool targetBAtSet = false;
        Mutate(s =>
        {
            bool targetB = receiver == TxVfo.B && s.Rx2Enabled;
            targetBAtSet = targetB;
            var currentMode = targetB ? s.ModeB : s.Mode;
            var currentPreset = targetB ? s.FilterPresetNameB : s.FilterPresetName;
            var currentFilterLow = targetB ? s.FilterLowHzB : s.FilterLowHz;
            var currentFilterHigh = targetB ? s.FilterHighHzB : s.FilterHighHz;

            departingMode = currentMode;
            departingPreset = currentPreset;

            // Save departing mode's preset name to the in-memory cache.
            var presetCache = targetB ? _lastPresetPerModeB : _lastPresetPerMode;
            presetCache[currentMode] = currentPreset;

            // 1) Save current abs-filter into the mode we are LEAVING.
            int curLoAbs = Math.Min(Math.Abs(currentFilterLow), Math.Abs(currentFilterHigh));
            int curHiAbs = Math.Max(Math.Abs(currentFilterLow), Math.Abs(currentFilterHigh));
            StoreFamilyFilter(currentMode, curLoAbs, curHiAbs, targetB ? TxVfo.B : TxVfo.A);
            if (!targetB)
            {
                int curTxLoAbs = Math.Min(Math.Abs(s.TxFilterLowHz), Math.Abs(s.TxFilterHighHz));
                int curTxHiAbs = Math.Max(Math.Abs(s.TxFilterLowHz), Math.Abs(s.TxFilterHighHz));
                StoreTxFamilyFilter(currentMode, curTxLoAbs, curTxHiAbs);
            }

            // 2) Look up the target family's remembered filter (RX + TX).
            var fam = FamilyFilterFor(mode, targetB ? TxVfo.B : TxVfo.A);

            // 3) Re-sign per target mode's sideband convention.
            var (lo, hi) = SignedFilterForMode(mode, fam.LoAbs, fam.HiAbs);

            // 4) Restore the last-known preset name for the incoming mode.
            presetCache.TryGetValue(mode, out var restoredPreset);

            // 5) Thetis-style dial bump on SSB↔CW transitions so the
            //    effective LO doesn't jump under the operator's feet — the
            //    dial absorbs the ±cw_pitch step and the radio stays on the
            //    same physical signal. Within CWU↔CWL the dial stays put
            //    (Thetis console.cs:34037-34052, 34203-34298 mirrored here).
            //    Non-CW↔non-CW transitions return 0, so SSB/AM/FM/DIG
            //    behaviour is unchanged.
            long bump = CwOffset.DialBumpForModeTransition(currentMode, mode);
            long nextVfoA = targetB ? s.VfoHz : Math.Clamp(s.VfoHz + bump, 0L, 60_000_000L);
            long nextVfoB = targetB ? Math.Clamp(s.VfoBHz + bump, 0L, 60_000_000L) : s.VfoBHz;
            newVfoAHz = nextVfoA;

            if (targetB)
            {
                return s with
                {
                    ModeB = mode,
                    VfoBHz = nextVfoB,
                    FilterLowHzB = lo,
                    FilterHighHzB = hi,
                    FilterPresetNameB = restoredPreset,
                };
            }

            var txFam = TxFamilyFilterFor(mode);
            var (txLo, txHi) = SignedFilterForMode(mode, txFam.LoAbs, txFam.HiAbs);

            return s with
            {
                Mode = mode,
                VfoHz = nextVfoA,
                VfoBHz = nextVfoB,
                FilterLowHz = lo, FilterHighHz = hi,
                TxFilterLowHz = txLo, TxFilterHighHz = txHi,
                FilterPresetName = restoredPreset,
            };
        });

        // Persist the departing mode's last preset outside the lock.
        if (departingPreset != null && !targetBAtSet)
            _filterPresetStore?.UpsertLastSelectedPreset(departingMode, departingPreset);

        // Push the new effective LO. Even with no dial bump, switching
        // into/out of CW changes EffectiveLoHz by ±cw_pitch and the radio
        // needs the new tuning before the next IQ block arrives. P2 is
        // pushed via DspPipelineService.OnRadioStateChanged.
        if (!targetBAtSet)
            ActiveClient?.SetVfoAHz(CwOffset.EffectiveLoHz(mode, newVfoAHz));

        return Snapshot();
    }

    public StateDto SetFilter(int lowHz, int highHz, string? presetName = null)
        => SetFilter(lowHz, highHz, presetName, TxVfo.A);

    public StateDto SetFilter(int lowHz, int highHz, string? presetName, TxVfo receiver)
    {
        if (!Enum.IsDefined(receiver))
            throw new ArgumentOutOfRangeException(nameof(receiver), receiver, "Unknown VFO receiver");
        if (highHz < lowHz) (lowHz, highHz) = (highHz, lowHz);
        RxMode modeAtSet = RxMode.USB;
        string? resolvedName = presetName;
        bool targetBAtSet = false;
        Mutate(s =>
        {
            bool targetB = receiver == TxVfo.B && s.Rx2Enabled;
            targetBAtSet = targetB;
            modeAtSet = targetB ? s.ModeB : s.Mode;
            // Normalize the slot name: if (low,high) exactly matches a non-VAR
            // preset for this mode, use that slot's name regardless of what the
            // caller passed. Prevents dual selection where a stored VAR happens
            // to equal a standard preset width and edges.
            var match = FilterPresets.DefaultsForMode(modeAtSet)
                .FirstOrDefault(e => !e.IsVar && e.LowHz == lowHz && e.HighHz == highHz);
            if (match is not null) resolvedName = match.SlotName;
            if (resolvedName != null)
            {
                var presetCache = targetB ? _lastPresetPerModeB : _lastPresetPerMode;
                presetCache[modeAtSet] = resolvedName;
            }
            int loAbs = Math.Min(Math.Abs(lowHz), Math.Abs(highHz));
            int hiAbs = Math.Max(Math.Abs(lowHz), Math.Abs(highHz));
            StoreFamilyFilter(modeAtSet, loAbs, hiAbs, targetB ? TxVfo.B : TxVfo.A);
            if (targetB)
                return s with { FilterLowHzB = lowHz, FilterHighHzB = highHz, FilterPresetNameB = resolvedName };
            return s with { FilterLowHz = lowHz, FilterHighHz = highHz, FilterPresetName = resolvedName };
        });
        if (resolvedName != null && !targetBAtSet)
            _filterPresetStore?.UpsertLastSelectedPreset(modeAtSet, resolvedName);
        FlushState();
        return Snapshot();
    }

    // TX bandpass filter setter. Signed pair like SetFilter — caller is
    // expected to have already re-signed positive (abs) values per the current
    // mode's sideband convention. DspPipelineService picks up the state-change
    // and forwards to the engine via IDspEngine.SetTxFilter.
    public StateDto SetTxFilter(int lowHz, int highHz)
    {
        if (highHz < lowHz) (lowHz, highHz) = (highHz, lowHz);
        Mutate(s =>
        {
            int loAbs = Math.Min(Math.Abs(lowHz), Math.Abs(highHz));
            int hiAbs = Math.Max(Math.Abs(lowHz), Math.Abs(highHz));
            StoreTxFamilyFilter(s.Mode, loAbs, hiAbs);
            return s with { TxFilterLowHz = lowHz, TxFilterHighHz = highHz };
        });
        FlushState();
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
        => StoreFamilyFilter(mode, loAbs, hiAbs, TxVfo.A);

    private void StoreFamilyFilter(RxMode mode, int loAbs, int hiAbs, TxVfo receiver)
    {
        var slot = new FamilyFilter(loAbs, hiAbs);
        bool targetB = receiver == TxVfo.B;
        switch (mode)
        {
            case RxMode.USB: case RxMode.LSB: case RxMode.DIGU: case RxMode.DIGL:
                if (targetB) _ssbFilterB = slot; else _ssbFilter = slot; break;
            case RxMode.AM: case RxMode.SAM: case RxMode.DSB:
                if (targetB) _amFilterB = slot; else _amFilter = slot; break;
            case RxMode.FM:
                if (targetB) _fmFilterB = slot; else _fmFilter = slot; break;
            case RxMode.CWL: case RxMode.CWU:
                if (targetB) _cwFilterB = slot; else _cwFilter = slot; break;
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

    private FamilyFilter FamilyFilterFor(RxMode mode, TxVfo receiver)
    {
        if (receiver != TxVfo.B)
            return FamilyFilterFor(mode);

        return mode switch
        {
            RxMode.USB or RxMode.LSB or RxMode.DIGU or RxMode.DIGL => _ssbFilterB,
            RxMode.AM or RxMode.SAM or RxMode.DSB => _amFilterB,
            RxMode.FM => _fmFilterB,
            RxMode.CWL or RxMode.CWU => _cwFilterB,
            _ => _ssbFilterB,
        };
    }

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
        // Protocol 1 encodes the rate in 2 bits (ControlFrame masks &0x03), so it
        // cannot represent 768/1536 kHz — clamp on the P1 path so a stray request
        // can't silently wrap to 48/96 kHz. Protocol 2 (ActiveClient is null;
        // rate carried as u16 kHz) takes the full 48..1536 kHz ladder.
        bool protocol2;
        bool protocol1;
        lock (_sync)
        {
            protocol1 = _activeClient is not null;
            protocol2 = _p2Active;
        }

        if (protocol1 && rate > HpsdrSampleRate.Rate384k)
        {
            _log.LogWarning(
                "radio.setSampleRate rate={Rate} unsupported on Protocol 1; clamping to 384k", rate);
            rate = HpsdrSampleRate.Rate384k;
        }
        var board = ConnectedBoardKind;
        int maxHz = MaxAllowedSampleRateHz(board, protocol2 && !protocol1);
        if (rate.SampleRateHz() > maxHz)
        {
            _log.LogWarning(
                "radio.setSampleRate rate={Rate} exceeds board={Board} max={Max}; clamping",
                rate, board, maxHz);
            rate = MapSampleRate(maxHz);
        }
        int hz = rate.SampleRateHz();
        Mutate(s => s with { SampleRate = hz });
        // P1 client owns the rate bits directly. On P2 ActiveClient is null, so
        // the SampleRateChanged event is the only way the new rate reaches the
        // live Protocol2Client and re-rates the WDSP RX channel (issue: live
        // bandwidth change was a no-op on P2 / G2 before this).
        ActiveClient?.SetSampleRate(rate);
        SampleRateChanged?.Invoke(hz);
        if (board != HpsdrBoardKind.Unknown)
            _radioStateStore?.SetBoardSampleRate(board, hz, EffectiveOrionMkIIVariant);
        return Snapshot();
    }

    public StateDto SetPreamp(bool on)
    {
        Mutate(s =>
        {
            _preampOn = on;
            return s with { PreampOn = on };
        });
        // P1 path: Protocol1Client owns the bit; SetPreamp pushes the
        // updated CcState on the next outgoing frame. ActiveClient is
        // null on a P2 connection, so the PreampChanged event below is
        // what carries the bit into Protocol2Client (issue #126).
        ActiveClient?.SetPreamp(on);
        PreampChanged?.Invoke(on);
        FlushState();
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
        bool changed = false;
        lock (_sync)
        {
            if (_state.AutoAttEnabled == enabled) return _state;
            changed = true;
            _adcProtection = _adcProtection with { Enabled = enabled };
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
            else
            {
                _lastTickMs = long.MinValue;
            }
        }
        var snap = Snapshot();
        if (changed)
        {
            _stateDirty = true;
            FlushState();
        }
        StateChanged?.Invoke(snap);
        return snap;
    }

    public AdcProtectionStatusDto GetAdcProtectionStatus()
    {
        lock (_sync) return BuildAdcProtectionStatusNoLock();
    }

    public AdcProtectionStatusDto SetAdcProtection(AdcProtectionSetRequest req)
    {
        int? effectiveToApply = null;
        bool stateBroadcastNeeded = false;
        AdcProtectionStatusDto status;

        lock (_sync)
        {
            var next = NormalizeAdcProtection(new AdcProtectionConfig(
                Enabled: req.Enabled ?? _adcProtection.Enabled,
                AttackMs: req.AttackMs ?? _adcProtection.AttackMs,
                ReleaseMs: req.ReleaseMs ?? _adcProtection.ReleaseMs,
                AttackStepDb: req.AttackStepDb ?? _adcProtection.AttackStepDb,
                ReleaseStepDb: req.ReleaseStepDb ?? _adcProtection.ReleaseStepDb,
                MaxOffsetDb: req.MaxOffsetDb ?? _adcProtection.MaxOffsetDb,
                WarningThreshold: req.WarningThreshold ?? _adcProtection.WarningThreshold,
                MagnitudeSoftLimit: req.MagnitudeSoftLimit ?? _adcProtection.MagnitudeSoftLimit));

            if (next != _adcProtection)
            {
                _adcProtection = next;
                _lastTickMs = long.MinValue;
                _stateDirty = true;
            }

            if (_state.AutoAttEnabled != next.Enabled)
            {
                _state = _state with { AutoAttEnabled = next.Enabled };
                stateBroadcastNeeded = true;
                _stateDirty = true;
            }

            if (!next.Enabled)
            {
                _attOffsetDb = 0;
                _adcOverloadLevel = 0;
                _overloadSeenInWindow = false;
                if (_state.AttOffsetDb != 0 || _state.AdcOverloadWarning)
                {
                    _state = _state with { AttOffsetDb = 0, AdcOverloadWarning = false };
                    stateBroadcastNeeded = true;
                    _stateDirty = true;
                }
            }
            else if (_attOffsetDb > next.MaxOffsetDb)
            {
                _attOffsetDb = next.MaxOffsetDb;
                _state = _state with { AttOffsetDb = _attOffsetDb };
                stateBroadcastNeeded = true;
                _stateDirty = true;
            }

            int effective = Math.Clamp(_atten.ClampedDb + _attOffsetDb, HpsdrAtten.MinDb, HpsdrAtten.MaxDb);
            if (effective != _lastAppliedEffectiveDb)
            {
                _lastAppliedEffectiveDb = effective;
                effectiveToApply = effective;
            }

            status = BuildAdcProtectionStatusNoLock();
        }

        if (effectiveToApply is int eff)
        {
            ActiveClient?.SetAttenuator(new HpsdrAtten(eff));
        }

        if (_stateDirty) FlushState();
        if (stateBroadcastNeeded) StateChanged?.Invoke(Snapshot());
        return status;
    }

    private AdcProtectionStatusDto BuildAdcProtectionStatusNoLock()
    {
        int effective = Math.Clamp(_atten.ClampedDb + _attOffsetDb, HpsdrAtten.MinDb, HpsdrAtten.MaxDb);
        return new(
            Config: _adcProtection with { Enabled = _state.AutoAttEnabled },
            AttenDb: _atten.ClampedDb,
            OffsetDb: _attOffsetDb,
            EffectiveDb: effective,
            Warning: _state.AdcOverloadWarning,
            OverloadLevel: _adcOverloadLevel,
            LastOverloadBits: _lastAdcOverloadBits,
            Adc0MaxMagnitude: _lastAdc0MaxMagnitude,
            Adc1MaxMagnitude: _lastAdc1MaxMagnitude,
            Adc0MaxMagnitudeAtOverload: _adc0MaxMagnitudeAtOverload,
            Adc1MaxMagnitudeAtOverload: _adc1MaxMagnitudeAtOverload,
            LastTelemetryUtc: _lastAdcTelemetryUtc);
    }

    private static AdcProtectionConfig NormalizeAdcProtection(AdcProtectionConfig config) => config with
    {
        AttackMs = Math.Clamp(config.AttackMs, 25, 1_000),
        ReleaseMs = Math.Clamp(config.ReleaseMs, 50, 5_000),
        AttackStepDb = Math.Clamp(config.AttackStepDb, 1, 6),
        ReleaseStepDb = Math.Clamp(config.ReleaseStepDb, 1, 6),
        MaxOffsetDb = Math.Clamp(config.MaxOffsetDb, HpsdrAtten.MinDb, HpsdrAtten.MaxDb),
        WarningThreshold = Math.Clamp(config.WarningThreshold, 0, 5),
        MagnitudeSoftLimit = Math.Clamp(config.MagnitudeSoftLimit, 0, ushort.MaxValue),
    };

    public StateDto SetAutoAgc(bool enabled)
    {
        bool changed = false;
        lock (_sync)
        {
            if (_state.AutoAgcEnabled == enabled) return _state;
            changed = true;
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
        if (changed)
        {
            _stateDirty = true;
            FlushState();
        }
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
    internal void HandleRxMeterForAutoAgc(double signalDbm, long nowMs) =>
        HandleRxMetersForAutoAgc(signalDbm, double.NaN, double.NaN, nowMs);

    internal void HandleRxMetersForAutoAgc(double signalDbm, double adcPkDbfs, double agcGainDb, long nowMs)
    {
        bool changedOffset = false;
        double newOffset = 0.0;
        double noiseFloor = double.NaN;

        lock (_sync)
        {
            if (!_state.AutoAgcEnabled) return;
            if (_mox) return;   // Pause during TX
            bool hasSignalMeter = double.IsFinite(signalDbm) && signalDbm > -250.0;
            bool hasAgcGain = double.IsFinite(agcGainDb) && agcGainDb > -199.5;
            bool hasAdcPeak = double.IsFinite(adcPkDbfs) && adcPkDbfs > -199.5;
            bool adcUnderPressure = hasAdcPeak && adcPkDbfs > AgcAdcPressureThresholdDbfs;
            if (!hasSignalMeter && !hasAgcGain) return;

            // If we paused for longer than the analysis window (TX,
            // just-toggled-on, RX dropout) the
            // window may hold stale samples — clear before re-accumulating.
            if (_lastAgcTickMs != long.MinValue && nowMs - _lastAgcTickMs > AgcNoiseFloorWindowSamples * 500)
            {
                _noiseFloorWindowFill = 0;
                _noiseFloorWindowIdx = 0;
            }

            if (_lastAgcTickMs != long.MinValue && nowMs - _lastAgcTickMs < 500)
                return;
            _lastAgcTickMs = nowMs;

            if (hasSignalMeter)
            {
                _noiseFloorWindow[_noiseFloorWindowIdx] = signalDbm;
                _noiseFloorWindowIdx = (_noiseFloorWindowIdx + 1) % _noiseFloorWindow.Length;
                if (_noiseFloorWindowFill < _noiseFloorWindow.Length) _noiseFloorWindowFill++;
            }

            bool windowReady = _noiseFloorWindowFill >= _noiseFloorWindow.Length;
            double desiredOffset = _agcOffsetDb;
            if (windowReady)
            {
                var sorted = new double[_noiseFloorWindowFill];
                for (int i = 0; i < _noiseFloorWindowFill; i++)
                    sorted[i] = _noiseFloorWindow[i];
                Array.Sort(sorted);
                int floorIndex = Math.Clamp(
                    (int)Math.Round((sorted.Length - 1) * AgcNoiseFloorPercentile),
                    0,
                    sorted.Length - 1);
                noiseFloor = sorted[floorIndex];

                // Auto-AGC chooses an *absolute* effective AGC-T from the noise
                // floor: place the band noise at TargetAudioDb after WDSP's
                // max-gain stage, so a quiet band lands at ~80 dB and a noisy
                // band lands at ~50 dB regardless of where the user parked the
                // slider baseline. The offset is whatever it takes to reach that
                // absolute target on top of the current AgcTopDb baseline.
                const double TargetAudioDb = -40.0;     // desired audio-output noise level
                double targetEffective = Math.Clamp(
                    TargetAudioDb - noiseFloor, AgcMinEffectiveAgcT, AgcMaxEffectiveAgcT);
                // Preserve weak/quiet-band behavior: noise-floor tracking can
                // add live gain, but it does not lower the user's baseline.
                // Normal WDSP AGC reduction is the loudness normalizer; only
                // ADC pressure below is allowed to pull AGC-T down.
                desiredOffset = Math.Max(0.0, targetEffective - _state.AgcTopDb);
            }
            else if (_agcOffsetDb < 0.0 && (!hasAgcGain || agcGainDb >= AgcCutStressThresholdDb || !adcUnderPressure))
            {
                desiredOffset = 0.0;
            }

            if (hasAgcGain && agcGainDb < AgcCutStressThresholdDb && adcUnderPressure)
            {
                double effectiveNow = _state.AgcTopDb + _agcOffsetDb;
                double adcUrgencyDb = Math.Min(6.0, adcPkDbfs + 6.0);
                double targetEffective = Math.Clamp(
                    effectiveNow + (agcGainDb - AgcCutTargetDb) - adcUrgencyDb,
                    AgcMinEffectiveAgcT,
                    AgcMaxEffectiveAgcT);
                desiredOffset = Math.Min(desiredOffset, targetEffective - _state.AgcTopDb);
            }

            double delta = desiredOffset - _agcOffsetDb;
            if (Math.Abs(delta) < AgcDeadbandDb) return;

            _agcOffsetDb = delta > 0
                ? Math.Min(desiredOffset, _agcOffsetDb + AgcSlewPerTickDb)
                : Math.Max(desiredOffset, _agcOffsetDb - AgcSlewPerTickDb);

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
        // CTUN: the hardware NCO is frozen off the dial for RX. Snap it to the
        // dial before the wire MOX bit flips so TX lands on frequency, and
        // restore the frozen centre after un-key so the RX view returns. This
        // is the universal keying chokepoint — MOX, TUN, CW, and two-tone all
        // route through here — so every TX path is covered. Both helpers are
        // no-ops when CTUN is off. (CW pre-aligns in CwEngine for its baseband
        // calc; AlignLoForTx then finds the LO already on the dial and is a
        // no-op, but the frozen centre it recorded is still restored below.)
        if (on) AlignLoForTx();
        lock (_sync) _mox = on;
        ActiveClient?.SetMox(on);
        MoxChanged?.Invoke(on);
        if (!on) RestoreLoAfterTx();
    }

    // Drive is transient like MOX — latched on the Protocol1Client so the
    // DriveFilter register on the next outgoing frame carries it. We clamp
    // here rather than at the endpoint so every entry point (REST, future
    // CAT bridge, tests) gets the same range guarantee.
    public void SetDrive(int percent)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        Interlocked.Exchange(ref _drivePct, clamped);
        // Mutate() broadcasts the new StateDto to subscribed clients and
        // flips _stateDirty so the debounce flush persists to LiteDB. Without
        // the broadcast a fresh client connect would not see the hydrated
        // value until something else dirtied the state.
        Mutate(s => s with { DrivePct = clamped });
        RecomputePaAndPush();
    }

    // ---- TX pre-key (MOX) delay (issue #630) -----------------------------
    // Max operator-settable pre-key delay. Thetis RF-Delay parity range.
    internal const int MaxPreKeyDelayMs = 500;
    // Safety margin the pre-key window must stay below the PS MOX hold-off by,
    // so the IQ mute is fully open before WDSP calcc can leave LMOXDELAY and
    // start binning feedback samples. A pre-key window that outlasts the PS
    // hold-off would let PS collect zero-envelope samples → COLLECT never
    // completes → the documented PS calibration stall. 50 ms is comfortably
    // longer than one WDSP TX block at any supported rate.
    private const int PsPreKeyMarginMs = 50;

    // Clamp a requested pre-key delay to [0, MaxPreKeyDelayMs] AND strictly
    // below (psMoxDelaySec*1000 - margin). With the default PS hold-off of
    // 200 ms this caps the pre-key at 150 ms — ample for amp T/R sequencing
    // (the #630 reporter needs ~30 ms) while keeping PS safe by construction.
    private static int ClampPreKeyToPs(int requestedMs, double psMoxDelaySec)
    {
        int ceiling = (int)(psMoxDelaySec * 1000.0) - PsPreKeyMarginMs;
        if (ceiling < 0) ceiling = 0;
        if (ceiling > MaxPreKeyDelayMs) ceiling = MaxPreKeyDelayMs;
        return Math.Clamp(requestedMs, 0, ceiling);
    }

    /// <summary>
    /// Set the TX pre-key (MOX) delay in milliseconds. Clamped to
    /// [0, <see cref="MaxPreKeyDelayMs"/>] and hard-clamped strictly below the
    /// current PureSignal MOX hold-off (bidirectional invariant — the PS setter
    /// re-clamps this downward too). Returns the updated snapshot so the caller
    /// can surface the actually-applied value (which may be lower than asked).
    /// </summary>
    public StateDto SetTxMoxPreKeyDelayMs(int ms)
    {
        int clamped = ClampPreKeyToPs(ms, Snapshot().PsMoxDelaySec);
        Interlocked.Exchange(ref _txMoxPreKeyDelayMs, clamped);
        Mutate(s => s with { TxMoxPreKeyDelayMs = clamped });
        return Snapshot();
    }

    /// <summary>Authoritative pre-key delay (ms) read by TxService on the MOX
    /// rising edge. Already PS-clamped.</summary>
    public int TxMoxPreKeyDelayMs => Volatile.Read(ref _txMoxPreKeyDelayMs);

    // Re-clamp the stored pre-key delay after the PS MOX hold-off changed, so
    // lowering PsMoxDelaySec can never leave a now-too-large pre-key window in
    // place. Called from SetPsAdvanced after the PS mutate commits.
    private void ReclampPreKeyToPs()
    {
        int current = Volatile.Read(ref _txMoxPreKeyDelayMs);
        int reclamped = ClampPreKeyToPs(current, Snapshot().PsMoxDelaySec);
        if (reclamped != current)
        {
            Interlocked.Exchange(ref _txMoxPreKeyDelayMs, reclamped);
            Mutate(s => s with { TxMoxPreKeyDelayMs = reclamped });
        }
    }

    /// <summary>
    /// Forward the on-board CW keyer config (speed + mode) to the connected
    /// radio's C&amp;C register 0x0B, and remember it so a reconnect re-applies
    /// it. Called by the CW settings endpoint whenever the operator changes
    /// WPM or keyer mode. No-op (cached only) when no radio is connected.
    /// See zeus-bks.
    /// </summary>
    public void SetCwKeyerConfig(int wpm, CwKeyerMode mode)
    {
        Volatile.Write(ref _cwKeyerWpm, wpm);
        Volatile.Write(ref _cwKeyerMode, (int)mode);
        ActiveClient?.SetCwKeyerConfig(wpm, mode);
    }

    // Independent TUN drive %. Applies on the very next frame if TUN is already
    // keyed; otherwise it sits until TxService flips _tunActive.
    public void SetTuneDrive(int percent)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        Interlocked.Exchange(ref _tunePct, clamped);
        Mutate(s => s with { TunePct = clamped });
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

    private bool SupportsG2AdcOptions(HpsdrBoardKind board, OrionMkIIVariant variant) =>
        BoardCapabilitiesTable.For(board, variant).SupportsG2AdcOptions;

    private (bool Supported, bool DitherEnabled, bool RandomEnabled, bool Rx1AttenuatorSupported, int Rx1AttenuatorDb)
        ResolveG2AdcOptionsFor(
        HpsdrBoardKind board,
        OrionMkIIVariant variant)
    {
        var caps = BoardCapabilitiesTable.For(board, variant);
        bool supported = caps.SupportsG2AdcOptions;
        bool dither = supported && (_preferredRadioStore?.GetG2AdcDitherEnabled() ?? true);
        bool random = supported && (_preferredRadioStore?.GetG2AdcRandomEnabled() ?? true);
        bool rx1AttenuatorSupported = supported && caps.HasSteppedAttenuationRx2;
        int rx1AttenuatorDb = rx1AttenuatorSupported
            ? Math.Clamp(_preferredRadioStore?.GetG2Rx1AttenuatorDb() ?? 0, 0, 31)
            : 0;
        return (supported, dither, random, rx1AttenuatorSupported, rx1AttenuatorDb);
    }

    public (bool Supported, bool DitherEnabled, bool RandomEnabled, bool Rx1AttenuatorSupported, int Rx1AttenuatorDb)
        ResolveG2AdcOptionsForWire(
        HpsdrBoardKind connectedBoard)
    {
        var board = connectedBoard != HpsdrBoardKind.Unknown
            ? connectedBoard
            : EffectiveBoardKind;
        return ResolveG2AdcOptionsFor(board, EffectiveOrionMkIIVariant);
    }

    public G2OptionsDto GetG2Options()
    {
        var options = ResolveG2AdcOptionsFor(EffectiveBoardKind, EffectiveOrionMkIIVariant);
        return new G2OptionsDto(
            DitherEnabled: _preferredRadioStore?.GetG2AdcDitherEnabled() ?? true,
            RandomEnabled: _preferredRadioStore?.GetG2AdcRandomEnabled() ?? true,
            MaxRxFreqMHz: 60.0,
            Supported: options.Supported,
            Rx1AttenuatorDb: _preferredRadioStore?.GetG2Rx1AttenuatorDb() ?? 0,
            Rx1AttenuatorMinDb: 0,
            Rx1AttenuatorMaxDb: 31,
            Rx1AttenuatorSupported: options.Rx1AttenuatorSupported);
    }

    public G2OptionsDto SetG2Options(G2OptionsSetRequest req)
    {
        _preferredRadioStore?.SetG2AdcOptions(req.DitherEnabled, req.RandomEnabled, req.Rx1AttenuatorDb);
        var options = GetG2Options();
        ApplyG2AdcOptionsToP2Client(_p2Client, ConnectedBoardKind);
        return options;
    }

    public void ApplyG2AdcOptionsToP2Client(Protocol2Client? client, HpsdrBoardKind connectedBoard)
    {
        if (client is null) return;
        var options = ResolveG2AdcOptionsForWire(connectedBoard);
        client.SetAdcDitherRandom(options.DitherEnabled, options.RandomEnabled);
        client.SetRx1Attenuator(options.Rx1AttenuatorSupported ? options.Rx1AttenuatorDb : 0);
    }

    /// <summary>
    /// Raised after the operator's frequency-correction factor (issue #325)
    /// has been persisted and pushed to the active P1 client. P2 subscribers
    /// (<c>DspPipelineService</c>) use this to forward the new factor to the
    /// live <see cref="Zeus.Protocol2.Protocol2Client"/>, since
    /// <see cref="ActiveClient"/> is always null in P2 mode.
    /// </summary>
    public event Action<double>? FrequencyCorrectionFactorChanged;

    /// <summary>
    /// Reads the per-radio frequency-correction factor (issue #325). 1.0
    /// when no store is wired or no calibration has been run.
    /// </summary>
    public double GetFrequencyCorrectionFactor() =>
        _preferredRadioStore?.GetFrequencyCorrectionFactor() ?? 1.0;

    /// <summary>
    /// Persists the frequency-correction factor, pushes it to the live P1
    /// client (if any), raises <see cref="FrequencyCorrectionFactorChanged"/>
    /// for the P2 listener, and re-pushes the current dial VFO so the new
    /// factor reaches the wire immediately. Clamps to ±100 ppm
    /// (factor ∈ [0.9999, 1.0001]) — matches piHPSDR's range and is far
    /// wider than any crystal-stabilised HPSDR board needs.
    /// </summary>
    public double SetFrequencyCorrectionFactor(double factor)
    {
        if (double.IsNaN(factor) || double.IsInfinity(factor))
            throw new ArgumentException("factor must be a finite real number", nameof(factor));
        double clamped = Math.Clamp(factor, 0.9999, 1.0001);

        // Write-through to persistent storage first so a crash between the
        // store write and the live-client push can't lose the calibration.
        _preferredRadioStore?.SetFrequencyCorrectionFactor(clamped);
        _activeClient?.SetFrequencyCorrectionFactor(clamped);
        FrequencyCorrectionFactorChanged?.Invoke(clamped);

        // Re-push the current dial Hz so the new factor lands on the wire.
        // SetVfo's CwOffset application + ActiveClient push handle P1; the
        // FrequencyCorrectionFactorChanged event handler in DspPipelineService
        // covers the P2 client.
        long currentDial = Snapshot().VfoHz;
        SetVfo(currentDial);

        return clamped;
    }

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
        var txHz = TxFrequencyHz(stateSnap);
        var bandName = BandUtils.FreqToBand(txHz);
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
            "pa.recompute tunActive={Tun} pct={Pct} txVfo={TxVfo} txHz={TxHz} band={Band} gainDb={Gain:F2} maxW={Max} profile={Profile} -> byte={Byte} paEn={PaEn} ocTx=0x{OcTx:X2} ocRx=0x{OcRx:X2} ocDxTx=0x{OcDxTx:X2} ocDxRx=0x{OcDxRx:X2}",
            tunActive, activePct, stateSnap.TxVfo, txHz, bandName ?? "?", bandCfg.PaGainDb, cfg.Global.PaMaxPowerWatts, driveProfile.BoardLabel, driveByte, paEnabled,
            bandCfg.OcTx, bandCfg.OcRx, bandCfg.OcDxTx, bandCfg.OcDxRx);

        ActiveClient?.SetDriveByte(driveByte);
        ActiveClient?.SetOcMasks(bandCfg.OcTx, bandCfg.OcRx);

        PaSnapshotChanged?.Invoke(new PaRuntimeSnapshot(
            DriveByte: driveByte,
            OcTxMask: bandCfg.OcTx,
            OcRxMask: bandCfg.OcRx,
            PaEnabled: paEnabled,
            // Anvelina-PRO3 DX OC masks (issue #407) — always emitted in
            // the snapshot so DspPipelineService can forward them to the
            // Protocol2Client. The wire-encode in SendCmdHighPriority is
            // gated by board+variant, so non-Anvelina radios receive a
            // SetOcDxMasks call but the bytes never reach the wire.
            OcDxTxMask: bandCfg.OcDxTx,
            OcDxRxMask: bandCfg.OcDxRx));
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

    // TX mic gain in dB. Server-clamped to [-40, +10] to match the endpoint
    // contract and Thetis's MicGainMin/Max defaults. The dB → linear (10^(db/20))
    // conversion happens at the engine seam in DspPipelineService so the wire
    // and persisted form is the operator-friendly integer.
    public StateDto SetTxMicGain(int db)
    {
        int clamped = Math.Clamp(db, -40, 10);
        Mutate(s => s with { MicGainDb = clamped });
        return Snapshot();
    }

    // TX Leveler max-gain ceiling in dB. Server-clamped to [0, 20] for Thetis
    // parity (radio.cs leveler top range 0..20); previously 0..15.
    public StateDto SetTxLevelerMaxGain(double db)
    {
        double clamped = Math.Clamp(db, 0.0, 20.0);
        Mutate(s => s with { LevelerMaxGainDb = clamped });
        return Snapshot();
    }

    public StateDto SetNr(NrConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var normalized = NormalizeNrConfig(cfg);
        Mutate(s => s with { Nr = normalized });

        // Persist the new DSP settings to the store
        _dspSettingsStore.Upsert(normalized);

        return Snapshot();
    }

    private static NrConfig NormalizeNrConfig(NrConfig cfg) =>
        IsSupportedNrMode(cfg.NrMode) ? cfg : cfg with { NrMode = NrMode.Off };

    private static bool IsSupportedNrMode(NrMode mode) =>
        mode is NrMode.Off or NrMode.Anr or NrMode.Emnr or NrMode.Sbnr;

    // AGC mode + custom/fixed params. Replace-style like SetNr; the engine apply
    // happens in DspPipelineService via the _appliedAgc latch. The separate AGC
    // max-gain path (SetAgcTop) is untouched.
    public StateDto SetAgc(AgcConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        Mutate(s => s with { Agc = cfg });
        _dspSettingsStore.SetAgc(cfg);
        return Snapshot();
    }

    // RX squelch (mode-aware single control). Replace-style like SetAgc; the
    // engine apply happens in DspPipelineService via the _appliedSquelch latch.
    // Level and fixed-mode sensitivity are clamped to 0..100 here so a
    // persisted/echoed value is always sane. Adaptive defaults in
    // SquelchConfig keep older clients dynamic.
    public StateDto SetSquelch(SquelchConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var clamped = cfg with
        {
            Level = Math.Clamp(cfg.Level, 0, 100),
            FixedSensitivity = Math.Clamp(
                cfg.FixedSensitivity,
                SquelchConfig.MinFixedSensitivity,
                SquelchConfig.MaxFixedSensitivity),
        };
        Mutate(s => s with { Squelch = clamped });
        _dspSettingsStore.SetSquelch(clamped);
        return Snapshot();
    }

    // TX leveling — ALC (max-gain/decay), Leveler (on/off/decay), Compressor
    // (on/off/gain). Replace-style like SetSquelch; the engine apply happens in
    // DspPipelineService via the _appliedTxLeveling latch. All ranges are
    // clamped here so a persisted/echoed value is always sane (Thetis parity:
    // AlcMaxGainDb 0..120, AlcDecayMs 1..50, LevelerDecayMs 1..5000,
    // CompressorGainDb 0..20). The Leveler max-gain stays on the separate
    // SetTxLevelerMaxGain path and is never duplicated here.
    public StateDto SetTxLeveling(TxLevelingConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var clamped = cfg with
        {
            AlcMaxGainDb = Math.Clamp(cfg.AlcMaxGainDb, 0.0, 120.0),
            AlcDecayMs = Math.Clamp(cfg.AlcDecayMs, 1, 50),
            LevelerDecayMs = Math.Clamp(cfg.LevelerDecayMs, 1, 5000),
            CompressorGainDb = Math.Clamp(cfg.CompressorGainDb, 0.0, 20.0),
        };
        Mutate(s => s with { TxLeveling = clamped });
        _dspSettingsStore.SetTxLeveling(clamped);
        return Snapshot();
    }

    // Replace the full manual-notch set. The client posts the whole list on
    // every change (and on connect), so there's nothing to merge — store it and
    // raise NotchesChanged for DspPipelineService to push to the engine. Notch
    // centre/width are validated as finite, positive-width, and clamped to a
    // sane count so a malformed client can't flood the WDSP notch database.
    public void SetNotches(IReadOnlyList<NotchDto> notches)
    {
        ArgumentNullException.ThrowIfNull(notches);
        var cleaned = new List<NotchDto>(Math.Min(notches.Count, MaxNotches));
        foreach (var n in notches)
        {
            if (!double.IsFinite(n.CenterHz) || !double.IsFinite(n.WidthHz)) continue;
            if (n.WidthHz < MinNotchWidthHz || n.WidthHz > MaxNotchWidthHz) continue;
            if (n.CenterHz <= 0) continue;
            cleaned.Add(new NotchDto(n.CenterHz, n.WidthHz, n.Active, NormalizeNotchSource(n.Source)));
            if (cleaned.Count >= MaxNotches) break;
        }

        IReadOnlyList<NotchDto> snapshot;
        lock (_sync)
        {
            _notches = cleaned;
            snapshot = cleaned.ToArray();
        }
        _stateDirty = true;
        FlushState();
        NotchesChanged?.Invoke(snapshot);
    }

    // WDSP's notch database is bounded; keep well under it and reject absurd
    // widths so the panadapter paint gesture can't push garbage into the DSP.
    private const int MaxNotches = 64;
    private const double MinNotchWidthHz = 1.0;
    private const double MaxNotchWidthHz = 50_000.0;

    private static string? NormalizeNotchSource(string? source) =>
        string.Equals(source, "auto", StringComparison.OrdinalIgnoreCase) ? "auto" : null;

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
        // Persist PS arm + cal-mode preference so the operator's selected PS
        // state survives restarts. Actual transmit/keying actions still do not.
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
        // If the PS MOX hold-off just dropped, shrink the pre-key window so the
        // pre-key < PS-hold-off invariant holds regardless of setter ordering.
        ReclampPreKeyToPs();
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
    /// across sessions.
    /// </summary>
    public StateDto SetPsMonitor(PsMonitorSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        _log.LogInformation("setPsMonitor enabled={Enabled}", req.Enabled);
        Mutate(s => s with { PsMonitorEnabled = req.Enabled });
        return Snapshot();
    }

    /// <summary>TX-monitor toggle (preview path). Mutates StateDto so the
    /// next DspPipelineService.UpdateState tick latches the value into
    /// engine.SetTxMonitorEnabled. Mirrors PsMonitor's lifecycle — operator
    /// preference, not persisted across sessions; resets to off on each new
    /// connect so the radio doesn't come up previewing unintentionally.</summary>
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

    // Surface calcc-stall state to the frontend. PsAutoAttenuateService raises
    // this when info5 stays at 0 for >5s while keyed; the frontend renders a
    // banner pointing operator at HW peak. No-ops if the flag isn't changing.
    public void SetPsCalibrationStalled(bool stalled)
    {
        if (Snapshot().PsCalibrationStalled == stalled) return;
        Mutate(s => s with { PsCalibrationStalled = stalled });
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
            // HL2: mi0bot clsHardwareSpecific.cs:312 PSDefaultPeak = 0.233.
            // Same value regardless of protocol — HL2 hardware peak does
            // not change between P1 and P2.
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
    /// Apply a per-radio PS hardware-peak to the StateDto. Called by
    /// DspPipelineService after a successful connect (P1 or P2) so the
    /// engine sees the correct curve scale before the operator arms PS.
    ///
    /// Resolution order:
    ///   1. Operator-calibrated value from PsSettingsStore.HwPeakByBoard
    ///      (set by SetPsAdvanced or the auto-cal control loop) — wins when
    ///      present so chains that don't match the factory default
    ///      (external amp sample taps, non-stock attenuator pads) keep
    ///      their hard-won calibration across reconnects.
    ///   2. Per-board factory default from ResolvePsHwPeak.
    ///
    /// PsHwPeakDefault always tracks (2) so the frontend can render a
    /// "differs from factory default" hint when the operator value is
    /// active. Doesn't fire StateChanged unless something actually moves.
    /// </summary>
    public void ApplyPsHwPeakForConnection(bool isProtocol2, HpsdrBoardKind board)
    {
        var variant = EffectiveOrionMkIIVariant;
        string boardKey = GetPsBoardKey(isProtocol2, board, variant);
        double factoryDefault = ResolvePsHwPeak(isProtocol2, board, variant);
        // Prefer a persisted operator-calibrated value for this exact
        // board / variant. Missing entry → fall through to the factory
        // default (first connect on a new board, or operator hasn't tuned).
        var persisted = _psStore?.Get();
        bool usingPersisted = persisted?.HwPeakByBoard is { } map
            && map.TryGetValue(boardKey, out double saved)
            && saved > 0.0;
        double peak = usingPersisted ? persisted!.HwPeakByBoard[boardKey] : factoryDefault;
        // Cache the board key so PersistPsState routes future SetPsAdvanced
        // writes into the right slot.
        _currentPsBoardKey = boardKey;
        // Surface the TX feedback attenuation for the PURESIGNAL panel's manual
        // control: the per-board floor (HL2 reaches -28, others 0) and the
        // persisted value for this board (0 when none saved). GetPersistedPsTxAttnDb
        // also seeds _currentPsTxAttnDb so PersistPsState keeps the slot.
        int attnMin = board == HpsdrBoardKind.HermesLite2 ? -28 : 0;
        int attn = GetPersistedPsTxAttnDb() ?? 0;
        Mutate(s =>
            s.PsHwPeak == peak && s.PsHwPeakDefault == factoryDefault
            && s.PsTxFeedbackAttenuationDb == attn && s.PsTxFeedbackAttenuationDbMin == attnMin
                ? s
                : s with
                {
                    PsHwPeak = peak,
                    PsHwPeakDefault = factoryDefault,
                    PsTxFeedbackAttenuationDb = attn,
                    PsTxFeedbackAttenuationDbMin = attnMin,
                });
        _log.LogInformation(
            "radio.applyPsHwPeak proto={Proto} board={Board} variant={Variant} key={Key} peak={Peak:F4} default={Default:F4} source={Source}",
            isProtocol2 ? "P2" : "P1", board, variant, boardKey, peak, factoryDefault,
            usingPersisted ? "persisted" : "factory");
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
        AdcProtectionConfig adcProtection;
        FamilyFilter ssb, am, fm, cw, ssbTx, amTx, fmTx, cwTx;
        List<NotchDto> notches;
        lock (_sync)
        {
            snap = _state;
            adcProtection = _adcProtection;
            ssb = _ssbFilter; am = _amFilter; fm = _fmFilter; cw = _cwFilter;
            ssbTx = _ssbTxFilter; amTx = _amTxFilter; fmTx = _fmTxFilter; cwTx = _cwTxFilter;
            notches = _notches.ToList();
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
                AdcProtectionAttackMs = adcProtection.AttackMs,
                AdcProtectionReleaseMs = adcProtection.ReleaseMs,
                AdcProtectionAttackStepDb = adcProtection.AttackStepDb,
                AdcProtectionReleaseStepDb = adcProtection.ReleaseStepDb,
                AdcProtectionMaxOffsetDb = adcProtection.MaxOffsetDb,
                AdcProtectionWarningThreshold = adcProtection.WarningThreshold,
                AdcProtectionMagnitudeSoftLimit = adcProtection.MagnitudeSoftLimit,
                AttenDb = snap.AttenDb,
                AutoAgcEnabled = snap.AutoAgcEnabled,
                PreampOn = snap.PreampOn,
                RxAfGainDb = snap.RxAfGainDb,
                MicGainDb = snap.MicGainDb,
                LevelerMaxGainDb = snap.LevelerMaxGainDb,
                ZoomLevel = snap.ZoomLevel,
                SsbFilterLoAbs = ssb.LoAbs,   SsbFilterHiAbs = ssb.HiAbs,
                AmFilterLoAbs = am.LoAbs,     AmFilterHiAbs = am.HiAbs,
                FmFilterLoAbs = fm.LoAbs,     FmFilterHiAbs = fm.HiAbs,
                CwFilterLoAbs = cw.LoAbs,     CwFilterHiAbs = cw.HiAbs,
                SsbTxFilterLoAbs = ssbTx.LoAbs, SsbTxFilterHiAbs = ssbTx.HiAbs,
                AmTxFilterLoAbs = amTx.LoAbs,   AmTxFilterHiAbs = amTx.HiAbs,
                FmTxFilterLoAbs = fmTx.LoAbs,   FmTxFilterHiAbs = fmTx.HiAbs,
                CwTxFilterLoAbs = cwTx.LoAbs,   CwTxFilterHiAbs = cwTx.HiAbs,
                DrivePct = snap.DrivePct,
                TunePct = snap.TunePct,
                TxMoxPreKeyDelayMs = snap.TxMoxPreKeyDelayMs,
                RadioLoHz = snap.RadioLoHz,
                Rx2Enabled = snap.Rx2Enabled,
                VfoBHz = snap.VfoBHz,
                ModeB = snap.ModeB,
                FilterLowHzB = snap.FilterLowHzB,
                FilterHighHzB = snap.FilterHighHzB,
                FilterPresetNameB = snap.FilterPresetNameB,
                Rx2AudioMode = snap.Rx2AudioMode,
                Rx2AfGainDb = snap.Rx2AfGainDb,
                TxVfo = snap.TxVfo,
                CtunEnabled = snap.CtunEnabled,
                Notches = notches.Select(n => new RadioStateNotchEntry
                {
                    CenterHz = n.CenterHz,
                    WidthHz = n.WidthHz,
                    Active = n.Active,
                    Source = NormalizeNotchSource(n.Source),
                }).ToList(),
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
        Protocol2Client? client = null,
        HpsdrBoardKind boardKind = HpsdrBoardKind.Unknown)
    {
        Protocol2Client? previous;
        lock (_sync)
        {
            previous = _p2Client;
            _p2Client = client;
            _p2Active = true;
            _p2BoardKind = boardKind;
            _attOffsetDb = 0;
            _adcOverloadLevel = 0;
            _overloadSeenInWindow = false;
            _lastTickMs = long.MinValue;
            _lastAppliedEffectiveDb = -1;
            _lastAdcOverloadBits = 0;
            _lastAdc0MaxMagnitude = null;
            _lastAdc1MaxMagnitude = null;
            _adc0MaxMagnitudeAtOverload = 0;
            _adc1MaxMagnitudeAtOverload = 0;
            _lastAdcTelemetryUtc = null;
        }
        if (previous is not null) previous.TelemetryReceived -= OnP2Telemetry;
        if (client is not null) client.TelemetryReceived += OnP2Telemetry;
        Mutate(s => s with
        {
            Status = ConnectionStatus.Connected,
            Endpoint = endpoint,
            SampleRate = sampleRateHz,
            AttOffsetDb = 0,
            AdcOverloadWarning = false,
        });
        // P2 is alive — PA defaults should reflect G2 / Orion class so the
        // operator sees realistic numbers when they open the PA panel.
        RecomputePaAndPush();
        ApplyG2AdcOptionsToP2Client(client, ConnectedBoardKind);
        // Fire AFTER the state mutation + PA recompute so subscribers see a
        // fully-coherent RadioService when they read board kind / snapshot.
        if (client is not null) P2Connected?.Invoke(client);
    }

    public void MarkProtocol2Disconnected()
    {
        Protocol2Client? previous;
        lock (_sync)
        {
            previous = _p2Client;
            _p2Client = null;
            _p2Active = false;
            _p2BoardKind = HpsdrBoardKind.Unknown;
            _attOffsetDb = 0;
            _adcOverloadLevel = 0;
            _overloadSeenInWindow = false;
            _lastTickMs = long.MinValue;
            _lastAppliedEffectiveDb = -1;
        }
        if (previous is not null) previous.TelemetryReceived -= OnP2Telemetry;
        Mutate(s => s with
        {
            Status = ConnectionStatus.Disconnected,
            Endpoint = null,
            AttOffsetDb = 0,
            AdcOverloadWarning = false,
        });
        // Same reasoning as P1 DisconnectAsync — clear the board key so
        // disconnected SetPsAdvanced writes don't leak into the previous
        // radio's per-board HW Peak slot.
        _currentPsBoardKey = string.Empty;
        _currentPsTxAttnDb = -1;
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

    private void OnP2Telemetry(P2TelemetryReading reading) =>
        HandleP2AdcTelemetry(reading, Environment.TickCount64);

    /// <summary>
    /// Protocol-1 compatibility entrypoint for tests and the P1 overload
    /// event. Uses the same configurable protection core as Protocol 2; the
    /// default config preserves the old Thetis-style 100 ms / 1 dB loop.
    /// </summary>
    internal void HandleAdcOverload(AdcOverloadStatus status, long nowMs)
    {
        byte bits = (byte)((status.Adc0 ? 0x01 : 0) | (status.Adc1 ? 0x02 : 0));
        HandleAdcProtection(status.AnyOverload, bits, null, null, nowMs);
    }

    /// <summary>
    /// Protocol-2 hi-priority telemetry path. Consumes overload bits and, when
    /// configured, ADC max-magnitude words so the G2/Orion board can protect
    /// before the overload bit latches.
    /// </summary>
    internal void HandleP2AdcTelemetry(P2TelemetryReading reading, long nowMs)
    {
        bool anyOverload = reading.AdcOverloadBits != 0;
        HandleAdcProtection(
            anyOverload,
            reading.AdcOverloadBits,
            reading.Adc0MaxMagnitude,
            reading.Adc1MaxMagnitude,
            nowMs);
    }

    /// <summary>
    /// Port of Thetis' handleOverload (console.cs:22167) plus the
    /// <c>_adc_overload_level</c> counter (console.cs:22093-22113), extended
    /// for G2/P2 telemetry and user-configurable attack/release. Applies at
    /// most one policy step per attack/release window so the ramp is bounded.
    /// </summary>
    private void HandleAdcProtection(
        bool hardOverload,
        byte overloadBits,
        ushort? adc0MaxMagnitude,
        ushort? adc1MaxMagnitude,
        long nowMs)
    {
        bool changedWarning = false;
        int? effectiveToApply = null;
        bool newWarning = false;
        int newOffset = 0;

        lock (_sync)
        {
            _lastAdcOverloadBits = overloadBits;
            _lastAdc0MaxMagnitude = adc0MaxMagnitude;
            _lastAdc1MaxMagnitude = adc1MaxMagnitude;
            _lastAdcTelemetryUtc = DateTimeOffset.UtcNow;
            if ((overloadBits & 0x01) != 0 && adc0MaxMagnitude is ushort adc0)
                _adc0MaxMagnitudeAtOverload = adc0;
            if ((overloadBits & 0x02) != 0 && adc1MaxMagnitude is ushort adc1)
                _adc1MaxMagnitudeAtOverload = adc1;

            if (!_state.AutoAttEnabled) return;
            if (_mox) return;   // TX-side ATT is owned by a different code path

            var cfg = _adcProtection;
            bool magnitudeSoftHit = cfg.MagnitudeSoftLimit > 0
                && ((adc0MaxMagnitude ?? 0) >= cfg.MagnitudeSoftLimit
                    || (adc1MaxMagnitude ?? 0) >= cfg.MagnitudeSoftLimit);

            if (hardOverload || magnitudeSoftHit) _overloadSeenInWindow = true;

            if (_lastTickMs == long.MinValue)
            {
                _lastTickMs = nowMs;
                return;
            }

            int intervalMs = _overloadSeenInWindow ? cfg.AttackMs : cfg.ReleaseMs;
            if (nowMs - _lastTickMs < intervalMs) return;
            _lastTickMs = nowMs;

            bool seen = _overloadSeenInWindow;
            _overloadSeenInWindow = false;

            if (seen)
            {
                if (_attOffsetDb < cfg.MaxOffsetDb)
                    _attOffsetDb = Math.Min(cfg.MaxOffsetDb, _attOffsetDb + cfg.AttackStepDb);
                _adcOverloadLevel = Math.Min(5, _adcOverloadLevel + 2);
            }
            else
            {
                if (_attOffsetDb > 0)
                    _attOffsetDb = Math.Max(0, _attOffsetDb - cfg.ReleaseStepDb);
                if (_adcOverloadLevel > 0) _adcOverloadLevel--;
            }

            int effective = Math.Clamp(_atten.ClampedDb + _attOffsetDb, HpsdrAtten.MinDb, HpsdrAtten.MaxDb);
            if (effective != _lastAppliedEffectiveDb)
            {
                _lastAppliedEffectiveDb = effective;
                effectiveToApply = effective;
            }

            bool warn = _adcOverloadLevel > cfg.WarningThreshold;
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
        768_000 => HpsdrSampleRate.Rate768k,     // P2 only (ANAN G2)
        1_536_000 => HpsdrSampleRate.Rate1536k,  // P2 only (ANAN G2)
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
        HpsdrSampleRate.Rate768k => 768_000,
        HpsdrSampleRate.Rate1536k => 1_536_000,
        _ => throw new ArgumentOutOfRangeException(nameof(rate), rate, null),
    };
}
