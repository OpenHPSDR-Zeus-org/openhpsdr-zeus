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

import { useVfoLockStore as vfoLockStore } from '../state/vfo-lock-store';
import {
  parseBoardCapabilities,
  type BoardCapabilities,
} from './board-capabilities';

export type ConnectionStatus =
  | 'Disconnected'
  | 'Connecting'
  | 'Connected'
  | 'Error';

export type RxMode =
  | 'LSB'
  | 'USB'
  | 'CWL'
  | 'CWU'
  | 'AM'
  | 'FM'
  | 'SAM'
  | 'DSB'
  | 'DIGL'
  | 'DIGU';

export type NrMode = 'Off' | 'Anr' | 'Emnr' | 'Sbnr';
export type NbMode = 'Off' | 'Nb1' | 'Nb2';

// RXA AGC mode. PascalCase strings match the server's JsonStringEnumConverter
// (AgcMode enum). Custom unlocks the per-param controls; Fixed unlocks the
// fixed-gain field. Med is the Thetis (and Zeus) default.
export type AgcMode = 'Fixed' | 'Long' | 'Slow' | 'Med' | 'Fast' | 'Custom';

// AGC mode + custom/fixed params. Null params = "use the canned preset" — only
// consulted in Custom mode (and fixedGainDb only in Fixed mode). The AGC
// max-gain ("top") is NOT here — it lives on RadioStateDto.agcTopDb with its
// own /api/agcGain path. Mirrors Zeus.Contracts AgcConfig.
export type AgcConfigDto = {
  mode: AgcMode;
  slope?: number | null;
  decayMs?: number | null;
  hangMs?: number | null;
  hangThreshold?: number | null;
  fixedGainDb?: number | null;
};

export const AGC_CONFIG_DEFAULT: AgcConfigDto = {
  mode: 'Med',
};

// RX squelch — a single mode-aware control (Thetis parity §5). The server
// routes run + threshold to the WDSP squelch stage matching the current RX
// mode (SSB/CW → SSQL, AM/SAM → AMSQ, FM → FMSQ). `adaptive` enables the
// server-side noise-floor gate; false keeps the fixed WDSP squelch stages.
// `level` is a unitless 0..100 where higher = tighter squelch. Mirrors
// Zeus.Contracts SquelchConfig.
export type SquelchConfigDto = {
  enabled: boolean;
  level: number;
  adaptive: boolean;
};

export const SQUELCH_CONFIG_DEFAULT: SquelchConfigDto = {
  enabled: false,
  level: 0,
  adaptive: true,
};

// TX leveling — ALC (max-gain + decay), Leveler (on/off + decay), Compressor
// (on/off + gain). Mirrors Zeus.Contracts TxLevelingConfig. The ALC run state
// is NOT exposed (always on); the Leveler MAX-GAIN ("top") lives separately on
// RadioStateDto.levelerMaxGainDb with its own /api/tx/leveler-max-gain path.
// Ranges/defaults mirror Thetis: alcMaxGainDb 0..120 (3), alcDecayMs 1..50
// (10), levelerEnabled default true, levelerDecayMs 1..5000 (100),
// compressorEnabled default false, compressorGainDb 0..20 (0).
export type TxLevelingConfigDto = {
  alcMaxGainDb: number;
  alcDecayMs: number;
  levelerEnabled: boolean;
  levelerDecayMs: number;
  compressorEnabled: boolean;
  compressorGainDb: number;
};

export const TX_LEVELING_CONFIG_DEFAULT: TxLevelingConfigDto = {
  alcMaxGainDb: 3,
  alcDecayMs: 10,
  levelerEnabled: true,
  levelerDecayMs: 100,
  compressorEnabled: false,
  compressorGainDb: 0,
};

export type NrConfigDto = {
  nrMode: NrMode;
  anfEnabled: boolean;
  snbEnabled: boolean;
  nbpNotchesEnabled: boolean;
  nbMode: NbMode;
  nbThreshold: number;
  // NR2 (EMNR) post2 comfort-noise tunables — null means "use engine default".
  emnrPost2Run?: boolean | null;
  emnrPost2Factor?: number | null;
  emnrPost2Nlevel?: number | null;
  emnrPost2Rate?: number | null;
  emnrPost2Taper?: number | null;
  // NR2 (EMNR) core algorithm selectors + Trained-method tuning.
  //   gainMethod: 0=Linear 1=Log 2=Gamma 3=Trained
  //   npeMethod : 0=OSMS   1=MMSE 2=NSTAT
  // T1/T2 only consulted by WDSP when gainMethod=3.
  emnrGainMethod?: number | null;
  emnrNpeMethod?: number | null;
  emnrAeRun?: boolean | null;
  emnrTrainT1?: number | null;
  emnrTrainT2?: number | null;
  // NR4 (SBNR / libspecbleach) tunables — null means "use engine default".
  nr4ReductionAmount?: number | null;
  nr4SmoothingFactor?: number | null;
  nr4WhiteningFactor?: number | null;
  nr4NoiseRescale?: number | null;
  nr4PostFilterThreshold?: number | null;
  nr4NoiseScalingType?: number | null;
  nr4Position?: number | null;
};

export const NR_CONFIG_DEFAULT: NrConfigDto = {
  nrMode: 'Off',
  anfEnabled: false,
  snbEnabled: false,
  nbpNotchesEnabled: false,
  nbMode: 'Off',
  nbThreshold: 20,
};

// Engine-side defaults for the popover. Sourced from
// WdspDspEngine.NrDefaults / Thetis radio.cs:2103/2122/2160. Factor/nlevel
// are the Thetis NumericUpDown raw values (0..100); WDSP itself divides
// by 100 internally at emnr.c:1035/1042. Rate has no /100 in WDSP.
export const NR2_POST2_DEFAULTS = {
  run: true,
  factor: 15,
  nlevel: 15,
  rate: 5.0,
  taper: 12,
} as const;

// EMNR core defaults — Thetis Setup → DSP factory state. Mirrors
// WdspDspEngine.NrDefaults so a "reset" reproduces what create_emnr() would
// give on a fresh channel. T1/T2 only matter when gainMethod=3 but the
// Defaults button still resets them so a Trained → revert → Trained cycle
// returns to factory.
export const NR2_CORE_DEFAULTS = {
  gainMethod: 2 as 0 | 1 | 2 | 3,    // Gamma
  npeMethod: 0 as 0 | 1 | 2,         // OSMS
  aeRun: true,
  trainT1: -0.5,
  trainT2: 2.0,
} as const;

export const GAIN_METHOD_LABELS = ['Linear', 'Log', 'Gamma', 'Trained'] as const;
export const NPE_METHOD_LABELS = ['OSMS', 'MMSE', 'NSTAT'] as const;

export const NR4_DEFAULTS = {
  reductionAmount: 10.0,
  smoothingFactor: 0.0,
  whiteningFactor: 0.0,
  noiseRescale: 2.0,
  // -10 matches Thetis's UI default + WDSP's create_sbnr seed (sbnr.c:84) — see
  // WdspDspEngine.NrDefaults.Nr4PostFilterThreshold for the full reasoning.
  postFilterThreshold: -10.0,
  noiseScalingType: 0,
  position: 1,
} as const;

export const NR4_ALGO_LABELS = ['Algo 1', 'Algo 2', 'Algo 3'] as const;

// Integer 1..32. Matches the backend cap (SyntheticDspEngine.MaxZoomLevel).
// At 32× the WDSP analyzer's centre-clipped bin count drops below typical
// pan pixel widths, softening the trace — usable for narrow-signal (CW)
// hunting even if not pixel-sharp.
export type ZoomLevel = number;
export const ZOOM_MIN: ZoomLevel = 1;
export const ZOOM_MAX: ZoomLevel = 32;

export type AdcProtectionConfigDto = {
  enabled: boolean;
  attackMs: number;
  releaseMs: number;
  attackStepDb: number;
  releaseStepDb: number;
  maxOffsetDb: number;
  warningThreshold: number;
  magnitudeSoftLimit: number;
};

export const ADC_PROTECTION_CONFIG_DEFAULT: AdcProtectionConfigDto = {
  enabled: true,
  attackMs: 100,
  releaseMs: 100,
  attackStepDb: 1,
  releaseStepDb: 1,
  maxOffsetDb: 31,
  warningThreshold: 3,
  magnitudeSoftLimit: 0,
};

export type AdcProtectionStatusDto = {
  config: AdcProtectionConfigDto;
  attenDb: number;
  offsetDb: number;
  effectiveDb: number;
  warning: boolean;
  overloadLevel: number;
  lastOverloadBits: number;
  adc0MaxMagnitude: number | null;
  adc1MaxMagnitude: number | null;
  adc0MaxMagnitudeAtOverload: number;
  adc1MaxMagnitudeAtOverload: number;
  lastTelemetryUtc: string | null;
};

export type AdcProtectionSetRequest = Partial<AdcProtectionConfigDto>;

export type RadioStateDto = {
  status: ConnectionStatus;
  endpoint: string | null;
  vfoHz: number;
  mode: RxMode;
  filterLowHz: number;
  filterHighHz: number;
  // Null after a drag edit without a named-slot context (PRD §4.1).
  filterPresetName: string | null;
  // Advanced-filter ribbon visibility; persisted server-side.
  filterAdvancedPaneOpen: boolean;
  // TX bandpass (signed, per-sideband). Per-mode family memory on the server.
  txFilterLowHz: number;
  txFilterHighHz: number;
  sampleRate: number;
  agcTopDb: number;
  // AGC mode + custom/fixed params (separate from agcTopDb max-gain).
  agc: AgcConfigDto;
  // RX squelch (mode-aware single control).
  squelch: SquelchConfigDto;
  // TX leveling (ALC + Leveler + Compressor). Leveler max-gain stays separate.
  txLeveling: TxLevelingConfigDto;
  autoAgcEnabled: boolean;
  agcOffsetDb: number;
  rxAfGainDb: number;
  // TX mic gain in dB ([-40, +10]) and TX Leveler max-gain ceiling in dB
  // ([0, 15]). Server is authoritative; hydrated into tx-store on every fresh
  // RadioStateDto. Previously localStorage-only and reverted on every restart
  // when the desktop webview wiped its storage.
  micGainDb: number;
  levelerMaxGainDb: number;
  attenDb: number;
  autoAttEnabled: boolean;
  attOffsetDb: number;
  adcOverloadWarning: boolean;
  preampOn: boolean;
  nr: NrConfigDto;
  zoomLevel: ZoomLevel;
  // PureSignal persisted settings — server is the source of truth, hydrated
  // into tx-store on connect so a fresh browser (no localStorage) sees the
  // operator's last dial-in. PsEnabled is the persisted standing arm
  // preference; PsSingle and TwoToneEnabled remain session-only.
  psEnabled: boolean;
  psAuto: boolean;
  psPtol: boolean;
  psAutoAttenuate: boolean;
  psMoxDelaySec: number;
  psLoopDelaySec: number;
  psAmpDelayNs: number;
  // psHwPeak is the live operator-tunable HW-peak; psHwPeakDefault is the
  // per-board factory default frozen by RadioService at connect time. UI
  // shows a "differs from default" hint when they don't match.
  // mi0bot ref: PSForm.cs:830 `pbWarningSetPk.Visible = _PShwpeak !=
  // HardwareSpecific.PSDefaultPeak;`.
  psHwPeak: number;
  psHwPeakDefault: number;
  // Live PS TX feedback attenuation (dB) and the per-board floor (HL2 -28,
  // others 0; max 31). Operator can set it directly as a manual alternative
  // to AutoAttenuate; restored on connect.
  psTxFeedbackAttenuationDb: number;
  psTxFeedbackAttenuationDbMin: number;
  // Server raises this when calcc is alive (PS armed + keyed) for >5 s with
  // CalibrationAttempts pinned at 0 — almost always means hw_peak is set
  // higher than the actual TX envelope peak. Drives the HW-peak warning
  // banner in the PURESIGNAL panel.
  psCalibrationStalled?: boolean;
  psIntsSpiPreset: string;
  psFeedbackSource: 'internal' | 'external';
  txMonitorEnabled: boolean;
  // Drive slider state — server is authoritative, hydrated into tx-store on
  // every fresh RadioStateDto so a relaunch picks up the operator's last
  // value instead of the localStorage default clobbering the server's
  // persisted value on connect.
  drivePercent: number;
  tunePercent: number;
  // TX pre-key (MOX) delay in ms (issue #630). Withholds RF after a UI MOX/TUNE
  // key-down so an external amp's T/R relay settles before RF. Server-clamped
  // below the PS MOX hold-off; hydrated on connect like the drive sliders.
  txMoxPreKeyDelayMs: number;
  twoToneFreq1: number;
  twoToneFreq2: number;
  twoToneMag: number;
  // CFC (Continuous Frequency Compressor) — issue #123. Always present
  // after normalisation; falls back to CFC_CONFIG_DEFAULT when the server
  // omits it (legacy state frames).
  cfc: CfcConfigDto;
  // Hardware NCO. Independent of vfoHz — the panadapter centres on radioLoHz
  // and WDSP's shift stage relocates the operator's tuned signal so it still
  // demodulates. Moves only on explicit retune (/api/radio/lo, band change,
  // external CAT). The legacy CTUN toggle was removed in the pure-pan rework
  // (PRD: docs/prd/panfall_behavior.md); this is now the only tuning model.
  radioLoHz: number;
  // CW sidetone pitch in Hz. Today a baked-in constant (600); exposed so
  // the frontend doesn't duplicate it. Will become configurable later.
  cwPitchHz: number;
  // CTUN (click-tune / centred tuning). When true, a panadapter click tunes
  // the dial off-centre and leaves radioLoHz frozen (the gesture skips the
  // view-centre nudge so the dial marker roams); when false, a click recentres
  // the display on the tuned frequency. Toggled via setCtun → POST
  // /api/radio/ctun. See docs/prd/panfall_behavior.md.
  ctunEnabled: boolean;
};

// CFC mirrors Zeus.Contracts.CfcConfig. Bands array is fixed at 10 entries
// — the panel layout depends on it; the server validates the same.
export type CfcBandDto = {
  freqHz: number;
  compLevelDb: number;
  postGainDb: number;
};
export type CfcConfigDto = {
  enabled: boolean;
  postEqEnabled: boolean;
  preCompDb: number;
  prePeqDb: number;
  bands: CfcBandDto[];
};

export type CfcPresetDto = {
  name: string;
  config: CfcConfigDto;
  createdUtc: string;
  updatedUtc: string;
};

export type TxStationProfileDto = {
  id: string;
  label: string;
  summary: string;
  applyTitle: string;
  audioSuiteRoute: 'native' | 'vst';
  audioSuiteBypassed: boolean;
  audioSuiteProfileName?: string | null;
  micGainDb: number;
  levelerMaxGainDb: number;
  txLeveling: TxLevelingConfigDto;
  cfcConfig: CfcConfigDto;
  lowCutHz: number;
  highCutHz: number;
  spectralDensity: number;
};

export type TxStationProfilesResponseDto = {
  profiles: TxStationProfileDto[];
};

export type TxFidelityPolicyDto = {
  profileId: string;
  targetSpectralDensity: number;
};

export const TX_FIDELITY_POLICY_DEFAULT: TxFidelityPolicyDto = {
  profileId: 'studio-ssb',
  targetSpectralDensity: 55,
};

// Pihpsdr classic-mode default — voice-band split the operator recognises
// from PowerSDR. Master OFF + zeroed comp/post means a fresh enable is
// audibly transparent. Mirrors CfcConfig.Default on the server.
export const CFC_CONFIG_DEFAULT: CfcConfigDto = {
  enabled: false,
  postEqEnabled: false,
  preCompDb: 0,
  prePeqDb: 0,
  bands: [
    { freqHz: 50,   compLevelDb: 0, postGainDb: 0 },
    { freqHz: 100,  compLevelDb: 0, postGainDb: 0 },
    { freqHz: 200,  compLevelDb: 0, postGainDb: 0 },
    { freqHz: 500,  compLevelDb: 0, postGainDb: 0 },
    { freqHz: 1000, compLevelDb: 0, postGainDb: 0 },
    { freqHz: 1500, compLevelDb: 0, postGainDb: 0 },
    { freqHz: 2000, compLevelDb: 0, postGainDb: 0 },
    { freqHz: 2500, compLevelDb: 0, postGainDb: 0 },
    { freqHz: 3000, compLevelDb: 0, postGainDb: 0 },
    { freqHz: 5000, compLevelDb: 0, postGainDb: 0 },
  ],
};

export type FilterPresetDto = {
  slotName: string;
  label: string;
  lowHz: number;
  highHz: number;
  isVar: boolean;
};

export type RadioInfoDto = {
  macAddress: string;
  ipAddress: string;
  boardId: string;
  firmwareVersion: string;
  busy: boolean;
  details: Record<string, string> | null;
};

export type HardwareDiagnosticItemDto = {
  field: string;
  source?: string;
  status: string;
  notes: string;
};

export type HardwareFeatureSurfaceDto = {
  id: string;
  title: string;
  category: string;
  implementationStatus: string;
  userConfigurable: boolean;
  source: string;
  telemetryPaths: string[];
  candidateControls: string[];
  safetyClass: string;
  notes: string;
};

export type HardwareDisplayBufferDiagnosticsDto = {
  valid: boolean;
  ageMs: number | null;
  validBins: number;
  minDb: number | null;
  maxDb: number | null;
  meanDb: number | null;
  dynamicRangeDb: number | null;
};

export type HardwareDisplayDiagnosticsDto = {
  schemaVersion: number;
  status: string;
  clientCount: number;
  framesBroadcast: number;
  lastSeq: number;
  lastFrameAgeMs: number | null;
  lastFrameUnixMs: number | null;
  panValid: boolean;
  waterfallValid: boolean;
  panSource: string;
  waterfallSource: string;
  keyed: boolean;
  psMonitorRequested: boolean;
  psFeedbackCorrecting: boolean;
  width: number;
  centerHz: number | null;
  hzPerPixel: number | null;
  pan: HardwareDisplayBufferDiagnosticsDto;
  waterfall: HardwareDisplayBufferDiagnosticsDto;
  diagnosticRecommendation: string | null;
};

export type HardwareDspDiagnosticsDto = {
  schemaVersion: number;
  engine: string;
  engineKind: string;
  wdspActive: boolean;
  synthetic: boolean;
  wdspNativeLoadable: boolean;
  wdspEmnrPost2Available: boolean;
  wdspNr4SbnrAvailable: boolean;
  nr4Readiness: string;
  requestedNrMode: string;
  effectiveNrMode: string;
  channelId: number;
  sampleRateHz: number;
  displayWidth: number;
  tickRateHz: number;
  audioOutputRateHz: number;
  txBlockSamples: number;
  txOutputSamples: number;
  txMonitorRequested: boolean;
  rxSinkAttached: boolean;
  audioSinkCount: number;
  monitorBacklogSamples: number;
  display: HardwareDisplayDiagnosticsDto;
  wdspWisdomPhase: string;
  wdspWisdomStatus: string;
  readiness: string;
};

