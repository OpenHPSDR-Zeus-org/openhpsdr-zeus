// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// TX Stage Meters — four-row summary panel: MIC / ALC / PWR / SWR. Lives
// in the right-hand side stack as a Dockable. Replaces the previous
// per-stage strip (MIC / EQ / LVLR / CFC / COMP / OUT) — operators
// asked for a tighter, four-line at-a-glance display that matches the
// near-black Zeus chrome instead of the gamer-LED look.
//
// Wire sources (TxMetersFrame, MsgType 0x16, ~10 Hz during MOX/TUN):
//   MIC  — wdspMicPk (TXA_MIC_PK, post-panel-gain)        — dBFS
//   ALC  — alcGr (gain reduction)                          — dB (0..25)
//   PWR  — fwdWatts (forward power, MsgType 0x14 / wire)   — W
//   SWR  — swr (forward/reflected ratio)                   — :1
//
// Bypass sentinel: WDSP returns ≤ −200 dBFS when a stage is idle / not
// running. We treat ≤ −200 as bypassed and render an em-dash readout
// rather than painting a misleading floor bar.

import { useRef } from 'react';
import { useTxStore } from '../state/tx-store';
import { useConnectionStore } from '../state/connection-store';
import { usePaStore } from '../state/pa-store';
import { useRadioStore } from '../state/radio-store';
import { useBallisticReading } from './meters/useBallisticReading';
import { recessedWellBackground, recessedWellShadow } from './meters/render/recessedWell';
import { mercuryGradientCss } from './meters/render/fillGradient';
import { MeterGlass } from './meters/render/MeterGlass';

// ────────────────────────────────────────────────────────────────────────────
// Overdrive indicator (re-exported so App.tsx can keep wiring it as the
// Dockable's `actions` slot). Fires when the mic ADC or post-DSP TX output
// peak exceeds 0 dBFS by a small margin, with a short hold so a single
// transient produces a visible flash.
//
// The +2 dB margin above 0 dBFS keeps the indicator from co-tripping with
// the MIC meter the moment its bar saturates at the 0 dBFS axis ceiling
// (post-panel-gain TXA_MIC_PK; same source value the MIC row renders).
// Operators want a real "you're driving past clean" cue, not a duplicate
// of the mic-meter peg.
// ────────────────────────────────────────────────────────────────────────────
const OVERDRIVE_CLIP_DBFS = 2;
const OVERDRIVE_HOLD_MS = 250;
const BYPASSED_DBFS_THRESHOLD = -200;

function isBypassed(dbfs: number): boolean {
  return dbfs <= BYPASSED_DBFS_THRESHOLD;
}

type OverdriveState = { tripped: boolean; mic: boolean; out: boolean };

function useOverdrive(): OverdriveState {
  const micPk = useTxStore((s) => s.wdspMicPk);
  const outPk = useTxStore((s) => s.outPk);
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const lastMicClipRef = useRef<number>(0);
  const lastOutClipRef = useRef<number>(0);

  const transmitting = moxOn || tunOn;
  const now =
    typeof performance !== 'undefined' ? performance.now() : Date.now();

  const micClip =
    transmitting && isFinite(micPk) && !isBypassed(micPk) && micPk >= OVERDRIVE_CLIP_DBFS;
  const outClip =
    transmitting && isFinite(outPk) && !isBypassed(outPk) && outPk >= OVERDRIVE_CLIP_DBFS;

  if (micClip) lastMicClipRef.current = now;
  if (outClip) lastOutClipRef.current = now;

  const mic = transmitting && now - lastMicClipRef.current < OVERDRIVE_HOLD_MS;
  const out = transmitting && now - lastOutClipRef.current < OVERDRIVE_HOLD_MS;

  return { tripped: mic || out, mic, out };
}

