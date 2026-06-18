// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Horizontal LED bar — VuColumn rotated 90°. Mirrors the immersive
// MIC PK / ALC PK column meter element-for-element: same NAME / SUB
// stack, same dark `#080a10` recessed track with bloom-behind-crisp
// gradient fill, same LED segment lines, same numeric scale labels at
// every tick position, same dashed red 0 dBFS reference, same white
// peak-hold tick with drop-shadow glow, same numeric readout.
// Layout flips column → row: NAME / SUB on the left, bar in the
// middle, readout on the right; tick lines + numeric labels sit
// above and below the bar (instead of left / right of the column).
//
// Color rules (CLAUDE.md, plan §4.6):
//   - rx-signal       → amber #FFA028 (single-hue gradient, alpha
//                       rises with strength) — the only allowed raw hex
//   - everything else → good → warn → tx signal-status gradient (same
//                       six stops as VuColumn so a row of horizontal
//                       bars reads as the same family as a row of
//                       vertical columns)

import type { CSSProperties } from 'react';
import type { MeterReadingDef, MeterUnit } from '../meterCatalog';
import { immersiveZoneTickColor, type ZoneTick } from '../meterCatalog';
import type { WidgetSettings } from '../widgetSettings';
import { BloomFill, BloomFilter, PeakTick, prefixDefs } from '../render/svgChrome';

interface HBarMeterProps {
  value: number;
  peak?: number;
  def: MeterReadingDef;
  settings: WidgetSettings;
  /** Operator label override. When set, replaces the catalog NAME / SUB
   *  split with the override as a single-line name (no sub). */
  label?: string;
  /** Coloured tick marks at zone-level boundaries — same `frac` convention
   *  as VuColumn (left → right linear from min..max). Rendered as short
   *  vertical lines below the bar, mirroring VuColumn's right-side ticks. */
  zoneTicks?: ReadonlyArray<ZoneTick>;
}

const SENTINEL_THRESHOLD = -200;

function isSilent(v: number): boolean {
  return !isFinite(v) || v <= SENTINEL_THRESHOLD;
}

function fractionOf(min: number, max: number, value: number): number {
  if (!isFinite(value)) return 0;
  if (max <= min) return 0;
  return Math.max(0, Math.min(1, (value - min) / (max - min)));
}

function formatReadout(def: MeterReadingDef, value: number): string {
  if (isSilent(value)) return '−∞';
  switch (def.unit) {
    case 'ratio':
      return value.toFixed(2);
    case 'W':
      return value < 10 ? value.toFixed(2) : value.toFixed(1);
    case 'dB':
    case 'dBFS':
    case 'dBm':
      return value.toFixed(0);
    default:
      return value.toFixed(1);
  }
}

/** Split a catalog `short` into NAME + SUB the same way MeterRenderer
 *  does for VuColumn ("MIC Pk" → "MIC" + "PK"). When there's no Pk/Av/GR
 *  marker, fall back to using the whole short as the name. */
function splitShort(short: string): { name: string; sub: string } {
  const trimmed = short.trim();
  const m = trimmed.match(/^(.+?)\s+(Pk|Av|Avg|GR)$/i);
  return {
    name: m?.[1] ?? trimmed,
    sub: m?.[2]?.toUpperCase() ?? '',
  };
}

interface ScaleTick {
  frac: number;
  label: string;
  isReference: boolean;
}

/** Generate scale tick positions + labels for the bar's axis. dBFS
 *  matches VuColumn's canonical [-60, -40, -20, -10, -6, -3, 0] set so
 *  a horizontal MIC / ALC bar reads as the same scale as the vertical
 *  column. Other units divide the axis into 5 evenly-spaced ticks
 *  formatted per unit. */
