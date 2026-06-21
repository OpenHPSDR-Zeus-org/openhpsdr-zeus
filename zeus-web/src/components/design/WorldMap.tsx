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

// Lightweight equirectangular world map drawn from hand-digitised lon/lat
// polylines for each continent outline. This is enough for the design to
// read correctly without pulling in a 1 MB GeoJSON. Projection is a simple
// equirectangular fit to the SVG viewBox (-180..180 longitude, 75..-60
// latitude vertical range so mid-latitude land fills the frame).

type WorldMapProps = {
  home: { call: string; lat: number; lon: number };
  target: { call: string; lat: number; lon: number } | null;
  active: boolean;
};

const VB_W = 960;
const VB_H = 540;
const LON_MIN = -180;
const LON_MAX = 180;
const LAT_MAX = 75;
const LAT_MIN = -60;

function lonLatToXY(lon: number, lat: number): [number, number] {
  const x = ((lon - LON_MIN) / (LON_MAX - LON_MIN)) * VB_W;
  const y = ((LAT_MAX - lat) / (LAT_MAX - LAT_MIN)) * VB_H;
  return [x, y];
}

function path(points: [number, number][]): string {
  return points
    .map(([lon, lat], i) => {
      const [x, y] = lonLatToXY(lon, lat);
      return `${i === 0 ? 'M' : 'L'}${x.toFixed(1)} ${y.toFixed(1)}`;
    })
    .join(' ') + ' Z';
}

// Extremely abbreviated continent outlines — just enough landmass shape.
const NORTH_AMERICA: [number, number][] = [
  [-168, 66], [-156, 71], [-140, 70], [-125, 70], [-104, 68], [-95, 70],
  [-82, 65], [-78, 56], [-65, 52], [-55, 52], [-63, 45], [-71, 42],
  [-75, 35], [-81, 31], [-80, 25], [-84, 30], [-90, 29], [-97, 26],
  [-110, 23], [-117, 32], [-124, 41], [-125, 48], [-133, 54], [-140, 59],
  [-152, 60], [-165, 55], [-168, 66],
];
const SOUTH_AMERICA: [number, number][] = [
  [-78, 11], [-72, 11], [-60, 8], [-50, 0], [-35, -5], [-35, -22], [-42, -23],
  [-48, -27], [-58, -35], [-65, -40], [-71, -53], [-74, -52], [-74, -40],
  [-77, -20], [-80, -5], [-78, 11],
];
const EUROPE: [number, number][] = [
  [-9, 43], [-5, 36], [3, 36], [8, 40], [12, 38], [18, 40], [25, 36],
  [28, 37], [28, 42], [35, 45], [40, 50], [28, 54], [12, 56], [6, 60],
  [15, 68], [25, 70], [33, 69], [33, 60], [22, 60], [20, 55], [5, 51],
  [-2, 48], [-5, 48], [-9, 43],
];
const AFRICA: [number, number][] = [
  [-17, 15], [-12, 22], [-5, 30], [10, 34], [22, 32], [33, 31], [35, 23],
  [43, 12], [50, 11], [45, 0], [41, -15], [35, -25], [20, -34], [18, -32],
  [14, -20], [9, -5], [0, 4], [-5, 5], [-9, 8], [-14, 10], [-17, 15],
];
const ASIA: [number, number][] = [
  [33, 45], [40, 50], [55, 55], [60, 66], [78, 72], [100, 75], [140, 73],
  [170, 66], [177, 65], [168, 60], [145, 57], [135, 45], [135, 35],
  [125, 38], [122, 30], [108, 20], [98, 14], [95, 17], [90, 21], [80, 10],
  [72, 18], [67, 25], [55, 25], [48, 30], [40, 38], [33, 45],
];
const AUSTRALIA: [number, number][] = [
  [113, -22], [122, -18], [132, -12], [139, -17], [145, -16], [152, -25],
  [150, -37], [143, -39], [131, -35], [118, -35], [113, -22],
];
const GREENLAND: [number, number][] = [
  [-48, 60], [-32, 62], [-25, 70], [-20, 80], [-35, 83], [-55, 80], [-58, 70], [-48, 60],
];
const UK: [number, number][] = [
  [-5, 50], [0, 51], [1, 54], [-3, 58], [-6, 58], [-8, 55], [-5, 50],
];
const JAPAN: [number, number][] = [
  [130, 30], [136, 34], [141, 37], [145, 43], [141, 45], [136, 37], [132, 33], [130, 30],
];
const NEW_ZEALAND: [number, number][] = [
  [167, -46], [170, -41], [174, -39], [177, -39], [175, -42], [170, -47], [167, -46],
];
const MADAGASCAR: [number, number][] = [
  [43, -12], [49, -16], [50, -25], [44, -25], [43, -12],
];

