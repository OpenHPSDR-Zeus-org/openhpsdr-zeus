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

namespace Zeus.Contracts;

public enum RxMode : byte
{
    LSB, USB, CWL, CWU, AM, FM, SAM, DSB, DIGL, DIGU,
}

// PureSignal feedback antenna source. On G2/MkII the wire-format diff
// between Internal coupler and External (Bypass) is exactly one bit
// (ALEX_RX_ANTENNA_BYPASS = 0x00000800) in alex0 when xmit && PS armed.
// pihpsdr's three-way Internal/Ext1/Bypass collapses to two on the wire
// for this hardware, so Zeus exposes a two-way selector.
public enum PsFeedbackSource : byte { Internal = 0, External = 1 }

public enum ConnectionStatus { Disconnected, Connecting, Connected, Error }

// Thetis NR-button state: Off = no spectral NR, Anr = NR1 (time-domain LMS),
// Emnr = NR2 (Ephraim–Malah spectral), Sbnr = NR4 (libspecbleach spectral
// bleaching — issue #79). NR3 (RNNR) is intentionally absent: training data
// for the bundled RNNoise model is voice-corpus-only and underperforms on
// HF noise. All four modes are mutually exclusive in WDSP, so the button
// carries them in one enum. Byte order is fixed — appending only — because
// persisted DspSettingsStore rows would mis-deserialize on a reorder.
public enum NrMode : byte { Off, Anr, Emnr, Sbnr = 3 }

// Pre-RXA time-domain blanker. Nb1 = ANB (noise blanker), Nb2 = NOB (noise gate).
// Engine silently ignores this until the pre-RXA pipeline lands (task #4);
// kept in the contract so the UI shape doesn't churn when it does.
public enum NbMode : byte { Off, Nb1, Nb2 }

// Thetis default NbThreshold = 3.3 (WDSP units), which is `0.165 × 20` — the
// Thetis UI slider sitting at 20. Kept here so REST round-trips preserve the
// UI-space value rather than the scaled one.
//
// NR2 post2 + NR4 (Sbnr) tunables are nullable so legacy state frames (no
// fields present) deserialize unchanged; null at the engine seam means
// "use the WdspDspEngine.NrDefaults baseline". Persisted globally (not
// per-band/mode/profile) per Thetis behaviour — see DspSettingsStore.
public sealed record NrConfig(
    NrMode NrMode = NrMode.Off,
    bool AnfEnabled = false,
    bool SnbEnabled = false,
    bool NbpNotchesEnabled = false,
    NbMode NbMode = NbMode.Off,
    double NbThreshold = 20.0,
    // ---- NR2 (EMNR) post2 comfort-noise tunables ----
    // Already wired in WdspDspEngine.NrDefaults; surfacing them via the
    // right-click popover. Slider scale: factor/nlevel UI 0..100 → WDSP
    // 0..1 (Thetis divides by 100). Taper is bins (0..100), Rate is
    // time-constant in seconds. Run gates the comfort-noise injection.
    bool? EmnrPost2Run = null,
    double? EmnrPost2Factor = null,
    double? EmnrPost2Nlevel = null,
    double? EmnrPost2Rate = null,
    int? EmnrPost2Taper = null,
    // ---- NR4 (SBNR / libspecbleach) tunables ----
    // Defaults from Thetis radio.cs:2350-2462. Native setters take float;
    // we marshal to double on the wire and downcast at the P/Invoke seam.
    double? Nr4ReductionAmount = null,
    double? Nr4SmoothingFactor = null,
    double? Nr4WhiteningFactor = null,
    double? Nr4NoiseRescale = null,
    double? Nr4PostFilterThreshold = null,
    int? Nr4NoiseScalingType = null,
    int? Nr4Position = null,
    // ---- NR2 (EMNR) core algorithm selectors + trained-method tuning ----
    // Thetis Setup → DSP tab radio groups + AE checkbox + T1/T2 NUDs. Defaults
    // match Thetis (Gamma=2, OSMS=0, AE on, T1=-0.5, T2=2.0). T1/T2 are only
    // consulted by WDSP when EmnrGainMethod=3 (Trained); the engine still
    // writes them through so the channel state is coherent on mode-cycle.
    int? EmnrGainMethod = null,
    int? EmnrNpeMethod = null,
    bool? EmnrAeRun = null,
    double? EmnrTrainT1 = null,
    double? EmnrTrainT2 = null);