export type HardwarePureSignalDiagnosticsDto = {
  schemaVersion: number;
  enabled: boolean;
  monitorEnabled: boolean;
  auto: boolean;
  single: boolean;
  autoAttenuate: boolean;
  feedbackSource: 'internal' | 'external';
  externalFeedback: boolean;
  externalFeedbackPathSupported: boolean;
  rfBypassRequired: boolean;
  rfBypassSelected: boolean;
  feedbackLevelRaw: number;
  feedbackLevelPct: number;
  feedbackTargetRaw: number;
  feedbackUsableMinRaw: number;
  feedbackUsableMaxRaw: number;
  feedbackCenteredMinRaw: number;
  feedbackCenteredMaxRaw: number;
  txFeedbackAttenuationDb: number;
  txFeedbackAttenuationDbMin: number;
  hwPeak: number;
  hwPeakDefault: number;
  calState: number;
  correcting: boolean;
  calibrationStalled: boolean;
  healthStatus: string;
  manualReference: string;
  diagnosticRecommendation: string | null;
};

export type FrontendDspSceneDiagnosticsDto = {
  schemaVersion: number;
  available: boolean;
  ageMs: number | null;
  status: string;
  fresh: boolean;
  stale: boolean;
  diagnosticRecommendation: string | null;
  atUtc: string | null;
  sourceAtUtc: string | null;
  sourceAgeMs: number | null;
  sourceClockSkewMs: number | null;
  sourceClientId: string | null;
  mode: RxMode | null;
  signalProfile: string | null;
  signalReason: string | null;
  smartNrProfile: string | null;
  smartNrReason: string | null;
  smartNrRecommendation: string | null;
  smartNrHeldByRxChain: boolean | null;
  smartNrRxChainLabel: string | null;
  smartNrRxChainRecommendation: string | null;
  smartNrRxChainTone: string | null;
  smartNrRxChainScore: number | null;
  maxSnrDb: number | null;
  coherentMaxSnrDb: number | null;
  occupiedPct: number | null;
  coherentOccupiedPct: number | null;
  impulsivePct: number | null;
  peakCount: number | null;
  coherentPeakCount: number | null;
  coherentSubthresholdSignal: boolean | null;
};

export type FrontendDspSceneDiagnosticsPayload = {
  sourceAtUtc?: string | null;
  sourceClientId?: string | null;
  mode?: RxMode | null;
  signalProfile?: string | null;
  signalReason?: string | null;
  smartNrProfile?: string | null;
  smartNrReason?: string | null;
  smartNrRecommendation?: string | null;
  smartNrHeldByRxChain?: boolean | null;
  smartNrRxChainLabel?: string | null;
  smartNrRxChainRecommendation?: string | null;
  smartNrRxChainTone?: string | null;
  smartNrRxChainScore?: number | null;
  maxSnrDb?: number | null;
  coherentMaxSnrDb?: number | null;
  occupiedPct?: number | null;
  coherentOccupiedPct?: number | null;
  impulsivePct?: number | null;
  peakCount?: number | null;
  coherentPeakCount?: number | null;
  coherentSubthresholdSignal?: boolean | null;
};

export type SmartNrConditionDto = {
  schemaVersion: number;
  available: boolean;
  status: string;
  fresh: boolean;
  stale: boolean;
  ageMs: number | null;
  atUtc: string | null;
  sourceAtUtc: string | null;
  sourceAgeMs: number | null;
  sourceClockSkewMs: number | null;
  sourceClientId: string | null;
  mode: RxMode | null;
  profile: string | null;
  reason: string | null;
  recommendation: string | null;
  heldByRxChain: boolean | null;
  rxChainLabel: string | null;
  rxChainRecommendation: string | null;
  rxChainTone: string | null;
  rxChainScore: number | null;
  maxSnrDb: number | null;
  coherentMaxSnrDb: number | null;
  occupiedPct: number | null;
  coherentOccupiedPct: number | null;
  impulsivePct: number | null;
  peakCount: number | null;
  coherentPeakCount: number | null;
  coherentSubthresholdSignal: boolean | null;
  wdspActive: boolean;
  wdspNativeLoadable: boolean;
  wdspEmnrPost2Available: boolean;
  wdspNr4SbnrAvailable: boolean;
  nr4Readiness: string;
  requestedNrMode: string;
  effectiveNrMode: string;
  rxChain: SmartNrRxChainRuntimeDto;
  diagnosticRecommendation: string | null;
  generatedUtc: string;
};

export type SmartNrRxChainRuntimeDto = {
  schemaVersion: number;
  source: string;
  autoAgcEnabled: boolean;
  agcMode: string;
  agcTopDb: number;
  agcOffsetDb: number;
  effectiveAgcTopDb: number;
  autoAttEnabled: boolean;
  adcProtectionEnabled: boolean;
  attenDb: number;
  attOffsetDb: number;
  effectiveAttenDb: number;
  adcOverloadWarning: boolean;
  adcOverloadLevel: number;
  lastOverloadBits: number;
  adc0MaxMagnitude: number | null;
  adc1MaxMagnitude: number | null;
  adc0MaxMagnitudeAtOverload: number;
  adc1MaxMagnitudeAtOverload: number;
  lastAdcTelemetryUtc: string | null;
  squelchEnabled: boolean;
  squelchAdaptive: boolean;
  squelchLevel: number;
  preampOn: boolean;
};

export type ExternalPttStatusDto = {
  schemaVersion: number;
  available: boolean;
  protocol: string;
  hardwarePtt: boolean | null;
  cwKeyDown: boolean | null;
  ownedMox: boolean;
  hangTimeMs: number;
  moxOn: boolean;
  tunOn: boolean;
  twoToneOn: boolean;
  moxOwner: string | null;
  cwMode: boolean;
  sidetoneAvailable: boolean;
  diagnosticRecommendation: string | null;
  generatedUtc: string;
};

export type HardwareKeyingStatusDto = {
  schemaVersion: number;
  activeProtocol: 'P1' | 'P2' | null;
  p1Packets: number;
  p1LastUpdatedUtc: string | null;
  p1HardwarePtt: boolean | null;
  p1CwKeyDown: boolean | null;
  p2Packets: number;
  p2LastUpdatedUtc: string | null;
  p2PttIn: boolean | null;
  p2DotIn: boolean | null;
  p2DashIn: boolean | null;
  p2SidetoneActive: boolean | null;
  externalPtt: ExternalPttStatusDto;
  diagnosticRecommendation: string | null;
  generatedUtc: string;
};

export type RadioPowerReadingDto = {
  packets: number;
  lastUpdatedUtc: string | null;
  exciterAdc: number | null;
  fwdAdc: number | null;
  revAdc: number | null;
  fwdWatts: number | null;
  refWatts: number | null;
  swr: number | null;
};

export type RadioPowerCalibrationDto = {
  schemaVersion: number;
  activeProtocol: 'P1' | 'P2' | null;
  connectedBoard: string;
  effectiveBoard: string;
  orionMkIIVariant: string;
  calibrationBoard: string;
  bridgeVolt: number;
  refVoltage: number;
  adcCalOffset: number;
  calibrationMaxWatts: number;
  calibrationFallbackApplied: boolean;
  capabilityMaxPowerWatts: number;
  p1: RadioPowerReadingDto;
  p2: RadioPowerReadingDto;
  diagnosticRecommendation: string | null;
  generatedUtc: string;
};

export type RadioSupplyReadingDto = {
  packets: number;
  lastUpdatedUtc: string | null;
  supplyVoltsAdc: number | null;
  supplyVolts: number | null;
  rawScaledSupplyVolts: number | null;
  supplyVoltsTrusted: boolean;
  scaleStatus: string;
};

export type RadioSupplyAlarmsDto = {
  schemaVersion: number;
  activeProtocol: 'P1' | 'P2' | null;
  effectiveBoard: string;
  orionMkIIVariant: string;
  supportsSupplyTelemetry: boolean;
  adcSupplyMv: number;
  activeThresholdsConfigured: boolean;
  alarmActive: boolean;
  alarmStatus: string;
  p1: RadioSupplyReadingDto;
  p2: RadioSupplyReadingDto;
  diagnosticRecommendation: string | null;
  generatedUtc: string;
};

export type RadioNetworkCountersDto = {
  attached: boolean;
  totalFrames: number;
  droppedFrames: number;
  dropRatioPct: number;
  hiPriorityPackets: number | null;
  psPairedPackets: number | null;
};

export type RadioNetworkProfileDto = {
  schemaVersion: number;
  connectionStatus: string;
  endpoint: string | null;
  activeProtocol: 'P1' | 'P2' | null;
  sampleRateHz: number;
  connectedBoard: string;
  effectiveBoard: string;
  orionMkIIVariant: string;
  transport: string;
  p1: RadioNetworkCountersDto;
  p2: RadioNetworkCountersDto;
  healthStatus: string;
  diagnosticRecommendation: string | null;
  generatedUtc: string;
};

export type UserIoLineDto = {
  id: string;
  kind: string;
  label: string;
  rawAdc: number | null;
  normalizedPct: number | null;
  digitalState: boolean | null;
};

export type UserIoLabelsDto = {
  schemaVersion: number;
  activeProtocol: 'P1' | 'P2' | null;
  p2Attached: boolean;
  p2Packets: number;
  p2LastUpdatedUtc: string | null;
  lines: UserIoLineDto[];
  diagnosticRecommendation: string | null;
  generatedUtc: string;
};

export type UserIoActionsDto = {
  schemaVersion: number;
  activeProtocol: 'P1' | 'P2' | null;
  p2Attached: boolean;
  p2Packets: number;
  p2LastUpdatedUtc: string | null;
  actionBindingsConfigured: boolean;
  lines: UserIoLineDto[];
  diagnosticRecommendation: string | null;
  generatedUtc: string;
};

export type RadioDigInDiagnosticsDto = {
  schemaVersion: number;
  activeProtocol: 'P1' | 'P2' | null;
  connectedBoard: string;
  effectiveBoard: string;
  orionMkIIVariant: string;
  p2Attached: boolean;
  p2Packets: number;
  p2LastUpdatedUtc: string | null;
  userDigitalIn: number | null;
  txDisableLineId: string;
  txDisableLineName: string;
  txDisableBit: number;
  txDisableRawHigh: boolean | null;
  txDisableActive: boolean | null;
  txDisablePolarity: string;
  txDisableMappingStatus: string;
  txInhibitBehaviorArmed: boolean;
  cwKeyTipSource: string;
  cwKeyTipDown: boolean | null;
  cwDashInputDown: boolean | null;
  manualReference: string | null;
  diagnosticRecommendation: string | null;
  generatedUtc: string;
};

export type TxRingDiagnosticsDto = {
  totalWritten: number;
  totalRead: number;
  count: number;
  dropped: number;
  capacity: number;
  recentMag: number;
};

export type TxIngestDiagnosticsDto = {
  totalMicSamples: number;
  totalTxBlocks: number;
  droppedFrames: number;
};

export type Protocol2TxIqDiagnosticsDto = {
  inputComplexSamples: number;
  packetsQueued: number;
  packetsSent: number;
  queuedPackets: number;
  queueWriteFailures: number;
  sendFailures: number;
  resetDrainedPackets: number;
  scratchComplexSamples: number;
  nextSequence: number;
  lastPacketsPerSecond: number;
  lastFifoModelSamples: number;
  lastRateTimestampUtc: string | null;
  senderRunning: boolean;
};

export type TxEgressHealthDto = {
  schemaVersion: number;
  generatedUtc: string;
  activeTransport: string;
  healthStatus: string;
  p2Attached: boolean;
  p2Live: boolean;
  p2LastActivityAgeMs: number | null;
  p1RingDropRatioPct: number;
  hostMoxOn: boolean;
  hostTunOn: boolean;
  hostTwoToneOn: boolean;
  hostTxActive: boolean;
  hardwarePtt: boolean | null;
  forwardWatts: number | null;
  rfDetected: boolean;
  rfEvidenceStatus: string;
  qualityScore: number;
  qualityTone: string;
  p2PacketRateStatus: string;
  p2LastPacketsPerSecond: number | null;
  p2FifoModelSamples: number | null;
  p2QueuedPackets: number | null;
  p2TransportFailures: number;
  qualityReasons: string[];
  txDutyProfile: string;
  continuousDutyRecommendedMaxWatts: number | null;
  continuousDutyLimitExceeded: boolean;
  continuousDutyManualReference: string | null;
  diagnosticRecommendation: string | null;
};

export type TxPluginDiagnosticsDto = {
  masterBypassed: boolean;
  bypassedForRemoteTx: boolean;
};

export type VstEngineDiagnosticsDto = {
  active: boolean;
  degradedBlocks: number;
};

export type TxStageDiagnosticsDto = {
  schemaVersion: number;
  source: string;
  status: string;
  hostTxActive: boolean;
  micPkDbfs: number | null;
  micAvDbfs: number | null;
  eqPkDbfs: number | null;
  eqAvDbfs: number | null;
  lvlrPkDbfs: number | null;
  lvlrAvDbfs: number | null;
  lvlrGrDb: number;
  cfcPkDbfs: number | null;
  cfcAvDbfs: number | null;
  cfcGrDb: number;
  compPkDbfs: number | null;
  compAvDbfs: number | null;
  alcPkDbfs: number | null;
  alcAvDbfs: number | null;
  alcGrDb: number;
  outPkDbfs: number | null;
  outAvDbfs: number | null;
  diagnosticRecommendation: string | null;
};

export type TxDiagnosticsDto = {
  generatedUtc: string;
  iqSourceType: string | null;
  iqSourceIsRing: boolean;
  ring: TxRingDiagnosticsDto;
  ingest: TxIngestDiagnosticsDto;
  protocol2: Protocol2TxIqDiagnosticsDto | null;
  stage: TxStageDiagnosticsDto;
  egress: TxEgressHealthDto;
  txPlugins: TxPluginDiagnosticsDto | null;
  vstEngine: VstEngineDiagnosticsDto | null;
};

export type HardwareP1DiagnosticsDto = {
  packets: number;
  lastUpdatedUtc: string | null;
  lastC0Address: number | null;
  lastAin0: number | null;
  lastAin1: number | null;
  exciterAdc: number | null;
  fwdAdc: number | null;
  revAdc: number | null;
  userAdc0: number | null;
  userAdc1: number | null;
  supplyVoltsAdc: number | null;
  adcOverloadBits: number;
  hardwarePtt: boolean | null;
  cwKeyDown: boolean | null;
};

export type HardwareP2DiagnosticsDto = {
  packets: number;
  lastUpdatedUtc: string | null;
  pttIn: boolean | null;
  dotIn: boolean | null;
  dashIn: boolean | null;
  pllLocked: boolean | null;
  sidetoneActive: boolean | null;
  adcOverloadBits: number | null;
  exciterAdc: number | null;
  fwdAdc: number | null;
  revAdc: number | null;
  adc0MaxMagnitude: number | null;
  adc1MaxMagnitude: number | null;
  adc0MaxMagnitudeAtOverload: number;
  adc1MaxMagnitudeAtOverload: number;
  supplyVoltsAdc: number | null;
  userAdc0: number | null;
  userAdc1: number | null;
  userAdc2: number | null;
  userAdc3: number | null;
  userDigitalIn: number | null;
  hardwareLeds: number | null;
};

export type HardwareMapByteDto = {
  offset: number;
  hexOffset: string;
  known: string | null;
  first: number;
  last: number;
  min: number;
  max: number;
  changedMask: number;
  changedMaskHex: string;
  changeCount: number;
  bitSetCounts: number[];
  bitChangeCounts: number[];
};

export type HardwareMapWordDto = {
  offset: number;
  hexOffset: string;
  known: string | null;
  first: number;
  last: number;
  min: number;
  max: number;
  changeCount: number;
};

export type HardwareByteStreamMapDto = {
  stream: string;
  source: string;
  samples: number;
  startedUtc: string | null;
  lastUpdatedUtc: string | null;
  length: number;
  lastHex: string;
  changedByteCount: number;
  changedWordCount: number;
  bytes: HardwareMapByteDto[];
  words: HardwareMapWordDto[];
};

export type HardwareP1AddressMapDto = {
  c0Address: number;
  hexAddress: string;
  samples: number;
  lastRawC0: number;
  lastRawC0Hex: string;
  firstAin0: number;
  firstAin1: number;
  lastAin0: number;
  lastAin1: number;
  minAin0: number;
  maxAin0: number;
  minAin1: number;
  maxAin1: number;
  ain0ChangeCount: number;
  ain1ChangeCount: number;
  knownAin0: string | null;
  knownAin1: string | null;
  notes: string;
};

export type HardwareP1MapDto = {
  stream: string;
  source: string;
  samples: number;
  startedUtc: string | null;
  lastUpdatedUtc: string | null;
  addresses: HardwareP1AddressMapDto[];
  rawGap: string;
};

export type HardwareP1MarkerDeltaDto = {
  c0Address: number;
  hexAddress: string;
  knownAin0: string | null;
  knownAin1: string | null;
  previousAin0: number | null;
  currentAin0: number | null;
  previousAin1: number | null;
  currentAin1: number | null;
  ain0Delta: number | null;
  ain1Delta: number | null;
  ain0ChangeCountDelta: number;
  ain1ChangeCountDelta: number;
};

export type HardwareByteMarkerDeltaDto = {
  offset: number;
  hexOffset: string;
  known: string | null;
  previous: number | null;
  current: number | null;
  previousHex: string;
  currentHex: string;
  xorMask: number;
  xorMaskHex: string;
  intervalChangeCount: number;
  intervalChangedBits: number[];
};

export type HardwareWordMarkerDeltaDto = {
  offset: number;
  hexOffset: string;
  known: string | null;
  previous: number | null;
  current: number | null;
  previousHex: string;
  currentHex: string;
  valueDelta: number | null;
  intervalChangeCount: number;
};

export type HardwareMappingMarkerDeltaDto = {
  previousId: number | null;
  previousLabel: string | null;
  baseline: boolean;
  p1SampleDelta: number;
  p2SampleDelta: number;
  p1ChangedAddresses: HardwareP1MarkerDeltaDto[];
  p2ChangedBytes: HardwareByteMarkerDeltaDto[];
  p2ChangedWords: HardwareWordMarkerDeltaDto[];
};

export type HardwareMappingMarkerDto = {
  id: number;
  label: string;
  notes: string | null;
  createdUtc: string;
  activeProtocol: 'P1' | 'P2' | null;
  endpoint: string | null;
  p1Packets: number;
  p2Packets: number;
  p1Samples: number;
  p2Samples: number;
  p1LastUpdatedUtc: string | null;
  p2LastUpdatedUtc: string | null;
  sincePrevious: HardwareMappingMarkerDeltaDto;
};