function overdriveTooltip(s: OverdriveState): string {
  const triggers: string[] = [];
  if (s.mic) triggers.push(`mic clipping (≥ +${OVERDRIVE_CLIP_DBFS} dBFS)`);
  if (s.out) triggers.push(`TX output clipping (≥ +${OVERDRIVE_CLIP_DBFS} dBFS)`);
  if (s.tripped) {
    return `Overdrive: ${triggers.join(' + ')}. Reduce mic gain or drive.`;
  }
  return `Overdrive detector — flashes when mic input or TX output exceeds +${OVERDRIVE_CLIP_DBFS} dBFS (≥ 2 dB past the MIC meter ceiling).`;
}

export function OverdriveIndicator() {
  const state = useOverdrive();
  const tripped = state.tripped;
  return (
    <span
      aria-live="polite"
      aria-label={tripped ? 'Overdrive detected' : 'Overdrive clear'}
      title={overdriveTooltip(state)}
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: 4,
        padding: '2px 6px',
        borderRadius: 0,
        fontSize: 9,
        fontWeight: 700,
        letterSpacing: '0.08em',
        textTransform: 'uppercase',
        background: tripped ? 'var(--tx)' : 'transparent',
        color: tripped ? '#fff' : 'var(--fg-3)',
        border: `1px solid ${tripped ? 'var(--tx)' : 'var(--panel-border)'}`,
        opacity: tripped ? 1 : 0.35,
        transition: 'background 120ms, opacity 120ms, color 120ms',
        boxShadow: tripped ? '0 0 6px var(--tx-soft)' : undefined,
      }}
    >
      <span
        aria-hidden="true"
        style={{
          width: 6,
          height: 6,
          borderRadius: '50%',
          background: tripped ? '#fff' : 'var(--fg-3)',
        }}
      />
      Overdrive
    </span>
  );
}

// Peak-hold and ballistic smoothing now come from the shared
// useBallisticReading hook (components/meters/useBallisticReading.ts) so
// these rows feel the same as every other meter in the app — same
// attack/decay, same peak ghost decay rate, all driven by the same per-
// widget rAF loop.

// ────────────────────────────────────────────────────────────────────────────
// Per-meter axis helpers. Each meter has its own scale + tick set, picked
// to match what an SSB operator wants to read at a glance.
//
//   MIC : −60 .. 0 dBFS                    ticks: −60 −40 −20 −10 −3 0
//   ALC : 0 .. 12 dB gain reduction        ticks: 0 3 6 9 12   warn zone ≥ 7
//   PWR : 0 .. paMaxWatts (or 100 fallback)ticks: 0 20 40 60 80 100 (rated %)
//   SWR : 1.0 .. 3.0+                      ticks: 1.0 1.5 2.0 2.5 3.0+
//                                          warn zone 1.5..2.5, hot ≥ 2.5
// ────────────────────────────────────────────────────────────────────────────

const MIC_FLOOR_DBFS = -60;
const MIC_CEIL_DBFS = 0;
const MIC_TICKS = ['-60', '-40', '-20', '-10', '-3', '0'];

function micPctOf(dbfs: number): number {
  if (!isFinite(dbfs) || isBypassed(dbfs)) return 0;
  const clamped = Math.max(MIC_FLOOR_DBFS, Math.min(MIC_CEIL_DBFS, dbfs));
  return ((clamped - MIC_FLOOR_DBFS) / (MIC_CEIL_DBFS - MIC_FLOOR_DBFS)) * 100;
}

const ALC_MAX_GR_DB = 12;
const ALC_TICKS = ['0', '3', '6', '9', '12'];

function alcPctOf(grDb: number): number {
  if (!isFinite(grDb) || isBypassed(grDb)) return 0;
  const clamped = Math.max(0, Math.min(ALC_MAX_GR_DB, grDb));
  return (clamped / ALC_MAX_GR_DB) * 100;
}

function pwrPctOf(watts: number, max: number): number {
  if (!isFinite(watts) || max <= 0) return 0;
  const clamped = Math.max(0, Math.min(max, watts));
  return (clamped / max) * 100;
}