public sealed record StateDto(
    ConnectionStatus Status,
    string? Endpoint,
    long VfoHz,
    RxMode Mode,
    int FilterLowHz,
    int FilterHighHz,
    int SampleRate,
    double AgcTopDb = 80.0,
    // User-baseline attenuator in dB, 0..31. Hardware receives
    // <c>AttenDb + AttOffsetDb</c> (clamped to 31) while auto-ATT is engaged.
    // Default is 0 — auto-ATT ramps the offset up on observed ADC overloads.
    int AttenDb = 0,
    NrConfig? Nr = null,
    int ZoomLevel = 1,
    // Auto-attenuator control loop. When on (default), the server raises
    // AttOffsetDb by 1 per ~100 ms window in which any ADC-overload bit was
    // seen, and decays it by 1 per clean window. Ported from Thetis
    // console.cs:22167 (handleOverload).
    bool AutoAttEnabled = true,
    int AttOffsetDb = 0,
    // Red-lamp flag derived from Thetis' overload-level counter
    // (+2 per overload cycle, -1 per clean, clamped 0..5, warn when >3).
    bool AdcOverloadWarning = false,
    // Currently active filter preset slot name (e.g. "F6", "VAR1"). Null when
    // the filter was set by a drag edit without a named slot context.
    string? FilterPresetName = null,
    // Advanced-filter ribbon visibility, persisted across server restarts via
    // FilterPresetStore so the operator's close-the-ribbon choice sticks.
    bool FilterAdvancedPaneOpen = false,
    // TX bandpass filter (WDSP TXA SetTXABandpassFreqs). Signed per sideband
    // like RX FilterLowHz/HighHz: USB positive, LSB negative, AM/FM symmetric
    // around 0. RadioService keeps per-mode-family memory so switching USB →
    // LSB flips the sign and USB → AM swaps to AM's remembered width.
    // Default 150/2850 matches Thetis's stock SSB TX bandpass.
    int TxFilterLowHz = 150,
    int TxFilterHighHz = 2850,
    // Master RX AF gain in dB. 0 dB ≡ WDSP SetRXAPanelGain1(1.0), the
    // engine's open-time default — a fresh session that never touches this
    // field is audibly identical to pre-#77 builds. Operator slider range
    // is −50..+20 dB (see RadioService.SetRxAfGain). Per-RX not supported
    // yet; when multi-RX lands this becomes the master and the per-RX
    // values layer on top.
    double RxAfGainDb = 0.0,
    // Auto-AGC control loop. When on, the server automatically adjusts
    // AgcTopDb based on signal conditions. Similar to Auto-ATT but for AGC.
    // Default is OFF — operator must explicitly enable. The control loop
    // adjusts AgcOffsetDb, which is added to the user baseline AgcTopDb.
    bool AutoAgcEnabled = false,
    double AgcOffsetDb = 0.0,

    // ---- PureSignal predistortion (TXA-side; WDSP calcc/iqc stages) ----
    // PsEnabled is the master arm bit. Deliberately NOT persisted server-side
    // — operator must re-arm each session (parity with MOX). The other PS
    // fields ARE persisted via PsSettingsStore so the operator's calibration
    // tuning survives restarts.
    bool PsEnabled = false,
    // PsMonitorEnabled — operator-facing "Monitor PA output" toggle
    // (issue #121). When true AND PsEnabled AND PS has converged
    // (info[14]==1), DspPipelineService.Tick switches the TX panadapter /
    // waterfall source from the post-CFIR predistorted-IQ analyzer to the
    // PS-feedback analyzer fed from the radio's loopback ADC, so the
    // operator sees the actual on-air signal. Default off — preserves the
    // Thetis-style predistorted view. Hidden / disabled in the UI on
    // boards that have no PS feedback path (e.g. HermesLite2). NOT
    // persisted server-side: this is an operator viewing preference,
    // resets to off each session same as PsEnabled.
    bool PsMonitorEnabled = false,
    // TX Monitor — operator-facing audition toggle (issue #106 follow-up).
    // When true, the engine demodulates the post-CFIR TX IQ back to mono
    // baseband audio so the operator hears the chain output (mic → EQ →
    // Leveler → VST → CFC → ALC → bandpass) at the actual TX bandwidth
    // profile. Equivalent to Thetis MON, but also runs the chain when MOX
    // is OFF so VST plugins receive samples and meters animate continuously.
    // RX audio is suppressed in the broadcast while monitor is on so the
    // operator hears only the TX audition. NOT persisted across sessions —
    // resets to off each connect, matching MOX/TUN/PsEnabled discipline.
    bool TxMonitorEnabled = false,
    bool PsAuto = true,             // continuous adapt by default once armed
    bool PsSingle = false,          // one-shot SetPSControl(1,1,0,0)
    bool PsPtol = false,            // false = strict 0.4; true = relax 0.8
    bool PsAutoAttenuate = true,
    double PsMoxDelaySec = 0.2,
    double PsLoopDelaySec = 0.0,
    double PsAmpDelayNs = 150.0,
    // PS hardware peak — set per protocol/hardware by RadioService at connect
    // time. P1 = 0.4072 (Hermes/ANAN-10/100); P2 OrionMkII/Saturn = 0.6121;
    // P2 ANAN-7000/8000 = 0.2899. Default here (P1) is a safe neutral; the
    // RadioService HW-peak switch overrides on the first ConnectAsync /
    // ConnectP2Async. See PLAN section 7 / hermes.md §7.1.
    double PsHwPeak = 0.4072,
    // PS hardware-peak per-board default — frozen at connect time from
    // ResolvePsHwPeak(isProtocol2, board) and surfaced for the UI to compare
    // against the live PsHwPeak so the operator gets a "differs from default"
    // hint when they've dialed away from the factory curve.
    // mi0bot ref: PSForm.cs:830 `pbWarningSetPk.Visible = _PShwpeak !=
    // HardwareSpecific.PSDefaultPeak;` + clsHardwareSpecific.cs:303-328
    // PSDefaultPeak per-board switch.
    double PsHwPeakDefault = 0.4072,
    PsFeedbackSource PsFeedbackSource = PsFeedbackSource.Internal,
    string PsIntsSpiPreset = "16/256",
    double PsFeedbackLevel = 0.0,   // info[4] read-back, 0..256
    byte PsCalState = 0,            // info[15] enum
    bool PsCorrecting = false,      // info[14]
    // ---- TwoTone test generator (TXA PostGen mode=1; protocol-agnostic) ----
    // Standard PureSignal calibration excitation. Defaults match pihpsdr's
    // TwoTone defaults — 700/1900 Hz, 0.49 linear amplitude per tone.
    bool TwoToneEnabled = false,
    double TwoToneFreq1 = 700.0,
    double TwoToneFreq2 = 1900.0,
    double TwoToneMag = 0.49,

    // ---- CFC (Continuous Frequency Compressor) — issue #123 ----
    // Nullable so legacy state frames (no Cfc field) deserialize unchanged;
    // null at the engine seam means "use CfcConfig.Default" — same pattern
    // as the Nr field above. Persisted globally via DspSettingsStore.
    CfcConfig? Cfc = null);

