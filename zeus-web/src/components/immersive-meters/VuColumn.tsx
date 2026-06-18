// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Vertical VU column — the full five-layer premium instrument stack (the same
// recipe the loved S-meter uses), scaled UP for the small/dense TX-stage rows:
//   (1) recessed machined well behind the track,
//   (2) volumetric bloom-behind-crisp fill under a vertical cylinder gradient,
//   (3) a bright glowing leading-edge sliver at the live fill tip,
//   (4) a specular GlassDome (wet-glass + drifting sheen),
//   (5) a machined GaugeBezel chrome ring framing each column.
// The live fill height glides at up to 60 Hz on the shared draw-bus
// (useGlidedFraction); the peak-hold tick latches INSTANTLY and is never
// glided. Used for the six signal-chain readings (MIC PK/AVG, LEV PK/AVG,
// ALC PK/AVG). Position math (dbToFrac) and the peak-hold hook are byte-
// identical to the prior implementation — only HOW the value is drawn changed.

import { useRef, type CSSProperties } from 'react';
import { dbToFrac, fmtDb, isSilent } from './dbScale';
import { usePeakHoldFrac } from './usePeakHold';
import { immersiveZoneTickColor, type ZoneTick } from '../meters/meterCatalog';
import { BloomFilter, PeakTick, prefixDefs } from '../meters/render/svgChrome';
import { GaugeBezel } from '../meters/render/GaugeBezel';
import { GlassDome } from '../meters/render/GlassDome';
import { recessedWell } from '../meters/render/recessedWell';
import { useGlidedFraction } from '../meters/render/useGlidedDraw';
import { lampCardStyle } from '../meters/render/lampCard';

interface VuColumnProps {
  /** Live value in dBFS. */
  valueDb: number;
  /** Top label (e.g. "MIC"). */
  name: string;
  /** Sub-label (e.g. "PK"). */
  sub: string;
  /** Stable id prefix for SVG `<defs>` — required to avoid collisions. */
  defsId: string;
  /** Average partner of a PK column — drawn slightly dimmer/desaturated so the
   *  eye reads peak-vs-average across a stage pair instead of six clones. */
  variant?: 'pk' | 'avg';
  /** Host element ref for the draw-bus off-screen gate (the panel body). */
  hostRef?: React.RefObject<Element | null>;
  /** Optional green/amber/red tick marks at zone-level boundaries.
   *  Rendered as short horizontal lines on the right-hand side of the
   *  column (x=48..54). `frac` is the linear position 0..1 along the
   *  column (BOT → TOP); callers using a non-linear axis (e.g. dBFS via
   *  `dbToFrac`) must remap before passing in. The immersive TX Stage
   *  Meters panel passes none — its colouring already comes from the
   *  fill gradient + dashed 0 dBFS reference line. */
  zoneTicks?: ReadonlyArray<ZoneTick>;
}

const TOP_Y = 12;
const BOT_Y = 148;
const COL_HEIGHT = BOT_Y - TOP_Y;
const COL_X = 22;
const COL_W = 16;
const EDGE_H = 3; // bright leading-edge sliver height (viewBox units)
const SIDE_TICKS = [0, -3, -6, -10, -20, -40, -60] as const;
const SEG_COUNT = 17;