// Generate six tick labels (0..max in five equal steps) so the PWR axis
// numbers track the actual rated wattage of the connected radio. HL2 = 10
// → "0 2 4 6 8 10"; ANAN-100 = 120 → "0 24 48 72 96 120"; 8000DLE = 250 →
// "0 50 100 150 200 250"; G2-1K = 1000 → "0 200 400 600 800 1000". Sub-W
// resolution shows one decimal so a 5 W HL2 stays readable.
function pwrTicks(max: number): ReadonlyArray<string> {
  if (!isFinite(max) || max <= 0) return ['0', '20', '40', '60', '80', '100'];
  const step = max / 5;
  const decimals = step >= 10 ? 0 : step >= 1 ? 0 : 1;
  return Array.from({ length: 6 }, (_, i) => (i * step).toFixed(decimals));
}

const SWR_FLOOR = 1.0;
const SWR_CEIL = 3.0;
const SWR_TICKS = ['1.0', '1.5', '2.0', '2.5', '3.0+'];

function swrPctOf(swr: number): number {
  if (!isFinite(swr) || swr < SWR_FLOOR) return 0;
  const clamped = Math.min(SWR_CEIL, swr);
  return ((clamped - SWR_FLOOR) / (SWR_CEIL - SWR_FLOOR)) * 100;
}