public sealed record RadioInfo(
    string MacAddress,
    string IpAddress,
    string BoardId,
    string FirmwareVersion,
    bool Busy,
    IReadOnlyDictionary<string, string>? Details = null);

public sealed record ConnectRequest(
    string Endpoint,
    int SampleRate = 192_000,
    bool? PreampOn = null,
    int? Atten = null,
    // Raw HPSDR board byte from discovery (P2's reply parser maps this to
    // <see cref="HpsdrBoardKind"/>). When provided on /api/connect/p2 the
    // server uses it as the connected board kind instead of the historical
    // "P2 active ⇒ assume OrionMkII" fallback. Null/omitted = legacy
    // behaviour. Issue #171.
    byte? BoardId = null);

public sealed record VfoSetRequest(long Hz);

public sealed record ModeSetRequest(RxMode Mode);

public sealed record BandwidthSetRequest(int Low, int High);

/// <summary>TX bandpass set request — signed Hz pair matching StateDto's
/// TxFilterLowHz/TxFilterHighHz convention (LSB-style passbands are negative,
/// DSB/AM/FM symmetric around 0).</summary>
public sealed record TxFilterSetRequest(int LowHz, int HighHz);

public sealed record SampleRateSetRequest(int Rate);

public sealed record PreampSetRequest(bool On);