function generateScaleTicks(
  unit: MeterUnit,
  min: number,
  max: number,
): ScaleTick[] {
  const span = max - min;
  if (span <= 0) return [];

  if (unit === 'dBFS') {
    return [-60, -40, -20, -10, -6, -3, 0]
      .filter((v) => v >= min && v <= max)
      .map((v) => ({
        frac: (v - min) / span,
        label: v === 0 ? '0' : String(Math.abs(v)),
        isReference: v === 0,
      }));
  }

  const N = 5;
  const ticks: ScaleTick[] = [];
  for (let i = 0; i < N; i++) {
    const f = i / (N - 1);
    const v = min + span * f;
    let label: string;
    switch (unit) {
      case 'ratio':
        label = v.toFixed(1);
        break;
      case 'W':
        label = v < 10 ? v.toFixed(1) : v.toFixed(0);
        break;
      default:
        label = Math.round(v).toString();
    }
    ticks.push({ frac: f, label, isReference: false });
  }
  return ticks;
}

const VB_W = 200;
const VB_H = 50;
const BAR_Y = 18;
const BAR_H = 16;
const SEG_COUNT = 17;
// Bar container height in CSS pixels. The SVG renders at this height; the
// numeric label row floats above it via absolute positioning so the labels
// don't get distorted by the SVG's preserveAspectRatio="none" stretch.
const CONTAINER_H = 64;

