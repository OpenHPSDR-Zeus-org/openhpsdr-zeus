// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// This file is part of the configurable Meters Panel feature. The catalog
// here is the SINGLE source of truth for "what meter readings exist": both
// the Library drawer (operator-facing list of available meters) and the
// runtime sampler (`sampleByMeterId` / `useBallisticReadingById`) read from
// it. Adding a new reading is one row in METER_CATALOG plus one branch in the
// sampler switch.
//
// Color discipline (CLAUDE.md, plan §4.6): the only raw hex permitted is
// amber #FFA028, and only for RX signal-strength bar fills + peak-hold
// ticks. Every other widget surface must come from a token in tokens.css.

/** Stable ID per Thetis-supported reading, RX + TX. */
export enum MeterReadingId {
  // RX (RxMetersV2Frame 0x19 + RxMeterFrame 0x14)
  RxSignalPk = 'rx.signal.pk',
  RxSignalAv = 'rx.signal.av',
  RxAdcPk = 'rx.adc.pk',
  RxAdcAv = 'rx.adc.av',
  RxAgcGain = 'rx.agc.gain',
  RxAgcEnvPk = 'rx.agc.env.pk',
  RxAgcEnvAv = 'rx.agc.env.av',
  // TX (TxMetersV2Frame 0x16 — already on wire)
  TxFwdWatts = 'tx.fwd.watts',
  TxRefWatts = 'tx.ref.watts',
  TxSwr = 'tx.swr',
  TxMicPk = 'tx.mic.pk',
  TxMicAv = 'tx.mic.av',
  TxEqPk = 'tx.eq.pk',
  TxEqAv = 'tx.eq.av',
  TxLvlrPk = 'tx.lvlr.pk',
  TxLvlrAv = 'tx.lvlr.av',
  TxLvlrGr = 'tx.lvlr.gr',
  TxCfcPk = 'tx.cfc.pk',
  TxCfcAv = 'tx.cfc.av',
  TxCfcGr = 'tx.cfc.gr',
  TxCompPk = 'tx.comp.pk',
  TxCompAv = 'tx.comp.av',
  TxAlcPk = 'tx.alc.pk',
  TxAlcAv = 'tx.alc.av',
  TxAlcGr = 'tx.alc.gr',
  TxOutPk = 'tx.out.pk',
  TxOutAv = 'tx.out.av',
}

export type MeterUnit = 'dBm' | 'dBFS' | 'dB' | 'W' | 'ratio';

export type MeterCategory =
  | 'rx-signal'
  | 'rx-adc'
  | 'rx-agc'
  | 'tx-power'
  | 'tx-stage'
  | 'tx-protection';

/**
 * Color-token tag — the widget renderer maps this onto a tokens.css variable
 * (or, for `amber-signal`, the amber #FFA028 raw hex permitted only for RX
 * signal-strength fills + peak-hold ticks).
 */
export type MeterColorToken = 'amber-signal' | 'power' | 'tx' | 'accent';

/** The four meter shapes the operator can pick from. `bigarc` is the
 *  half-circle analog gauge (Forward Power / SWR), `vucolumn` is the
 *  vertical LED column (signal-chain levels), `pulldown` is the
 *  right-anchored arc for gain reduction, `hbar` is the horizontal LED
 *  bar (the row-friendly counterpart of `vucolumn`). Legacy `sparkline`
 *  and `digital` kinds were dropped — operator workspaces persisting
 *  them auto-migrate to the catalog default at parse time. */
export type MeterDefaultKind = 'bigarc' | 'vucolumn' | 'pulldown' | 'hbar';

/** All four kinds, in display order — used by the AddMeter modal to lay
 *  out the kind picker. */
export const METER_KINDS: ReadonlyArray<MeterDefaultKind> = [
  'bigarc',
  'vucolumn',
  'pulldown',
  'hbar',
];

/** Operator-facing label for each kind — shown in the AddMeter kind picker. */
export const METER_KIND_LABELS: Record<MeterDefaultKind, string> = {
  bigarc: 'Analog gauge',
  vucolumn: 'Vertical bar',
  pulldown: 'Pull-down arc',
  hbar: 'Horizontal bar',
};

/** Three-level severity used to paint zone bands on meter widgets.
 *  Maps to: ok → --ok green, warn → --power amber, danger → --tx red. */
export type MeterZoneLevel = 'ok' | 'warn' | 'danger';