public sealed record AgcGainSetRequest(double TopDb);

public sealed record RxAfGainSetRequest(double Db);

public sealed record AttenuatorSetRequest(int Db);

public sealed record MoxSetRequest(bool On);

public sealed record DriveSetRequest(int Percent);

// TUN has its own drive % so the operator can pre-set a lower tune level
// without touching the MOX drive. Same per-band PA gain compensates both,
// so equal slider positions yield equal watts on air (Thetis parity —
// `console.cs:46756-46788`).
public sealed record TuneDriveSetRequest(int Percent);

public sealed record NrSetRequest(NrConfig Nr);

// Per-popover save requests for the NR right-click panels. Nullable shape so
// the popover can PATCH a single field without disturbing siblings (the server
// merges on top of the persisted NrConfig and re-applies the engine state).
public sealed record Nr2Post2ConfigSetRequest(
    bool? Post2Run = null,
    double? Post2Factor = null,
    double? Post2Nlevel = null,
    double? Post2Rate = null,
    int? Post2Taper = null);

public sealed record Nr4ConfigSetRequest(
    double? ReductionAmount = null,
    double? SmoothingFactor = null,
    double? WhiteningFactor = null,
    double? NoiseRescale = null,
    double? PostFilterThreshold = null,
    int? NoiseScalingType = null,
    int? Position = null);

// NR2 (EMNR) core algorithm selectors + trained-method tuning. Mirrors
// Nr2Post2ConfigSetRequest's nullable-merge pattern: each field absent
// from the PATCH leaves the persisted value untouched.
//   GainMethod: 0=Linear, 1=Log, 2=Gamma (default), 3=Trained
//   NpeMethod : 0=OSMS (default), 1=MMSE, 2=NSTAT
//   TrainT1/T2 are only meaningful when GainMethod=3.
public sealed record Nr2CoreConfigSetRequest(
    int? GainMethod = null,
    int? NpeMethod = null,
    bool? AeRun = null,
    double? TrainT1 = null,
    double? TrainT2 = null);

// Panadapter/waterfall zoom levels. Level=1 means the analyzer covers the full
// sample-rate span; level=2 means VFO-centered half-span (×2 bins/Hz), and so
// on. The span-centering math lives in the engine; this contract just carries
// the discrete factor on the wire.
public sealed record ZoomSetRequest(int Level);

public sealed record AutoAttSetRequest(bool Enabled);

public sealed record AutoAgcSetRequest(bool Enabled);

public sealed record TunSetRequest(bool On);

public sealed record MicGainSetRequest(int Db);

// Leveler max-gain ceiling in dB. Server clamps to [0, 15]; outside that is
// 400. Frontend POSTs this whenever the slider moves and on WS reconnect so
// the operator's preferred ceiling is re-applied after a server restart
// (backend holds no persistent state for this setting).
public sealed record LevelerMaxGainSetRequest(double Gain);