const LANDS = [
  NORTH_AMERICA,
  SOUTH_AMERICA,
  EUROPE,
  AFRICA,
  ASIA,
  AUSTRALIA,
  GREENLAND,
  UK,
  JAPAN,
  NEW_ZEALAND,
  MADAGASCAR,
];

function greatCircle(
  a: { lat: number; lon: number },
  b: { lat: number; lon: number },
  steps = 80,
): [number, number][] {
  // Intermediate points along a great-circle arc, returned as [lon, lat].
  const toRad = (d: number) => (d * Math.PI) / 180;
  const toDeg = (r: number) => (r * 180) / Math.PI;
  const φ1 = toRad(a.lat);
  const λ1 = toRad(a.lon);
  const φ2 = toRad(b.lat);
  const λ2 = toRad(b.lon);
  const Δφ = φ2 - φ1;
  const Δλ = λ2 - λ1;
  const aa =
    Math.sin(Δφ / 2) ** 2 + Math.cos(φ1) * Math.cos(φ2) * Math.sin(Δλ / 2) ** 2;
  const d = 2 * Math.atan2(Math.sqrt(aa), Math.sqrt(1 - aa));
  if (d === 0) return [[a.lon, a.lat]];
  const pts: [number, number][] = [];
  for (let i = 0; i <= steps; i++) {
    const f = i / steps;
    const A = Math.sin((1 - f) * d) / Math.sin(d);
    const B = Math.sin(f * d) / Math.sin(d);
    const x = A * Math.cos(φ1) * Math.cos(λ1) + B * Math.cos(φ2) * Math.cos(λ2);
    const y = A * Math.cos(φ1) * Math.sin(λ1) + B * Math.cos(φ2) * Math.sin(λ2);
    const z = A * Math.sin(φ1) + B * Math.sin(φ2);
    const φ = Math.atan2(z, Math.sqrt(x * x + y * y));
    const λ = Math.atan2(y, x);
    pts.push([toDeg(λ), toDeg(φ)]);
  }
  return pts;
}

function arcPath(points: [number, number][]): string {
  // Project + break at antimeridian crossings so the path doesn't streak across the map.
  const segments: [number, number][][] = [[]];
  let prevLon: number | null = null;
  for (const [lon, lat] of points) {
    if (prevLon != null && Math.abs(lon - prevLon) > 180) segments.push([]);
    const current = segments[segments.length - 1]!;
    current.push([lon, lat]);
    prevLon = lon;
  }
  return segments
    .filter((s) => s.length > 1)
    .map((s) =>
      s
        .map(([lon, lat], i) => {
          const [x, y] = lonLatToXY(lon, lat);
          return `${i === 0 ? 'M' : 'L'}${x.toFixed(1)} ${y.toFixed(1)}`;
        })
        .join(' '),
    )
    .join(' ');
}