/** A coloured band of the meter's value axis. `from` and `to` are in the
 *  meter's native unit (dBFS, dB, W, ratio) — widgets project them onto the
 *  current axis range. Bands are rendered behind the live fill at low alpha
 *  so the operator always sees where "healthy / borderline / unexpected" is,
 *  even when the bar is empty. */
export interface MeterZone {
  from: number;
  to: number;
  level: MeterZoneLevel;
}

export interface MeterReadingDef {
  id: MeterReadingId;
  /** Operator-friendly long label, used in the Library drawer + tooltips. */
  label: string;
  /** Compact label for narrow tile widgets and chip-sized widgets. */
  short: string;
  category: MeterCategory;
  unit: MeterUnit;
  /** Default axis range for non-signal widgets. Signal-strength widgets fall
   * back to the SMeter S-unit scale at render time. */
  defaultMin: number;
  defaultMax: number;
  /** Threshold at which a level/protection widget switches to --tx red.
   *  null/undefined means "no danger zone". */
  dangerAt?: number;
  /** Soft-warn threshold (e.g. -6 dBFS for level meters). Widget renders
   *  --power yellow once value crosses this. */
  warnAt?: number;
  /** Optional explicit zone bands. When omitted, widgets derive a 3-zone
   *  layout from min / warnAt / dangerAt / max. Provide explicit zones for
   *  meters where "too low is also bad" (mic peak, output peak, gain
   *  reduction stages) — those need 5-band layouts the 3-zone derivation
   *  can't express. */
  zones?: ReadonlyArray<MeterZone>;
  /** Color-token tag. Widget falls back to --accent if absent. */
  colorToken: MeterColorToken;
  /** What widget kind the Library drawer creates by default for this reading. */
  defaultKind: MeterDefaultKind;
}

/** Resolve the zone list for a reading at the operator's current axis range,
 *  falling back to a 3-zone (ok/warn/danger) layout derived from
 *  warnAt/dangerAt when the def has no explicit zones. Returns an empty
 *  array when no thresholds are defined (e.g. RX signal-strength bars whose
 *  amber gradient already conveys the same information). */
export function resolveZones(
  def: MeterReadingDef,
  min: number,
  max: number,
): ReadonlyArray<MeterZone> {
  if (def.zones && def.zones.length > 0) return def.zones;
  if (def.warnAt === undefined && def.dangerAt === undefined) return [];
  const out: MeterZone[] = [];
  const warn = def.warnAt;
  const danger = def.dangerAt;
  if (warn !== undefined) {
    out.push({ from: min, to: warn, level: 'ok' });
    if (danger !== undefined && danger > warn) {
      out.push({ from: warn, to: danger, level: 'warn' });
      out.push({ from: danger, to: max, level: 'danger' });
    } else {
      out.push({ from: warn, to: max, level: 'warn' });
    }
  } else if (danger !== undefined) {
    out.push({ from: min, to: danger, level: 'ok' });
    out.push({ from: danger, to: max, level: 'danger' });
  }
  return out;
}

/** Immersive-palette CSS color for a zone tick rendered on a BigArc /
 *  VuColumn / PullDownArc widget. The immersive primitives use the
 *  `--immersive-*` token family rather than the global `--ok / --power /
 *  --tx` palette so the gauges keep their dark, high-contrast aesthetic. */
export function immersiveZoneTickColor(level: MeterZoneLevel): string {
  switch (level) {
    case 'ok':
      return 'var(--immersive-good)';
    case 'warn':
      return 'var(--immersive-warn)';
    case 'danger':
      return 'var(--immersive-tx)';
  }
}

/** A coloured tick rendered at a zone-level transition on a meter widget.
 *  `frac` is the position along the widget's value axis as a 0..1 fraction
 *  (linear from min..max — widgets with non-linear internal scales such as
 *  the VuColumn's log dBFS axis must remap before rendering). `level` is
 *  the colour of the zone ENTERED at this boundary, so the tick reads as
 *  "you cross into <colour> at this point on the axis". */
export interface ZoneTick {
  frac: number;
  level: MeterZoneLevel;
}