export function HBarMeter({
  value,
  peak,
  def,
  settings,
  label,
  zoneTicks,
}: HBarMeterProps) {
  const min = settings.min ?? def.defaultMin;
  const max = settings.max ?? def.defaultMax;
  const silent = isSilent(value);
  const liveFrac = silent ? 0 : fractionOf(min, max, value);
  const peakFrac =
    peak !== undefined && !isSilent(peak) ? fractionOf(min, max, peak) : null;
  const showPeak =
    settings.peakHold !== false && peakFrac !== null && peakFrac > liveFrac && !silent;

  const isSignalGradient = def.colorToken === 'amber-signal';

  // Operator label override wins; otherwise split the catalog short the
  // same way MeterRenderer does for VuColumn (so the same reading reads as
  // the same widget regardless of orientation). NOTE: only `settings.label`
  // counts as an "override" — the `label` prop from MeterRenderer always
  // falls back to `def.label`, so checking it would skip the NAME / SUB
  // split for every widget the operator hasn't customised.
  const { name, sub } = settings.label
    ? { name: settings.label, sub: '' }
    : splitShort(def.short);
  // Suppress unused-import warning when neither `label` nor TS strict mode
  // bites — the prop stays in the public surface for symmetry with the
  // older HBarMeter contract callers may depend on.
  void label;

  // dBFS readings always anchor the dashed reference at 0 dBFS — the
  // universal "you're clipping" line, same as VuColumn. Other units fall
  // back to the catalog's `dangerAt` (e.g. 5 W on a fwd-power meter,
  // 2:1 SWR). Skip the line when the value would land on / outside the
  // visible window so it doesn't read as "always pegged."
  const refValue = def.unit === 'dBFS' ? 0 : def.dangerAt;
  const refFrac =
    refValue !== undefined && isFinite(refValue)
      ? fractionOf(min, max, refValue)
      : null;
  const showRef = refFrac !== null && refFrac > 0.02 && refFrac < 0.98;
  const refX = refFrac !== null ? VB_W * refFrac : 0;

  const scaleTicks = generateScaleTicks(def.unit, min, max);

  const fillId = prefixDefs(`hb-${def.id}`, 'fill');
  const bloomId = prefixDefs(`hb-${def.id}`, 'bloom');
  const blurId = prefixDefs(`hb-${def.id}`, 'blur');
  const maskId = prefixDefs(`hb-${def.id}`, 'mask');

  const fillW = VB_W * liveFrac;
  const peakX = peakFrac !== null ? VB_W * peakFrac : 0;
  const isOver = !silent && refValue !== undefined && value > refValue;

  // ── styles — track VuColumn so a row of horizontal bars reads as the
  // same family of widget when sat next to a row of vertical columns ──
  const cardStyle: CSSProperties = {
    flex: 1,
    minWidth: 0,
    position: 'relative',
    display: 'flex',
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    padding: '8px 12px',
    background:
      'radial-gradient(80% 60% at 50% 100%, var(--immersive-bloom), transparent 60%),' +
      ' linear-gradient(180deg, var(--immersive-well) 0%, var(--immersive-well-2) 100%)',
    border: '1px solid var(--immersive-line)',
    borderRadius: 7,
    boxShadow:
      'inset 0 1px 0 var(--immersive-rim), inset 0 0 22px rgba(0,0,0,0.40)',
  };
  const labelColStyle: CSSProperties = {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    flex: '0 0 auto',
    minWidth: 36,
  };
  const nameStyle: CSSProperties = {
    fontSize: 9,
    letterSpacing: '0.16em',
    textTransform: 'uppercase',
    color: 'var(--fg-1)',
    fontWeight: 700,
    fontFamily: 'var(--font-sans)',
    whiteSpace: 'nowrap',
  };
  const subStyle: CSSProperties = {
    fontSize: 8.5,
    letterSpacing: '0.14em',
    textTransform: 'uppercase',
    color: 'var(--fg-3)',
    fontWeight: 600,
    fontFamily: 'var(--font-sans)',
    marginTop: 1,
  };
  const barWrapStyle: CSSProperties = {
    position: 'relative',
    flex: '1 1 auto',
    minWidth: 0,
    height: CONTAINER_H,
  };
  const barStyle: CSSProperties = {
    position: 'absolute',
    inset: 0,
    width: '100%',
    height: '100%',
    display: 'block',
  };
  const readoutColStyle: CSSProperties = {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-end',
    flex: '0 0 auto',
  };
  const numStyle: CSSProperties = {
    fontFamily: 'var(--font-mono)',
    fontSize: 11,
    color: isOver ? '#ffb8a4' : 'var(--fg-0)',
    fontWeight: 600,
    fontVariantNumeric: 'tabular-nums',
    textShadow: isOver ? '0 0 10px var(--immersive-tx-glow)' : undefined,
    whiteSpace: 'nowrap',
  };
  const unitStyle: CSSProperties = {
    color: 'var(--fg-3)',
    fontWeight: 500,
    fontSize: 8.5,
    marginLeft: 2,
  };

  // Top of the bar in CSS pixels — labels float just above it. The bar
  // SVG runs the full container height (CONTAINER_H), with the bar
  // itself at viewBox y=BAR_Y..BAR_Y+BAR_H out of VB_H. Convert to a
  // percentage so the label row sits flush with the bar's top edge.
  const labelTopPct = ((BAR_Y - 10) / VB_H) * 100;

  return (
    <div style={cardStyle} aria-hidden="true">
      <div style={labelColStyle}>
        <span style={nameStyle} title={name}>
          {name}
        </span>
        {sub ? <span style={subStyle}>{sub}</span> : null}
      </div>

      <div style={barWrapStyle}>
        {/* Numeric scale labels — HTML so they don't distort with the
            SVG's horizontal stretch. Positioned at the same fractional
            x as the SVG tick lines below, centred via translateX(-50%).
            VuColumn paints these in the SVG margin; doing the same in
            HTML keeps text crisp at any card width. */}
        <div
          style={{
            position: 'absolute',
            top: `${labelTopPct.toFixed(1)}%`,
            left: 0,
            right: 0,
            height: 9,
            pointerEvents: 'none',
            fontFamily: 'var(--font-mono)',
            fontSize: 7.5,
            lineHeight: 1,
          }}
        >
          {scaleTicks.map((t, i) => (
            <span
              key={`label-${i}`}
              style={{
                position: 'absolute',
                left: `${(t.frac * 100).toFixed(2)}%`,
                top: 0,
                transform: 'translateX(-50%)',
                color: t.isReference ? 'var(--immersive-tx)' : 'var(--fg-4)',
                whiteSpace: 'nowrap',
                fontWeight: t.isReference ? 700 : 500,
              }}
            >
              {t.label}
            </span>
          ))}
        </div>

        <svg
          viewBox={`0 0 ${VB_W} ${VB_H}`}
          preserveAspectRatio="none"
          style={barStyle}
          aria-hidden="true"
        >
          <defs>
            {/* Horizontal gradient — same six stops as VuColumn, just
                rotated left → right so a horizontal bar fills with the
                same colour progression a vertical column would as it
                grows from BOT to TOP. */}
            <linearGradient
              id={fillId}
              x1="0"
              y1="0"
              x2={VB_W}
              y2="0"
              gradientUnits="userSpaceOnUse"
            >
              {isSignalGradient ? (
                <>
                  <stop offset="0" stopColor="#FFA028" stopOpacity="0.18" />
                  <stop offset="0.5" stopColor="#FFA028" stopOpacity="0.55" />
                  <stop offset="1" stopColor="#FFA028" stopOpacity="1" />
                </>
              ) : (
                <>
                  <stop offset="0" stopColor="var(--immersive-good)" />
                  <stop offset="0.45" stopColor="var(--immersive-good)" />
                  <stop offset="0.62" stopColor="#7cd1a8" />
                  <stop offset="0.74" stopColor="var(--immersive-warn)" />
                  <stop offset="0.88" stopColor="var(--immersive-tx)" />
                  <stop offset="1" stopColor="var(--immersive-tx)" />
                </>
              )}
            </linearGradient>
            <linearGradient
              id={bloomId}
              x1="0"
              y1="0"
              x2={VB_W}
              y2="0"
              gradientUnits="userSpaceOnUse"
            >
              {isSignalGradient ? (
                <>
                  <stop offset="0" stopColor="#FFA028" stopOpacity="0.4" />
                  <stop offset="1" stopColor="#FFA028" stopOpacity="0.6" />
                </>
              ) : (
                <>
                  <stop offset="0" stopColor="var(--immersive-good)" stopOpacity="0.5" />
                  <stop offset="0.74" stopColor="var(--immersive-warn)" stopOpacity="0.5" />
                  <stop offset="1" stopColor="var(--immersive-tx)" stopOpacity="0.5" />
                </>
              )}
            </linearGradient>
            <BloomFilter id={blurId} region={['-10%', '-50%', '120%', '200%']} />
            <mask id={maskId}>
              <rect x={0} y={BAR_Y} width={VB_W} height={BAR_H} fill="white" />
            </mask>
          </defs>

          {/* scale tick lines — pair above + below the bar at every
              numeric label position. `vectorEffect="non-scaling-stroke"`
              keeps them crisp 1-px lines regardless of how wide the
              card's SVG gets stretched. The 0 dBFS tick is bumped to
              the --immersive-tx red, matching VuColumn's "0" tick. */}
          {scaleTicks.map((t, i) => {
            const x = VB_W * t.frac;
            const stroke = t.isReference
              ? 'var(--immersive-tx)'
              : 'rgba(255,255,255,0.20)';
            const sw = t.isReference ? 1.4 : 1;
            return (
              <g key={`tick-${i}`} stroke={stroke} strokeWidth={sw}>
                <line
                  x1={x.toFixed(2)}
                  y1={BAR_Y - 5}
                  x2={x.toFixed(2)}
                  y2={BAR_Y - 1}
                  vectorEffect="non-scaling-stroke"
                />
                <line
                  x1={x.toFixed(2)}
                  y1={BAR_Y + BAR_H + 1}
                  x2={x.toFixed(2)}
                  y2={BAR_Y + BAR_H + 5}
                  vectorEffect="non-scaling-stroke"
                />
              </g>
            );
          })}

          {/* track background — dark well + light inner stroke, same
              recipe as VuColumn so the bar reads as a recessed channel. */}
          <rect
            x={0}
            y={BAR_Y}
            width={VB_W}
            height={BAR_H}
            rx={2}
            fill="var(--immersive-vu-track)"
            stroke="var(--immersive-rim)"
            strokeWidth={1}
            vectorEffect="non-scaling-stroke"
          />
          {/* inset top shadow — same 2 px deep band as VuColumn. */}
          <rect
            x={0.5}
            y={BAR_Y + 0.5}
            width={VB_W - 1}
            height={2}
            fill="rgba(0,0,0,0.6)"
          />

          {/* bloom-behind-crisp fill — shared kit */}
          {!silent && fillW > 0 && (
            <BloomFill
              shape={{ kind: 'rect', x: 0, y: BAR_Y, width: fillW, height: BAR_H }}
              fill={`url(#${fillId})`}
              bloomFill={`url(#${bloomId})`}
              filterId={blurId}
            />
          )}

          {/* LED segment separators — thin vertical lines through the
              bar. Pinned to 1 screen px via non-scaling-stroke so they
              stay as crisp LED demarcations regardless of bar width —
              the same visual density as VuColumn's horizontal segment
              lines. */}
          <g mask={`url(#${maskId})`}>
            {Array.from({ length: SEG_COUNT }).map((_, i) => {
              const segX = ((i + 1) * VB_W) / (SEG_COUNT + 1);
              return (
                <line
                  key={`hseg-${i}`}
                  x1={segX.toFixed(1)}
                  y1={BAR_Y}
                  x2={segX.toFixed(1)}
                  y2={BAR_Y + BAR_H}
                  stroke="var(--immersive-bg)"
                  strokeWidth={1}
                  vectorEffect="non-scaling-stroke"
                />
              );
            })}
          </g>

          {/* peak-hold tick — white with drop-shadow glow, extending
              2 px past each edge of the bar (same overhang VuColumn
              uses on its peak tick relative to the column edge). */}
          {showPeak && (
            <PeakTick
              x1={peakX.toFixed(2)}
              y1={BAR_Y - 2}
              x2={peakX.toFixed(2)}
              y2={BAR_Y + BAR_H + 2}
              nonScaling
            />
          )}

          {/* dashed reference at 0 dBFS (or dangerAt for non-dBFS
              units) — the horizontal equivalent of VuColumn's dashed
              0 dBFS line. Same dash recipe (--immersive-tx red, 2 2
              dash, 0.55 opacity). */}
          {showRef && (
            <line
              x1={refX.toFixed(2)}
              y1={BAR_Y - 2}
              x2={refX.toFixed(2)}
              y2={BAR_Y + BAR_H + 2}
              stroke="var(--immersive-tx)"
              strokeWidth={1}
              strokeDasharray="2 2"
              opacity={0.55}
              vectorEffect="non-scaling-stroke"
            />
          )}

          {/* zone-transition ticks — short coloured vertical lines
              below the bar. Mirrors VuColumn's right-side zone ticks
              (same colour recipe, same idle-visible "sweet spot"
              cue). */}
          {zoneTicks && zoneTicks.length > 0 && (
            <g strokeLinecap="round">
              {zoneTicks.map((zt, i) => {
                const x = VB_W * zt.frac;
                return (
                  <line
                    key={`hzt-${i}`}
                    x1={x.toFixed(2)}
                    y1={BAR_Y + BAR_H + 6}
                    x2={x.toFixed(2)}
                    y2={BAR_Y + BAR_H + 11}
                    stroke={immersiveZoneTickColor(zt.level)}
                    strokeWidth={2}
                    vectorEffect="non-scaling-stroke"
                  />
                );
              })}
            </g>
          )}
        </svg>
      </div>

      <div style={readoutColStyle}>
        <span style={numStyle}>
          {formatReadout(def, value)}
          <span style={unitStyle}>{def.unit}</span>
        </span>
      </div>
    </div>
  );
}