export type HardwareMappingDto = {
  schemaVersion: number;
  p1: HardwareP1MapDto;
  p2HiPriority: HardwareByteStreamMapDto;
  markers: HardwareMappingMarkerDto[];
};

export type HardwareDiagnosticsDto = {
  hardwareDiagnosticsApiVersion: number;
  generatedUtc: string;
  connectionStatus: ConnectionStatus;
  endpoint: string | null;
  vfoHz: number;
  sampleRate: number;
  mode: RxMode;
  connectedBoard: string;
  effectiveBoard: string;
  orionMkIIVariant: string;
  capabilities: BoardCapabilities;
  dsp: HardwareDspDiagnosticsDto;
  frontendDspScene: FrontendDspSceneDiagnosticsDto;
  pureSignal: HardwarePureSignalDiagnosticsDto;
  digIn: RadioDigInDiagnosticsDto;
  activeProtocol: 'P1' | 'P2' | null;
  p1: HardwareP1DiagnosticsDto;
  p2: HardwareP2DiagnosticsDto;
  mapping: HardwareMappingDto;
  referenceMap: HardwareDiagnosticItemDto[];
  candidateSettings: HardwareDiagnosticItemDto[];
  featureSurfaces: HardwareFeatureSurfaceDto[];
};

export type ConnectRequest = {
  endpoint: string;
  sampleRate: number;
  preampOn?: boolean;
  // Server accepts 0..3 (→ 0/10/20/30 dB attenuation).
  atten?: number;
  // Raw HPSDR board byte from discovery's details.rawBoardId. Passed to
  // /api/connect/p2 so the server knows the real board kind instead of
  // defaulting to OrionMkII for every P2 connection (issue #171 — Brick2
  // identifies as Hermes/0x01 on P2). Omit for manual connects where the
  // board is unknown.
  boardId?: number;
};

// System.Text.Json can serialize enums as either numbers (default) or strings
// (with JsonStringEnumConverter). Accept both so the client stays robust to
// server config drift.
const STATUS_ORDER: readonly ConnectionStatus[] = [
  'Disconnected',
  'Connecting',
  'Connected',
  'Error',
];

const MODE_ORDER: readonly RxMode[] = [
  'LSB',
  'USB',
  'CWL',
  'CWU',
  'AM',
  'FM',
  'SAM',
  'DSB',
  'DIGL',
  'DIGU',
];

const NR_MODE_ORDER: readonly NrMode[] = ['Off', 'Anr', 'Emnr', 'Sbnr'];
const NB_MODE_ORDER: readonly NbMode[] = ['Off', 'Nb1', 'Nb2'];

export function normalizeStatus(v: unknown): ConnectionStatus {
  if (typeof v === 'string') {
    return (STATUS_ORDER as readonly string[]).includes(v)
      ? (v as ConnectionStatus)
      : 'Error';
  }
  if (typeof v === 'number' && Number.isInteger(v)) {
    return STATUS_ORDER[v] ?? 'Error';
  }
  return 'Error';
}

function modeFromWire(v: unknown): RxMode | null {
  if (typeof v === 'string') {
    return (MODE_ORDER as readonly string[]).includes(v)
      ? (v as RxMode)
      : null;
  }
  if (typeof v === 'number' && Number.isInteger(v)) {
    return MODE_ORDER[v] ?? null;
  }
  return null;
}

export function normalizeMode(v: unknown): RxMode {
  return modeFromWire(v) ?? 'USB';
}

export function normalizeNrMode(v: unknown): NrMode {
  if (typeof v === 'string') {
    return (NR_MODE_ORDER as readonly string[]).includes(v)
      ? (v as NrMode)
      : 'Off';
  }
  if (typeof v === 'number' && Number.isInteger(v)) {
    return NR_MODE_ORDER[v] ?? 'Off';
  }
  return 'Off';
}

export function normalizeNbMode(v: unknown): NbMode {
  if (typeof v === 'string') {
    return (NB_MODE_ORDER as readonly string[]).includes(v)
      ? (v as NbMode)
      : 'Off';
  }
  if (typeof v === 'number' && Number.isInteger(v)) {
    return NB_MODE_ORDER[v] ?? 'Off';
  }
  return 'Off';
}

// AGC mode wire order matches the AgcMode enum values (Fixed=0..Custom=5), so a
// numeric payload maps by index. Strings are validated against the union.
const AGC_MODE_ORDER: readonly AgcMode[] = [
  'Fixed',
  'Long',
  'Slow',
  'Med',
  'Fast',
  'Custom',
];

export function normalizeAgcMode(v: unknown): AgcMode {
  if (typeof v === 'string') {
    return (AGC_MODE_ORDER as readonly string[]).includes(v)
      ? (v as AgcMode)
      : 'Med';
  }
  if (typeof v === 'number' && Number.isInteger(v)) {
    return AGC_MODE_ORDER[v] ?? 'Med';
  }
  return 'Med';
}

export function normalizeAgc(raw: unknown): AgcConfigDto {
  if (!raw || typeof raw !== 'object') return { ...AGC_CONFIG_DEFAULT };
  const r = raw as Record<string, unknown>;
  return {
    mode: normalizeAgcMode(r.mode),
    slope: nullableInt(r.slope),
    decayMs: nullableInt(r.decayMs),
    hangMs: nullableInt(r.hangMs),
    hangThreshold: nullableInt(r.hangThreshold),
    fixedGainDb: nullableNumber(r.fixedGainDb),
  };
}

// RX squelch normaliser. A null/garbage payload (older server, missing field)
// collapses to the off default. `level` is clamped to 0..100 so a malformed
// server value can never push the slider out of range.
export function normalizeSquelch(raw: unknown): SquelchConfigDto {
  if (!raw || typeof raw !== 'object') return { ...SQUELCH_CONFIG_DEFAULT };
  const r = raw as Record<string, unknown>;
  const level =
    typeof r.level === 'number' && Number.isFinite(r.level)
      ? Math.max(0, Math.min(100, Math.round(r.level)))
      : 0;
  return {
    enabled: typeof r.enabled === 'boolean' ? r.enabled : false,
    level,
    adaptive: typeof r.adaptive === 'boolean' ? r.adaptive : true,
  };
}

// TX leveling normaliser. A null/garbage payload (older server, missing field)
// collapses to the defaults. Each numeric field is clamped to its Thetis range
// and the booleans coerce strictly so a malformed server value can never push a
// control out of range. Mirrors normalizeSquelch's defensive shape.
export function normalizeTxLeveling(raw: unknown): TxLevelingConfigDto {
  if (!raw || typeof raw !== 'object')
    return { ...TX_LEVELING_CONFIG_DEFAULT };
  const r = raw as Record<string, unknown>;
  const num = (v: unknown, fallback: number, min: number, max: number) =>
    typeof v === 'number' && Number.isFinite(v)
      ? Math.max(min, Math.min(max, v))
      : fallback;
  const int = (v: unknown, fallback: number, min: number, max: number) =>
    typeof v === 'number' && Number.isFinite(v)
      ? Math.max(min, Math.min(max, Math.round(v)))
      : fallback;
  return {
    alcMaxGainDb: num(r.alcMaxGainDb, TX_LEVELING_CONFIG_DEFAULT.alcMaxGainDb, 0, 120),
    alcDecayMs: int(r.alcDecayMs, TX_LEVELING_CONFIG_DEFAULT.alcDecayMs, 1, 50),
    levelerEnabled:
      typeof r.levelerEnabled === 'boolean'
        ? r.levelerEnabled
        : TX_LEVELING_CONFIG_DEFAULT.levelerEnabled,
    levelerDecayMs: int(r.levelerDecayMs, TX_LEVELING_CONFIG_DEFAULT.levelerDecayMs, 1, 5000),
    compressorEnabled:
      typeof r.compressorEnabled === 'boolean'
        ? r.compressorEnabled
        : TX_LEVELING_CONFIG_DEFAULT.compressorEnabled,
    compressorGainDb: num(r.compressorGainDb, TX_LEVELING_CONFIG_DEFAULT.compressorGainDb, 0, 20),
  };
}

// `null` means "no operator override yet — use engine default" and round-
// trips that signal back to the server. Anything else (number/bool) is
// preserved; missing keys collapse to null so an older server payload
// doesn't accidentally invent a value.
function nullableNumber(v: unknown): number | null {
  return typeof v === 'number' ? v : null;
}
function nullableBool(v: unknown): boolean | null {
  return typeof v === 'boolean' ? v : null;
}
function nullableInt(v: unknown): number | null {
  return typeof v === 'number' && Number.isInteger(v) ? v : null;
}

export function normalizeNr(raw: unknown): NrConfigDto {
  if (!raw || typeof raw !== 'object') return { ...NR_CONFIG_DEFAULT };
  const r = raw as Record<string, unknown>;
  return {
    nrMode: normalizeNrMode(r.nrMode),
    anfEnabled: Boolean(r.anfEnabled),
    snbEnabled: Boolean(r.snbEnabled),
    nbpNotchesEnabled: Boolean(r.nbpNotchesEnabled),
    nbMode: normalizeNbMode(r.nbMode),
    nbThreshold:
      typeof r.nbThreshold === 'number'
        ? r.nbThreshold
        : NR_CONFIG_DEFAULT.nbThreshold,
    emnrPost2Run: nullableBool(r.emnrPost2Run),
    emnrPost2Factor: nullableNumber(r.emnrPost2Factor),
    emnrPost2Nlevel: nullableNumber(r.emnrPost2Nlevel),
    emnrPost2Rate: nullableNumber(r.emnrPost2Rate),
    emnrPost2Taper: nullableInt(r.emnrPost2Taper),
    emnrGainMethod: nullableInt(r.emnrGainMethod),
    emnrNpeMethod: nullableInt(r.emnrNpeMethod),
    emnrAeRun: nullableBool(r.emnrAeRun),
    emnrTrainT1: nullableNumber(r.emnrTrainT1),
    emnrTrainT2: nullableNumber(r.emnrTrainT2),
    nr4ReductionAmount: nullableNumber(r.nr4ReductionAmount),
    nr4SmoothingFactor: nullableNumber(r.nr4SmoothingFactor),
    nr4WhiteningFactor: nullableNumber(r.nr4WhiteningFactor),
    nr4NoiseRescale: nullableNumber(r.nr4NoiseRescale),
    nr4PostFilterThreshold: nullableNumber(r.nr4PostFilterThreshold),
    nr4NoiseScalingType: nullableInt(r.nr4NoiseScalingType),
    nr4Position: nullableInt(r.nr4Position),
  };
}

function normalizeAdcProtectionConfig(raw: unknown): AdcProtectionConfigDto {
  if (!raw || typeof raw !== 'object') return { ...ADC_PROTECTION_CONFIG_DEFAULT };
  const r = raw as Record<string, unknown>;
  return {
    enabled:
      typeof r.enabled === 'boolean'
        ? r.enabled
        : ADC_PROTECTION_CONFIG_DEFAULT.enabled,
    attackMs:
      typeof r.attackMs === 'number'
        ? r.attackMs
        : ADC_PROTECTION_CONFIG_DEFAULT.attackMs,
    releaseMs:
      typeof r.releaseMs === 'number'
        ? r.releaseMs
        : ADC_PROTECTION_CONFIG_DEFAULT.releaseMs,
    attackStepDb:
      typeof r.attackStepDb === 'number'
        ? r.attackStepDb
        : ADC_PROTECTION_CONFIG_DEFAULT.attackStepDb,
    releaseStepDb:
      typeof r.releaseStepDb === 'number'
        ? r.releaseStepDb
        : ADC_PROTECTION_CONFIG_DEFAULT.releaseStepDb,
    maxOffsetDb:
      typeof r.maxOffsetDb === 'number'
        ? r.maxOffsetDb
        : ADC_PROTECTION_CONFIG_DEFAULT.maxOffsetDb,
    warningThreshold:
      typeof r.warningThreshold === 'number'
        ? r.warningThreshold
        : ADC_PROTECTION_CONFIG_DEFAULT.warningThreshold,
    magnitudeSoftLimit:
      typeof r.magnitudeSoftLimit === 'number'
        ? r.magnitudeSoftLimit
        : ADC_PROTECTION_CONFIG_DEFAULT.magnitudeSoftLimit,
  };
}

export function normalizeAdcProtectionStatus(raw: unknown): AdcProtectionStatusDto {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    config: normalizeAdcProtectionConfig(r.config),
    attenDb: typeof r.attenDb === 'number' ? r.attenDb : 0,
    offsetDb: typeof r.offsetDb === 'number' ? r.offsetDb : 0,
    effectiveDb: typeof r.effectiveDb === 'number' ? r.effectiveDb : 0,
    warning: typeof r.warning === 'boolean' ? r.warning : false,
    overloadLevel: typeof r.overloadLevel === 'number' ? r.overloadLevel : 0,
    lastOverloadBits:
      typeof r.lastOverloadBits === 'number' ? r.lastOverloadBits : 0,
    adc0MaxMagnitude:
      typeof r.adc0MaxMagnitude === 'number' ? r.adc0MaxMagnitude : null,
    adc1MaxMagnitude:
      typeof r.adc1MaxMagnitude === 'number' ? r.adc1MaxMagnitude : null,
    adc0MaxMagnitudeAtOverload:
      typeof r.adc0MaxMagnitudeAtOverload === 'number'
        ? r.adc0MaxMagnitudeAtOverload
        : 0,
    adc1MaxMagnitudeAtOverload:
      typeof r.adc1MaxMagnitudeAtOverload === 'number'
        ? r.adc1MaxMagnitudeAtOverload
        : 0,
    lastTelemetryUtc:
      typeof r.lastTelemetryUtc === 'string' ? r.lastTelemetryUtc : null,
  };
}

export function normalizeState(raw: unknown): RadioStateDto {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    status: normalizeStatus(r.status),
    endpoint: typeof r.endpoint === 'string' ? r.endpoint : null,
    vfoHz: typeof r.vfoHz === 'number' ? r.vfoHz : 0,
    mode: normalizeMode(r.mode),
    filterLowHz: typeof r.filterLowHz === 'number' ? r.filterLowHz : 0,
    filterHighHz: typeof r.filterHighHz === 'number' ? r.filterHighHz : 0,
    filterPresetName: typeof r.filterPresetName === 'string' ? r.filterPresetName : null,
    filterAdvancedPaneOpen: typeof r.filterAdvancedPaneOpen === 'boolean' ? r.filterAdvancedPaneOpen : false,
    txFilterLowHz: typeof r.txFilterLowHz === 'number' ? r.txFilterLowHz : 150,
    txFilterHighHz: typeof r.txFilterHighHz === 'number' ? r.txFilterHighHz : 2850,
    sampleRate: typeof r.sampleRate === 'number' ? r.sampleRate : 0,
    // Default 80 matches WdspDspEngine.ApplyAgcDefaults and the Thetis
    // AGC_MEDIUM preset. Missing from older servers — tolerate absence.
    agcTopDb: typeof r.agcTopDb === 'number' ? r.agcTopDb : 80,
    // StateDto.Agc is nullable on the server (older clients) — fall back to the
    // Med default so the UI always has a mode to render.
    agc: normalizeAgc(r.agc),
    squelch: normalizeSquelch(r.squelch),
    txLeveling: normalizeTxLeveling(r.txLeveling),
    autoAgcEnabled: typeof r.autoAgcEnabled === 'boolean' ? r.autoAgcEnabled : false,
    agcOffsetDb: typeof r.agcOffsetDb === 'number' ? r.agcOffsetDb : 0,
    rxAfGainDb: typeof r.rxAfGainDb === 'number' ? r.rxAfGainDb : 0,
    // 0 dB = unity panel-gain (WDSP TXA open-time default).
    micGainDb: typeof r.micGainDb === 'number' ? r.micGainDb : 0,
    // 8 dB matches WdspDspEngine.DefaultLevelerMaxGainDb so older servers
    // without the field hydrate to the same ceiling the engine opens with.
    levelerMaxGainDb:
      typeof r.levelerMaxGainDb === 'number' ? r.levelerMaxGainDb : 8,
    // Attenuator value in dB, range 0..31 (HpsdrAtten.MaxDb). 4-button UI
    // sends 0/10/20/30 today; #23 will unlock the full fine-grained range.
    attenDb: typeof r.attenDb === 'number' ? r.attenDb : 0,
    // Auto-ATT control loop (server default ON); offset added to attenDb on
    // the hardware. adcOverloadWarning is OR'd across both ADCs with a small
    // hysteresis — flips back false on its own when the loop backs off.
    autoAttEnabled: typeof r.autoAttEnabled === 'boolean' ? r.autoAttEnabled : true,
    attOffsetDb: typeof r.attOffsetDb === 'number' ? r.attOffsetDb : 0,
    adcOverloadWarning:
      typeof r.adcOverloadWarning === 'boolean' ? r.adcOverloadWarning : false,
    preampOn: typeof r.preampOn === 'boolean' ? r.preampOn : false,
    // StateDto.Nr is nullable on the server (older clients) — fall back to
    // the engine's declared defaults so the UI has something to render.
    nr: normalizeNr(r.nr),
    zoomLevel: normalizeZoomLevel(r.zoomLevel),
    // PureSignal persisted settings. Defaults match RadioService.cs init and
    // PsSettingsEntry — older servers without the fields fall back cleanly.
    psEnabled: typeof r.psEnabled === 'boolean' ? r.psEnabled : false,
    psAuto: typeof r.psAuto === 'boolean' ? r.psAuto : true,
    psPtol: typeof r.psPtol === 'boolean' ? r.psPtol : false,
    psAutoAttenuate: typeof r.psAutoAttenuate === 'boolean' ? r.psAutoAttenuate : true,
    psMoxDelaySec: typeof r.psMoxDelaySec === 'number' ? r.psMoxDelaySec : 0.2,
    psLoopDelaySec: typeof r.psLoopDelaySec === 'number' ? r.psLoopDelaySec : 0,
    psAmpDelayNs: typeof r.psAmpDelayNs === 'number' ? r.psAmpDelayNs : 150,
    // mi0bot ref: PSForm.cs:830 / clsHardwareSpecific.cs:303-328 — server
    // freezes psHwPeakDefault at connect via ResolvePsHwPeak; psHwPeak is the
    // operator-tunable live value. UI compares them for the warning hint.
    psHwPeak: typeof r.psHwPeak === 'number' ? r.psHwPeak : 0.4072,
    psHwPeakDefault:
      typeof r.psHwPeakDefault === 'number' ? r.psHwPeakDefault : 0.4072,
    psTxFeedbackAttenuationDb:
      typeof r.psTxFeedbackAttenuationDb === 'number' ? r.psTxFeedbackAttenuationDb : 0,
    psTxFeedbackAttenuationDbMin:
      typeof r.psTxFeedbackAttenuationDbMin === 'number' ? r.psTxFeedbackAttenuationDbMin : 0,
    psCalibrationStalled:
      typeof r.psCalibrationStalled === 'boolean' ? r.psCalibrationStalled : false,
    psIntsSpiPreset: typeof r.psIntsSpiPreset === 'string' ? r.psIntsSpiPreset : '16/256',
    psFeedbackSource:
      r.psFeedbackSource === 'External' || r.psFeedbackSource === 'external' ? 'external' : 'internal',
    txMonitorEnabled:
      typeof r.txMonitorEnabled === 'boolean' ? r.txMonitorEnabled : false,
    // Drive sliders — server is authoritative. Defaults mirror RadioService
    // private-field seeds (_drivePct=0, _tunePct=10) so a state frame from an
    // older server (no DrivePct/TunePct fields) deserialises cleanly.
    drivePercent: typeof r.drivePct === 'number' ? r.drivePct : 0,
    tunePercent: typeof r.tunePct === 'number' ? r.tunePct : 10,
    txMoxPreKeyDelayMs:
      typeof r.txMoxPreKeyDelayMs === 'number' ? r.txMoxPreKeyDelayMs : 0,
    twoToneFreq1: typeof r.twoToneFreq1 === 'number' ? r.twoToneFreq1 : 700,
    twoToneFreq2: typeof r.twoToneFreq2 === 'number' ? r.twoToneFreq2 : 1900,
    twoToneMag: typeof r.twoToneMag === 'number' ? r.twoToneMag : 0.49,
    cfc: normalizeCfc(r.cfc),
    // Hardware NCO. Legacy server without the field → fall back to vfoHz so
    // the panadapter centre stays sensible during a rolling deploy. Any
    // stale ctunEnabled field from an old payload is ignored.
    radioLoHz:
      typeof r.radioLoHz === 'number' && r.radioLoHz > 0
        ? r.radioLoHz
        : typeof r.vfoHz === 'number'
        ? r.vfoHz
        : 0,
    cwPitchHz: typeof r.cwPitchHz === 'number' ? r.cwPitchHz : 600,
    // Legacy server without the field → CTUN off (classic recenter-on-click).
    ctunEnabled: typeof r.ctunEnabled === 'boolean' ? r.ctunEnabled : false,
  };
}