// Per-band memory: last-used frequency and mode for a given ham band
// (e.g. "20m"). The server keeps these in an unencrypted LiteDB file so they
// survive restarts and follow the backend (not the browser). Band buttons
// read the full map on mount and write on every tune/mode change
// (debounced on the web).
public sealed record BandMemoryDto(string Band, long Hz, RxMode Mode);

public sealed record BandMemorySetRequest(long Hz, RxMode Mode);

// UI layout: opaque workspace JSON persisted server-side so the operator's
// panel arrangement survives page reloads and reinstalls. The JSON is stored
// as a string to avoid strongly-typing the workspace tree on the wire — the
// frontend owns the schema.
//
// `UiLayoutDto` / `UiLayoutSetRequest` are the legacy single-layout shape
// (one workspace per server). Kept so older clients keep working and so the
// new multi-layout system can migrate the legacy row on first read.
public sealed record UiLayoutDto(string LayoutJson, long UpdatedUtc);

public sealed record UiLayoutSetRequest(string LayoutJson);

// Multi-layout shape (issue #241). Layouts are keyed per radio (board kind /
// "default" while disconnected). Each radio holds a list of named layouts and
// remembers which one was active.
//
// `Icon` is a short string (typically a single emoji) shown above the layout
// label in the LeftLayoutBar; `Description` is a longer free-form string used
// as the hover tooltip. Both are optional — older layouts without these
// fields render with a letter fallback and the layout name as tooltip.
public sealed record NamedLayoutDto(
    string Id,
    string Name,
    string LayoutJson,
    long UpdatedUtc,
    string? Icon = null,
    string? Description = null);

public sealed record RadioLayoutsDto(string RadioKey, IReadOnlyList<NamedLayoutDto> Layouts, string ActiveLayoutId);

public sealed record SaveNamedLayoutRequest(
    string RadioKey,
    string LayoutId,
    string Name,
    string LayoutJson,
    string? Icon = null,
    string? Description = null);

public sealed record SetActiveLayoutRequest(string RadioKey, string LayoutId);

// Per-band PA settings. Mirrors Thetis `PAProfile._gainValues[]` / piHPSDR
// `band->pa_calibration` (single scalar dB per band — 9-point curve is a
// Phase-4 follow-up). OcTx / OcRx are 7-bit Open-Collector masks driving the
// N2ADR filter board on HL2 and ALEX/OC outputs on Orion-class radios; they
// are OR'd with the board's auto-filter logic so stock HL2 filter switching
// keeps working when the user hasn't set anything.
//
// AutoOcMask is informational only — the read-only N2ADR board mask the
// firmware will OR onto OcRx/OcTx when HasN2adr is on (HL2). PUT requests
// ignore it; the server recomputes from the connected board on the next GET.
public sealed record PaBandSettingsDto(
    string Band,
    double PaGainDb = 0.0,
    bool DisablePa = false,
    byte OcTx = 0,
    byte OcRx = 0,
    byte AutoOcMask = 0);

// Globals shared across bands. PaMaxPowerWatts=0 disables the watts
// conversion path and falls back to the legacy "drive% = raw 0-255 byte"
// behavior so existing installs behave identically until the user runs
// a calibration. OC bits during TUN follow the per-band OcTx mask (same
// as TX) — Thetis behaves this way and the inherited piHPSDR-style
// "OcTune" override was removed in #124 for hardware-safety reasons (a
// global override can hand an external amp a confused band-select state
// during a steady tune carrier and damage finals).
public sealed record PaGlobalSettingsDto(
    bool PaEnabled = true,
    int PaMaxPowerWatts = 0);

public sealed record PaSettingsDto(
    PaGlobalSettingsDto Global,
    IReadOnlyList<PaBandSettingsDto> Bands);

public sealed record PaSettingsSetRequest(
    PaGlobalSettingsDto Global,
    IReadOnlyList<PaBandSettingsDto> Bands);

