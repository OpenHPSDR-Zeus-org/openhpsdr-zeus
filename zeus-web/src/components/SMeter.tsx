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

import { useEffect, useMemo, useRef, useState } from 'react';
import {
  SMETER_PEAK_EPSILON,
  initialSMeterPeakHoldState,
  stepSMeterPeakHold,
} from './sMeterPeakHold';

// RX scale: amateur-radio S-units. S9 = -73 dBm on HF. Each S-unit = 6 dB.
// Above S9 labelled in dB over S9 (+10, +20, +40, +60).
const S9_DBM = -73;
const DB_PER_S = 6;
const RX_MIN_DBM = S9_DBM - 9 * DB_PER_S; // S0 = -127 dBm
const RX_MAX_DBM = S9_DBM + 60;            // +60 over S9

type Tick = { pos: number; label: string; major: boolean };

const RX_TICKS: readonly Tick[] = (() => {
  const span = RX_MAX_DBM - RX_MIN_DBM;
  const at = (dbm: number) => (dbm - RX_MIN_DBM) / span;
  const ticks: Tick[] = [];
  // S-unit ticks (S0..S9)
  for (let s = 0; s <= 9; s++) {
    const dbm = RX_MIN_DBM + s * DB_PER_S;
    const major = s === 0 || s === 1 || s === 3 || s === 5 || s === 7 || s === 9;
    ticks.push({ pos: at(dbm), label: major ? `S${s}` : '', major });
  }
  // Over-S9 ticks
  for (const over of [10, 20, 40, 60]) {
    ticks.push({ pos: at(S9_DBM + over), label: `+${over}`, major: true });
  }
  return ticks;
})();

function rxFraction(dbm: number): number {
  const clamped = Math.max(RX_MIN_DBM, Math.min(RX_MAX_DBM, dbm));
  return (clamped - RX_MIN_DBM) / (RX_MAX_DBM - RX_MIN_DBM);
}

// Decide which tick labels to render given the available pixel width. Tick
// marks always draw; only the text thins out so labels never overlap when the
// meter is narrow. Greedy left-to-right: keep a label only if its box clears
// the last kept label's box by LABEL_GAP_PX.
const CHAR_PX = 5.4; // ~width of one glyph at the 9px mono label size
const LABEL_GAP_PX = 4; // minimum whitespace between adjacent label boxes
function visibleLabels(ticks: readonly Tick[], width: number): boolean[] {
  // Before first measurement, show everything (matches prior behaviour).
  if (width <= 0) return ticks.map((t) => !!t.label);
  let lastRight = -Infinity;
  return ticks.map((t) => {
    if (!t.label) return false;
    const halfW = (t.label.length * CHAR_PX) / 2;
    const center = t.pos * width;
    if (center - halfW >= lastRight + LABEL_GAP_PX) {
      lastRight = center + halfW;
      return true;
    }
    return false;
  });
}

// TX scale: 0..maxW, linear. Ticks at 0, 25, 50, 75, 100%.
function txTicks(maxW: number): Tick[] {
  return [0, 0.25, 0.5, 0.75, 1].map((p) => ({
    pos: p,
    label: `${Math.round(p * maxW)}`,
    major: true,
  }));
}

export type SMeterProps =
  | {
      mode: 'rx';
      /** Current RX signal strength in dBm. */
      dbm: number;
      /** Current signal-to-noise estimate in dB from the display floor. */
      noiseDeltaDb?: number | null;
    }
  | {
      mode: 'tx';
      /** Forward power in Watts. */
      watts: number;
      /** Max scale in Watts. Default 100. */
      maxWatts?: number;
    };