// Normalise the wire CFC config. Missing or malformed payload falls back to
// CFC_CONFIG_DEFAULT so a legacy server (no `cfc` field) still gives the
// settings panel something to render. Bands are clamped to length 10 by
// padding with the matching default-band slot if the server somehow returns
// fewer; extras are truncated. The server validates length on POST.
export function normalizeCfc(raw: unknown): CfcConfigDto {
  if (!raw || typeof raw !== 'object') return cloneCfc(CFC_CONFIG_DEFAULT);
  const r = raw as Record<string, unknown>;
  const rawBands = Array.isArray(r.bands) ? (r.bands as unknown[]) : [];
  const bands: CfcBandDto[] = [];
  for (let i = 0; i < 10; i++) {
    const b = (rawBands[i] ?? {}) as Record<string, unknown>;
    // CFC_CONFIG_DEFAULT.bands has exactly 10 entries (frozen at module
    // init), so the indexed lookup is always defined — but tsc's
    // noUncheckedIndexedAccess can't see that, so fall through with a
    // zeroed band as a belt-and-braces guard.
    const fallback =
      CFC_CONFIG_DEFAULT.bands[i] ?? { freqHz: 0, compLevelDb: 0, postGainDb: 0 };
    bands.push({
      freqHz: typeof b.freqHz === 'number' ? b.freqHz : fallback.freqHz,
      compLevelDb: typeof b.compLevelDb === 'number' ? b.compLevelDb : fallback.compLevelDb,
      postGainDb: typeof b.postGainDb === 'number' ? b.postGainDb : fallback.postGainDb,
    });
  }
  return {
    enabled: typeof r.enabled === 'boolean' ? r.enabled : false,
    postEqEnabled: typeof r.postEqEnabled === 'boolean' ? r.postEqEnabled : false,
    preCompDb: typeof r.preCompDb === 'number' ? r.preCompDb : 0,
    prePeqDb: typeof r.prePeqDb === 'number' ? r.prePeqDb : 0,
    bands,
  };
}

function normalizeCfcPreset(raw: unknown): CfcPresetDto | null {
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Record<string, unknown>;
  const name = typeof r.name === 'string' ? r.name.trim() : '';
  if (!name) return null;
  return {
    name,
    config: normalizeCfc(r.config),
    createdUtc: typeof r.createdUtc === 'string' ? r.createdUtc : '',
    updatedUtc: typeof r.updatedUtc === 'string' ? r.updatedUtc : '',
  };
}

function normalizeCfcPresetsResponse(raw: unknown): CfcPresetDto[] {
  const r = raw as { presets?: unknown };
  const presets = Array.isArray(r?.presets) ? r.presets : [];
  return presets
    .map(normalizeCfcPreset)
    .filter((preset): preset is CfcPresetDto => preset !== null);
}

function normalizeTxStationProfile(raw: unknown): TxStationProfileDto | null {
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Record<string, unknown>;
  const id = typeof r.id === 'string' ? r.id.trim() : '';
  if (!id) return null;
  return {
    id,
    label: typeof r.label === 'string' ? r.label : id,
    summary: typeof r.summary === 'string' ? r.summary : '',
    applyTitle: typeof r.applyTitle === 'string' ? r.applyTitle : '',
    audioSuiteRoute: r.audioSuiteRoute === 'vst' ? 'vst' : 'native',
    audioSuiteBypassed:
      typeof r.audioSuiteBypassed === 'boolean' ? r.audioSuiteBypassed : true,
    audioSuiteProfileName:
      typeof r.audioSuiteProfileName === 'string' ? r.audioSuiteProfileName : '',
    micGainDb: typeof r.micGainDb === 'number' ? r.micGainDb : 0,
    levelerMaxGainDb:
      typeof r.levelerMaxGainDb === 'number' ? r.levelerMaxGainDb : 8,
    txLeveling: normalizeTxLeveling(r.txLeveling),
    cfcConfig: normalizeCfc(r.cfcConfig),
    lowCutHz: clampInt(r.lowCutHz, 20, 600, 150),
    highCutHz: clampInt(r.highCutHz, 1500, 6000, 2900),
    spectralDensity: clampInt(r.spectralDensity, 0, 100, 50),
  };
}

function normalizeTxStationProfileResponse(raw: unknown): TxStationProfileDto[] {
  const r = raw as { profiles?: unknown };
  const profiles = Array.isArray(r?.profiles) ? r.profiles : [];
  return profiles
    .map(normalizeTxStationProfile)
    .filter((profile): profile is TxStationProfileDto => profile !== null);
}

function normalizeTxFidelityPolicy(raw: unknown): TxFidelityPolicyDto {
  if (!raw || typeof raw !== 'object') return TX_FIDELITY_POLICY_DEFAULT;
  const r = raw as Record<string, unknown>;
  const profileId = typeof r.profileId === 'string'
    ? r.profileId.trim().toLowerCase()
    : TX_FIDELITY_POLICY_DEFAULT.profileId;
  return {
    profileId: profileId || TX_FIDELITY_POLICY_DEFAULT.profileId,
    targetSpectralDensity: clampInt(
      r.targetSpectralDensity,
      0,
      100,
      TX_FIDELITY_POLICY_DEFAULT.targetSpectralDensity,
    ),
  };
}

function cloneCfc(c: CfcConfigDto): CfcConfigDto {
  return {
    enabled: c.enabled,
    postEqEnabled: c.postEqEnabled,
    preCompDb: c.preCompDb,
    prePeqDb: c.prePeqDb,
    bands: c.bands.map((b) => ({ ...b })),
  };
}

function normalizeZoomLevel(v: unknown): ZoomLevel {
  if (typeof v === 'number' && Number.isInteger(v) && v >= ZOOM_MIN && v <= ZOOM_MAX) {
    return v;
  }
  return ZOOM_MIN;
}

function normalizeRadios(raw: unknown): RadioInfoDto[] {
  if (!Array.isArray(raw)) return [];
  return raw.map((entry) => {
    const r = (entry ?? {}) as Record<string, unknown>;
    const details = r.details;
    return {
      macAddress: typeof r.macAddress === 'string' ? r.macAddress : '',
      ipAddress: typeof r.ipAddress === 'string' ? r.ipAddress : '',
      boardId: typeof r.boardId === 'string' ? r.boardId : '',
      firmwareVersion:
        typeof r.firmwareVersion === 'string' ? r.firmwareVersion : '',
      busy: Boolean(r.busy),
      details:
        details && typeof details === 'object'
          ? (details as Record<string, string>)
          : null,
    };
  });
}

function asDiagRecord(raw: unknown): Record<string, unknown> {
  return raw && typeof raw === 'object' ? (raw as Record<string, unknown>) : {};
}

function diagNumber(v: unknown): number | null {
  return typeof v === 'number' && Number.isFinite(v) ? v : null;
}

function diagBool(v: unknown): boolean | null {
  return typeof v === 'boolean' ? v : null;
}

function diagString(v: unknown): string | null {
  return typeof v === 'string' ? v : null;
}

function normalizeDiagnosticItems(raw: unknown): HardwareDiagnosticItemDto[] {
  if (!Array.isArray(raw)) return [];
  return raw.map((entry) => {
    const r = asDiagRecord(entry);
    return {
      field: typeof r.field === 'string' ? r.field : '',
      source: typeof r.source === 'string' ? r.source : undefined,
      status: typeof r.status === 'string' ? r.status : '',
      notes: typeof r.notes === 'string' ? r.notes : '',
    };
  });
}

function diagStringArray(raw: unknown): string[] {
  if (!Array.isArray(raw)) return [];
  return raw.filter((v): v is string => typeof v === 'string');
}

function normalizeFeatureSurfaces(raw: unknown): HardwareFeatureSurfaceDto[] {
  if (!Array.isArray(raw)) return [];
  return raw.map((entry) => {
    const r = asDiagRecord(entry);
    return {
      id: diagString(r.id) ?? '',
      title: diagString(r.title) ?? '',
      category: diagString(r.category) ?? '',
      implementationStatus: diagString(r.implementationStatus) ?? '',
      userConfigurable: Boolean(r.userConfigurable),
      source: diagString(r.source) ?? '',
      telemetryPaths: diagStringArray(r.telemetryPaths),
      candidateControls: diagStringArray(r.candidateControls),
      safetyClass: diagString(r.safetyClass) ?? '',
      notes: diagString(r.notes) ?? '',
    };
  });
}

function normalizeDspDiagnostics(raw: unknown): HardwareDspDiagnosticsDto {
  const r = asDiagRecord(raw);
  return {
    schemaVersion: diagNumber(r.schemaVersion) ?? 0,
    engine: diagString(r.engine) ?? 'Unknown',
    engineKind: diagString(r.engineKind) ?? 'Unknown',
    wdspActive: Boolean(r.wdspActive),
    synthetic: Boolean(r.synthetic),
    wdspNativeLoadable: Boolean(r.wdspNativeLoadable),
    wdspEmnrPost2Available: Boolean(r.wdspEmnrPost2Available),
    wdspNr4SbnrAvailable: Boolean(r.wdspNr4SbnrAvailable),
    nr4Readiness: diagString(r.nr4Readiness) ?? 'unknown',
    requestedNrMode: diagString(r.requestedNrMode) ?? 'Off',
    effectiveNrMode: diagString(r.effectiveNrMode) ?? 'Off',
    channelId: diagNumber(r.channelId) ?? 0,
    sampleRateHz: diagNumber(r.sampleRateHz) ?? 0,
    displayWidth: diagNumber(r.displayWidth) ?? 0,
    tickRateHz: diagNumber(r.tickRateHz) ?? 0,
    audioOutputRateHz: diagNumber(r.audioOutputRateHz) ?? 0,
    txBlockSamples: diagNumber(r.txBlockSamples) ?? 0,
    txOutputSamples: diagNumber(r.txOutputSamples) ?? 0,
    txMonitorRequested: Boolean(r.txMonitorRequested),
    rxSinkAttached: Boolean(r.rxSinkAttached),
    audioSinkCount: diagNumber(r.audioSinkCount) ?? 0,
    monitorBacklogSamples: diagNumber(r.monitorBacklogSamples) ?? 0,
    display: normalizeHardwareDisplayDiagnostics(r.display),
    wdspWisdomPhase: diagString(r.wdspWisdomPhase) ?? 'Unknown',
    wdspWisdomStatus: diagString(r.wdspWisdomStatus) ?? '',
    readiness: diagString(r.readiness) ?? 'unknown',
  };
}

function normalizeHardwareDisplayBufferDiagnostics(raw: unknown): HardwareDisplayBufferDiagnosticsDto {
  const r = asDiagRecord(raw);
  return {
    valid: Boolean(r.valid),
    ageMs: diagNumber(r.ageMs),
    validBins: diagNumber(r.validBins) ?? 0,
    minDb: diagNumber(r.minDb),
    maxDb: diagNumber(r.maxDb),
    meanDb: diagNumber(r.meanDb),
    dynamicRangeDb: diagNumber(r.dynamicRangeDb),
  };
}

function normalizeHardwareDisplayDiagnostics(raw: unknown): HardwareDisplayDiagnosticsDto {
  const r = asDiagRecord(raw);
  return {
    schemaVersion: diagNumber(r.schemaVersion) ?? 0,
    status: diagString(r.status) ?? 'unknown',
    clientCount: diagNumber(r.clientCount) ?? 0,
    framesBroadcast: diagNumber(r.framesBroadcast) ?? 0,
    lastSeq: diagNumber(r.lastSeq) ?? 0,
    lastFrameAgeMs: diagNumber(r.lastFrameAgeMs),
    lastFrameUnixMs: diagNumber(r.lastFrameUnixMs),
    panValid: Boolean(r.panValid),
    waterfallValid: Boolean(r.waterfallValid),
    panSource: diagString(r.panSource) ?? 'none',
    waterfallSource: diagString(r.waterfallSource) ?? 'none',
    keyed: Boolean(r.keyed),
    psMonitorRequested: Boolean(r.psMonitorRequested),
    psFeedbackCorrecting: Boolean(r.psFeedbackCorrecting),
    width: diagNumber(r.width) ?? 0,
    centerHz: diagNumber(r.centerHz),
    hzPerPixel: diagNumber(r.hzPerPixel),
    pan: normalizeHardwareDisplayBufferDiagnostics(r.pan),
    waterfall: normalizeHardwareDisplayBufferDiagnostics(r.waterfall),
    diagnosticRecommendation: diagString(r.diagnosticRecommendation),
  };
}

function normalizePureSignalDiagnostics(raw: unknown): HardwarePureSignalDiagnosticsDto {
  const r = asDiagRecord(raw);
  const source = r.feedbackSource === 'External' || r.feedbackSource === 'external'
    ? 'external'
    : 'internal';
  return {
    schemaVersion: diagNumber(r.schemaVersion) ?? 0,
    enabled: Boolean(r.enabled),
    monitorEnabled: Boolean(r.monitorEnabled),
    auto: r.auto === undefined ? true : Boolean(r.auto),
    single: Boolean(r.single),
    autoAttenuate: r.autoAttenuate === undefined ? true : Boolean(r.autoAttenuate),
    feedbackSource: source,
    externalFeedback: Boolean(r.externalFeedback),
    externalFeedbackPathSupported: Boolean(r.externalFeedbackPathSupported),
    rfBypassRequired: Boolean(r.rfBypassRequired),
    rfBypassSelected: Boolean(r.rfBypassSelected),
    feedbackLevelRaw: diagNumber(r.feedbackLevelRaw) ?? 0,
    feedbackLevelPct: diagNumber(r.feedbackLevelPct) ?? 0,
    feedbackTargetRaw: diagNumber(r.feedbackTargetRaw) ?? 152.293,
    feedbackUsableMinRaw: diagNumber(r.feedbackUsableMinRaw) ?? 128,
    feedbackUsableMaxRaw: diagNumber(r.feedbackUsableMaxRaw) ?? 181,
    feedbackCenteredMinRaw: diagNumber(r.feedbackCenteredMinRaw) ?? 138,
    feedbackCenteredMaxRaw: diagNumber(r.feedbackCenteredMaxRaw) ?? 176,
    txFeedbackAttenuationDb: diagNumber(r.txFeedbackAttenuationDb) ?? 0,
    txFeedbackAttenuationDbMin: diagNumber(r.txFeedbackAttenuationDbMin) ?? 0,
    hwPeak: diagNumber(r.hwPeak) ?? 0,
    hwPeakDefault: diagNumber(r.hwPeakDefault) ?? 0,
    calState: diagNumber(r.calState) ?? 0,
    correcting: Boolean(r.correcting),
    calibrationStalled: Boolean(r.calibrationStalled),
    healthStatus: diagString(r.healthStatus) ?? 'unknown',
    manualReference: diagString(r.manualReference) ?? '',
    diagnosticRecommendation: diagString(r.diagnosticRecommendation),
  };
}

function normalizeFrontendDspScene(raw: unknown): FrontendDspSceneDiagnosticsDto {
  const r = asDiagRecord(raw);
  return {
    schemaVersion: diagNumber(r.schemaVersion) ?? 0,
    available: Boolean(r.available),
    ageMs: diagNumber(r.ageMs),
    status: diagString(r.status) ?? 'unknown',
    fresh: Boolean(r.fresh),
    stale: Boolean(r.stale),
    diagnosticRecommendation: diagString(r.diagnosticRecommendation),
    atUtc: diagString(r.atUtc),
    sourceAtUtc: diagString(r.sourceAtUtc),
    sourceAgeMs: diagNumber(r.sourceAgeMs),
    sourceClockSkewMs: diagNumber(r.sourceClockSkewMs),
    sourceClientId: diagString(r.sourceClientId),
    mode: modeFromWire(r.mode),
    signalProfile: diagString(r.signalProfile),
    signalReason: diagString(r.signalReason),
    smartNrProfile: diagString(r.smartNrProfile),
    smartNrReason: diagString(r.smartNrReason),
    smartNrRecommendation: diagString(r.smartNrRecommendation),
    smartNrHeldByRxChain: diagBool(r.smartNrHeldByRxChain),
    smartNrRxChainLabel: diagString(r.smartNrRxChainLabel),
    smartNrRxChainRecommendation: diagString(r.smartNrRxChainRecommendation),
    smartNrRxChainTone: diagString(r.smartNrRxChainTone),
    smartNrRxChainScore: diagNumber(r.smartNrRxChainScore),
    maxSnrDb: diagNumber(r.maxSnrDb),
    coherentMaxSnrDb: diagNumber(r.coherentMaxSnrDb),
    occupiedPct: diagNumber(r.occupiedPct),
    coherentOccupiedPct: diagNumber(r.coherentOccupiedPct),
    impulsivePct: diagNumber(r.impulsivePct),
    peakCount: diagNumber(r.peakCount),
    coherentPeakCount: diagNumber(r.coherentPeakCount),
    coherentSubthresholdSignal: diagBool(r.coherentSubthresholdSignal),
  };
}