// Radio-selection header for the Settings menu. `Preferred` is the operator's
// explicit pick ("Auto" = no override); `Connected` is what discovery found
// on the wire ("Unknown" when nothing's connected); `Effective` is the board
// whose defaults the PA / per-band tables seed from. Discovery wins whenever
// a radio is actually connected **unless** `OverrideDetection` is true — the
// preference is normally a before-connect hint, but with override it forces
// specific board behavior even when a different board is detected.
public sealed record RadioSelectionDto(
    string Preferred,
    string Connected,
    string Effective,
    bool OverrideDetection);

public sealed record RadioSelectionSetRequest(
    string Preferred,
    bool? OverrideDetection);

// Operator-selected variant for the 0x0A wire-byte alias family
// (issue #218). String-typed for forward compatibility — server parses
// against the OrionMkIIVariant enum. Empty / unknown rejected with 400.
public sealed record RadioVariantSetRequest(string Variant);

// HL2-specific optional toggles surfaced via /api/radio/hl2-options.
// Shape is an object (not a bare bool) so future mi0bot HL2 toggles can
// slot in without breaking the contract. Currently carries Band Volts
// PWM enable (issue #279) — the C3 bit 3 Protocol-1 Config flag the HL2
// fork repurposes from the obsolete LT2208 DITHER bit; lit, HL2 emits
// per-band-tagged PWM voltage on the FAN connector so an external amp
// (Xiegu XPA125B etc.) can auto-band-switch.
public sealed record Hl2OptionsDto(bool BandVolts);

// Mutating version — currently a passthrough of Hl2OptionsDto but kept
// distinct so the GET-vs-PUT request shapes can diverge in the future
// (e.g. PUT becoming a partial update with nullable fields).
public sealed record Hl2OptionsSetRequest(bool BandVolts);

// Panadapter background settings — Mode is one of "basic" | "beam-map" |
// "image"; Fit is one of "fit" | "fill" | "stretch". Image bytes are NOT
// shipped in this DTO; HasImage signals whether GET /api/display-settings/image
// will return content. Persisted server-side so the setting follows the
// operator across browsers / devices.
public sealed record DisplaySettingsDto(
    string Mode,
    string Fit,
    bool HasImage,
    string? ImageMime);

public sealed record DisplaySettingsSetRequest(
    string Mode,
    string Fit);

// Per-mode disclosure state for the inline NR settings accordion that hangs
// below the DSP NR toggle row. Three independent booleans — one per NR
// algorithm. Persisted server-side (LiteDB) so the operator's "I always
// have NR2 tunables open" preference follows them across browsers.
public sealed record NrUiPrefsDto(
    bool Nr1Expanded,
    bool Nr2Expanded,
    bool Nr4Expanded);

public sealed record NrUiPrefsSetRequest(
    bool Nr1Expanded,
    bool Nr2Expanded,
    bool Nr4Expanded);

// Per-slot pin state for the classic-layout bottom row (Logbook + TX
// Stage Meters). True = panel is pinned (full body visible). False =
// collapsed to a chip strip below the pinned tier. Persisted server-side
// so the layout choice follows the operator across browsers / devices,
// same as DisplaySettings.
public sealed record BottomPinDto(
    bool Logbook,
    bool TxMeters);

public sealed record BottomPinSetRequest(
    bool Logbook,
    bool TxMeters);

// ---- PureSignal request records ----
// PsControlSetRequest = master arm (Enabled) + mode (Auto vs Single).
// PsAdvancedSetRequest = nullable so partial updates from the settings
// panel don't reset other fields.
public sealed record PsControlSetRequest(bool Enabled, bool Auto, bool Single);

public sealed record PsAdvancedSetRequest(
    bool? Ptol = null,
    bool? AutoAttenuate = null,
    double? MoxDelaySec = null,
    double? LoopDelaySec = null,
    double? AmpDelayNs = null,
    double? HwPeak = null,
    string? IntsSpiPreset = null);

public sealed record PsResetRequest();

