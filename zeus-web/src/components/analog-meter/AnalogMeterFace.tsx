// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Pure-SVG analog dial: concentric scale arcs, ticks, labels, and the
// moving-coil needle (with shadow + counterweight + bright tip + peak-hold
// ghost). All colors come from tokens.css; no raw hex.
//
// Translated from the design handoff (display/project/s-meter-face.jsx) and
// adapted to the Zeus palette: the warm-red needle uses --tx, the active
// arc + +dB region uses --accent, base chrome uses --fg-* / --bg-* / --line.

import { Fragment } from 'react';
import { GlassDome } from '../meters/render/GlassDome';
import {
  FACE,
  pt,
  arcPath,
  normToDeg,
  SCALES,
  type ScaleDef,
  type ScaleId,
} from './analogMeterShared';

interface ScaleArcProps {
  scale: ScaleDef;
  radius: number;
  active: boolean;
  enabled: boolean;
  peakValueN: number | null;
}

function ScaleArc({ scale, radius, active, enabled, peakValueN }: ScaleArcProps) {
  if (!enabled) return null;

  const half = FACE.sweep / 2;
  const a0 = -half;
  const a1 = +half;

  const trackColor = active ? 'var(--accent)' : 'var(--fg-2)';
  const trackOpacity = active ? 0.9 : 0.45;
  const trackWidth = active ? 2 : 1;
  const labelColor = active ? 'var(--fg-0)' : 'var(--fg-1)';
  const labelOpacity = active ? 1 : 0.85;

  const tickAngle = (v: number) => normToDeg(scale.n(v));
  const peakDeg = active && peakValueN != null ? normToDeg(peakValueN) : null;
  const isS = scale.id === 's';

  return (
    <g>
      <path
        d={arcPath(FACE.cx, FACE.cy, radius, a0, a1)}
        stroke={trackColor}
        strokeOpacity={trackOpacity}
        strokeWidth={trackWidth}
        fill="none"
      />

      {peakDeg != null && (
        <path
          d={arcPath(FACE.cx, FACE.cy, radius, a0, peakDeg)}
          stroke="var(--accent)"
          strokeOpacity={0.35}
          strokeWidth={6}
          fill="none"
          strokeLinecap="round"
        />
      )}

      {/* +dB region of the S-scale always reads in accent, even when not the
          active scale, so the operator can see at a glance where they are. */}
      {isS && (
        <path
          d={arcPath(FACE.cx, FACE.cy, radius, normToDeg(scale.n(9)), a1)}
          stroke="var(--accent)"
          strokeOpacity={active ? 0.95 : 0.5}
          strokeWidth={trackWidth + 0.5}
          fill="none"
        />
      )}

      {scale.ticks.map((t, i) => {
        const ang = tickAngle(t.v);
        const len = t.major ? 12 : 6;
        const [x0, y0] = pt(FACE.cx, FACE.cy, radius - 1, ang);
        const [x1, y1] = pt(FACE.cx, FACE.cy, radius + len, ang);
        const isPlus = t.plus;
        return (
          <line
            key={`tk-${i}`}
            x1={x0}
            y1={y0}
            x2={x1}
            y2={y1}
            stroke={isPlus ? 'var(--accent)' : trackColor}
            strokeOpacity={isPlus ? (active ? 1 : 0.7) : trackOpacity}
            strokeWidth={t.major ? 2 : 1}
          />
        );
      })}

      {scale.ticks.map((t, i) => {
        if (!t.label) return null;
        const ang = tickAngle(t.v);
        const lr = radius + (t.major ? 32 : 26);
        const [lx, ly] = pt(FACE.cx, FACE.cy, lr, ang);
        const isPlus = t.plus;
        return (
          <text
            key={`lb-${i}`}
            x={lx}
            y={ly}
            fill={isPlus ? 'var(--accent)' : labelColor}
            opacity={isPlus ? (active ? 1 : 0.85) : labelOpacity}
            fontSize={t.major ? 24 : 19}
            fontWeight={t.major ? 800 : 700}
            fontFamily="var(--font-sans)"
            textAnchor="middle"
            dominantBaseline="middle"
          >
            {t.label}
          </text>
        );
      })}

      {/* Scale label on the left (S, PO, SWR). */}
      {(() => {
        const [lx, ly] = pt(FACE.cx, FACE.cy, radius + 6, a0 - 2.5);
        return (
          <text
            x={lx - 18}
            y={ly}
            fill={active ? 'var(--fg-0)' : 'var(--fg-1)'}
            opacity={active ? 1 : 0.85}
            fontSize={20}
            fontWeight={800}
            fontFamily="var(--font-sans)"
            letterSpacing="0.08em"
            textAnchor="end"
            dominantBaseline="middle"
          >
            {scale.label}
          </text>
        );
      })()}

      {active && scale.unit && (() => {
        const tip = scale.ticks[scale.ticks.length - 1];
        if (!tip) return null;
        const ang = tickAngle(tip.v) + 2;
        const [ux, uy] = pt(FACE.cx, FACE.cy, radius + 32, ang);
        return (
          <text
            x={ux + 4}
            y={uy}
            fill="var(--accent)"
            fontSize={20}
            fontWeight={800}
            fontFamily="var(--font-sans)"
            textAnchor="start"
            dominantBaseline="middle"
          >
            {scale.unit}
          </text>
        );
      })()}
    </g>
  );
}