function normalizeSmartNrCondition(raw: unknown): SmartNrConditionDto {
  const r = asDiagRecord(raw);
  return {
    schemaVersion: diagNumber(r.schemaVersion) ?? 0,
    available: Boolean(r.available),
    status: diagString(r.status) ?? 'unknown',
    fresh: Boolean(r.fresh),
    stale: Boolean(r.stale),
    ageMs: diagNumber(r.ageMs),
    atUtc: diagString(r.atUtc),
    sourceAtUtc: diagString(r.sourceAtUtc),
    sourceAgeMs: diagNumber(r.sourceAgeMs),
    sourceClockSkewMs: diagNumber(r.sourceClockSkewMs),
    sourceClientId: diagString(r.sourceClientId),
    mode: modeFromWire(r.mode),
    profile: diagString(r.profile),
    reason: diagString(r.reason),
    recommendation: diagString(r.recommendation),
    heldByRxChain: diagBool(r.heldByRxChain),
    rxChainLabel: diagString(r.rxChainLabel),
    rxChainRecommendation: diagString(r.rxChainRecommendation),
    rxChainTone: diagString(r.rxChainTone),
    rxChainScore: diagNumber(r.rxChainScore),
    maxSnrDb: diagNumber(r.maxSnrDb),
    coherentMaxSnrDb: diagNumber(r.coherentMaxSnrDb),
    occupiedPct: diagNumber(r.occupiedPct),
    coherentOccupiedPct: diagNumber(r.coherentOccupiedPct),
    impulsivePct: diagNumber(r.impulsivePct),
    peakCount: diagNumber(r.peakCount),
    coherentPeakCount: diagNumber(r.coherentPeakCount),
    coherentSubthresholdSignal: diagBool(r.coherentSubthresholdSignal),
    wdspActive: Boolean(r.wdspActive),
    wdspNativeLoadable: Boolean(r.wdspNativeLoadable),
    wdspEmnrPost2Available: Boolean(r.wdspEmnrPost2Available),
    wdspNr4SbnrAvailable: Boolean(r.wdspNr4SbnrAvailable),
    nr4Readiness: diagString(r.nr4Readiness) ?? 'unknown',
    requestedNrMode: diagString(r.requestedNrMode) ?? 'Off',
    effectiveNrMode: diagString(r.effectiveNrMode) ?? 'Off',
    rxChain: normalizeSmartNrRxChainRuntime(r.rxChain),
    diagnosticRecommendation: diagString(r.diagnosticRecommendation),
    generatedUtc:
      typeof r.generatedUtc === 'string'
        ? r.generatedUtc
        : new Date().toISOString(),
  };
}

function normalizeSmartNrRxChainRuntime(raw: unknown): SmartNrRxChainRuntimeDto {
  const r = asDiagRecord(raw);
  return {
    schemaVersion: diagNumber(r.schemaVersion) ?? 0,
    source: diagString(r.source) ?? 'unknown',
    autoAgcEnabled: Boolean(r.autoAgcEnabled),
    agcMode: diagString(r.agcMode) ?? 'unknown',
    agcTopDb: diagNumber(r.agcTopDb) ?? 0,
    agcOffsetDb: diagNumber(r.agcOffsetDb) ?? 0,
    effectiveAgcTopDb: diagNumber(r.effectiveAgcTopDb) ?? 0,
    autoAttEnabled: Boolean(r.autoAttEnabled),
    adcProtectionEnabled: Boolean(r.adcProtectionEnabled),
    attenDb: diagNumber(r.attenDb) ?? 0,
    attOffsetDb: diagNumber(r.attOffsetDb) ?? 0,
    effectiveAttenDb: diagNumber(r.effectiveAttenDb) ?? 0,
    adcOverloadWarning: Boolean(r.adcOverloadWarning),
    adcOverloadLevel: diagNumber(r.adcOverloadLevel) ?? 0,
    lastOverloadBits: diagNumber(r.lastOverloadBits) ?? 0,
    adc0MaxMagnitude: diagNumber(r.adc0MaxMagnitude),
    adc1MaxMagnitude: diagNumber(r.adc1MaxMagnitude),
    adc0MaxMagnitudeAtOverload: diagNumber(r.adc0MaxMagnitudeAtOverload) ?? 0,
    adc1MaxMagnitudeAtOverload: diagNumber(r.adc1MaxMagnitudeAtOverload) ?? 0,
    lastAdcTelemetryUtc: diagString(r.lastAdcTelemetryUtc),
    squelchEnabled: Boolean(r.squelchEnabled),
    squelchAdaptive: r.squelchAdaptive === undefined ? true : Boolean(r.squelchAdaptive),
    squelchLevel: diagNumber(r.squelchLevel) ?? 0,
    preampOn: Boolean(r.preampOn),
  };
}

function normalizeTxRingDiagnostics(raw: unknown): TxRingDiagnosticsDto {
  const r = asDiagRecord(raw);
  return {
    totalWritten: diagNumber(r.totalWritten) ?? 0,
    totalRead: diagNumber(r.totalRead) ?? 0,
    count: diagNumber(r.count) ?? 0,
    dropped: diagNumber(r.dropped) ?? 0,
    capacity: diagNumber(r.capacity) ?? 0,
    recentMag: diagNumber(r.recentMag) ?? 0,
  };
}

function normalizeTxIngestDiagnostics(raw: unknown): TxIngestDiagnosticsDto {
  const r = asDiagRecord(raw);
  return {
    totalMicSamples: diagNumber(r.totalMicSamples) ?? 0,
    totalTxBlocks: diagNumber(r.totalTxBlocks) ?? 0,
    droppedFrames: diagNumber(r.droppedFrames) ?? 0,
  };
}

function normalizeProtocol2TxIqDiagnostics(raw: unknown): Protocol2TxIqDiagnosticsDto | null {
  if (raw === null || raw === undefined) return null;
  const r = asDiagRecord(raw);
  return {
    inputComplexSamples: diagNumber(r.inputComplexSamples) ?? 0,
    packetsQueued: diagNumber(r.packetsQueued) ?? 0,
    packetsSent: diagNumber(r.packetsSent) ?? 0,
    queuedPackets: diagNumber(r.queuedPackets) ?? 0,
    queueWriteFailures: diagNumber(r.queueWriteFailures) ?? 0,
    sendFailures: diagNumber(r.sendFailures) ?? 0,
    resetDrainedPackets: diagNumber(r.resetDrainedPackets) ?? 0,
    scratchComplexSamples: diagNumber(r.scratchComplexSamples) ?? 0,
    nextSequence: diagNumber(r.nextSequence) ?? 0,
    lastPacketsPerSecond: diagNumber(r.lastPacketsPerSecond) ?? 0,
    lastFifoModelSamples: diagNumber(r.lastFifoModelSamples) ?? 0,
    lastRateTimestampUtc: diagString(r.lastRateTimestampUtc),
    senderRunning: Boolean(r.senderRunning),
  };
}

function normalizeTxEgressHealth(raw: unknown): TxEgressHealthDto {
  if (raw === null || raw === undefined) {
    return {
      schemaVersion: 0,
      generatedUtc: new Date().toISOString(),
      activeTransport: 'unknown',
      healthStatus: 'unknown',
      p2Attached: false,
      p2Live: false,
      p2LastActivityAgeMs: null,
      p1RingDropRatioPct: 0,
      hostMoxOn: false,
      hostTunOn: false,
      hostTwoToneOn: false,
      hostTxActive: false,
      hardwarePtt: null,
      forwardWatts: null,
      rfDetected: false,
      rfEvidenceStatus: 'unknown',
      qualityScore: 0,
      qualityTone: 'unknown',
      p2PacketRateStatus: 'missing',
      p2LastPacketsPerSecond: null,
      p2FifoModelSamples: null,
      p2QueuedPackets: null,
      p2TransportFailures: 0,
      qualityReasons: ['tx-egress-health-unavailable'],
      txDutyProfile: 'unknown',
      continuousDutyRecommendedMaxWatts: null,
      continuousDutyLimitExceeded: false,
      continuousDutyManualReference: null,
      diagnosticRecommendation: 'TX egress health is not available from this backend yet; use raw counters until OpenhpsdrZeus is restarted.',
    };
  }
  const r = asDiagRecord(raw);
  return {
    schemaVersion: diagNumber(r.schemaVersion) ?? 0,
    generatedUtc:
      typeof r.generatedUtc === 'string'
        ? r.generatedUtc
        : new Date().toISOString(),
    activeTransport: diagString(r.activeTransport) ?? 'unknown',
    healthStatus: diagString(r.healthStatus) ?? 'unknown',
    p2Attached: Boolean(r.p2Attached),
    p2Live: Boolean(r.p2Live),
    p2LastActivityAgeMs: diagNumber(r.p2LastActivityAgeMs),
    p1RingDropRatioPct: diagNumber(r.p1RingDropRatioPct) ?? 0,
    hostMoxOn: Boolean(r.hostMoxOn),
    hostTunOn: Boolean(r.hostTunOn),
    hostTwoToneOn: Boolean(r.hostTwoToneOn),
    hostTxActive: Boolean(r.hostTxActive),
    hardwarePtt: diagBool(r.hardwarePtt),
    forwardWatts: diagNumber(r.forwardWatts),
    rfDetected: Boolean(r.rfDetected),
    rfEvidenceStatus: diagString(r.rfEvidenceStatus) ?? 'unknown',
    qualityScore: diagNumber(r.qualityScore) ?? 0,
    qualityTone: diagString(r.qualityTone) ?? 'unknown',
    p2PacketRateStatus: diagString(r.p2PacketRateStatus) ?? 'missing',
    p2LastPacketsPerSecond: diagNumber(r.p2LastPacketsPerSecond),
    p2FifoModelSamples: diagNumber(r.p2FifoModelSamples),
    p2QueuedPackets: diagNumber(r.p2QueuedPackets),
    p2TransportFailures: diagNumber(r.p2TransportFailures) ?? 0,
    qualityReasons: Array.isArray(r.qualityReasons)
      ? r.qualityReasons.map((item) => diagString(item)).filter((item): item is string => item !== null)
      : [],
    txDutyProfile: diagString(r.txDutyProfile) ?? 'unknown',
    continuousDutyRecommendedMaxWatts: diagNumber(r.continuousDutyRecommendedMaxWatts),
    continuousDutyLimitExceeded: Boolean(r.continuousDutyLimitExceeded),
    continuousDutyManualReference: diagString(r.continuousDutyManualReference),
    diagnosticRecommendation: diagString(r.diagnosticRecommendation),
  };
}

function normalizeTxStageDiagnostics(raw: unknown): TxStageDiagnosticsDto {
  const r = asDiagRecord(raw);
  return {
    schemaVersion: diagNumber(r.schemaVersion) ?? 0,
    source: diagString(r.source) ?? 'unknown',
    status: diagString(r.status) ?? 'idle',
    hostTxActive: Boolean(r.hostTxActive),
    micPkDbfs: diagNumber(r.micPkDbfs),
    micAvDbfs: diagNumber(r.micAvDbfs),
    eqPkDbfs: diagNumber(r.eqPkDbfs),
    eqAvDbfs: diagNumber(r.eqAvDbfs),
    lvlrPkDbfs: diagNumber(r.lvlrPkDbfs),
    lvlrAvDbfs: diagNumber(r.lvlrAvDbfs),
    lvlrGrDb: diagNumber(r.lvlrGrDb) ?? 0,
    cfcPkDbfs: diagNumber(r.cfcPkDbfs),
    cfcAvDbfs: diagNumber(r.cfcAvDbfs),
    cfcGrDb: diagNumber(r.cfcGrDb) ?? 0,
    compPkDbfs: diagNumber(r.compPkDbfs),
    compAvDbfs: diagNumber(r.compAvDbfs),
    alcPkDbfs: diagNumber(r.alcPkDbfs),
    alcAvDbfs: diagNumber(r.alcAvDbfs),
    alcGrDb: diagNumber(r.alcGrDb) ?? 0,
    outPkDbfs: diagNumber(r.outPkDbfs),
    outAvDbfs: diagNumber(r.outAvDbfs),
    diagnosticRecommendation: diagString(r.diagnosticRecommendation),
  };
}

function normalizeTxDiagnostics(raw: unknown): TxDiagnosticsDto {
  const r = asDiagRecord(raw);
  const pluginRaw = r.txPlugins === null || r.txPlugins === undefined
    ? null
    : asDiagRecord(r.txPlugins);
  const vstRaw = r.vstEngine === null || r.vstEngine === undefined
    ? null
    : asDiagRecord(r.vstEngine);
  return {
    generatedUtc:
      typeof r.generatedUtc === 'string'
        ? r.generatedUtc
        : new Date().toISOString(),
    iqSourceType: diagString(r.iqSourceType),
    iqSourceIsRing: Boolean(r.iqSourceIsRing),
    ring: normalizeTxRingDiagnostics(r.ring),
    ingest: normalizeTxIngestDiagnostics(r.ingest),
    protocol2: normalizeProtocol2TxIqDiagnostics(r.protocol2),
    stage: normalizeTxStageDiagnostics(r.stage),
    egress: normalizeTxEgressHealth(r.egress),
    txPlugins: pluginRaw === null
      ? null
      : {
          masterBypassed: Boolean(pluginRaw.masterBypassed),
          bypassedForRemoteTx: Boolean(pluginRaw.bypassedForRemoteTx),
        },
    vstEngine: vstRaw === null
      ? null
      : {
          active: Boolean(vstRaw.active),
          degradedBlocks: diagNumber(vstRaw.degradedBlocks) ?? 0,
        },
  };
}

function normalizeExternalPttStatus(raw: unknown): ExternalPttStatusDto {
  const r = asDiagRecord(raw);
  return {
    schemaVersion: diagNumber(r.schemaVersion) ?? 0,
    available: Boolean(r.available),
    protocol: diagString(r.protocol) ?? 'none',
    hardwarePtt: diagBool(r.hardwarePtt),
    cwKeyDown: diagBool(r.cwKeyDown),
    ownedMox: Boolean(r.ownedMox),
    hangTimeMs: diagNumber(r.hangTimeMs) ?? 0,
    moxOn: Boolean(r.moxOn),
    tunOn: Boolean(r.tunOn),
    twoToneOn: Boolean(r.twoToneOn),
    moxOwner: diagString(r.moxOwner),
    cwMode: Boolean(r.cwMode),
    sidetoneAvailable: Boolean(r.sidetoneAvailable),
    diagnosticRecommendation: diagString(r.diagnosticRecommendation),
    generatedUtc:
      typeof r.generatedUtc === 'string'
        ? r.generatedUtc
        : new Date().toISOString(),
  };
}

function diagActiveProtocol(raw: unknown): 'P1' | 'P2' | null {
  return raw === 'P1' || raw === 'P2' ? raw : null;
}

function normalizeHardwareKeyingStatus(raw: unknown): HardwareKeyingStatusDto {
  const r = asDiagRecord(raw);
  return {
    schemaVersion: diagNumber(r.schemaVersion) ?? 0,
    activeProtocol: diagActiveProtocol(r.activeProtocol),
    p1Packets: diagNumber(r.p1Packets) ?? 0,
    p1LastUpdatedUtc: diagString(r.p1LastUpdatedUtc),
    p1HardwarePtt: diagBool(r.p1HardwarePtt),
    p1CwKeyDown: diagBool(r.p1CwKeyDown),
    p2Packets: diagNumber(r.p2Packets) ?? 0,
    p2LastUpdatedUtc: diagString(r.p2LastUpdatedUtc),
    p2PttIn: diagBool(r.p2PttIn),
    p2DotIn: diagBool(r.p2DotIn),
    p2DashIn: diagBool(r.p2DashIn),
    p2SidetoneActive: diagBool(r.p2SidetoneActive),
    externalPtt: normalizeExternalPttStatus(r.externalPtt),
    diagnosticRecommendation: diagString(r.diagnosticRecommendation),
    generatedUtc:
      typeof r.generatedUtc === 'string'
        ? r.generatedUtc
        : new Date().toISOString(),
  };
}

function normalizeRadioPowerReading(raw: unknown): RadioPowerReadingDto {
  const r = asDiagRecord(raw);
  return {
    packets: diagNumber(r.packets) ?? 0,
    lastUpdatedUtc: diagString(r.lastUpdatedUtc),
    exciterAdc: diagNumber(r.exciterAdc),
    fwdAdc: diagNumber(r.fwdAdc),
    revAdc: diagNumber(r.revAdc),
    fwdWatts: diagNumber(r.fwdWatts),
    refWatts: diagNumber(r.refWatts),
    swr: diagNumber(r.swr),
  };
}

function normalizeRadioPowerCalibration(raw: unknown): RadioPowerCalibrationDto {
  const r = asDiagRecord(raw);
  return {
    schemaVersion: diagNumber(r.schemaVersion) ?? 0,
    activeProtocol: diagActiveProtocol(r.activeProtocol),
    connectedBoard: diagString(r.connectedBoard) ?? 'Unknown',
    effectiveBoard: diagString(r.effectiveBoard) ?? 'Unknown',
    orionMkIIVariant: diagString(r.orionMkIIVariant) ?? 'G2',
    calibrationBoard: diagString(r.calibrationBoard) ?? 'Unknown',
    bridgeVolt: diagNumber(r.bridgeVolt) ?? 0,
    refVoltage: diagNumber(r.refVoltage) ?? 0,
    adcCalOffset: diagNumber(r.adcCalOffset) ?? 0,
    calibrationMaxWatts: diagNumber(r.calibrationMaxWatts) ?? 0,
    calibrationFallbackApplied: Boolean(r.calibrationFallbackApplied),
    capabilityMaxPowerWatts: diagNumber(r.capabilityMaxPowerWatts) ?? 0,
    p1: normalizeRadioPowerReading(r.p1),
    p2: normalizeRadioPowerReading(r.p2),
    diagnosticRecommendation: diagString(r.diagnosticRecommendation),
    generatedUtc:
      typeof r.generatedUtc === 'string'
        ? r.generatedUtc
        : new Date().toISOString(),
  };
}

function normalizeRadioSupplyReading(raw: unknown): RadioSupplyReadingDto {
  const r = asDiagRecord(raw);
  return {
    packets: diagNumber(r.packets) ?? 0,
    lastUpdatedUtc: diagString(r.lastUpdatedUtc),
    supplyVoltsAdc: diagNumber(r.supplyVoltsAdc),
    supplyVolts: diagNumber(r.supplyVolts),
    rawScaledSupplyVolts: diagNumber(r.rawScaledSupplyVolts),
    supplyVoltsTrusted: Boolean(r.supplyVoltsTrusted),
    scaleStatus: diagString(r.scaleStatus) ?? 'unknown',
  };
}

