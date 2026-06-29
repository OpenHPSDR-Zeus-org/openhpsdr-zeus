// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
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

import { useEffect, useRef, useState } from 'react';
import type { Contact } from './data';

type AzimuthMapProps = {
  myGrid?: string;
  target?: Contact | null;
};

const RINGS_KM = [2500, 5000, 10000, 15000, 20000];
const MAX_KM = 20000;
const BEARING_LABELS = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'];
const MIN_MAP_PX = 80;

export function AzimuthMap({ myGrid = 'EM48', target }: AzimuthMapProps) {
  const mapAreaRef = useRef<HTMLDivElement>(null);
  const [mapSize, setMapSize] = useState(0);

  useEffect(() => {
    const el = mapAreaRef.current;
    if (!el) return;
    const ro = new ResizeObserver((entries) => {
      const entry = entries[0];
      if (!entry) return;
      const { width, height } = entry.contentRect;
      setMapSize(Math.floor(Math.min(width, height)));
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const showMap = mapSize >= MIN_MAP_PX;
  const size = 280;
  const cx = size / 2;
  const cy = size / 2;
  const maxR = size / 2 - 10;

  return (
    <div className="az-map">
      <div className="az-map-area" ref={mapAreaRef}>
        {showMap && (
          <svg
            viewBox={`0 0 ${size} ${size}`}
            width={mapSize}
            height={mapSize}
            preserveAspectRatio="xMidYMid meet"
          >
        <defs>
          <radialGradient id="az-grad" cx="50%" cy="50%" r="50%">
            <stop offset="0%" stopColor="var(--bg-2)" stopOpacity="1" />
            <stop offset="100%" stopColor="var(--bg-1)" stopOpacity="1" />
          </radialGradient>
        </defs>
        <circle cx={cx} cy={cy} r={maxR} fill="url(#az-grad)" stroke="var(--panel-border)" strokeWidth="1" />
        {RINGS_KM.map((r, i) => (
          <circle
            key={i}
            cx={cx}
            cy={cy}
            r={(r / MAX_KM) * maxR}
            fill="none"
            stroke="rgba(255,255,255,0.08)"
            strokeDasharray={i === RINGS_KM.length - 1 ? '0' : '2 3'}
            strokeWidth="0.6"
          />
        ))}
        {Array.from({ length: 12 }, (_, i) => {
          const a = (i / 12) * Math.PI * 2 - Math.PI / 2;
          return (
            <line
              key={i}
              x1={cx}
              y1={cy}
              x2={cx + Math.cos(a) * maxR}
              y2={cy + Math.sin(a) * maxR}
              stroke="rgba(255,255,255,0.05)"
              strokeWidth="0.5"
            />
          );
        })}
        {BEARING_LABELS.map((b, i) => {
          const a = (i / 8) * Math.PI * 2 - Math.PI / 2;
          return (
            <text
              key={b}
              x={cx + Math.cos(a) * maxR * 0.92}
              y={cy + Math.sin(a) * maxR * 0.92}
              fill="var(--fg-2)"
              fontSize="8"
              fontFamily="var(--font-mono)"
              textAnchor="middle"
              dominantBaseline="middle"
              letterSpacing="0.1em"
            >
              {b}
            </text>
          );
        })}
        <circle cx={cx} cy={cy} r={3} fill="var(--accent)" />
        <circle cx={cx} cy={cy} r={8} fill="none" stroke="var(--accent)" strokeWidth="1" opacity={0.4} />
        {target && (() => {
          const bearing = ((target.bearing - 90) * Math.PI) / 180;
          const r = (Math.min(target.distance, MAX_KM) / MAX_KM) * maxR;
          const tx = cx + Math.cos(bearing) * r;
          const ty = cy + Math.sin(bearing) * r;
          return (
            <g key={target.callsign}>
              <line x1={cx} y1={cy} x2={tx} y2={ty} stroke="var(--tx)" strokeWidth="1.2" strokeDasharray="3 2">
                <animate attributeName="stroke-dashoffset" from="0" to="10" dur="1s" repeatCount="indefinite" />
              </line>
              <circle cx={tx} cy={ty} r={4} fill="var(--tx)" />
              <circle cx={tx} cy={ty} r={9} fill="none" stroke="var(--tx)" strokeWidth="1" opacity={0.5}>
                <animate attributeName="r" from="4" to="14" dur="1.4s" repeatCount="indefinite" />
                <animate attributeName="opacity" from="0.6" to="0" dur="1.4s" repeatCount="indefinite" />
              </circle>
              <text
                x={tx}
                y={ty - 10}
                fill="var(--fg-0)"
                fontSize="9"
                fontFamily="var(--font-mono)"
                textAnchor="middle"
                fontWeight={600}
              >
                {target.callsign}
              </text>
            </g>
          );
        })()}
          </svg>
        )}
      </div>
      <div className="az-stats">
        <div>
          <span className="label-xs">Home</span>
          <span className="mono">{myGrid}</span>
        </div>
        {target && (
          <>
            <div>
              <span className="label-xs">Bearing</span>
              <span className="mono">{target.bearing.toFixed(0)}°</span>
            </div>
            <div>
              <span className="label-xs">Distance</span>
              <span className="mono">{target.distance.toLocaleString()} km</span>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
