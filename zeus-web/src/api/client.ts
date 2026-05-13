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

import { warnOnce } from '../util/logger';
import { useVfoLockStore as vfoLockStore } from '../state/vfo-lock-store';

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
  autoAgcEnabled: boolean;
  agcOffsetDb: number;
  rxAfGainDb: number;
  attenDb: number;
  autoAttEnabled: boolean;
  attOffsetDb: number;
  adcOverloadWarning: boolean;
  nr: NrConfigDto;
  zoomLevel: ZoomLevel;
  // PureSignal persisted tunings — server is the source of truth, hydrated
  // into tx-store on connect so a fresh browser (no localStorage) sees the
  // operator's last dial-in. PsEnabled, PsSingle, TwoToneEnabled (master-arm
  // flags) are intentionally session-only and left out.
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
  psIntsSpiPreset: string;
  psFeedbackSource: 'internal' | 'external';
  twoToneFreq1: number;
  twoToneFreq2: number;
  twoToneMag: number;
  // CFC (Continuous Frequency Compressor) — issue #123. Always present
  // after normalisation; falls back to CFC_CONFIG_DEFAULT when the server
  // omits it (legacy state frames).
  cfc: CfcConfigDto;
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

export function normalizeMode(v: unknown): RxMode {
  if (typeof v === 'string') {
    return (MODE_ORDER as readonly string[]).includes(v)
      ? (v as RxMode)
      : 'USB';
  }
  if (typeof v === 'number' && Number.isInteger(v)) {
    return MODE_ORDER[v] ?? 'USB';
  }
  return 'USB';
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
    autoAgcEnabled: typeof r.autoAgcEnabled === 'boolean' ? r.autoAgcEnabled : false,
    agcOffsetDb: typeof r.agcOffsetDb === 'number' ? r.agcOffsetDb : 0,
    rxAfGainDb: typeof r.rxAfGainDb === 'number' ? r.rxAfGainDb : 0,
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
    // StateDto.Nr is nullable on the server (older clients) — fall back to
    // the engine's declared defaults so the UI has something to render.
    nr: normalizeNr(r.nr),
    zoomLevel: normalizeZoomLevel(r.zoomLevel),
    // PureSignal persisted tunings. Defaults match RadioService.cs init and
    // PsSettingsEntry — older servers without the fields fall back cleanly.
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
    psIntsSpiPreset: typeof r.psIntsSpiPreset === 'string' ? r.psIntsSpiPreset : '16/256',
    psFeedbackSource:
      r.psFeedbackSource === 'External' || r.psFeedbackSource === 'external' ? 'external' : 'internal',
    twoToneFreq1: typeof r.twoToneFreq1 === 'number' ? r.twoToneFreq1 : 700,
    twoToneFreq2: typeof r.twoToneFreq2 === 'number' ? r.twoToneFreq2 : 1900,
    twoToneMag: typeof r.twoToneMag === 'number' ? r.twoToneMag : 0.49,
    cfc: normalizeCfc(r.cfc),
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

export type SampleRate = 48_000 | 96_000 | 192_000 | 384_000;

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
// server. Same 404-tolerant pattern as setMicGain because the backend handler
// lands after this UI.
export async function setTun(
  on: boolean,
  signal?: AbortSignal,
): Promise<{ tunOn: boolean }> {
  try {
    return await jsonFetch(
      '/api/tx/tun',
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ on }),
        signal,
      },
      (raw) => ({ tunOn: Boolean((raw as { tunOn?: unknown }).tunOn) }),
    );
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) {
      warnOnce('tx-tun-404', 'POST /api/tx/tun not implemented yet — treating as accepted');
      return { tunOn: on };
    }
    throw err;
  }
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
// { levelerMaxGainDb }. Backend clamps to [0, 15] and echoes the applied
// value; stateless across backend restart, so ConnectPanel re-POSTs the
// persisted value when the connection comes up. Same 404-tolerant pattern as
// setMicGain for the frontend-ahead-of-backend window.
export async function setLevelerMaxGain(
  gain: number,
  signal?: AbortSignal,
): Promise<{ levelerMaxGainDb: number }> {
  try {
    return await jsonFetch(
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
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) {
      warnOnce(
        'tx-leveler-max-gain-404',
        'POST /api/tx/leveler-max-gain not implemented yet — treating as accepted',
      );
      return { levelerMaxGainDb: gain };
    }
    throw err;
  }
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
// Backend may not have landed the handler yet — a 404 is downgraded to a
// silent warnOnce so the console doesn't fill with noise during the
// frontend-ahead-of-backend window. Non-404 failures bubble up so the
// slider can roll back the optimistic update.
export async function setMicGain(
  db: number,
  signal?: AbortSignal,
): Promise<{ micGainDb: number }> {
  try {
    return await jsonFetch(
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
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) {
      warnOnce('mic-gain-404', 'POST /api/mic-gain not implemented yet — treating as accepted');
      return { micGainDb: Math.round(db) };
    }
    throw err;
  }
}