export function WorldMap({ home, target, active }: WorldMapProps) {
  const [hx, hy] = lonLatToXY(home.lon, home.lat);
  const tpos = target ? lonLatToXY(target.lon, target.lat) : null;
  const arc = target ? arcPath(greatCircle(home, target)) : '';

  return (
    <svg
      className={`world-map ${active ? 'active' : ''}`}
      viewBox={`0 0 ${VB_W} ${VB_H}`}
      preserveAspectRatio="xMidYMid slice"
    >
      <defs>
        <radialGradient id="wm-ocean" cx="50%" cy="50%" r="70%">
          <stop offset="0%" stopColor="#12284a" stopOpacity="1" />
          <stop offset="100%" stopColor="#030812" stopOpacity="1" />
        </radialGradient>
      </defs>
      <rect width={VB_W} height={VB_H} fill="url(#wm-ocean)" />

      {/* lat/lon grid */}
      <g stroke="rgba(74,158,255,0.08)" strokeWidth="0.6">
        {Array.from({ length: 13 }, (_, i) => {
          const lon = -180 + i * 30;
          const [x] = lonLatToXY(lon, 0);
          return <line key={`lon-${lon}`} x1={x} y1={0} x2={x} y2={VB_H} />;
        })}
        {Array.from({ length: 9 }, (_, i) => {
          const lat = 75 - i * 15;
          const [, y] = lonLatToXY(0, lat);
          return <line key={`lat-${lat}`} x1={0} y1={y} x2={VB_W} y2={y} />;
        })}
      </g>
      {/* equator */}
      <line
        x1={0}
        y1={lonLatToXY(0, 0)[1]}
        x2={VB_W}
        y2={lonLatToXY(0, 0)[1]}
        stroke="rgba(74,158,255,0.18)"
        strokeWidth="0.8"
        strokeDasharray="4 4"
      />

      {/* continents */}
      <g
        fill="rgba(74,158,255,0.18)"
        stroke="rgba(120,190,255,0.55)"
        strokeWidth="0.8"
        strokeLinejoin="round"
      >
        {LANDS.map((poly, i) => (
          <path key={i} d={path(poly)} />
        ))}
      </g>

      {/* home marker */}
      <g>
        <circle cx={hx} cy={hy} r={5} fill="#4a9eff" />
        <circle cx={hx} cy={hy} r={10} fill="none" stroke="#4a9eff" strokeWidth="1.2" opacity={0.6}>
          <animate attributeName="r" from="5" to="20" dur="2.4s" repeatCount="indefinite" />
          <animate attributeName="opacity" from="0.6" to="0" dur="2.4s" repeatCount="indefinite" />
        </circle>
        <text
          x={hx + 10}
          y={hy + 4}
          fill="#cfe5ff"
          fontSize="12"
          fontWeight={700}
          fontFamily="var(--font-mono)"
        >
          {home.call}
        </text>
      </g>

      {/* great-circle arc + target */}
      {target && tpos && arc && (
        <g>
          <path
            className="arc-line"
            d={arc}
            fill="none"
            stroke="#ff3838"
            strokeWidth="1.4"
            strokeLinecap="round"
          />
          <path
            className="arc-dash"
            d={arc}
            fill="none"
            stroke="#ffdcd9"
            strokeWidth="0.8"
            strokeDasharray="6 10"
            opacity={0.8}
          />
          <circle cx={tpos[0]} cy={tpos[1]} r={5} fill="#ff3838" />
          <circle
            cx={tpos[0]}
            cy={tpos[1]}
            r={10}
            fill="none"
            stroke="#ff3838"
            strokeWidth="1.2"
            opacity={0.6}
          >
            <animate attributeName="r" from="5" to="22" dur="1.8s" repeatCount="indefinite" />
            <animate attributeName="opacity" from="0.7" to="0" dur="1.8s" repeatCount="indefinite" />
          </circle>
          <text
            x={tpos[0] + 10}
            y={tpos[1] - 6}
            fill="#ffdcd9"
            fontSize="13"
            fontWeight={700}
            fontFamily="var(--font-mono)"
          >
            {target.call}
          </text>
        </g>
      )}
    </svg>
  );
}