export function SMeter(props: SMeterProps) {
  const isTx = props.mode === 'tx';
  const maxWatts = isTx ? props.maxWatts ?? 100 : 100;

  const fraction = isTx
    ? Math.max(0, Math.min(1, props.watts / maxWatts))
    : rxFraction(props.dbm);

  const ticks = isTx ? txTicks(maxWatts) : RX_TICKS;

  // Track width drives responsive label thinning so the tick text never
  // collides on a narrow panel.
  const scaleRef = useRef<HTMLDivElement>(null);
  const [scaleW, setScaleW] = useState(0);
  useEffect(() => {
    const el = scaleRef.current;
    if (!el) return;
    const ro = new ResizeObserver((entries) => {
      const w = entries[0]?.contentRect.width ?? 0;
      setScaleW(w);
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  const labelShown = useMemo(() => visibleLabels(ticks, scaleW), [ticks, scaleW]);

  // Peak-hold: rises instantly, latches briefly after the signal leaves the
  // peak, then falls back to the live value.
  const [peak, setPeak] = useState(fraction);
  const peakStateRef = useRef(initialSMeterPeakHoldState(fraction));
  const targetFractionRef = useRef(fraction);

  useEffect(() => {
    let raf: number | null = null;
    let timeout: number | null = null;

    const cancel = () => {
      if (raf != null) {
        cancelAnimationFrame(raf);
        raf = null;
      }
      if (timeout != null) {
        window.clearTimeout(timeout);
        timeout = null;
      }
    };

    const publish = (nowMs: number) => {
      const next = stepSMeterPeakHold(
        peakStateRef.current,
        targetFractionRef.current,
        nowMs,
      );
      peakStateRef.current = next;
      setPeak(next.peak);
      return next;
    };

    const tick = (nowMs: number) => {
      raf = null;
      const next = publish(nowMs);
      if (next.peak > targetFractionRef.current + SMETER_PEAK_EPSILON) {
        raf = requestAnimationFrame(tick);
      }
    };

    const scheduleDecay = () => {
      const state = peakStateRef.current;
      if (state.peak <= targetFractionRef.current + SMETER_PEAK_EPSILON) {
        return;
      }
      const delayMs = state.holdUntilMs - performance.now();
      if (delayMs > 0) {
        timeout = window.setTimeout(() => {
          timeout = null;
          raf = requestAnimationFrame(tick);
        }, delayMs);
      } else {
        raf = requestAnimationFrame(tick);
      }
    };

    targetFractionRef.current = fraction;
    publish(performance.now());
    scheduleDecay();

    return cancel;
  }, [fraction]);

  const mainValue = isTx ? props.watts.toFixed(1) : props.dbm.toFixed(0);
  const unit = isTx ? 'W' : 'dBm';
  const subLabel = isTx ? null : sUnitLabel(props.dbm);
  const noiseDeltaLabel =
    !isTx &&
    props.noiseDeltaDb !== null &&
    props.noiseDeltaDb !== undefined &&
    Number.isFinite(props.noiseDeltaDb) &&
    props.noiseDeltaDb > 0
      ? `SNR ${props.noiseDeltaDb.toFixed(0)} dB`
      : null;
  // RX-only: light the sub-label when the signal is at/over S9. Hoisted to a
  // boolean because the `subLabel &&` block below can't narrow `props` back to
  // the rx variant from subLabel's truthiness alone.
  const subLabelHot = !isTx && props.dbm >= S9_DBM;

  const label = isTx ? 'PWR' : 'RX';

  // The colour ramp transitions from amber into power/TX-red at this anchor.
  // RX: anchored at S9 (strong-signal "into the red" feel, classic transceiver).
  // TX: anchored at 75% so over-drive reads hot.
  const anchor = isTx ? 0.75 : rxFraction(S9_DBM);
  const anchorPct = anchor * 100;

  // Full-width signal ramp drawn behind the clip. The lit colour therefore
  // depends on absolute bar position (not fill amount), so the bar warms as it
  // climbs past the anchor — amber (#FFA028, the Zeus signal-trace hue) below,
  // power-yellow then TX-red above.
  const rampBg =
    `linear-gradient(90deg,` +
    ` rgba(255,160,40,0.30) 0%,` +
    ` rgba(255,160,40,0.92) ${(anchorPct * 0.55).toFixed(1)}%,` +
    ` #FFA028 ${anchorPct.toFixed(1)}%,` +
    ` var(--power) ${Math.min(100, anchorPct + (100 - anchorPct) * 0.45).toFixed(1)}%,` +
    ` var(--tx) 100%)`;

  return (
    <div
      role="meter"
      aria-label={isTx ? 'Transmit power' : 'Signal strength'}
      aria-valuemin={0}
      aria-valuemax={isTx ? maxWatts : Math.round(RX_MAX_DBM)}
      aria-valuenow={isTx ? Math.round(props.watts) : Math.round(props.dbm)}
      aria-valuetext={
        !isTx && noiseDeltaLabel
          ? `${mainValue} ${unit}, ${subLabel}, ${noiseDeltaLabel}`
          : undefined
      }
      className="relative flex select-none items-stretch gap-3"
      style={{
        padding: '10px 12px',
        background: 'linear-gradient(180deg, var(--bg-2) 0%, var(--bg-1) 100%)',
        border: '1px solid var(--line)',
        borderRadius: 'var(--r-md)',
        boxShadow: '0 1px 0 rgba(255,255,255,0.04) inset, 0 1px 3px rgba(0,0,0,0.35)',
        fontFamily: 'var(--font-mono)',
      }}
    >
      {/* Mode label */}
      <div className="flex flex-col justify-between" style={{ width: 30, paddingTop: 1, paddingBottom: 1 }}>
        <span
          style={{
            fontSize: 11,
            fontWeight: 700,
            letterSpacing: '0.12em',
            color: isTx ? 'var(--tx)' : 'var(--fg-1)',
          }}
        >
          {label}
        </span>
        <span style={{ fontSize: 9, color: 'var(--fg-3)', letterSpacing: '0.06em' }}>
          {unit}
        </span>
      </div>

      <div className="relative flex-1">
        {/* Recessed meter well */}
        <div
          className="relative overflow-hidden"
          style={{
            height: 26,
            borderRadius: 'var(--r-sm)',
            background: 'var(--bg-meter)',
            boxShadow:
              'inset 0 0 0 1px rgba(0,0,0,0.6), inset 0 1px 4px rgba(0,0,0,0.7), 0 1px 0 rgba(255,255,255,0.05)',
          }}
        >
          {/* Signal fill — clipped to the current level, warm halo glow. */}
          <div
            aria-hidden
            className="absolute inset-0 transition-[clip-path] duration-75 ease-out"
            style={{
              clipPath: `inset(0 ${(1 - fraction) * 100}% 0 0)`,
              background: rampBg,
              boxShadow: fraction > 0.01 ? 'var(--meter-halo)' : 'none',
            }}
          />
          {/* LED segmentation — opaque well-colour notches cut the fill into
              discrete bars, giving the lit-instrument look in both themes. */}
          <div
            aria-hidden
            className="absolute inset-0"
            style={{
              backgroundImage:
                'repeating-linear-gradient(90deg, transparent 0 5px, var(--bg-meter) 5px 7px)',
            }}
          />
          {/* S9 reference marker (RX only) — faint divider at the anchor. */}
          {!isTx && (
            <div
              aria-hidden
              className="absolute inset-y-0"
              style={{
                left: `${anchorPct}%`,
                width: 1,
                background: 'rgba(255,255,255,0.18)',
              }}
            />
          )}
          {/* Peak-hold marker — bright warm pip. */}
          <div
            aria-hidden
            className="s-meter-peak absolute inset-y-0"
            style={{
              left: `calc(${peak * 100}% - 1px)`,
              width: 2,
              background: 'rgba(255,224,180,0.95)',
              boxShadow: '0 0 6px rgba(255,176,60,0.9)',
            }}
          />
        </div>

        {/* Tick scale — marks always draw; labels thin out responsively. */}
        <div
          ref={scaleRef}
          className="relative"
          style={{ marginTop: 5, height: 12, fontSize: 9, color: 'var(--fg-3)' }}
        >
          {ticks.map((t, i) => {
            const overAnchor = !isTx && t.pos > anchor + 0.001;
            return (
              <div
                key={`${t.label}-${i}`}
                className="absolute top-0 flex -translate-x-1/2 flex-col items-center"
                style={{ left: `${t.pos * 100}%` }}
              >
                <div
                  style={{
                    width: 1,
                    height: t.major ? 5 : 3,
                    background: t.major ? 'var(--line-strong)' : 'var(--line)',
                  }}
                />
                {labelShown[i] && (
                  <div
                    className="whitespace-nowrap leading-none"
                    style={{
                      marginTop: 2,
                      fontWeight: 600,
                      letterSpacing: '0.02em',
                      color: overAnchor ? 'var(--power)' : 'var(--fg-3)',
                    }}
                  >
                    {t.label}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </div>

      {/* Digital readout */}
      <div
        className="flex flex-col items-end justify-center"
        style={{ width: 78, paddingBottom: 2 }}
      >
        <span style={{ lineHeight: 1, color: 'var(--fg-0)', fontVariantNumeric: 'tabular-nums' }}>
          <span style={{ fontSize: 20, fontWeight: 700, letterSpacing: '-0.01em' }}>
            {mainValue}
          </span>
          <span style={{ fontSize: 10, color: 'var(--fg-2)', marginLeft: 3 }}>{unit}</span>
        </span>
        {(subLabel || noiseDeltaLabel) && (
          <div className="flex flex-col items-end" style={{ marginTop: 3, gap: 1 }}>
            {subLabel && (
              <span
                style={{
                  fontSize: 10,
                  fontWeight: 600,
                  letterSpacing: '0.04em',
                  color: subLabelHot ? 'var(--power)' : 'var(--fg-2)',
                }}
              >
                {subLabel}
              </span>
            )}
            {noiseDeltaLabel && (
              <span
                title="Signal-to-noise estimate from the display noise floor"
                style={{
                  fontSize: 9,
                  fontWeight: 700,
                  letterSpacing: '0.04em',
                  color: 'var(--cyan)',
                  whiteSpace: 'nowrap',
                }}
              >
                {noiseDeltaLabel}
              </span>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

function sUnitLabel(dbm: number): string {
  if (dbm >= S9_DBM) {
    const over = dbm - S9_DBM;
    return `S9+${over.toFixed(0)}`;
  }
  const s = Math.max(0, Math.min(9, Math.round((dbm - RX_MIN_DBM) / DB_PER_S)));
  return `S${s}`;
}