public sealed record PsSaveRequest(string Filename);

public sealed record PsRestoreRequest(string Filename);

// Feedback antenna selector — Internal coupler vs External (Bypass).
// Sent from the PS settings panel. Affects only the radio-side ALEX bit;
// the WDSP cal/iqc stages operate on whatever IQ arrives at DDC0/DDC1.
public sealed record PsFeedbackSourceSetRequest(PsFeedbackSource Source);

// "Monitor PA output" toggle (issue #121). Pure UI/source-routing flag —
// no WDSP setter, no wire-format change. RadioService just stamps the
// StateDto, DspPipelineService reads it on Tick to pick which analyzer
// to drain. Default off; operator opt-in.
public sealed record PsMonitorSetRequest(bool Enabled);

// TX Monitor toggle (issue #106 follow-up). Engages a parallel demod of the
// post-CFIR TX IQ so the operator hears the chain output at the actual TX
// bandwidth profile, with or without keying. Implemented in WdspDspEngine via
// a private RXA channel; pure operator toggle, no persistence. See StateDto
// .TxMonitorEnabled for the discipline notes.
public sealed record TxMonitorSetRequest(bool Enabled);

// Two-tone test generator (used as PS calibration excitation but works
// standalone too). Protocol-agnostic.
public sealed record TwoToneSetRequest(
    bool Enabled,
    double? Freq1 = null,
    double? Freq2 = null,
    double? Mag = null);

// ---- CFC (Continuous Frequency Compressor) — issue #123 ------------------
// Multi-band frequency-domain compressor exposed by WDSP's xcfcomp stage
// (already wired in xtxa between xeqp and xbandpass). Mirrors pihpsdr's
// classic 10-band non-parametric design — see cfc_menu.c. The architecture
// proposal on issue #123 enumerates every WDSP CFCOMP setter we surface.
//
// Persisted GLOBALLY (not per-band/mode) per kb2uka's spec — operator
// profiles are a future feature. CFC defaults to OFF so existing operators
// (including the project owner's external analog rack workflow) see no
// behavior change unless they enable. PostEqEnabled is a separate toggle
// from the master Enabled to mirror pihpsdr — operators may want CFC
// compression without the EQ branch.

/// <summary>One CFC band: centre frequency in Hz (operator-typed),
/// compression-level threshold in dB, and post-comp makeup gain in dB.
/// WDSP sorts the band array internally (cfcomp.c:147), so the on-the-wire
/// order is informational only — the engine relies on WDSP to canonicalise.
/// </summary>
public sealed record CfcBand(double FreqHz, double CompLevelDb, double PostGainDb);

/// <summary>Operator-tunable CFC configuration. <c>Bands</c> length is
/// fixed at 10 to match pihpsdr's classic-mode default and keep the panel
/// layout stable. Engine validates length at the seam.</summary>
public sealed record CfcConfig(
    bool Enabled,
    bool PostEqEnabled,
    double PreCompDb,
    double PrePeqDb,
    CfcBand[] Bands)
{
    /// <summary>Pihpsdr's vfo.c:284-314 baseline — 10 bands at the voice-band
    /// frequencies operators recognise from PowerSDR. All compression and
    /// gains zeroed so enabling neutral CFC is audibly transparent.</summary>
    public static CfcConfig Default => new(
        Enabled: false,
        PostEqEnabled: false,
        PreCompDb: 0.0,
        PrePeqDb: 0.0,
        Bands: new[]
        {
            new CfcBand(50,    0, 0),
            new CfcBand(100,   0, 0),
            new CfcBand(200,   0, 0),
            new CfcBand(500,   0, 0),
            new CfcBand(1000,  0, 0),
            new CfcBand(1500,  0, 0),
            new CfcBand(2000,  0, 0),
            new CfcBand(2500,  0, 0),
            new CfcBand(3000,  0, 0),
            new CfcBand(5000,  0, 0),
        });
}

public sealed record CfcSetRequest(CfcConfig Config);