function normalizeRadioSupplyAlarms(raw: unknown): RadioSupplyAlarmsDto {
  const r = asDiagRecord(raw);
  return {
    schemaVersion: diagNumber(r.schemaVersion) ?? 0,
    activeProtocol: diagActiveProtocol(r.activeProtocol),
    effectiveBoard: diagString(r.effectiveBoard) ?? 'Unknown',
    orionMkIIVariant: diagString(r.orionMkIIVariant) ?? 'G2',
    supportsSupplyTelemetry: Boolean(r.supportsSupplyTelemetry),
    adcSupplyMv: diagNumber(r.adcSupplyMv) ?? 0,
    activeThresholdsConfigured: Boolean(r.activeThresholdsConfigured),
    alarmActive: Boolean(r.alarmActive),
    alarmStatus: diagString(r.alarmStatus) ?? 'unknown',
    p1: normalizeRadioSupplyReading(r.p1),
    p2: normalizeRadioSupplyReading(r.p2),
    diagnosticRecommendation: diagString(r.diagnosticRecommendation),
    generatedUtc:
      typeof r.generatedUtc === 'string'
        ? r.generatedUtc
        : new Date().toISOString(),
  };
}

function normalizeRadioNetworkCounters(raw: unknown): RadioNetworkCountersDto {
  const r = asDiagRecord(raw);
  return {
    attached: Boolean(r.attached),
    totalFrames: diagNumber(r.totalFrames) ?? 0,
    droppedFrames: diagNumber(r.droppedFrames) ?? 0,
    dropRatioPct: diagNumber(r.dropRatioPct) ?? 0,
    hiPriorityPackets: diagNumber(r.hiPriorityPackets),
    psPairedPackets: diagNumber(r.psPairedPackets),
  };
}

function normalizeRadioNetworkProfile(raw: unknown): RadioNetworkProfileDto {
  const r = asDiagRecord(raw);
  return {
    schemaVersion: diagNumber(r.schemaVersion) ?? 0,
    connectionStatus: diagString(r.connectionStatus) ?? 'Disconnected',
    endpoint: diagString(r.endpoint),
    activeProtocol: diagActiveProtocol(r.activeProtocol),
    sampleRateHz: diagNumber(r.sampleRateHz) ?? 0,
    connectedBoard: diagString(r.connectedBoard) ?? 'Unknown',
    effectiveBoard: diagString(r.effectiveBoard) ?? 'Unknown',
    orionMkIIVariant: diagString(r.orionMkIIVariant) ?? 'G2',
    transport: diagString(r.transport) ?? 'udp',
    p1: normalizeRadioNetworkCounters(r.p1),
    p2: normalizeRadioNetworkCounters(r.p2),
    healthStatus: diagString(r.healthStatus) ?? 'unknown',
    diagnosticRecommendation: diagString(r.diagnosticRecommendation),
    generatedUtc:
      typeof r.generatedUtc === 'string'
        ? r.generatedUtc
        : new Date().toISOString(),
  };
}

function normalizeUserIoLine(raw: unknown): UserIoLineDto {
  const r = asDiagRecord(raw);
  return {
    id: diagString(r.id) ?? 'unknown',
    kind: diagString(r.kind) ?? 'unknown',
    label: diagString(r.label) ?? 'User I/O',
    rawAdc: diagNumber(r.rawAdc),
    normalizedPct: diagNumber(r.normalizedPct),
    digitalState: diagBool(r.digitalState),
  };
}

function normalizeUserIoLines(raw: unknown): UserIoLineDto[] {
  if (!Array.isArray(raw)) return [];
  return raw.map(normalizeUserIoLine);
}

function normalizeUserIoLabels(raw: unknown): UserIoLabelsDto {
  const r = asDiagRecord(raw);
  return {
    schemaVersion: diagNumber(r.schemaVersion) ?? 0,
    activeProtocol: diagActiveProtocol(r.activeProtocol),
    p2Attached: Boolean(r.p2Attached),
    p2Packets: diagNumber(r.p2Packets) ?? 0,
    p2LastUpdatedUtc: diagString(r.p2LastUpdatedUtc),
    lines: normalizeUserIoLines(r.lines),
    diagnosticRecommendation: diagString(r.diagnosticRecommendation),
    generatedUtc:
      typeof r.generatedUtc === 'string'
        ? r.generatedUtc
        : new Date().toISOString(),
  };
}

function normalizeUserIoActions(raw: unknown): UserIoActionsDto {
  const r = asDiagRecord(raw);
  return {
    schemaVersion: diagNumber(r.schemaVersion) ?? 0,
    activeProtocol: diagActiveProtocol(r.activeProtocol),
    p2Attached: Boolean(r.p2Attached),
    p2Packets: diagNumber(r.p2Packets) ?? 0,
    p2LastUpdatedUtc: diagString(r.p2LastUpdatedUtc),
    actionBindingsConfigured: Boolean(r.actionBindingsConfigured),
    lines: normalizeUserIoLines(r.lines),
    diagnosticRecommendation: diagString(r.diagnosticRecommendation),
    generatedUtc:
      typeof r.generatedUtc === 'string'
        ? r.generatedUtc
        : new Date().toISOString(),
  };
}

function normalizeRadioDigInDiagnostics(raw: unknown): RadioDigInDiagnosticsDto {
  const r = asDiagRecord(raw);
  return {
    schemaVersion: diagNumber(r.schemaVersion) ?? 0,
    activeProtocol: diagActiveProtocol(r.activeProtocol),
    connectedBoard: diagString(r.connectedBoard) ?? 'Unknown',
    effectiveBoard: diagString(r.effectiveBoard) ?? 'Unknown',
    orionMkIIVariant: diagString(r.orionMkIIVariant) ?? 'G2',
    p2Attached: Boolean(r.p2Attached),
    p2Packets: diagNumber(r.p2Packets) ?? 0,
    p2LastUpdatedUtc: diagString(r.p2LastUpdatedUtc),
    userDigitalIn: diagNumber(r.userDigitalIn),
    txDisableLineId: diagString(r.txDisableLineId) ?? 'userDigital0',
    txDisableLineName: diagString(r.txDisableLineName) ?? 'User I/O IO4',
    txDisableBit: diagNumber(r.txDisableBit) ?? 0,
    txDisableRawHigh: diagBool(r.txDisableRawHigh),
    txDisableActive: diagBool(r.txDisableActive),
    txDisablePolarity: diagString(r.txDisablePolarity) ?? 'active-low',
    txDisableMappingStatus: diagString(r.txDisableMappingStatus) ?? 'unknown',
    txInhibitBehaviorArmed: Boolean(r.txInhibitBehaviorArmed),
    cwKeyTipSource: diagString(r.cwKeyTipSource) ?? 'p2.dotIn',
    cwKeyTipDown: diagBool(r.cwKeyTipDown),
    cwDashInputDown: diagBool(r.cwDashInputDown),
    manualReference: diagString(r.manualReference),
    diagnosticRecommendation: diagString(r.diagnosticRecommendation),
    generatedUtc:
      typeof r.generatedUtc === 'string'
        ? r.generatedUtc
        : new Date().toISOString(),
  };
}

function diagNumberArray(raw: unknown): number[] {
  if (!Array.isArray(raw)) return [];
  return raw.map((v) => diagNumber(v) ?? 0);
}

function normalizeMapBytes(raw: unknown): HardwareMapByteDto[] {
  if (!Array.isArray(raw)) return [];
  return raw.map((entry) => {
    const r = asDiagRecord(entry);
    return {
      offset: diagNumber(r.offset) ?? 0,
      hexOffset: diagString(r.hexOffset) ?? '0x00',
      known: diagString(r.known),
      first: diagNumber(r.first) ?? 0,
      last: diagNumber(r.last) ?? 0,
      min: diagNumber(r.min) ?? 0,
      max: diagNumber(r.max) ?? 0,
      changedMask: diagNumber(r.changedMask) ?? 0,
      changedMaskHex: diagString(r.changedMaskHex) ?? '0x00',
      changeCount: diagNumber(r.changeCount) ?? 0,
      bitSetCounts: diagNumberArray(r.bitSetCounts),
      bitChangeCounts: diagNumberArray(r.bitChangeCounts),
    };
  });
}

function normalizeMapWords(raw: unknown): HardwareMapWordDto[] {
  if (!Array.isArray(raw)) return [];
  return raw.map((entry) => {
    const r = asDiagRecord(entry);
    return {
      offset: diagNumber(r.offset) ?? 0,
      hexOffset: diagString(r.hexOffset) ?? '0x00',
      known: diagString(r.known),
      first: diagNumber(r.first) ?? 0,
      last: diagNumber(r.last) ?? 0,
      min: diagNumber(r.min) ?? 0,
      max: diagNumber(r.max) ?? 0,
      changeCount: diagNumber(r.changeCount) ?? 0,
    };
  });
}

function normalizeByteStreamMap(raw: unknown): HardwareByteStreamMapDto {
  const r = asDiagRecord(raw);
  return {
    stream: diagString(r.stream) ?? '',
    source: diagString(r.source) ?? '',
    samples: diagNumber(r.samples) ?? 0,
    startedUtc: diagString(r.startedUtc),
    lastUpdatedUtc: diagString(r.lastUpdatedUtc),
    length: diagNumber(r.length) ?? 0,
    lastHex: diagString(r.lastHex) ?? '',
    changedByteCount: diagNumber(r.changedByteCount) ?? 0,
    changedWordCount: diagNumber(r.changedWordCount) ?? 0,
    bytes: normalizeMapBytes(r.bytes),
    words: normalizeMapWords(r.words),
  };
}

function normalizeP1AddressMap(raw: unknown): HardwareP1AddressMapDto[] {
  if (!Array.isArray(raw)) return [];
  return raw.map((entry) => {
    const r = asDiagRecord(entry);
    return {
      c0Address: diagNumber(r.c0Address) ?? 0,
      hexAddress: diagString(r.hexAddress) ?? '0x00',
      samples: diagNumber(r.samples) ?? 0,
      lastRawC0: diagNumber(r.lastRawC0) ?? 0,
      lastRawC0Hex: diagString(r.lastRawC0Hex) ?? '0x00',
      firstAin0: diagNumber(r.firstAin0) ?? 0,
      firstAin1: diagNumber(r.firstAin1) ?? 0,
      lastAin0: diagNumber(r.lastAin0) ?? 0,
      lastAin1: diagNumber(r.lastAin1) ?? 0,
      minAin0: diagNumber(r.minAin0) ?? 0,
      maxAin0: diagNumber(r.maxAin0) ?? 0,
      minAin1: diagNumber(r.minAin1) ?? 0,
      maxAin1: diagNumber(r.maxAin1) ?? 0,
      ain0ChangeCount: diagNumber(r.ain0ChangeCount) ?? 0,
      ain1ChangeCount: diagNumber(r.ain1ChangeCount) ?? 0,
      knownAin0: diagString(r.knownAin0),
      knownAin1: diagString(r.knownAin1),
      notes: diagString(r.notes) ?? '',
    };
  });
}

function normalizeP1Map(raw: unknown): HardwareP1MapDto {
  const r = asDiagRecord(raw);
  return {
    stream: diagString(r.stream) ?? '',
    source: diagString(r.source) ?? '',
    samples: diagNumber(r.samples) ?? 0,
    startedUtc: diagString(r.startedUtc),
    lastUpdatedUtc: diagString(r.lastUpdatedUtc),
    addresses: normalizeP1AddressMap(r.addresses),
    rawGap: diagString(r.rawGap) ?? '',
  };
}

function normalizeP1MarkerDeltas(raw: unknown): HardwareP1MarkerDeltaDto[] {
  if (!Array.isArray(raw)) return [];
  return raw.map((entry) => {
    const r = asDiagRecord(entry);
    return {
      c0Address: diagNumber(r.c0Address) ?? 0,
      hexAddress: diagString(r.hexAddress) ?? '0x00',
      knownAin0: diagString(r.knownAin0),
      knownAin1: diagString(r.knownAin1),
      previousAin0: diagNumber(r.previousAin0),
      currentAin0: diagNumber(r.currentAin0),
      previousAin1: diagNumber(r.previousAin1),
      currentAin1: diagNumber(r.currentAin1),
      ain0Delta: diagNumber(r.ain0Delta),
      ain1Delta: diagNumber(r.ain1Delta),
      ain0ChangeCountDelta: diagNumber(r.ain0ChangeCountDelta) ?? 0,
      ain1ChangeCountDelta: diagNumber(r.ain1ChangeCountDelta) ?? 0,
    };
  });
}

function normalizeByteMarkerDeltas(raw: unknown): HardwareByteMarkerDeltaDto[] {
  if (!Array.isArray(raw)) return [];
  return raw.map((entry) => {
    const r = asDiagRecord(entry);
    return {
      offset: diagNumber(r.offset) ?? 0,
      hexOffset: diagString(r.hexOffset) ?? '0x00',
      known: diagString(r.known),
      previous: diagNumber(r.previous),
      current: diagNumber(r.current),
      previousHex: diagString(r.previousHex) ?? '-',
      currentHex: diagString(r.currentHex) ?? '-',
      xorMask: diagNumber(r.xorMask) ?? 0,
      xorMaskHex: diagString(r.xorMaskHex) ?? '0x00',
      intervalChangeCount: diagNumber(r.intervalChangeCount) ?? 0,
      intervalChangedBits: diagNumberArray(r.intervalChangedBits),
    };
  });
}

function normalizeWordMarkerDeltas(raw: unknown): HardwareWordMarkerDeltaDto[] {
  if (!Array.isArray(raw)) return [];
  return raw.map((entry) => {
    const r = asDiagRecord(entry);
    return {
      offset: diagNumber(r.offset) ?? 0,
      hexOffset: diagString(r.hexOffset) ?? '0x00',
      known: diagString(r.known),
      previous: diagNumber(r.previous),
      current: diagNumber(r.current),
      previousHex: diagString(r.previousHex) ?? '-',
      currentHex: diagString(r.currentHex) ?? '-',
      valueDelta: diagNumber(r.valueDelta),
      intervalChangeCount: diagNumber(r.intervalChangeCount) ?? 0,
    };
  });
}

function normalizeMappingMarkerDelta(raw: unknown): HardwareMappingMarkerDeltaDto {
  const r = asDiagRecord(raw);
  return {
    previousId: diagNumber(r.previousId),
    previousLabel: diagString(r.previousLabel),
    baseline: Boolean(r.baseline),
    p1SampleDelta: diagNumber(r.p1SampleDelta) ?? 0,
    p2SampleDelta: diagNumber(r.p2SampleDelta) ?? 0,
    p1ChangedAddresses: normalizeP1MarkerDeltas(r.p1ChangedAddresses),
    p2ChangedBytes: normalizeByteMarkerDeltas(r.p2ChangedBytes),
    p2ChangedWords: normalizeWordMarkerDeltas(r.p2ChangedWords),
  };
}

function normalizeMappingMarkers(raw: unknown): HardwareMappingMarkerDto[] {
  if (!Array.isArray(raw)) return [];
  return raw.map((entry) => {
    const r = asDiagRecord(entry);
    const activeProtocol =
      r.activeProtocol === 'P1' || r.activeProtocol === 'P2'
        ? r.activeProtocol
        : null;
    return {
      id: diagNumber(r.id) ?? 0,
      label: diagString(r.label) ?? '',
      notes: diagString(r.notes),
      createdUtc: diagString(r.createdUtc) ?? new Date().toISOString(),
      activeProtocol,
      endpoint: diagString(r.endpoint),
      p1Packets: diagNumber(r.p1Packets) ?? 0,
      p2Packets: diagNumber(r.p2Packets) ?? 0,
      p1Samples: diagNumber(r.p1Samples) ?? 0,
      p2Samples: diagNumber(r.p2Samples) ?? 0,
      p1LastUpdatedUtc: diagString(r.p1LastUpdatedUtc),
      p2LastUpdatedUtc: diagString(r.p2LastUpdatedUtc),
      sincePrevious: normalizeMappingMarkerDelta(r.sincePrevious),
    };
  });
}

function normalizeHardwareMapping(raw: unknown): HardwareMappingDto {
  const r = asDiagRecord(raw);
  return {
    schemaVersion: diagNumber(r.schemaVersion) ?? 1,
    p1: normalizeP1Map(r.p1),
    p2HiPriority: normalizeByteStreamMap(r.p2HiPriority),
    markers: normalizeMappingMarkers(r.markers),
  };
}

function normalizeHardwareDiagnostics(raw: unknown): HardwareDiagnosticsDto {
  const r = asDiagRecord(raw);
  const p1 = asDiagRecord(r.p1);
  const p2 = asDiagRecord(r.p2);
  const activeProtocol =
    r.activeProtocol === 'P1' || r.activeProtocol === 'P2'
      ? r.activeProtocol
      : null;
  return {
    hardwareDiagnosticsApiVersion:
      diagNumber(r.hardwareDiagnosticsApiVersion) ?? 0,
    generatedUtc:
      typeof r.generatedUtc === 'string'
        ? r.generatedUtc
        : new Date().toISOString(),
    connectionStatus: normalizeStatus(r.connectionStatus),
    endpoint: diagString(r.endpoint),
    vfoHz: diagNumber(r.vfoHz) ?? 0,
    sampleRate: diagNumber(r.sampleRate) ?? 0,
    mode: normalizeMode(r.mode),
    connectedBoard:
      typeof r.connectedBoard === 'string' ? r.connectedBoard : 'Unknown',
    effectiveBoard:
      typeof r.effectiveBoard === 'string' ? r.effectiveBoard : 'Unknown',
    orionMkIIVariant:
      typeof r.orionMkIIVariant === 'string' ? r.orionMkIIVariant : 'G2',
    capabilities: parseBoardCapabilities(r.capabilities),
    dsp: normalizeDspDiagnostics(r.dsp),
    frontendDspScene: normalizeFrontendDspScene(r.frontendDspScene),
    pureSignal: normalizePureSignalDiagnostics(r.pureSignal),
    digIn: normalizeRadioDigInDiagnostics(r.digIn),
    activeProtocol,
    p1: {
      packets: diagNumber(p1.packets) ?? 0,
      lastUpdatedUtc: diagString(p1.lastUpdatedUtc),
      lastC0Address: diagNumber(p1.lastC0Address),
      lastAin0: diagNumber(p1.lastAin0),
      lastAin1: diagNumber(p1.lastAin1),
      exciterAdc: diagNumber(p1.exciterAdc),
      fwdAdc: diagNumber(p1.fwdAdc),
      revAdc: diagNumber(p1.revAdc),
      userAdc0: diagNumber(p1.userAdc0),
      userAdc1: diagNumber(p1.userAdc1),
      supplyVoltsAdc: diagNumber(p1.supplyVoltsAdc),
      adcOverloadBits: diagNumber(p1.adcOverloadBits) ?? 0,
      hardwarePtt: diagBool(p1.hardwarePtt),
      cwKeyDown: diagBool(p1.cwKeyDown),
    },
    p2: {
      packets: diagNumber(p2.packets) ?? 0,
      lastUpdatedUtc: diagString(p2.lastUpdatedUtc),
      pttIn: diagBool(p2.pttIn),
      dotIn: diagBool(p2.dotIn),
      dashIn: diagBool(p2.dashIn),
      pllLocked: diagBool(p2.pllLocked),
      sidetoneActive: diagBool(p2.sidetoneActive),
      adcOverloadBits: diagNumber(p2.adcOverloadBits),
      exciterAdc: diagNumber(p2.exciterAdc),
      fwdAdc: diagNumber(p2.fwdAdc),
      revAdc: diagNumber(p2.revAdc),
      adc0MaxMagnitude: diagNumber(p2.adc0MaxMagnitude),
      adc1MaxMagnitude: diagNumber(p2.adc1MaxMagnitude),
      adc0MaxMagnitudeAtOverload:
        diagNumber(p2.adc0MaxMagnitudeAtOverload) ?? 0,
      adc1MaxMagnitudeAtOverload:
        diagNumber(p2.adc1MaxMagnitudeAtOverload) ?? 0,
      supplyVoltsAdc: diagNumber(p2.supplyVoltsAdc),
      userAdc0: diagNumber(p2.userAdc0),
      userAdc1: diagNumber(p2.userAdc1),
      userAdc2: diagNumber(p2.userAdc2),
      userAdc3: diagNumber(p2.userAdc3),
      userDigitalIn: diagNumber(p2.userDigitalIn),
      hardwareLeds: diagNumber(p2.hardwareLeds),
    },
    mapping: normalizeHardwareMapping(r.mapping),
    referenceMap: normalizeDiagnosticItems(r.referenceMap),
    candidateSettings: normalizeDiagnosticItems(r.candidateSettings),
    featureSurfaces: normalizeFeatureSurfaces(r.featureSurfaces),
  };
}

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