// ────────────────────────────────────────────────────────────────────────────
// Tick label strip — sits above the track. Uses a proper minus sign for
// negative numbers so the typography reads cleanly at 8.5 px mono.
// ────────────────────────────────────────────────────────────────────────────
function TickLabels({ labels }: { labels: ReadonlyArray<string> }) {
  return (
    <div
      aria-hidden="true"
      style={{
        position: 'relative',
        height: 11,
        marginBottom: 3,
        fontFamily: 'var(--font-mono)',
        fontSize: 8.5,
        letterSpacing: '0.04em',
        color: 'var(--fg-3)',
      }}
    >
      {labels.map((label, i) => {
        const isFirst = i === 0;
        const isLast = i === labels.length - 1;
        const transform = isFirst
          ? 'translateX(0)'
          : isLast
            ? 'translateX(-100%)'
            : 'translateX(-50%)';
        return (
          <span
            key={i}
            style={{
              position: 'absolute',
              top: 0,
              left: `${(i / (labels.length - 1)) * 100}%`,
              transform,
              whiteSpace: 'nowrap',
            }}
          >
            {label.replace('-', '−')}
          </span>
        );
      })}
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// Single meter row — label + track + readout. The track holds optional
// warning zones (drawn at low alpha so they read as background hints),
// the live fill, and a peak-hold tick.
// ────────────────────────────────────────────────────────────────────────────
type Zone = { from: number; to: number; color: string };

type MeterRowProps = {
  id: string;
  label: string;
  ticks: ReadonlyArray<string>;
  /** Live value as 0..100 of the meter's axis. */
  pct: number;
  /** Peak-hold value as 0..100 of the meter's axis. Hidden when ≤ pct. */
  peakPct: number;
  /** Color for the live fill. PWR/SWR override this with a gradient — see
   *  `gradient` instead. */
  color: string;
  /** Optional gradient instead of a flat color (PWR / SWR healthy→hot). */
  gradient?: string;
  /** Optional warning zones drawn behind the fill, low-alpha. */
  zones?: ReadonlyArray<Zone>;
  /** Live numeric readout — formatted string, e.g. "−24.6". */
  readNow: string;
  /** Unit suffix shown small/dim, e.g. "dBFS" or "W". */
  readUnit: string;
  /** Peak / secondary readout label, e.g. "PK", "GR", "PEP", "REF". */
  pkLabel: string;
  /** Peak / secondary readout value as a formatted string. */
  pkValue: string;
  /** Hot state — flashes the readout red (PWR pushing rated, SWR > 2.5). */
  hot?: boolean;
  /** Hover-tooltip explaining what this meter shows. */
  hint: string;
};

function MeterRow({
  id,
  label,
  ticks,
  pct,
  peakPct,
  color,
  gradient,
  zones,
  readNow,
  readUnit,
  pkLabel,
  pkValue,
  hot,
  hint,
}: MeterRowProps) {
  const heldVisible = peakPct > pct + 0.5;
  return (
    <div
      data-id={id}
      title={hint}
      style={{
        display: 'grid',
        gridTemplateColumns: '40px 1fr 84px',
        alignItems: 'center',
        columnGap: 10,
      }}
    >
      <div
        style={{
          fontFamily: 'var(--font-mono)',
          fontSize: 10.5,
          letterSpacing: '0.18em',
          color: 'var(--fg-1)',
          fontWeight: 600,
          textTransform: 'uppercase',
        }}
      >
        {label}
      </div>
      <div style={{ position: 'relative' }}>
        <TickLabels labels={ticks} />
        <div
          style={{
            position: 'relative',
            height: 14,
            // Recessed liquid-metal well + the warm amber "lit instrument on a
            // black bench" halo.
            background: recessedWellBackground(),
            border: '1px solid var(--line)',
            borderRadius: 2,
            boxShadow: recessedWellShadow({ warmHalo: true }),
            overflow: 'hidden',
          }}
        >
          {/* Subtle 10 % tick grid on the track itself — same trick as the
              design HTML, gives the bar a "ruled" feel without adding hue. */}
          <div
            aria-hidden="true"
            style={{
              position: 'absolute',
              inset: 0,
              backgroundImage:
                'repeating-linear-gradient(90deg, transparent 0 calc(10% - 1px), rgba(255,255,255,0.05) calc(10% - 1px) 10%)',
              pointerEvents: 'none',
            }}
          />
          {/* Warning / danger zone bands behind the live fill. */}
          {zones?.map((z, i) => (
            <div
              key={i}
              aria-hidden="true"
              style={{
                position: 'absolute',
                top: 0,
                bottom: 0,
                left: `${z.from}%`,
                width: `${Math.max(0, z.to - z.from)}%`,
                background: z.color,
                opacity: 0.14,
                pointerEvents: 'none',
              }}
            />
          ))}
          {/* Live fill — flat color or gradient depending on the meter. */}
          <div
            aria-hidden="true"
            style={{
              position: 'absolute',
              left: 0,
              top: 0,
              bottom: 0,
              width: `${pct}%`,
              background: `${mercuryGradientCss()}, ${gradient ?? color}`,
              transition: 'width 120ms linear, background 150ms',
              pointerEvents: 'none',
            }}
          />
          {/* Peak-hold tick. PWR/SWR use a neutral tick (the bar colour is
              already a healthy→hot gradient); MIC/ALC use the meter colour. */}
          {heldVisible && (
            <div
              aria-hidden="true"
              style={{
                position: 'absolute',
                top: -1,
                bottom: -1,
                width: 2,
                left: `calc(${peakPct}% - 1px)`,
                background: gradient ? 'var(--fg-1)' : color,
                transition: 'left 250ms cubic-bezier(.2,.7,.3,1)',
                pointerEvents: 'none',
              }}
            />
          )}
          {/* Liquid-metal glass cover — static domed specular. */}
          <MeterGlass radius={2} />
        </div>
      </div>
      <div
        style={{
          textAlign: 'right',
          fontFamily: 'var(--font-mono)',
          fontSize: 11,
          letterSpacing: '0.04em',
          color: 'var(--fg-1)',
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'flex-end',
          lineHeight: 1.25,
        }}
      >
        <div
          style={{
            color: hot ? 'var(--tx)' : 'var(--fg-0)',
            fontWeight: 600,
            fontSize: 12.5,
            letterSpacing: 0,
            transition: 'color 120ms',
          }}
        >
          {readNow}{' '}
          <span style={{ color: 'var(--fg-3)', fontSize: 9 }}>{readUnit}</span>
        </div>
        <div
          style={{
            color: 'var(--fg-3)',
            fontSize: 9.5,
            letterSpacing: '0.08em',
            textTransform: 'uppercase',
          }}
        >
          {pkLabel} <b style={{ color: 'var(--fg-2)', fontWeight: 600, marginLeft: 4 }}>{pkValue}</b>
        </div>
      </div>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// Footer status strip — mode + frequency, live LED, PA temperature. Mirrors
// the design HTML's footer; values are pulled from the live stores so the
// strip is meaningful, not decorative.
// ────────────────────────────────────────────────────────────────────────────
function MhzFmt(hz: number): string {
  return (hz / 1_000_000).toFixed(3);
}

function StatusFooter() {
  const mode = useConnectionStore((s) => s.mode);
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const paTempC = useTxStore((s) => s.paTempC);
  const transmitting = moxOn || tunOn;

  const tempLabel = paTempC == null ? '—' : `${Math.round(paTempC)}°C`;

  return (
    <div
      style={{
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'space-between',
        height: 22,
        padding: '0 10px',
        background: 'var(--panel-head-bot)',
        borderTop: '1px solid var(--panel-border)',
        fontFamily: 'var(--font-mono)',
        fontSize: 9.5,
        letterSpacing: '0.18em',
        color: 'var(--fg-3)',
        textTransform: 'uppercase',
        flex: '0 0 auto',
      }}
    >
      <div style={{ color: 'var(--fg-2)' }}>
        {mode} · {MhzFmt(vfoHz)} MHz
      </div>
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 6,
          color: transmitting ? 'var(--tx)' : 'var(--fg-2)',
        }}
      >
        <span
          style={{
            width: 5,
            height: 5,
            borderRadius: '50%',
            background: transmitting ? 'var(--tx)' : 'var(--fg-3)',
            boxShadow: transmitting
              ? '0 0 0 2px var(--tx-soft)'
              : '0 0 0 2px rgba(155,155,160,0.10)',
          }}
        />
        {transmitting ? 'TX' : 'IDLE'}
      </div>
      <div style={{ color: 'var(--fg-2)' }}>PA {tempLabel}</div>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// Main panel — orchestrates the four meter rows + the footer. Re-renders
// whenever any of the live source fields change. We intentionally keep the
// readouts active during RX (just dimmed) so the operator can see the
// noise floor and current SWR before keying.
// ────────────────────────────────────────────────────────────────────────────

export function TxStageMeters() {
  // Shared by all four ballistic hooks so an IntersectionObserver on the
  // panel root pauses every rAF loop when the dock is scrolled away.
  const panelRef = useRef<HTMLDivElement | null>(null);

  const wdspMicPkRaw = useTxStore((s) => s.wdspMicPk);
  const alcGrRaw = useTxStore((s) => s.alcGr);
  const fwdWattsRaw = useTxStore((s) => s.fwdWatts);
  const swrRaw = useTxStore((s) => s.swr);
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const transmitting = moxOn || tunOn;

  // Rated PA power for the PWR axis. Resolution order:
  //   1. Operator override from the PA settings panel (paMaxPowerWatts > 0)
  //   2. Board's published MaxPowerWatts (from /api/radio/capabilities)
  //   3. 100 W last-ditch fallback for an unrecognised radio
  // The board-default path is what makes a fresh connect to an HL2 show a
  // 10 W axis instead of the historical 100 W axis where 5 W barely moved
  // the bar.
  const paMaxWatts = usePaStore((s) => s.settings.global.paMaxPowerWatts);
  const boardMaxWatts = useRadioStore((s) => s.capabilities.maxPowerWatts);
  const ratedW = paMaxWatts > 0 ? paMaxWatts : boardMaxWatts > 0 ? boardMaxWatts : 100;

  // Shared ballistic + peak-hold for every row. Each hook owns its own
  // rAF loop sampling the latest value via getState() — the four loops
  // also drive the 30 Hz repaint cadence the peak ticks need (previously
  // a separate useMeterRefresh raf). Returned `value` is the smoothed
  // bar/numeric readout; `peak` feeds the held peak tick.
  const mic = useBallisticReading(
    () => useTxStore.getState().wdspMicPk,
    { min: MIC_FLOOR_DBFS, max: MIC_CEIL_DBFS },
    panelRef,
  );
  const alc = useBallisticReading(
    () => useTxStore.getState().alcGr,
    { min: 0, max: ALC_MAX_GR_DB },
    panelRef,
  );
  const pwr = useBallisticReading(
    () => useTxStore.getState().fwdWatts,
    { min: 0, max: Math.max(1, ratedW) },
    panelRef,
  );
  const swrRead = useBallisticReading(
    () => useTxStore.getState().swr,
    { min: SWR_FLOOR, max: SWR_CEIL },
    panelRef,
  );

  // ── MIC ────────────────────────────────────────────────────────────
  const micBypassed = !isFinite(mic.value) || isBypassed(mic.value);
  const micPct = micPctOf(mic.value);
  const micPeakPct = micPctOf(mic.peak);
  const micNow = micBypassed ? '—' : mic.value.toFixed(1).replace('-', '−');
  // Absolute peak (the "PK" readout on the right) tracks the RAW wire value
  // so an SSB transient between TxMetersFrames isn't shaved by the smoother
  // feeding the bar. Resets between transmissions.
  const micPk = useRef<number>(MIC_FLOOR_DBFS);
  if (isFinite(wdspMicPkRaw) && !isBypassed(wdspMicPkRaw) && wdspMicPkRaw > micPk.current) {
    micPk.current = wdspMicPkRaw;
  }
  if (micBypassed) micPk.current = MIC_FLOOR_DBFS;
  const micPkValue =
    micPk.current <= MIC_FLOOR_DBFS
      ? '—'
      : micPk.current.toFixed(1).replace('-', '−');

  // ── ALC (gain reduction) ───────────────────────────────────────────
  const alcBypassed = !isFinite(alc.value) || isBypassed(alc.value);
  const alcGrClamped = alcBypassed ? 0 : Math.max(0, alc.value);
  const alcPct = alcPctOf(alcGrClamped);
  const alcPeakPct = alcPctOf(
    !isFinite(alc.peak) || isBypassed(alc.peak) ? 0 : Math.max(0, alc.peak),
  );
  const alcNow = alcBypassed ? '—' : alcGrClamped.toFixed(1);
  const alcPkRef = useRef<number>(0);
  const alcGrRawClamped =
    !isFinite(alcGrRaw) || isBypassed(alcGrRaw) ? 0 : Math.max(0, alcGrRaw);
  if (alcGrRawClamped > alcPkRef.current) alcPkRef.current = alcGrRawClamped;
  if (!transmitting) alcPkRef.current = 0;
  const alcPkValue = alcPkRef.current.toFixed(1);

  // ── PWR (forward watts) ────────────────────────────────────────────
  const pwrPct = pwrPctOf(pwr.value, ratedW);
  const pwrPeakPct = pwrPctOf(pwr.peak, ratedW);
  const pwrNow = isFinite(pwr.value) ? pwr.value.toFixed(1) : '—';
  const pwrPkRef = useRef<number>(0);
  if (isFinite(fwdWattsRaw) && fwdWattsRaw > pwrPkRef.current) pwrPkRef.current = fwdWattsRaw;
  if (!transmitting) pwrPkRef.current = 0;
  const pwrPkValue = pwrPkRef.current.toFixed(1);
  const pwrHot = transmitting && pwrPct > 95;

  // ── SWR ────────────────────────────────────────────────────────────
  const swrPct = swrPctOf(swrRead.value);
  const swrPeakPct = swrPctOf(swrRead.peak);
  const swrNow = isFinite(swrRead.value) ? swrRead.value.toFixed(2) : '—';
  const swrPkRef = useRef<number>(SWR_FLOOR);
  if (isFinite(swrRaw) && swrRaw > swrPkRef.current) swrPkRef.current = swrRaw;
  if (!transmitting) swrPkRef.current = SWR_FLOOR;
  const swrPkValue = swrPkRef.current.toFixed(1);
  const swrHot = transmitting && swrRead.value > 2.5;

  // PWR / SWR get the healthy → hot gradient so the bar's colour itself
  // communicates "you're pushing it." MIC stays cyan-ish (--accent),
  // ALC stays amber (--power) — both pure-colour fills.
  const pwrSwrGradient =
    'linear-gradient(90deg,' +
    ' #2e7a2e 0%,' +
    ' #2e7a2e 55%,' +
    ' #4aa04a 70%,' +
    ' var(--power) 82%,' +
    ' var(--tx) 100%)';

  // ALC warning zone — once GR exceeds ~7 dB we're into "limiter is
  // working hard" territory. Painted as a faint amber band so the eye
  // catches it before the readout flashes.
  const alcZones: ReadonlyArray<Zone> = [
    { from: (7 / ALC_MAX_GR_DB) * 100, to: 100, color: 'var(--power)' },
  ];

  // SWR zones — caution band 1.5..2.5, danger band ≥ 2.5. Matches the
  // common "1.5:1 is fine, > 2:1 start to worry, > 2.5:1 trips the
  // backend SWR alert" convention used elsewhere in Zeus.
  const swr15 = ((1.5 - SWR_FLOOR) / (SWR_CEIL - SWR_FLOOR)) * 100;
  const swr25 = ((2.5 - SWR_FLOOR) / (SWR_CEIL - SWR_FLOOR)) * 100;
  const swrZones: ReadonlyArray<Zone> = [
    { from: swr15, to: swr25, color: 'var(--power)' },
    { from: swr25, to: 100, color: 'var(--tx)' },
  ];

  return (
    <div
      ref={panelRef}
      aria-label="TX stage meters — MIC, ALC, PWR, SWR"
      style={{
        display: 'flex',
        flexDirection: 'column',
        height: '100%',
        opacity: transmitting ? 1 : 0.7,
        transition: 'opacity 120ms',
      }}
    >
      <div
        style={{
          padding: '12px 12px 10px',
          display: 'flex',
          flexDirection: 'column',
          gap: 12,
          flex: '1 1 auto',
          minHeight: 0,
        }}
      >
        <MeterRow
          id="mic"
          label="MIC"
          ticks={MIC_TICKS}
          pct={micPct}
          peakPct={micPeakPct}
          color="var(--accent)"
          readNow={micNow}
          readUnit="dBFS"
          pkLabel="PK"
          pkValue={micPkValue}
          hint="Mic peak entering WDSP TXA, post-panel-gain (TXA_MIC_PK)"
        />
        <MeterRow
          id="alc"
          label="ALC"
          ticks={ALC_TICKS}
          pct={alcPct}
          peakPct={alcPeakPct}
          color="var(--power)"
          zones={alcZones}
          readNow={alcNow}
          readUnit="dB"
          pkLabel="GR"
          pkValue={alcPkValue}
          hint="ALC gain reduction. Healthy SSB compression sits in the 3–10 dB band; sustained > 10 dB means the input is over-driving the limiter."
        />
        <MeterRow
          id="pwr"
          label="PWR"
          ticks={pwrTicks(ratedW)}
          pct={pwrPct}
          peakPct={pwrPeakPct}
          color="var(--power)"
          gradient={pwrSwrGradient}
          readNow={pwrNow}
          readUnit="W"
          pkLabel="PEP"
          pkValue={pwrPkValue}
          hot={pwrHot}
          hint={`Forward power. Axis 0..${ratedW} W (PA panel override → board default → 100 W fallback).`}
        />
        <MeterRow
          id="swr"
          label="SWR"
          ticks={SWR_TICKS}
          pct={swrPct}
          peakPct={swrPeakPct}
          color="var(--tx)"
          gradient={pwrSwrGradient}
          zones={swrZones}
          readNow={swrNow}
          readUnit=":1"
          pkLabel="PK"
          pkValue={swrPkValue}
          hot={swrHot}
          hint="Standing-wave ratio. ≤ 1.5:1 healthy, 1.5–2.5:1 caution, > 2.5:1 trips the backend SWR alert (and folds back drive)."
        />
      </div>
      <StatusFooter />
    </div>
  );
}
