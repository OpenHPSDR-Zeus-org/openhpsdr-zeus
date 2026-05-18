// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

import { forwardRef, useRef, useState, type PointerEvent } from 'react';
import { useRotatorStore } from '../../state/rotator-store';

function normalizeAz(deg: number | null | undefined): number | null {
  if (deg == null || !Number.isFinite(deg)) return null;
  return ((deg % 360) + 360) % 360;
}

function fmtAz(deg: number | null): string {
  if (deg == null) return '---°';
  return `${deg.toFixed(0).padStart(3, '0')}°`;
}

export function RotatorDialPanel() {
  const rotConnected = useRotatorStore((s) => !!s.status?.connected);
  const rotEnabled = useRotatorStore((s) => s.config.enabled);
  const rotMoving = useRotatorStore((s) => !!s.status?.moving);
  const rotCurrentAz = useRotatorStore((s) => normalizeAz(s.status?.currentAz));
  const rotTargetAz = useRotatorStore((s) => normalizeAz(s.status?.targetAz));
  const setAzimuth = useRotatorStore((s) => s.setAzimuth);
  const stopRotator = useRotatorStore((s) => s.stop);

  const rotReady = rotEnabled && rotConnected;
  const dialRef = useRef<SVGSVGElement | null>(null);
  const [manualInput, setManualInput] = useState('');

  const manualHeadingDeg = (() => {
    const n = Number.parseFloat(manualInput);
    if (!Number.isFinite(n)) return null;
    return ((n % 360) + 360) % 360;
  })();
  const manualHeadingValid = manualHeadingDeg != null && manualInput.trim() !== '';

  const submitManualHeading = () => {
    if (!rotReady || manualHeadingDeg == null) return;
    void setAzimuth(Math.round(manualHeadingDeg));
  };

  const handleDialPointerDown = (e: PointerEvent<SVGSVGElement>) => {
    if (!rotReady) return;
    const svg = dialRef.current;
    if (!svg) return;
    const rect = svg.getBoundingClientRect();
    const cx = rect.left + rect.width / 2;
    const cy = rect.top + rect.height / 2;
    const dx = e.clientX - cx;
    const dy = e.clientY - cy;
    // Reject clicks too close to the hub (avoid accidental commands).
    const dist = Math.hypot(dx, dy);
    if (dist < Math.min(rect.width, rect.height) * 0.12) return;
    // N = 0° at top, clockwise. atan2(dx, -dy) yields that convention.
    const rad = Math.atan2(dx, -dy);
    let deg = (rad * 180) / Math.PI;
    if (deg < 0) deg += 360;
    void setAzimuth(Math.round(deg));
  };

  const statusLabel = !rotEnabled
    ? 'Rotator disabled'
    : !rotConnected
      ? 'Connecting…'
      : null;

  return (
    <div className={`rotator-dial${rotReady ? '' : ' disabled'}`}>
      <div className="rd-stage">
        <CompassDial
          ref={dialRef}
          currentAz={rotCurrentAz}
          targetAz={rotMoving ? rotTargetAz : null}
          interactive={rotReady}
          onPointerDown={handleDialPointerDown}
        />
        <div className="rd-readout">
          <span className="rd-readout-label">HDG</span>
          <span className="rd-readout-now">{fmtAz(rotCurrentAz)}</span>
          {rotMoving && rotTargetAz != null && (
            <span className="rd-readout-tgt">→ {fmtAz(rotTargetAz)}</span>
          )}
        </div>
        {statusLabel && (
          <div className="rd-offline-banner" role="status">{statusLabel}</div>
        )}
      </div>

      <div className="rd-controls">
        <form
          className="rd-manual"
          onSubmit={(e) => {
            e.preventDefault();
            submitManualHeading();
          }}
        >
          <input
            type="number"
            inputMode="numeric"
            min={0}
            max={359}
            step={1}
            className="rd-input"
            placeholder="HDG"
            value={manualInput}
            onChange={(e) => setManualInput(e.currentTarget.value)}
            aria-label="Heading in degrees"
            disabled={!rotReady}
          />
          <button
            type="submit"
            className="rd-btn rd-btn--go"
            disabled={!rotReady || !manualHeadingValid}
            title={rotReady ? 'Rotate to entered heading' : 'Rotator not connected'}
          >
            GO
          </button>
        </form>
        <button
          type="button"
          className="rd-btn rd-btn--stop"
          onClick={() => { void stopRotator(); }}
          disabled={!rotReady || !rotMoving}
          title={rotMoving ? 'Stop rotator' : 'Rotator is idle'}
        >
          STOP
        </button>
      </div>
    </div>
  );
}