interface NeedleProps {
  angleDeg: number;
  peakAngleDeg: number | null;
}

function Needle({ angleDeg, peakAngleDeg }: NeedleProps) {
  const r = FACE.rOuter + 30;
  const tail = -55;
  const [tx, ty] = pt(FACE.cx, FACE.cy, r, 0);
  const [bx, by] = pt(FACE.cx, FACE.cy, tail, 0);

  return (
    <Fragment>
      {peakAngleDeg != null && (
        <g transform={`rotate(${peakAngleDeg} ${FACE.cx} ${FACE.cy})`}>
          <line
            x1={FACE.cx}
            y1={FACE.cy}
            x2={tx}
            y2={ty}
            stroke="var(--accent)"
            strokeOpacity={0.4}
            strokeWidth={1.5}
          />
        </g>
      )}

      <g transform={`rotate(${angleDeg} ${FACE.cx} ${FACE.cy})`}>
        {/* Soft shadow under the needle blade. */}
        <line
          x1={FACE.cx}
          y1={FACE.cy + 2}
          x2={tx}
          y2={ty + 2}
          stroke="#000"
          strokeOpacity={0.45}
          strokeWidth={3}
        />
        {/* Counterweight below the pivot. */}
        <line
          x1={FACE.cx}
          y1={FACE.cy}
          x2={bx}
          y2={by}
          stroke="var(--tx)"
          strokeOpacity={0.7}
          strokeWidth={3}
          strokeLinecap="round"
        />
        {/* Main blade. */}
        <line
          x1={FACE.cx}
          y1={FACE.cy}
          x2={tx}
          y2={ty}
          stroke="var(--tx)"
          strokeWidth={2.4}
          strokeLinecap="round"
        />
        {/* Bright tip. */}
        <line
          x1={FACE.cx + (tx - FACE.cx) * 0.55}
          y1={FACE.cy + (ty - FACE.cy) * 0.55}
          x2={tx}
          y2={ty}
          stroke="var(--power)"
          strokeWidth={2.6}
          strokeLinecap="round"
        />
      </g>

      {/* Pivot cap drawn last so it sits above the needle. */}
      <circle cx={FACE.cx} cy={FACE.cy} r={9} fill="var(--bg-3)" stroke="var(--panel-edge)" strokeWidth={1.5} />
      <circle cx={FACE.cx} cy={FACE.cy} r={4} fill="var(--tx)" />
    </Fragment>
  );
}

interface NeedleShadowProps {
  angleDeg: number;
}