export function VuColumn({ valueDb, name, sub, defsId, variant = 'pk', hostRef, zoneTicks }: VuColumnProps) {
  const silent = isSilent(valueDb);
  const liveFrac = silent ? 0 : dbToFrac(valueDb);
  const peakFrac = usePeakHoldFrac(valueDb, dbToFrac);

  const peakY = BOT_Y - COL_HEIGHT * peakFrac;
  const zeroY = BOT_Y - COL_HEIGHT * dbToFrac(0);

  const isAvg = variant === 'avg';

  // 60 Hz liquid glide for the live fill height. We glide `liveFrac` on the
  // shared draw-bus and write the fill rect's y/height + the leading-edge
  // sliver position imperatively each frame — NO setState in the hot path.
  // The peak tick (peakFrac) is NEVER glided; it stays instant.
  const fillCrispRef = useRef<SVGRectElement | null>(null);
  const fillBloomRef = useRef<SVGRectElement | null>(null);
  const fillVolRef = useRef<SVGRectElement | null>(null);
  const edgeRef = useRef<SVGRectElement | null>(null);
  const edgeGlowRef = useRef<SVGRectElement | null>(null);

  const writeFill = (drawn: number) => {
    const f = Math.max(0, Math.min(1, drawn));
    const h = COL_HEIGHT * f;
    const y = BOT_Y - h;
    const hs = h.toFixed(1);
    const ys = y.toFixed(1);
    fillCrispRef.current?.setAttribute('y', ys);
    fillCrispRef.current?.setAttribute('height', hs);
    fillBloomRef.current?.setAttribute('y', ys);
    fillBloomRef.current?.setAttribute('height', hs);
    fillVolRef.current?.setAttribute('y', ys);
    fillVolRef.current?.setAttribute('height', hs);
    // Leading-edge sliver rides the fill top; hide it when the bar is empty.
    const edgeY = Math.max(TOP_Y, y - EDGE_H * 0.5);
    const visible = f > 0.001;
    edgeRef.current?.setAttribute('y', edgeY.toFixed(1));
    edgeRef.current?.setAttribute('opacity', visible ? '0.95' : '0');
    edgeGlowRef.current?.setAttribute('y', edgeY.toFixed(1));
    edgeGlowRef.current?.setAttribute('opacity', visible ? '0.9' : '0');
  };

  const glide = useGlidedFraction(liveFrac, { hostRef, onDraw: writeFill });
  // Initial (pre-first-frame) geometry from the current drawn value so SSR /
  // first paint is correct without waiting for the bus.
  const drawn0 = Math.max(0, Math.min(1, glide.read()));
  const fillH0 = COL_HEIGHT * drawn0;
  const fillY0 = BOT_Y - fillH0;
  const edgeY0 = Math.max(TOP_Y, fillY0 - EDGE_H * 0.5);
  const edgeVisible0 = drawn0 > 0.001;

  const fillGradId = prefixDefs(defsId, 'fill');
  const bloomGradId = prefixDefs(defsId, 'bloom');
  const volGradId = prefixDefs(defsId, 'vol');
  const blurFilterId = prefixDefs(defsId, 'blur');
  const maskId = prefixDefs(defsId, 'mask');

  const isOver = !silent && valueDb > 0;

  // Recessed machined pocket behind the track — same warm-halo well the
  // S-meter uses, applied as the SVG track backdrop via a foreignObject-free
  // approach: we paint the concave floor + inset via a CSS box on the card,
  // but for the SVG track itself we draw a dark recessed rect + inset edges.
  // The card keeps the warm lamp wash so the cluster reads as one instrument.
  const cardStyle: CSSProperties = {
    ...lampCardStyle('column'),
    position: 'relative',
    padding: '10px 4px 10px',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: 4,
    minWidth: 0,
  };
  const nameStyle: CSSProperties = {
    fontSize: 9,
    letterSpacing: '0.16em',
    textTransform: 'uppercase',
    color: 'var(--immersive-lamp-readout)',
    fontWeight: 700,
  };
  const subStyle: CSSProperties = {
    fontSize: 8.5,
    letterSpacing: '0.14em',
    textTransform: 'uppercase',
    color: isAvg ? 'var(--immersive-lamp-corner)' : 'var(--immersive-lamp-label)',
    fontWeight: isAvg ? 500 : 700,
  };
  const numStyle: CSSProperties = {
    fontFamily: 'var(--font-mono)',
    fontSize: 11,
    color: isOver ? '#ffb8a4' : 'var(--immersive-lamp-readout)',
    fontWeight: 600,
    fontVariantNumeric: 'tabular-nums',
    marginTop: 2,
    textShadow: isOver
      ? '0 0 10px var(--immersive-tx-glow)'
      : '0 0 8px var(--immersive-lamp-readout-glow)',
  };
  const numUnitStyle: CSSProperties = {
    color: 'var(--immersive-lamp-corner-em)',
    fontWeight: 500,
    fontSize: 8.5,
    marginLeft: 2,
  };

  // The well CSS box sits behind the SVG, giving the column real concave
  // machined depth (inset shadow stack + warm halo) instead of a flat rect.
  // It is positioned to overlap exactly the SVG track region (COL_X..COL_X+W,
  // TOP_Y..BOT_Y) within the 60×160 viewBox stretched to the SVG element box.
  const wellBoxStyle: CSSProperties = {
    ...recessedWell({ radius: 2, warmHalo: true, glow: true }),
    position: 'absolute',
    // map viewBox units → % of the 60-wide / 160-tall SVG box
    left: `${(COL_X / 60) * 100}%`,
    width: `${(COL_W / 60) * 100}%`,
    top: `${(TOP_Y / 160) * 100}%`,
    height: `${(COL_HEIGHT / 160) * 100}%`,
    pointerEvents: 'none',
  };

  // Average columns read slightly softer so the eye groups each stage's
  // PK (bright) + AVG (dim) pair into one peak-vs-average instrument.
  const fillOpacity = isAvg ? 0.82 : 1;
  const bloomOpacity = isAvg ? 0.55 : 0.85;

  return (
    <div style={cardStyle} aria-hidden="true">
      {name ? <div style={nameStyle}>{name}</div> : null}
      <div style={subStyle}>{sub}</div>
      <div style={{ position: 'relative', width: '100%', height: 160 }}>
        {/* recessed machined well behind the SVG track */}
        <div style={wellBoxStyle} />
        <svg
          viewBox="0 0 60 160"
          preserveAspectRatio="none"
          style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', display: 'block' }}
        >
          <defs>
            <linearGradient id={fillGradId} x1="0" y1="148" x2="0" y2="12" gradientUnits="userSpaceOnUse">
              <stop offset="0" stopColor="var(--immersive-good)" />
              <stop offset="0.45" stopColor="var(--immersive-good)" />
              <stop offset="0.62" stopColor="#7cd1a8" />
              <stop offset="0.74" stopColor="var(--immersive-warn)" />
              <stop offset="0.88" stopColor="var(--immersive-tx)" />
              <stop offset="1" stopColor="var(--immersive-tx)" />
            </linearGradient>
            <linearGradient id={bloomGradId} x1="0" y1="148" x2="0" y2="12" gradientUnits="userSpaceOnUse">
              <stop offset="0" stopColor="var(--immersive-good)" stopOpacity="0.55" />
              <stop offset="0.74" stopColor="var(--immersive-warn)" stopOpacity="0.55" />
              <stop offset="1" stopColor="var(--immersive-tx)" stopOpacity="0.55" />
            </linearGradient>
            {/* horizontal cylinder shading across the column width + a soft
                white-screen lift at the floor so a quiet bar still glows. */}
            <linearGradient id={volGradId} x1={COL_X} y1="0" x2={COL_X + COL_W} y2="0" gradientUnits="userSpaceOnUse">
              <stop offset="0" stopColor="var(--meter-fill-base)" />
              <stop offset="0.32" stopColor="var(--vu-floor-lift)" />
              <stop offset="0.5" stopColor="var(--meter-fill-hot)" />
              <stop offset="0.68" stopColor="var(--vu-floor-lift)" />
              <stop offset="1" stopColor="var(--meter-fill-base)" />
            </linearGradient>
            {/* taller vertical bloom region so the halo actually shows on the
                thin column (was 120% → now 160% with extra side bleed). */}
            <BloomFilter id={blurFilterId} region={['-60%', '-20%', '220%', '160%']} />
            <mask id={maskId}>
              <rect x={COL_X} y={TOP_Y} width={COL_W} height={COL_HEIGHT} fill="white" />
            </mask>
          </defs>

          {/* side ticks + numeric labels — warm-cream lamp tone, with the
              0 dBFS ticks staying in tx-red as the "rail" cue. */}
          <g
            strokeWidth={0.6}
            fontFamily="var(--font-mono)"
            fontSize={6}
            textAnchor="end"
            vectorEffect="non-scaling-stroke"
          >
            {SIDE_TICKS.map((db) => {
              const f = dbToFrac(db);
              const y = BOT_Y - COL_HEIGHT * f;
              const zero = db === 0;
              const tickStroke = zero ? 'var(--immersive-tx)' : 'var(--immersive-lamp-tick)';
              const tickWidth = zero ? 1 : 0.6;
              return (
                <g key={`vt-${db}`}>
                  <line x1={14} y1={y} x2={COL_X} y2={y} stroke={tickStroke} strokeWidth={tickWidth} vectorEffect="non-scaling-stroke" />
                  <line x1={COL_X + COL_W} y1={y} x2={COL_X + COL_W + 8} y2={y} stroke={tickStroke} strokeWidth={tickWidth} vectorEffect="non-scaling-stroke" />
                  <text x={13} y={y + 2} fill={zero ? 'var(--immersive-tx)' : 'var(--immersive-lamp-label)'}>
                    {zero ? '0' : Math.abs(db)}
                  </text>
                </g>
              );
            })}
          </g>

          {/* translucent inner stage over the recessed well — darkens the
              pocket for the fill to glow against while letting the CSS well's
              concave floor + machined inset edges read through. */}
          <rect
            x={COL_X}
            y={TOP_Y}
            width={COL_W}
            height={COL_HEIGHT}
            rx={2}
            fill="var(--immersive-vu-track)"
            fillOpacity={0.55}
          />

          {/* fill stack (bloom-behind-crisp + cylinder volume), masked to the
              column so the bloom halo doesn't bleed past the well. */}
          <g mask={`url(#${maskId})`}>
            <rect
              ref={fillBloomRef}
              x={COL_X}
              y={fillY0.toFixed(1)}
              width={COL_W}
              height={fillH0.toFixed(1)}
              fill={`url(#${bloomGradId})`}
              filter={`url(#${blurFilterId})`}
              opacity={bloomOpacity}
            />
            <rect
              ref={fillCrispRef}
              x={COL_X}
              y={fillY0.toFixed(1)}
              width={COL_W}
              height={fillH0.toFixed(1)}
              fill={`url(#${fillGradId})`}
              opacity={fillOpacity}
            />
            <rect
              ref={fillVolRef}
              x={COL_X}
              y={fillY0.toFixed(1)}
              width={COL_W}
              height={fillH0.toFixed(1)}
              fill={`url(#${volGradId})`}
            />
          </g>

          {/* bright glowing leading-edge sliver at the live fill TOP — the tip
              glows as the bar rises (mirrors the S-meter's leading edge). A
              soft wide glow under a crisp bright cap. */}
          <g mask={`url(#${maskId})`}>
            <rect
              ref={edgeGlowRef}
              x={COL_X}
              y={edgeY0.toFixed(1)}
              width={COL_W}
              height={EDGE_H * 2}
              fill={isOver ? 'var(--vu-edge-hot)' : 'var(--meter-fill-glow)'}
              opacity={edgeVisible0 ? 0.9 : 0}
              style={{ filter: 'blur(2px)' }}
            />
            <rect
              ref={edgeRef}
              x={COL_X}
              y={edgeY0.toFixed(1)}
              width={COL_W}
              height={EDGE_H}
              fill={isOver ? 'var(--vu-edge-hot)' : 'var(--vu-edge-warn)'}
              opacity={edgeVisible0 ? 0.95 : 0}
              style={{ filter: `drop-shadow(0 0 3px ${isOver ? 'var(--vu-edge-hot)' : 'var(--meter-fill-glow)'})` }}
            />
          </g>

          {/* LED segment lines — thin dark gaps to read as a stacked ladder. */}
          <g mask={`url(#${maskId})`}>
            {Array.from({ length: SEG_COUNT }).map((_, i) => {
              const segY = TOP_Y + ((i + 1) * COL_HEIGHT) / 18;
              return (
                <line
                  key={`seg-${i}`}
                  x1={COL_X}
                  y1={segY.toFixed(1)}
                  x2={COL_X + COL_W}
                  y2={segY.toFixed(1)}
                  stroke="var(--immersive-bg)"
                  strokeWidth={1.2}
                />
              );
            })}
          </g>

          {/* specular glass dome + drifting sheen — brighter *-sm tokens so the
              wet-glass tell survives at the thin column size. */}
          <GlassDome
            defsId={defsId}
            x={COL_X}
            y={TOP_Y}
            width={COL_W}
            height={COL_HEIGHT}
            rx={2}
            domeColor="var(--meter-glass-dome-sm)"
            sheenColor="var(--meter-sheen-sm)"
          />

          {/* peak-hold tick — instant white double-bloom tick that pops like
              the S-meter's, latched directly from the raw peak (never glided). */}
          {!silent && peakFrac > 0 && (
            <PeakTick
              x1={COL_X - 1}
              y1={peakY.toFixed(1)}
              x2={COL_X + COL_W + 1}
              y2={peakY.toFixed(1)}
              stroke="#fff"
              glow="var(--meter-fill-glow)"
              strokeWidth={2.2}
              opacity={0.95}
              nonScaling
            />
          )}

          {/* dashed 0 dBFS reference */}
          <line
            x1={COL_X}
            y1={zeroY.toFixed(1)}
            x2={COL_X + COL_W}
            y2={zeroY.toFixed(1)}
            stroke="var(--immersive-tx)"
            strokeWidth={0.8}
            strokeDasharray="2 2"
            opacity={0.55}
            vectorEffect="non-scaling-stroke"
          />

          {/* machined chrome bezel ring framing the column — brighter *-sm
              highlight so the chrome reads at small bar size. LAST so it
              frames the fill + glass. */}
          <GaugeBezel
            variant="rect"
            defsId={defsId}
            x={COL_X}
            y={TOP_Y}
            width={COL_W}
            height={COL_HEIGHT}
            rx={2}
            thickness={2}
            hiColor="var(--meter-bezel-hi-sm)"
            nonScaling
          />

          {/* zone-transition ticks — short coloured horizontal lines on the
              right of the column, away from the side-tick numeric labels. */}
          {zoneTicks && zoneTicks.length > 0 && (
            <g strokeLinecap="round">
              {zoneTicks.map((zt, i) => {
                const y = BOT_Y - COL_HEIGHT * zt.frac;
                return (
                  <line
                    key={`zt-${i}`}
                    x1={48}
                    y1={y.toFixed(1)}
                    x2={54}
                    y2={y.toFixed(1)}
                    stroke={immersiveZoneTickColor(zt.level)}
                    strokeWidth={1.6}
                    vectorEffect="non-scaling-stroke"
                  />
                );
              })}
            </g>
          )}
        </svg>
      </div>
      <div style={numStyle}>
        {silent ? '−∞' : fmtDb(valueDb)}
        <span style={numUnitStyle}>dB</span>
      </div>
    </div>
  );
}