async function jsonFetch<T>(
  input: RequestInfo,
  init: RequestInit | undefined,
  parse: (raw: unknown) => T,
): Promise<T> {
  const res = await fetch(input, init);
  if (!res.ok) {
    // Server returns { error: "..." } on 400; fall back to status text otherwise.
    let message = `${res.status} ${res.statusText}`;
    try {
      const body = (await res.json()) as unknown;
      if (
        body &&
        typeof body === 'object' &&
        'error' in body &&
        typeof (body as { error: unknown }).error === 'string'
      ) {
        message = (body as { error: string }).error;
      }
    } catch {
      /* non-JSON body — keep status text */
    }
    throw new ApiError(res.status, message);
  }
  const raw = (await res.json()) as unknown;
  return parse(raw);
}

export function fetchState(signal?: AbortSignal): Promise<RadioStateDto> {
  return jsonFetch('/api/state', { signal }, normalizeState);
}

export function fetchRadios(signal?: AbortSignal): Promise<RadioInfoDto[]> {
  return jsonFetch('/api/radios', { signal }, normalizeRadios);
}

/** A POTA / SOTA / DX activation spot from GET /api/spots/activations. `freqHz`
 *  is absolute Hz (server normalizes POTA kHz / SOTA MHz / DX kHz). `mode` is
 *  the raw upstream string (SSB/CW/FT8/…) — map it to an RxMode at tune time. */
export interface ActivationSpotDto {
  source: 'POTA' | 'SOTA' | 'DX';
  activator: string;
  freqHz: number;
  mode: string;
  reference: string;
  name: string | null;
  location: string | null;
  grid: string | null;
  comments: string | null;
  spotter: string | null;
  spotTime: string;
}

export function fetchActivationSpots(
  signal?: AbortSignal,
): Promise<ActivationSpotDto[]> {
  return jsonFetch(
    '/api/spots/activations',
    { signal },
    (raw) => (Array.isArray(raw) ? (raw as ActivationSpotDto[]) : []),
  );
}

/** Operator settings for the Spots feature (GET/POST /api/spots/settings).
 *  Mirrors the server-side Zeus.Contracts.SpotsSettings record. */
export interface SpotsSettings {
  enabled: boolean;
  potaEnabled: boolean;
  sotaEnabled: boolean;
  pollIntervalSeconds: number;
  setModeOnTune: boolean;
  tuneOnlyWhenConnected: boolean;
  cwSideband: 'CWU' | 'CWL';
  /** Band-key allow-list (e.g. ['20m','40m']); empty = all bands. */
  bands: string[];
  /** Mode-group allow-list (CW / PHONE / DIGITAL / FM / AM); empty = all. */
  modes: string[];
  /** Drop spots whose comment marks the activator QRT/closing. */
  hideQrt: boolean;
  /** Hide spots older than this many minutes; 0 = no age limit. */
  maxAgeMinutes: number;
  /** Collapse to the single newest spot per activator. */
  latestPerActivator: boolean;
  /** Hz added to the dial when tuning a CW spot (e.g. for CW pitch offset). */
  cwTuneOffsetHz: number;
  /** Hz added to the dial when tuning a digital spot. */
  digiTuneOffsetHz: number;
  /** Include the DX-cluster feed (off by default — high volume). */
  dxEnabled: boolean;
  /** POTA feed endpoint (blank/invalid falls back to the default on the server). */
  potaUrl: string;
  /** SOTA feed endpoint. */
  sotaUrl: string;
  /** DX-cluster feed endpoint (DXSummit-compatible JSON shape). */
  dxUrl: string;
}

const DEFAULT_POTA_URL = 'https://api.pota.app/spot/activator';
const DEFAULT_SOTA_URL = 'https://api2.sota.org.uk/api/spots/50/all';
const DEFAULT_DX_URL = 'https://www.dxsummit.fi/api/v1/spots?limit=50';

export const SPOTS_SETTINGS_DEFAULTS: SpotsSettings = {
  enabled: true,
  potaEnabled: true,
  sotaEnabled: true,
  pollIntervalSeconds: 60,
  setModeOnTune: true,
  tuneOnlyWhenConnected: true,
  cwSideband: 'CWU',
  bands: [],
  modes: [],
  hideQrt: true,
  maxAgeMinutes: 0,
  latestPerActivator: false,
  cwTuneOffsetHz: 0,
  digiTuneOffsetHz: 0,
  dxEnabled: false,
  potaUrl: DEFAULT_POTA_URL,
  sotaUrl: DEFAULT_SOTA_URL,
  dxUrl: DEFAULT_DX_URL,
};

const SPOTS_MAX_AGE_LIMIT = 1440;
const SPOTS_MAX_TUNE_OFFSET = 5_000;

function urlOrDefault(v: unknown, fallback: string): string {
  return typeof v === 'string' && v.trim().length > 0 ? v.trim() : fallback;
}

function toStringArray(v: unknown): string[] {
  if (!Array.isArray(v)) return [];
  // Preserve case (canonical band keys are lowercase '20m', mode-group keys are
  // uppercase 'CW'); the filter pipeline compares case-insensitively.
  return v
    .filter((x): x is string => typeof x === 'string' && x.trim().length > 0)
    .map((x) => x.trim());
}

function clampInt(v: unknown, lo: number, hi: number, fallback: number): number {
  const n = typeof v === 'number' ? v : Number(v);
  if (!Number.isFinite(n)) return fallback;
  return Math.min(hi, Math.max(lo, Math.round(n)));
}

function normalizeSpotsSettings(raw: unknown): SpotsSettings {
  const r = (raw ?? {}) as Partial<SpotsSettings>;
  return {
    ...SPOTS_SETTINGS_DEFAULTS,
    ...r,
    cwSideband: r.cwSideband === 'CWL' ? 'CWL' : 'CWU',
    bands: toStringArray(r.bands),
    modes: toStringArray(r.modes),
    hideQrt: r.hideQrt ?? SPOTS_SETTINGS_DEFAULTS.hideQrt,
    maxAgeMinutes: clampInt(r.maxAgeMinutes, 0, SPOTS_MAX_AGE_LIMIT, 0),
    latestPerActivator: r.latestPerActivator ?? false,
    pollIntervalSeconds: clampInt(r.pollIntervalSeconds, 30, 600, 60),
    cwTuneOffsetHz: clampInt(r.cwTuneOffsetHz, -SPOTS_MAX_TUNE_OFFSET, SPOTS_MAX_TUNE_OFFSET, 0),
    digiTuneOffsetHz: clampInt(r.digiTuneOffsetHz, -SPOTS_MAX_TUNE_OFFSET, SPOTS_MAX_TUNE_OFFSET, 0),
    dxEnabled: r.dxEnabled ?? false,
    potaUrl: urlOrDefault(r.potaUrl, DEFAULT_POTA_URL),
    sotaUrl: urlOrDefault(r.sotaUrl, DEFAULT_SOTA_URL),
    dxUrl: urlOrDefault(r.dxUrl, DEFAULT_DX_URL),
  };
}

export function fetchSpotsSettings(signal?: AbortSignal): Promise<SpotsSettings> {
  return jsonFetch('/api/spots/settings', { signal }, normalizeSpotsSettings);
}

export function updateSpotsSettings(
  settings: SpotsSettings,
  signal?: AbortSignal,
): Promise<SpotsSettings> {
  return jsonFetch(
    '/api/spots/settings',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(settings),
      signal,
    },
    normalizeSpotsSettings,
  );
}

/** Status of the local git checkout vs its configured upstream
 *  (GET /api/system/update). Mirrors Zeus.Contracts.RepoUpdateStatus. */
export interface RepoUpdateStatus {
  isGitRepo: boolean;
  branch: string | null;
  currentSha: string | null;
  currentShortSha: string | null;
  currentSubject: string | null;
  upstreamRef: string | null;
  behind: number;
  ahead: number;
  dirty: boolean;
  canFastForward: boolean;
  latestRemoteSha: string | null;
  latestRemoteSubject: string | null;
  remoteUrl: string | null;
  checkedUtc: string | null;
  error: string | null;
}

/** Result of POST /api/system/update/pull. Mirrors Zeus.Contracts.RepoUpdateResult. */
export interface RepoUpdateResult {
  ok: boolean;
  newSha: string | null;
  requiresRebuild: boolean;
  message: string;
}

/** Check how far the running checkout is behind upstream. `fetch=false` skips
 *  the network and reports the last-known counts. */
export function fetchUpdateStatus(
  fetch = true,
  signal?: AbortSignal,
): Promise<RepoUpdateStatus> {
  return jsonFetch(
    `/api/system/update?fetch=${fetch ? 'true' : 'false'}`,
    { signal },
    (raw) => raw as RepoUpdateStatus,
  );
}

/** Fast-forward the checkout to upstream. Source only — a rebuild + restart is
 *  still required (result.requiresRebuild). */
export function pullUpdate(signal?: AbortSignal): Promise<RepoUpdateResult> {
  return jsonFetch(
    '/api/system/update/pull',
    { method: 'POST', signal },
    (raw) => raw as RepoUpdateResult,
  );
}

export function fetchHardwareDiagnostics(
  signal?: AbortSignal,
): Promise<HardwareDiagnosticsDto> {
  return jsonFetch(
    '/api/radio/diagnostics',
    { signal },
    normalizeHardwareDiagnostics,
  );
}

export function resetHardwareDiagnosticsMap(
  signal?: AbortSignal,
): Promise<HardwareDiagnosticsDto> {
  return jsonFetch(
    '/api/radio/diagnostics/map/reset',
    { method: 'POST', signal },
    normalizeHardwareDiagnostics,
  );
}

export function createHardwareDiagnosticsMarker(
  label: string,
  notes?: string,
  signal?: AbortSignal,
): Promise<HardwareDiagnosticsDto> {
  return jsonFetch(
    '/api/radio/diagnostics/map/marker',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ label, notes }),
      signal,
    },
    normalizeHardwareDiagnostics,
  );
}

export function publishFrontendDspSceneDiagnostics(
  payload: FrontendDspSceneDiagnosticsPayload,
  signal?: AbortSignal,
): Promise<FrontendDspSceneDiagnosticsDto> {
  return jsonFetch(
    '/api/radio/diagnostics/dsp-scene',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(payload),
      signal,
    },
    normalizeFrontendDspScene,
  );
}

export function fetchFrontendDspSceneDiagnostics(
  signal?: AbortSignal,
): Promise<FrontendDspSceneDiagnosticsDto> {
  return jsonFetch(
    '/api/radio/diagnostics/dsp-scene',
    { signal },
    normalizeFrontendDspScene,
  );
}

export function fetchSmartNrCondition(
  signal?: AbortSignal,
): Promise<SmartNrConditionDto> {
  return jsonFetch(
    '/api/dsp/nr-condition',
    { signal },
    normalizeSmartNrCondition,
  );
}

export function fetchTxDiagnostics(
  signal?: AbortSignal,
): Promise<TxDiagnosticsDto> {
  return jsonFetch(
    '/api/tx/diag',
    { signal },
    normalizeTxDiagnostics,
  );
}

export function fetchExternalPttStatus(
  signal?: AbortSignal,
): Promise<ExternalPttStatusDto> {
  return jsonFetch(
    '/api/tx/external-ptt',
    { signal },
    normalizeExternalPttStatus,
  );
}

export function fetchHardwareKeyingStatus(
  signal?: AbortSignal,
): Promise<HardwareKeyingStatusDto> {
  return jsonFetch(
    '/api/cw/hardware-keying',
    { signal },
    normalizeHardwareKeyingStatus,
  );
}

export function fetchRadioPowerCalibration(
  signal?: AbortSignal,
): Promise<RadioPowerCalibrationDto> {
  return jsonFetch(
    '/api/radio/power-calibration',
    { signal },
    normalizeRadioPowerCalibration,
  );
}

export function fetchRadioSupplyAlarms(
  signal?: AbortSignal,
): Promise<RadioSupplyAlarmsDto> {
  return jsonFetch(
    '/api/radio/supply-alarms',
    { signal },
    normalizeRadioSupplyAlarms,
  );
}

export function fetchRadioNetworkProfile(
  signal?: AbortSignal,
): Promise<RadioNetworkProfileDto> {
  return jsonFetch(
    '/api/radio/network-profile',
    { signal },
    normalizeRadioNetworkProfile,
  );
}

export function fetchUserIoLabels(
  signal?: AbortSignal,
): Promise<UserIoLabelsDto> {
  return jsonFetch(
    '/api/radio/user-io/labels',
    { signal },
    normalizeUserIoLabels,
  );
}

export function fetchUserIoActions(
  signal?: AbortSignal,
): Promise<UserIoActionsDto> {
  return jsonFetch(
    '/api/radio/user-io/actions',
    { signal },
    normalizeUserIoActions,
  );
}

export function fetchRadioDigInDiagnostics(
  signal?: AbortSignal,
): Promise<RadioDigInDiagnosticsDto> {
  return jsonFetch(
    '/api/radio/dig-in',
    { signal },
    normalizeRadioDigInDiagnostics,
  );
}

export function connect(
  req: ConnectRequest,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/connect',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(req),
      signal,
    },
    normalizeState,
  );
}

export function connectP2(
  req: ConnectRequest,
  signal?: AbortSignal,
): Promise<unknown> {
  return jsonFetch(
    '/api/connect/p2',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(req),
      signal,
    },
    (raw) => raw,
  );
}

export function disconnect(signal?: AbortSignal): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/disconnect',
    { method: 'POST', signal },
    normalizeState,
  );
}

export function disconnectP2(signal?: AbortSignal): Promise<unknown> {
  return jsonFetch(
    '/api/disconnect/p2',
    { method: 'POST', signal },
    (raw) => raw,
  );
}

// Move the hardware NCO center frequency without touching vfoHz. Called by
// the panadapter pan gesture (use-pan-tune-gesture.ts) when a drag releases
// past the edge of the current IQ capture window. Server returns the full
// updated StateDto; 400 if hz is out of range for the connected radio.
export function setRadioLo(
  hz: number,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/radio/lo',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ hz }),
      signal,
    },
    normalizeState,
  );
}

// Toggle CTUN (click-tune / centred tuning). Server returns the full updated
// StateDto; on enable the hardware NCO is frozen at its current centre, on
// disable it snaps back to the dial. See use-pan-tune-gesture.ts for how the
// gesture changes when ctunEnabled flips.
export function setCtun(
  enabled: boolean,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/radio/ctun',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ enabled }),
      signal,
    },
    normalizeState,
  );
}

export function setVfo(
  hz: number,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  // VFO-lock gate. The mobile shell exposes a padlock toggle that suppresses
  // tuning so a finger drag / band tap / scroll can't pull the radio off
  // frequency. We re-fetch the canonical state instead of returning a stub
  // so callers' `.then(applyState)` rolls back any optimistic local vfoHz
  // they wrote before calling us. `vfo-lock-store` has no api/client deps,
  // so this static import doesn't create a cycle.
  if (vfoLockStore.getState().locked) {
    return fetchState(signal);
  }
  return jsonFetch(
    '/api/vfo',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ hz }),
      signal,
    },
    normalizeState,
  );
}

export function setMode(
  mode: RxMode,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  // Server's System.Text.Json has no JsonStringEnumConverter — it expects
  // enum values as numeric ordinals on the write path. Normalizer handles
  // both forms on the read path, so the wire is asymmetric today.
  const modeIndex = MODE_ORDER.indexOf(mode);
  return jsonFetch(
    '/api/mode',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ mode: modeIndex }),
      signal,
    },
    normalizeState,
  );
}

export function setBandwidth(
  low: number,
  high: number,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/bandwidth',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ low, high }),
      signal,
    },
    normalizeState,
  );
}

// Preferred filter endpoint: includes optional preset name for chip tracking.
export function setFilter(
  lowHz: number,
  highHz: number,
  presetName?: string,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/filter',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ lowHz, highHz, presetName: presetName ?? null }),
      signal,
    },
    normalizeState,
  );
}

// TX bandpass filter — signed Hz pair, LSB negative, DSB symmetric. Per-mode
// memory is server-side; caller passes already-signed values for the active
// mode.
export function setTxFilter(
  lowHz: number,
  highHz: number,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/tx-filter',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ lowHz, highHz }),
      signal,
    },
    normalizeState,
  );
}

function normalizeFilterPreset(raw: unknown): FilterPresetDto | null {
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Record<string, unknown>;
  if (typeof r.slotName !== 'string' || typeof r.label !== 'string') return null;
  return {
    slotName: r.slotName,
    label: r.label,
    lowHz: typeof r.lowHz === 'number' ? r.lowHz : 0,
    highHz: typeof r.highHz === 'number' ? r.highHz : 0,
    isVar: Boolean(r.isVar),
  };
}