// ============================================================================
// CompassDial — SVG, 200×200 viewBox, scales to fit container.
// ============================================================================

type CompassDialProps = {
  currentAz: number | null;
  targetAz: number | null;
  interactive: boolean;
  onPointerDown?: (e: PointerEvent<SVGSVGElement>) => void;
};

const C = 100;
const NEEDLE_TRANSITION = 'transform 320ms cubic-bezier(.22,.61,.36,1)';

const CompassDial = forwardRef<SVGSVGElement, CompassDialProps>(function CompassDial(
  { currentAz, targetAz, interactive, onPointerDown },
  ref,
) {
  // Ticks every 5°; major at 30°; cardinal at 90°.
  const ticks: Array<{ deg: number; tier: 'cardinal' | 'major' | 'minor' }> = [];
  for (let d = 0; d < 360; d += 5) {
    ticks.push({
      deg: d,
      tier: d % 90 === 0 ? 'cardinal' : d % 30 === 0 ? 'major' : 'minor',
    });
  }

  const labels: ReadonlyArray<{ deg: number; text: string; size: 'lg' | 'sm'; cls?: string }> = [
    { deg: 0, text: 'N', size: 'lg', cls: 'rd-cardinal-n' },
    { deg: 45, text: 'NE', size: 'sm' },
    { deg: 90, text: 'E', size: 'lg' },
    { deg: 135, text: 'SE', size: 'sm' },
    { deg: 180, text: 'S', size: 'lg' },
    { deg: 225, text: 'SW', size: 'sm' },
    { deg: 270, text: 'W', size: 'lg' },
    { deg: 315, text: 'NW', size: 'sm' },
  ];

  // Inner degree numerals at every 30° (skip cardinals — those have letters).
  const numerals: number[] = [];
  for (let d = 0; d < 360; d += 30) {
    if (d % 90 !== 0) numerals.push(d);
  }

  function polar(deg: number, r: number): [number, number] {
    const rad = (deg * Math.PI) / 180;
    return [C + r * Math.sin(rad), C - r * Math.cos(rad)];
  }

  const showNeedle = currentAz != null;
  const showTarget = targetAz != null && Math.abs((targetAz ?? 0) - (currentAz ?? targetAz ?? 0)) > 0.5;

  return (
    <svg
      ref={ref}
      className="rd-dial"
      viewBox="0 0 200 200"
      preserveAspectRatio="xMidYMid meet"
      onPointerDown={onPointerDown}
      role="img"
      aria-label="Rotator compass dial"
    >
      <defs>
        <radialGradient id="rd-face" cx="50%" cy="50%" r="55%">
          <stop offset="0%" stopColor="var(--immersive-lamp-well-top)" />
          <stop offset="100%" stopColor="var(--immersive-lamp-well-bot)" />
        </radialGradient>
        <linearGradient id="rd-bezel" x1="0%" y1="0%" x2="0%" y2="100%">
          <stop offset="0%" stopColor="var(--immersive-rim-strong)" />
          <stop offset="48%" stopColor="var(--immersive-rim)" />
          <stop offset="100%" stopColor="rgba(0,0,0,0.55)" />
        </linearGradient>
        <radialGradient id="rd-bloom" cx="50%" cy="50%" r="50%">
          <stop offset="0%" stopColor="var(--immersive-lamp-bloom-1)" />
          <stop offset="55%" stopColor="var(--immersive-lamp-bloom-3)" />
          <stop offset="100%" stopColor="rgba(0,0,0,0)" />
        </radialGradient>
        <filter id="rd-needle-glow" x="-30%" y="-30%" width="160%" height="160%">
          <feGaussianBlur stdDeviation="1.2" result="b" />
          <feMerge>
            <feMergeNode in="b" />
            <feMergeNode in="SourceGraphic" />
          </feMerge>
        </filter>
        <filter id="rd-target-glow" x="-30%" y="-30%" width="160%" height="160%">
          <feGaussianBlur stdDeviation="1.6" />
        </filter>
      </defs>

      {/* outer chassis disc — gives the bezel its dark seat */}
      <circle cx={C} cy={C} r={98} fill="var(--bg-1)" />
      {/* brushed bezel ring */}
      <circle cx={C} cy={C} r={96} fill="url(#rd-bezel)" stroke="var(--line-strong)" strokeWidth="1" />
      <circle cx={C} cy={C} r={89} fill="none" stroke="var(--immersive-lamp-rim)" strokeWidth="0.6" />

      {/* face */}
      <circle cx={C} cy={C} r={87} fill="url(#rd-face)" stroke="var(--immersive-lamp-border)" strokeWidth="0.6" />
      {/* warm bloom in well — fades to transparent in light theme via tokens */}
      <circle cx={C} cy={C} r={82} fill="url(#rd-bloom)" pointerEvents="none" />
      {/* inset shadow rim — subtle */}
      <circle cx={C} cy={C} r={87} fill="none" stroke="var(--immersive-arc-track-shadow)" strokeWidth="1.4" opacity="0.55" pointerEvents="none" />

      {/* tick marks */}
      <g className="rd-ticks">
        {ticks.map(({ deg, tier }) => {
          const rOut = 80;
          const rIn = tier === 'cardinal' ? 66 : tier === 'major' ? 70 : 75;
          const [x1, y1] = polar(deg, rOut);
          const [x2, y2] = polar(deg, rIn);
          return (
            <line
              key={`t-${deg}`}
              x1={x1} y1={y1} x2={x2} y2={y2}
              className={`rd-tick rd-tick--${tier}`}
            />
          );
        })}
      </g>

      {/* inner degree numerals (small) */}
      <g className="rd-numerals">
        {numerals.map((deg) => {
          const [x, y] = polar(deg, 56);
          return (
            <text
              key={`n-${deg}`}
              x={x} y={y}
              textAnchor="middle"
              dominantBaseline="central"
              className="rd-numeral"
              transform={`rotate(${deg} ${x} ${y})`}
            >
              {deg.toString().padStart(3, '0')}
            </text>
          );
        })}
      </g>

      {/* cardinal + intercardinal labels */}
      <g className="rd-labels">
        {labels.map(({ deg, text, size, cls }) => {
          const r = size === 'lg' ? 41 : 44;
          const [x, y] = polar(deg, r);
          return (
            <text
              key={`l-${text}`}
              x={x} y={y}
              textAnchor="middle"
              dominantBaseline="central"
              className={`rd-label rd-label--${size}${cls ? ` ${cls}` : ''}`}
            >
              {text}
            </text>
          );
        })}
      </g>

      {/* fixed lubber-line at top — the "you are pointing here" reference */}
      <g className="rd-lubber" pointerEvents="none">
        <polygon points={`${C - 3.2},6 ${C + 3.2},6 ${C},12`} />
      </g>

      {/* target heading marker (pending rotation) */}
      {showTarget && targetAz != null && (
        <g
          className="rd-target"
          transform={`rotate(${targetAz} ${C} ${C})`}
          style={{ transition: NEEDLE_TRANSITION }}
          pointerEvents="none"
        >
          <line x1={C} y1={20} x2={C} y2={32} className="rd-target-line" />
          <polygon points={`${C - 4},20 ${C + 4},20 ${C},14`} className="rd-target-tip" />
        </g>
      )}

      {/* needle — drawn pointing north, rotated to currentAz */}
      {showNeedle && currentAz != null && (
        <g
          className="rd-needle"
          transform={`rotate(${currentAz} ${C} ${C})`}
          style={{ transition: NEEDLE_TRANSITION }}
          filter="url(#rd-needle-glow)"
          pointerEvents="none"
        >
          {/* tail (back half, subdued) */}
          <polygon
            points={`${C - 2.6},${C} ${C + 2.6},${C} ${C},${C + 38}`}
            className="rd-needle-tail"
          />
          {/* head (front half, bright) */}
          <polygon
            points={`${C - 3.2},${C} ${C + 3.2},${C} ${C},${C - 64}`}
            className="rd-needle-head"
          />
        </g>
      )}

      {/* hub cap */}
      <circle cx={C} cy={C} r={7} className="rd-hub-base" pointerEvents="none" />
      <circle cx={C} cy={C} r={4.2} className="rd-hub-ring" pointerEvents="none" />
      <circle cx={C} cy={C} r={2.2} className="rd-hub-pin" pointerEvents="none" />

      {/* invisible hit-target — makes the whole face clickable while
          the visible artwork is non-interactive */}
      {interactive && (
        <circle
          cx={C} cy={C} r={87}
          fill="rgba(0,0,0,0)"
          style={{ cursor: 'crosshair' }}
        />
      )}
    </svg>
  );
});