/** Emit a tick at every zone-level boundary in the widget's resolved zone
 *  list. Used to mark "the sweet spot lives between this green tick and
 *  this amber tick" without painting solid colour bands across the gauge
 *  (which read as a rainbow at idle).
 *
 *  Edge rule: skip ticks at `frac < 0.02` to keep them off the gauge
 *  anchor — a tick at 0 % of the axis is visually attached to the bezel
 *  and reads as a stray pixel. Ticks at `frac = 1.0` are kept: that is
 *  the "you're at the rated rail" cue (e.g. the red watts ceiling tick
 *  on `TxFwdWatts` whose `dangerAt === defaultMax`).
 *
 *  Adjacent zones with the same level produce no tick (we mark colour
 *  changes, not arbitrary zone boundaries). */
export function zoneTransitionTicks(
  def: MeterReadingDef,
  min: number,
  max: number,
): ReadonlyArray<ZoneTick> {
  if (max <= min) return [];
  const zones = resolveZones(def, min, max);
  if (zones.length < 2) return [];
  const span = max - min;
  const out: ZoneTick[] = [];
  for (let i = 1; i < zones.length; i++) {
    const prev = zones[i - 1];
    const curr = zones[i];
    if (!prev || !curr) continue;
    if (prev.level === curr.level) continue;
    // The boundary is shared between consecutive zones: prev.to === curr.from.
    // Use curr.from as the canonical boundary value.
    const boundary = curr.from;
    if (boundary < min || boundary > max) continue;
    const frac = (boundary - min) / span;
    if (frac < 0.02) continue;
    out.push({ frac, level: curr.level });
  }
  return out;
}

// Convenience factories — keeps the table below readable.
const rxSignal = (
  id: MeterReadingId,
  label: string,
  short: string,
): MeterReadingDef => ({
  id,
  label,
  short,
  category: 'rx-signal',
  unit: 'dBm',
  defaultMin: -127,
  defaultMax: -13,
  colorToken: 'amber-signal',
  defaultKind: 'hbar',
});

const rxAdc = (
  id: MeterReadingId,
  label: string,
  short: string,
): MeterReadingDef => ({
  id,
  label,
  short,
  category: 'rx-adc',
  unit: 'dBFS',
  defaultMin: -100,
  defaultMax: 0,
  warnAt: -12,
  dangerAt: -3,
  colorToken: 'accent',
  // dBFS axis fits the immersive VuColumn LED column (its native scale).
  defaultKind: 'vucolumn',
});

const rxAgcEnv = (
  id: MeterReadingId,
  label: string,
  short: string,
): MeterReadingDef => ({
  id,
  label,
  short,
  category: 'rx-agc',
  unit: 'dBm',
  defaultMin: -140,
  defaultMax: 0,
  colorToken: 'accent',
  defaultKind: 'hbar',
});

const txStageLevel = (
  id: MeterReadingId,
  label: string,
  short: string,
): MeterReadingDef => ({
  id,
  label,
  short,
  category: 'tx-stage',
  unit: 'dBFS',
  defaultMin: -30,
  defaultMax: 12,
  warnAt: -6,
  dangerAt: 0,
  colorToken: 'accent',
  // The immersive Signal Chain row uses VuColumn for every level reading;
  // configurable panel matches by default.
  defaultKind: 'vucolumn',
});

const txStageGr = (
  id: MeterReadingId,
  label: string,
  short: string,
): MeterReadingDef => ({
  id,
  label,
  short,
  category: 'tx-protection',
  unit: 'dB',
  defaultMin: 0,
  defaultMax: 25,
  warnAt: 3,
  dangerAt: 10,
  colorToken: 'tx',
  // GR is right-anchored "leveler pulling the chain down" — the
  // PullDownArc's native shape.
  defaultKind: 'pulldown',
});