function NeedleShadow({ angleDeg }: NeedleShadowProps) {
  const r = FACE.rOuter + 18;
  const half = FACE.sweep / 2;
  const startA = -half;
  const endA = angleDeg;
  if (endA <= startA + 0.5) return null;
  const [x0, y0] = pt(FACE.cx, FACE.cy, r, startA);
  const [x1, y1] = pt(FACE.cx, FACE.cy, r, endA);
  return (
    <path
      d={`M ${FACE.cx} ${FACE.cy} L ${x0} ${y0} A ${r} ${r} 0 0 1 ${x1} ${y1} Z`}
      fill="url(#analogMeterSweepGrad)"
      opacity={0.18}
    />
  );
}

export interface AnalogMeterFaceProps {
  enabledScales: Record<ScaleId, boolean>;
  activeScaleId: ScaleId;
  /** Needle position as 0..1 against the active scale. */
  needleN: number;
  /** Peak-hold position as 0..1 against the active scale, or null to suppress. */
  peakN: number | null;
  /** Per-render scale set; defaults to the canonical SCALES. The panel
   *  passes a customised set so operator tick selections + PO full-scale
   *  changes feed through without mutating module-global state. */
  scales?: Record<ScaleId, ScaleDef>;
}

export function AnalogMeterFace({
  enabledScales,
  activeScaleId,
  needleN,
  peakN,
  scales = SCALES,
}: AnalogMeterFaceProps) {
  const angle = normToDeg(Math.max(0, Math.min(1, needleN)));
  const peakAngle = peakN != null ? normToDeg(Math.max(0, Math.min(1, peakN))) : null;

  // Concentric radii: active scale takes the outermost slot; remaining enabled
  // scales render below it in a fixed display order (s, po, swr).
  const order: ScaleId[] = (['s', 'po', 'swr'] as const).filter((id) => enabledScales[id]);
  const radii: Partial<Record<ScaleId, number>> = {};
  let r = FACE.rOuter;
  radii[activeScaleId] = r;
  r -= FACE.arcGap;
  for (const id of order) {
    if (id === activeScaleId) continue;
    radii[id] = r;
    r -= FACE.arcGap;
  }

  return (
    <div className="analog-meter-face-wrap">
      <svg
        className="analog-meter-face"
        viewBox={`0 ${FACE.h * 0.05} ${FACE.w} ${FACE.h - FACE.h * 0.05}`}
        preserveAspectRatio="xMidYMid meet"
      >
        <defs>
          <radialGradient id="analogMeterDialBg" cx="50%" cy="100%" r="80%">
            <stop offset="0%" stopColor="var(--bg-2)" />
            <stop offset="60%" stopColor="var(--bg-1)" />
            <stop offset="100%" stopColor="var(--bg-0)" />
          </radialGradient>
          <linearGradient id="analogMeterSweepGrad" x1="0" x2="0" y1="1" y2="0">
            <stop offset="0%" stopColor="var(--accent)" stopOpacity="0" />
            <stop offset="100%" stopColor="var(--accent)" stopOpacity="0.6" />
          </linearGradient>
        </defs>

        <rect x="0" y="0" width={FACE.w} height={FACE.h} fill="url(#analogMeterDialBg)" />

        {(['s', 'po', 'swr'] as const).map((id) => {
          const radius = radii[id];
          if (radius == null) return null;
          return (
            <ScaleArc
              key={id}
              scale={scales[id]}
              radius={radius}
              active={id === activeScaleId}
              enabled={enabledScales[id]}
              peakValueN={id === activeScaleId ? peakN : null}
            />
          );
        })}

        <NeedleShadow angleDeg={angle} />
        <Needle angleDeg={angle} peakAngleDeg={peakAngle} />
        {/* Liquid-metal glass dome over the dial — static upper-left specular,
            the "instrument under curved glass" tell. */}
        <GlassDome
          defsId="analogMeterGlass"
          x={0}
          y={FACE.h * 0.05}
          width={FACE.w}
          height={FACE.h * 0.95}
        />
      </svg>
    </div>
  );
}