export function getFilterPresets(
  mode: RxMode,
  signal?: AbortSignal,
): Promise<FilterPresetDto[]> {
  return jsonFetch(
    `/api/filter/presets?mode=${encodeURIComponent(mode)}`,
    { signal },
    (raw) => {
      if (!Array.isArray(raw)) return [];
      return raw.flatMap((item) => {
        const p = normalizeFilterPreset(item);
        return p ? [p] : [];
      });
    },
  );
}

export function setFilterAdvancedPaneOpen(
  open: boolean,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/filter/advanced-pane',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ open }),
      signal,
    },
    normalizeState,
  );
}

export function setFilterPresetOverride(
  mode: RxMode,
  slotName: string,
  lowHz: number,
  highHz: number,
  signal?: AbortSignal,
): Promise<FilterPresetDto[]> {
  return jsonFetch(
    '/api/filter/presets',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ mode, slotName, lowHz, highHz }),
      signal,
    },
    (raw) => {
      if (!Array.isArray(raw)) return [];
      return raw.flatMap((item) => {
        const p = normalizeFilterPreset(item);
        return p ? [p] : [];
      });
    },
  );
}

export function getFavoriteFilterSlots(
  mode: RxMode,
  signal?: AbortSignal,
): Promise<string[]> {
  return jsonFetch(
    `/api/filter/favorites?mode=${mode}`,
    { method: 'GET', signal },
    (raw) => {
      if (typeof raw === 'object' && raw !== null && 'slotNames' in raw) {
        const slotNames = raw.slotNames;
        if (Array.isArray(slotNames)) {
          return slotNames.filter((s): s is string => typeof s === 'string');
        }
      }
      return ['F6', 'F5', 'F4']; // Default fallback
    },
  );
}

export function setFavoriteFilterSlots(
  mode: RxMode,
  slotNames: string[],
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/filter/favorites',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ mode, slotNames }),
      signal,
    },
    normalizeState,
  );
}

// 768/1536 kHz are Protocol-2 only (ANAN G2); Protocol 1 caps at 384 kHz.
// ConnectPanel gates the higher rungs to P2, and the backend rejects them on
// a P1 connect.
export type SampleRate = 48_000 | 96_000 | 192_000 | 384_000 | 768_000 | 1_536_000;

export function setSampleRate(
  rate: SampleRate,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/sampleRate',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ rate }),
      signal,
    },
    normalizeState,
  );
}

export function setPreamp(
  on: boolean,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/preamp',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ on }),
      signal,
    },
    normalizeState,
  );
}

export function setAgcTop(
  topDb: number,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/agcGain',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ topDb }),
      signal,
    },
    normalizeState,
  );
}

export function setRxAfGain(
  db: number,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/rx/afGain',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ db }),
      signal,
    },
    normalizeState,
  );
}

export function setAttenuator(
  db: number,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/attenuator',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ db }),
      signal,
    },
    normalizeState,
  );
}

export function setAutoAtt(
  enabled: boolean,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/auto-att',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ enabled }),
      signal,
    },
    normalizeState,
  );
}

export function fetchAdcProtection(
  signal?: AbortSignal,
): Promise<AdcProtectionStatusDto> {
  return jsonFetch(
    '/api/rx/adc-protection',
    { signal },
    normalizeAdcProtectionStatus,
  );
}

export function setAdcProtection(
  patch: AdcProtectionSetRequest,
  signal?: AbortSignal,
): Promise<AdcProtectionStatusDto> {
  return jsonFetch(
    '/api/rx/adc-protection',
    {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(patch),
      signal,
    },
    normalizeAdcProtectionStatus,
  );
}

export function setAutoAgc(
  enabled: boolean,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/auto-agc',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ enabled }),
      signal,
    },
    normalizeState,
  );
}

export function setZoom(
  level: ZoomLevel,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/rx/zoom',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ level }),
      signal,
    },
    normalizeState,
  );
}

export function setNr(
  nr: NrConfigDto,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/rx/nr',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      // Server registers JsonStringEnumConverter, so NrMode/NbMode travel as
      // PascalCase strings ("Off"/"Anr"/"Emnr"/"Sbnr", "Off"/"Nb1"/"Nb2").
      // Unknown values get a 400, which ApiError surfaces to the caller.
      body: JSON.stringify({ nr }),
      signal,
    },
    normalizeState,
  );
}

export function setAgc(
  agc: AgcConfigDto,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/rx/agc',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      // Server registers JsonStringEnumConverter, so AgcMode travels as a
      // PascalCase string ("Fixed"/"Long"/.../"Custom"). Unknown values get
      // a 400, which ApiError surfaces to the caller.
      body: JSON.stringify({ agc }),
      signal,
    },
    normalizeState,
  );
}

export function setSquelch(
  squelch: SquelchConfigDto,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/rx/squelch',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      // The server clamps level to 0..100 and 400s anything out of range.
      body: JSON.stringify({ squelch }),
      signal,
    },
    normalizeState,
  );
}

// TX leveling — replace-style like setSquelch. The server validates/clamps
// every range and 400s anything out of bounds. Returns the full state for
// optimistic-send + applyState reconcile.
export function setTxLeveling(
  txLeveling: TxLevelingConfigDto,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/tx/leveling',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ txLeveling }),
      signal,
    },
    normalizeState,
  );
}

// PATCH-style request for the NR2 right-click popover. All fields nullable;
// server merges onto the persisted NrConfig and returns the full state so
// the frontend can reconcile.
export type Nr2Post2PatchBody = {
  post2Run?: boolean | null;
  post2Factor?: number | null;
  post2Nlevel?: number | null;
  post2Rate?: number | null;
  post2Taper?: number | null;
};

export function setNr2Post2(
  body: Nr2Post2PatchBody,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/rx/nr2/post2',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body),
      signal,
    },
    normalizeState,
  );
}

// PATCH-style request for the NR2 core algorithm selectors + Trained-method
// T1/T2. Server merges null-absent fields onto the persisted NrConfig.
export type Nr2CorePatchBody = {
  gainMethod?: number | null;
  npeMethod?: number | null;
  aeRun?: boolean | null;
  trainT1?: number | null;
  trainT2?: number | null;
};

export function setNr2Core(
  body: Nr2CorePatchBody,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/rx/nr2/core',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body),
      signal,
    },
    normalizeState,
  );
}

// PATCH-style request for the NR4 right-click popover. Same merge semantics
// as setNr2Post2.
export type Nr4PatchBody = {
  reductionAmount?: number | null;
  smoothingFactor?: number | null;
  whiteningFactor?: number | null;
  noiseRescale?: number | null;
  postFilterThreshold?: number | null;
  noiseScalingType?: number | null;
  position?: number | null;
};

export function setNr4(
  body: Nr4PatchBody,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/rx/nr4',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body),
      signal,
    },
    normalizeState,
  );
}

// MOX endpoint returns {moxOn} — not a full StateDto — because MOX is
// transient and deliberately absent from the persisted state snapshot.
// 409 while disconnected surfaces as ApiError with the server's message.
export function setMox(
  on: boolean,
  signal?: AbortSignal,
): Promise<{ moxOn: boolean }> {
  return jsonFetch(
    '/api/tx/mox',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ on }),
      signal,
    },
    (raw) => ({ moxOn: Boolean((raw as { moxOn?: unknown }).moxOn) }),
  );
}

// Drive endpoint returns {drivePercent} — same pattern as MOX; drive is
// transient TX state that isn't part of the persisted radio snapshot.
export function setDrive(
  percent: number,
  signal?: AbortSignal,
): Promise<{ drivePercent: number }> {
  return jsonFetch(
    '/api/tx/drive',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ percent: Math.round(percent) }),
      signal,
    },
    (raw) => {
      const v = (raw as { drivePercent?: unknown }).drivePercent;
      return { drivePercent: typeof v === 'number' ? v : 0 };
    },
  );
}

// TX pre-key (MOX) delay: POST /api/tx/prekey-delay { delayMs }. Returns the
// server-applied value, which may be lower than requested (clamped below the
// PS MOX hold-off). Issue #630.
export function setTxPreKeyDelay(
  delayMs: number,
  signal?: AbortSignal,
): Promise<{ txMoxPreKeyDelayMs: number }> {
  return jsonFetch(
    '/api/tx/prekey-delay',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ delayMs: Math.round(delayMs) }),
      signal,
    },
    (raw) => {
      const v = (raw as { txMoxPreKeyDelayMs?: unknown }).txMoxPreKeyDelayMs;
      return { txMoxPreKeyDelayMs: typeof v === 'number' ? v : 0 };
    },
  );
}

// Tune-drive endpoint: POST /api/tx/tune-drive { percent }. Returns
// { tunePercent }. Backend picks this in place of drivePercent while TUN is
// keyed; same PA-gain calibration applies.
export function setTuneDrive(
  percent: number,
  signal?: AbortSignal,
): Promise<{ tunePercent: number }> {
  return jsonFetch(
    '/api/tx/tune-drive',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ percent: Math.round(percent) }),
      signal,
    },
    (raw) => {
      const v = (raw as { tunePercent?: unknown }).tunePercent;
      return { tunePercent: typeof v === 'number' ? v : 0 };
    },
  );
}

// TUN endpoint: POST /api/tx/tun { on }. Returns { tunOn }. Keys a single-tone
// carrier via WDSP SetTXAPostGen* and is mutually exclusive with MOX on the
// server.
export function setTun(
  on: boolean,
  signal?: AbortSignal,
): Promise<{ tunOn: boolean }> {
  return jsonFetch(
    '/api/tx/tun',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ on }),
      signal,
    },
    (raw) => ({ tunOn: Boolean((raw as { tunOn?: unknown }).tunOn) }),
  );
}

// Per-band memory: last-used (hz, mode) persisted server-side in LiteDB.
// Shared across any browser hitting the same backend — localStorage would
// trap the state in one device.
export type BandMemoryEntry = {
  band: string;
  hz: number;
  mode: RxMode;
};

function normalizeBandMemoryEntry(raw: unknown): BandMemoryEntry | null {
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Record<string, unknown>;
  const band = typeof r.band === 'string' ? r.band : null;
  const hz = typeof r.hz === 'number' ? r.hz : null;
  if (!band || hz === null) return null;
  return { band, hz, mode: normalizeMode(r.mode) };
}

export function fetchBandMemory(
  signal?: AbortSignal,
): Promise<BandMemoryEntry[]> {
  return jsonFetch('/api/bands/memory', { signal }, (raw) => {
    if (!Array.isArray(raw)) return [];
    const out: BandMemoryEntry[] = [];
    for (const entry of raw) {
      const n = normalizeBandMemoryEntry(entry);
      if (n) out.push(n);
    }
    return out;
  });
}

export function saveBandMemory(
  band: string,
  hz: number,
  mode: RxMode,
  signal?: AbortSignal,
): Promise<BandMemoryEntry> {
  // Mode travels as a numeric ordinal, matching the setMode convention the
  // server already validates against. The server's JsonStringEnumConverter
  // accepts both strings and ordinals on the read path.
  const modeIndex = MODE_ORDER.indexOf(mode);
  return jsonFetch(
    `/api/bands/memory/${encodeURIComponent(band)}`,
    {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ hz, mode: modeIndex }),
      signal,
    },
    (raw) => {
      const n = normalizeBandMemoryEntry(raw);
      return n ?? { band, hz, mode };
    },
  );
}

// Leveler max-gain endpoint: POST /api/tx/leveler-max-gain { gain }. Returns
// { levelerMaxGainDb }. Backend clamps to [0, 20] and echoes the applied
// value; RadioService persists the setting, and the DSP pipeline reapplies it
// to the active TXA engine after reconnects or engine swaps.
export function setLevelerMaxGain(
  gain: number,
  signal?: AbortSignal,
): Promise<{ levelerMaxGainDb: number }> {
  return jsonFetch(
    '/api/tx/leveler-max-gain',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ gain }),
      signal,
    },
    (raw) => {
      const v = (raw as { levelerMaxGainDb?: unknown }).levelerMaxGainDb;
      return { levelerMaxGainDb: typeof v === 'number' ? v : gain };
    },
  );
}

// PureSignal master arm + cal-mode. POST /api/tx/ps. Backend swaps the engine
// state machine (SetPSRunCal, SetPSControl) and toggles the radio-side
// feedback wire bits. Returns the updated StateDto.
export async function setPs(
  req: { enabled: boolean; auto: boolean; single: boolean },
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/tx/ps',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(req),
      signal,
    },
    (raw) => raw as RadioStateDto,
  );
}

// PureSignal advanced settings. Nullable fields = partial update so the
// settings panel doesn't have to round-trip every value.
export async function setPsAdvanced(
  req: {
    ptol?: boolean;
    autoAttenuate?: boolean;
    moxDelaySec?: number;
    loopDelaySec?: number;
    ampDelayNs?: number;
    hwPeak?: number;
    intsSpiPreset?: string;
  },
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/tx/ps/advanced',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(req),
      signal,
    },
    (raw) => raw as RadioStateDto,
  );
}

// PureSignal feedback antenna source. Internal coupler vs External
// (Bypass). Server enum is 0 (Internal) / 1 (External); the wire DTO
// uses 'Internal' / 'External' string serialization through System.Text.Json
// default StringEnumConverter setup.
export async function setPsFeedbackSource(
  source: 'internal' | 'external',
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/tx/ps/feedback-source',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        source: source === 'external' ? 'External' : 'Internal',
      }),
      signal,
    },
    (raw) => raw as RadioStateDto,
  );
}

// Manual PS TX feedback attenuation (dB). Operator alternative to
// AutoAttenuate for a fixed external-tap chain — sets the value that lands
// the feedback in calcc's range; server clamps to the board range and
// persists per board.
export async function setPsFeedbackAttenuation(
  db: number,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/tx/ps/feedback-attenuation',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ db: Math.round(db) }),
      signal,
    },
    (raw) => raw as RadioStateDto,
  );
}

// PS-Monitor toggle (issue #121). When on AND PS is armed AND PS has
// converged, the TX panadapter switches its source from the post-CFIR
// predistorted-IQ analyzer to the PS-feedback (post-PA loopback) analyzer
// so the operator sees the actual on-air RF instead of the predistorted
// baseband. Default off — preserves the Thetis-style predistorted view.
// Server-side this is a pure UI source-routing flag; no WDSP setter, no
// wire-format change, default-off is byte-identical to pre-#121.
export async function setPsMonitor(
  enabled: boolean,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/tx/ps/monitor',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ enabled }),
      signal,
    },
    (raw) => raw as RadioStateDto,
  );
}

// TX Monitor toggle — engages the engine's audition path. The server
// demodulates the post-CFIR TX IQ back to mono baseband audio at the actual
// TX bandwidth profile and substitutes it for RX audio in the AudioFrame
// stream while monitor is on. Operator preference, not persisted across
// sessions; defaults off on connect.
export async function setTxMonitor(
  enabled: boolean,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/tx/monitor',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ enabled }),
      signal,
    },
    (raw) => raw as RadioStateDto,
  );
}

export function fetchTxStationProfiles(
  signal?: AbortSignal,
): Promise<TxStationProfileDto[]> {
  return jsonFetch(
    '/api/tx/station-profiles',
    { signal },
    normalizeTxStationProfileResponse,
  );
}

export function fetchTxFidelityPolicy(
  signal?: AbortSignal,
): Promise<TxFidelityPolicyDto> {
  return jsonFetch(
    '/api/tx/fidelity-policy',
    { signal },
    normalizeTxFidelityPolicy,
  );
}

export function saveTxFidelityPolicy(
  policy: TxFidelityPolicyDto,
  signal?: AbortSignal,
): Promise<TxFidelityPolicyDto> {
  return jsonFetch(
    '/api/tx/fidelity-policy',
    {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(policy),
      signal,
    },
    normalizeTxFidelityPolicy,
  );
}

export function saveTxStationProfile(
  profile: TxStationProfileDto,
  signal?: AbortSignal,
): Promise<TxStationProfileDto> {
  return jsonFetch(
    `/api/tx/station-profiles/${encodeURIComponent(profile.id)}`,
    {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(profile),
      signal,
    },
    (raw) => normalizeTxStationProfile(raw) ?? profile,
  );
}

export async function resetTxStationProfile(
  id: string,
  signal?: AbortSignal,
): Promise<void> {
  await jsonFetch(
    `/api/tx/station-profiles/${encodeURIComponent(id)}`,
    { method: 'DELETE', signal },
    () => null,
  );
}

export async function resetPs(signal?: AbortSignal): Promise<void> {
  await jsonFetch('/api/tx/ps/reset', { method: 'POST', signal }, () => null);
}

export async function savePs(filename: string, signal?: AbortSignal): Promise<void> {
  await jsonFetch(
    '/api/tx/ps/save',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ filename }),
      signal,
    },
    () => null,
  );
}

export async function restorePs(filename: string, signal?: AbortSignal): Promise<void> {
  await jsonFetch(
    '/api/tx/ps/restore',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ filename }),
      signal,
    },
    () => null,
  );
}

// CFC (Continuous Frequency Compressor) — issue #123. POSTs the full
// 10-band CFC profile + master flags. Server treats this as the
// authoritative state and persists it. Optimistic-update pattern lives in
// the panel — failures roll the local store back to the prior config.
export async function setCfcConfig(
  cfg: CfcConfigDto,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/tx/cfc',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ config: cfg }),
      signal,
    },
    (raw) => normalizeState(raw),
  );
}

export async function fetchCfcPresets(signal?: AbortSignal): Promise<CfcPresetDto[]> {
  return jsonFetch(
    '/api/tx/cfc/presets',
    { signal },
    (raw) => normalizeCfcPresetsResponse(raw),
  );
}

export async function saveCfcPreset(
  name: string,
  cfg: CfcConfigDto,
  signal?: AbortSignal,
): Promise<CfcPresetDto> {
  return jsonFetch(
    `/api/tx/cfc/presets/${encodeURIComponent(name)}`,
    {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ config: cfg }),
      signal,
    },
    (raw) =>
      normalizeCfcPreset(raw) ?? {
        name,
        config: normalizeCfc(cfg),
        createdUtc: '',
        updatedUtc: '',
      },
  );
}

// Two-tone test generator. Protocol-agnostic — works on both P1 and P2.
export async function setTwoTone(
  req: { enabled: boolean; freq1?: number; freq2?: number; mag?: number },
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/tx/twotone',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(req),
      signal,
    },
    (raw) => raw as RadioStateDto,
  );
}

// Mic-gain endpoint: POST /api/mic-gain { db }. Returns { micGainDb }.
// Failures bubble up so the slider can roll back the optimistic update.
export function setMicGain(
  db: number,
  signal?: AbortSignal,
): Promise<{ micGainDb: number }> {
  return jsonFetch(
    '/api/mic-gain',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ db: Math.round(db) }),
      signal,
    },
    (raw) => {
      const v = (raw as { micGainDb?: unknown }).micGainDb;
      return { micGainDb: typeof v === 'number' ? v : 0 };
    },
  );
}