/** Single source of truth: every reading the Meters Panel can render. */
export const METER_CATALOG: Record<MeterReadingId, MeterReadingDef> = {
  // ---- RX ----
  [MeterReadingId.RxSignalPk]: rxSignal(
    MeterReadingId.RxSignalPk,
    'RX Signal (Pk)',
    'S Pk',
  ),
  [MeterReadingId.RxSignalAv]: rxSignal(
    MeterReadingId.RxSignalAv,
    'RX Signal (Avg)',
    'S Av',
  ),
  [MeterReadingId.RxAdcPk]: rxAdc(MeterReadingId.RxAdcPk, 'ADC Input (Pk)', 'ADC Pk'),
  [MeterReadingId.RxAdcAv]: rxAdc(MeterReadingId.RxAdcAv, 'ADC Input (Avg)', 'ADC Av'),
  [MeterReadingId.RxAgcGain]: {
    id: MeterReadingId.RxAgcGain,
    label: 'AGC Gain',
    short: 'AGC',
    category: 'rx-agc',
    unit: 'dB',
    // Signed swing: −80 (deep cut) … +60 (deep boost). Centre at zero.
    defaultMin: -40,
    defaultMax: 60,
    colorToken: 'accent',
    defaultKind: 'hbar',
  },
  [MeterReadingId.RxAgcEnvPk]: rxAgcEnv(
    MeterReadingId.RxAgcEnvPk,
    'AGC Envelope (Pk)',
    'AGC Pk',
  ),
  [MeterReadingId.RxAgcEnvAv]: rxAgcEnv(
    MeterReadingId.RxAgcEnvAv,
    'AGC Envelope (Avg)',
    'AGC Av',
  ),
  // ---- TX power / SWR ----
  [MeterReadingId.TxFwdWatts]: {
    id: MeterReadingId.TxFwdWatts,
    label: 'TX Forward Power',
    short: 'FWD W',
    category: 'tx-power',
    unit: 'W',
    defaultMin: 0,
    defaultMax: 5,
    warnAt: 4.5,
    dangerAt: 5,
    colorToken: 'power',
    // BigArc "watts" mode is exactly this meter's job in the immersive panel.
    defaultKind: 'bigarc',
  },
  [MeterReadingId.TxRefWatts]: {
    id: MeterReadingId.TxRefWatts,
    label: 'TX Reverse Power',
    short: 'REF W',
    category: 'tx-power',
    unit: 'W',
    defaultMin: 0,
    defaultMax: 1,
    warnAt: 0.25,
    dangerAt: 0.5,
    colorToken: 'tx',
    defaultKind: 'hbar',
  },
  [MeterReadingId.TxSwr]: {
    id: MeterReadingId.TxSwr,
    label: 'SWR',
    short: 'SWR',
    category: 'tx-power',
    unit: 'ratio',
    defaultMin: 1,
    defaultMax: 3,
    warnAt: 1.5,
    dangerAt: 2,
    colorToken: 'tx',
    // BigArc "swr" mode — same gauge the immersive Final Output row shows.
    defaultKind: 'bigarc',
  },
  // ---- TX stage levels ----
  // MIC: too quiet (mic broken / OS muted) is just as bad as clipping. Five
  // bands so the operator sees both edges of the healthy window.
  [MeterReadingId.TxMicPk]: {
    ...txStageLevel(MeterReadingId.TxMicPk, 'Mic (Pk)', 'MIC Pk'),
    zones: [
      { from: -30, to: -25, level: 'danger' },
      { from: -25, to: -20, level: 'warn' },
      { from: -20, to: -10, level: 'ok' },
      { from: -10, to: -3, level: 'warn' },
      { from: -3, to: 12, level: 'danger' },
    ],
  },
  [MeterReadingId.TxMicAv]: txStageLevel(
    MeterReadingId.TxMicAv,
    'Mic (Avg)',
    'MIC Av',
  ),
  [MeterReadingId.TxEqPk]: txStageLevel(
    MeterReadingId.TxEqPk,
    'EQ Output (Pk)',
    'EQ Pk',
  ),
  [MeterReadingId.TxEqAv]: txStageLevel(
    MeterReadingId.TxEqAv,
    'EQ Output (Avg)',
    'EQ Av',
  ),
  [MeterReadingId.TxLvlrPk]: txStageLevel(
    MeterReadingId.TxLvlrPk,
    'Leveler (Pk)',
    'LVLR Pk',
  ),
  [MeterReadingId.TxLvlrAv]: txStageLevel(
    MeterReadingId.TxLvlrAv,
    'Leveler (Avg)',
    'LVLR Av',
  ),
  // Leveler GR: amber when chain is starved (0..2 dB = leveler doing nothing;
  // input is already plenty hot OR mic is dead). Green in normal operating
  // range. Amber when working hard. Red when pegged at typical ceiling
  // settings — operator should raise ceiling or hot mic up.
  [MeterReadingId.TxLvlrGr]: {
    ...txStageGr(MeterReadingId.TxLvlrGr, 'Leveler Gain Reduction', 'LVLR GR'),
    zones: [
      { from: 0, to: 2, level: 'warn' },
      { from: 2, to: 10, level: 'ok' },
      { from: 10, to: 14, level: 'warn' },
      { from: 14, to: 25, level: 'danger' },
    ],
  },
  [MeterReadingId.TxCfcPk]: txStageLevel(
    MeterReadingId.TxCfcPk,
    'CFC (Pk)',
    'CFC Pk',
  ),
  [MeterReadingId.TxCfcAv]: txStageLevel(
    MeterReadingId.TxCfcAv,
    'CFC (Avg)',
    'CFC Av',
  ),
  // CFC GR: same shape as ALC GR — bypassed → bar empty; engaged with
  // healthy speech → 1..6 dB on peaks; > 8 dB is over-driving the CFC stage.
  [MeterReadingId.TxCfcGr]: {
    ...txStageGr(MeterReadingId.TxCfcGr, 'CFC Gain Reduction', 'CFC GR'),
    zones: [
      { from: 0, to: 1, level: 'warn' },
      { from: 1, to: 7, level: 'ok' },
      { from: 7, to: 12, level: 'warn' },
      { from: 12, to: 25, level: 'danger' },
    ],
  },
  [MeterReadingId.TxCompPk]: txStageLevel(
    MeterReadingId.TxCompPk,
    'Compressor (Pk)',
    'COMP Pk',
  ),
  [MeterReadingId.TxCompAv]: txStageLevel(
    MeterReadingId.TxCompAv,
    'Compressor (Avg)',
    'COMP Av',
  ),
  [MeterReadingId.TxAlcPk]: txStageLevel(
    MeterReadingId.TxAlcPk,
    'ALC (Pk)',
    'ALC Pk',
  ),
  [MeterReadingId.TxAlcAv]: txStageLevel(
    MeterReadingId.TxAlcAv,
    'ALC (Avg)',
    'ALC Av',
  ),
  // ALC GR: 0 dB during transmit means nothing is getting through to the
  // limiter (chain starved). Healthy SSB compression sits 1..6 dB on peaks;
  // > 7 dB is hard-limiting; > 12 dB is splatter risk.
  [MeterReadingId.TxAlcGr]: {
    ...txStageGr(MeterReadingId.TxAlcGr, 'ALC Gain Reduction', 'ALC GR'),
    zones: [
      { from: 0, to: 1, level: 'warn' },
      { from: 1, to: 7, level: 'ok' },
      { from: 7, to: 12, level: 'warn' },
      { from: 12, to: 25, level: 'danger' },
    ],
  },
  // OUT PK: WDSP modulator output peak. Should sit just under digital clip
  // (-3..-1 dBFS) — too low means undriven, ≥ 0 dBFS is rail-clipping which
  // produces spectral splatter on the air. Five-band shape mirrors MIC.
  [MeterReadingId.TxOutPk]: {
    ...txStageLevel(MeterReadingId.TxOutPk, 'Final Output (Pk)', 'OUT Pk'),
    zones: [
      { from: -30, to: -20, level: 'danger' },
      { from: -20, to: -10, level: 'warn' },
      { from: -10, to: -1, level: 'ok' },
      { from: -1, to: 0, level: 'warn' },
      { from: 0, to: 12, level: 'danger' },
    ],
  },
  [MeterReadingId.TxOutAv]: txStageLevel(
    MeterReadingId.TxOutAv,
    'Final Output (Avg)',
    'OUT Av',
  ),
};

/** Library-drawer filter chips, in display order. */
export type MeterFilter = 'all' | 'rx' | 'tx' | 'power' | 'stage' | 'agc';

export const METER_FILTERS: ReadonlyArray<MeterFilter> = [
  'all',
  'rx',
  'tx',
  'power',
  'stage',
  'agc',
];

/** Whether a catalog entry matches the given Library-drawer filter chip. */
export function meterMatchesFilter(
  def: MeterReadingDef,
  filter: MeterFilter,
): boolean {
  switch (filter) {
    case 'all':
      return true;
    case 'rx':
      return (
        def.category === 'rx-signal' ||
        def.category === 'rx-adc' ||
        def.category === 'rx-agc'
      );
    case 'tx':
      return (
        def.category === 'tx-power' ||
        def.category === 'tx-stage' ||
        def.category === 'tx-protection'
      );
    case 'power':
      return def.category === 'tx-power';
    case 'stage':
      return def.category === 'tx-stage' || def.category === 'tx-protection';
    case 'agc':
      return def.category === 'rx-agc';
  }
}

/** Ordered list of all readings (matches enum declaration order). */
export const METER_READINGS: ReadonlyArray<MeterReadingDef> = Object.values(
  METER_CATALOG,
);
