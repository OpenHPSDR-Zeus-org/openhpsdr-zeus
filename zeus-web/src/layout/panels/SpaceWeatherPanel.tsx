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
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// Comprehensive solar / space-weather dashboard. Pulls the full N0NBH feed
// (SFI, A/K, sunspots, X-ray, particle flux, solar wind, MUF, plus per-band HF
// and VHF conditions) from the Zeus backend, which proxies the HamClock
// sidecar. See api/spacewx.ts + SpaceWeatherService.cs.

import { useEffect } from 'react';
import { useSpaceWxStore } from '../../state/spacewx-store';
import type { SpaceWeatherSnapshot } from '../../api/spacewx';

const REFRESH_MS = 5 * 60 * 1000; // N0NBH updates ~3-hourly; poll modestly.

const PLACEHOLDERS = new Set(['', 'no report', 'norpt', 'n/a', 'none']);

/** Feed values are free-form; blank/"No Report"/"NoRpt" → em dash. */
function fmt(v: string | null | undefined): string {
  if (v == null) return '—';
  const t = v.trim();
  return PLACEHOLDERS.has(t.toLowerCase()) ? '—' : t;
}

/** Map an HF band condition word to a status colour class. */
function condClass(cond: string): string {
  switch (cond.trim().toLowerCase()) {
    case 'good': return 'swx--good';
    case 'fair': return 'swx--fair';
    case 'poor': return 'swx--poor';
    default: return 'swx--muted';
  }
}

/** K-index 0-2 quiet, 3-4 unsettled, 5+ storm. */
function kClass(v: string | null): string {
  const n = Number(v);
  if (!Number.isFinite(n)) return 'swx--muted';
  if (n <= 2) return 'swx--good';
  if (n <= 4) return 'swx--fair';
  return 'swx--poor';
}

/** A-index 0-7 quiet, 8-15 unsettled, 16+ active/storm. */
function aClass(v: string | null): string {
  const n = Number(v);
  if (!Number.isFinite(n)) return 'swx--muted';
  if (n <= 7) return 'swx--good';
  if (n <= 15) return 'swx--fair';
  return 'swx--poor';
}

/** VHF: anything other than a closed band is an opening worth flagging. */
function vhfClass(cond: string): string {
  return /closed/i.test(cond) || PLACEHOLDERS.has(cond.trim().toLowerCase())
    ? 'swx--muted'
    : 'swx--good';
}

const BAND_ROWS = ['80m-40m', '30m-20m', '17m-15m', '12m-10m'];

const VHF_LABELS: Record<string, string> = {
  'vhf-aurora': 'Aurora',
  'E-Skip': 'E-Skip',
};
function vhfLabel(b: { name: string; location: string }): string {
  const base = VHF_LABELS[b.name] ?? b.name;
  const loc = b.location.replace(/_/g, ' ').replace(/\bhemi\b/i, 'hemisphere');
  return loc ? `${base} · ${loc}` : base;
}

function Stat({ label, value, sub, cls }: { label: string; value: string; sub?: string; cls?: string }) {
  return (
    <div className="swx-stat">
      <div className="swx-stat-label label-xs">{label}</div>
      <div className={`swx-stat-value mono ${cls ?? ''}`}>{value}</div>
      {sub && <div className="swx-stat-sub label-xs">{sub}</div>}
    </div>
  );
}

function relAge(fetchedAt: number | null, lastFetch: number | null): string {
  const ts = fetchedAt ?? lastFetch;
  if (!ts) return '';
  const mins = Math.max(0, Math.round((Date.now() - ts) / 60000));
  if (mins < 1) return 'just now';
  if (mins < 60) return `${mins} min ago`;
  const h = Math.floor(mins / 60);
  return `${h} h ago`;
}

export function SpaceWeatherPanel() {
  const data = useSpaceWxStore((s) => s.data);
  const loading = useSpaceWxStore((s) => s.loading);
  const lastFetch = useSpaceWxStore((s) => s.lastFetch);
  const load = useSpaceWxStore((s) => s.load);

  useEffect(() => {
    const ctrl = new AbortController();
    void load(ctrl.signal);
    const id = window.setInterval(() => void load(), REFRESH_MS);
    return () => {
      ctrl.abort();
      window.clearInterval(id);
    };
  }, [load]);

  return (
    <div className="swx-panel">
      <div className="swx-head">
        <span className="swx-title">Solar · Space Weather</span>
        <span style={{ flex: 1 }} />
        {data?.available && (
          <span className="swx-updated label-xs">
            {data.source ?? 'N0NBH'}
            {data.updated ? ` · ${data.updated}` : ''} · {relAge(data.fetchedAt, lastFetch)}
          </span>
        )}
        <button
          type="button"
          className="btn sm"
          onClick={() => void load()}
          disabled={loading}
          title="Refresh now"
        >
          {loading ? '…' : '↻'}
        </button>
      </div>

      {!data || !data.available ? (
        <div className="swx-empty label-xs">
          {data?.unavailable ?? 'Loading space-weather data…'}
        </div>
      ) : (
        <SpaceWeatherBody data={data} />
      )}
    </div>
  );
}

function SpaceWeatherBody({ data }: { data: SpaceWeatherSnapshot }) {
  const dayCond = new Map<string, string>();
  const nightCond = new Map<string, string>();
  for (const b of data.bandConditions) {
    (b.time.toLowerCase() === 'night' ? nightCond : dayCond).set(b.name, b.condition);
  }

  return (
    <div className="swx-body">
      <div className="swx-stats">
        <Stat label="Solar Flux" value={fmt(data.solarFlux)} sub="SFI" />
        <Stat label="Sunspots" value={fmt(data.sunspots)} sub="SN" />
        <Stat label="A-Index" value={fmt(data.aIndex)} cls={aClass(data.aIndex)} />
        <Stat label="K-Index" value={fmt(data.kIndex)} cls={kClass(data.kIndex)} />
        <Stat label="X-Ray" value={fmt(data.xray)} />
        <Stat label="304Å He" value={fmt(data.heliumLine)} sub="SFU" />
        <Stat label="Sol Wind" value={fmt(data.solarWind)} sub="km/s" />
        <Stat label="Bz (IMF)" value={fmt(data.magneticField)} sub="nT" />
        <Stat label="Proton" value={fmt(data.protonFlux)} />
        <Stat label="Electron" value={fmt(data.electronFlux)} />
        <Stat label="Aurora" value={fmt(data.aurora)} sub={data.latDegree ? `lat ${fmt(data.latDegree)}°` : undefined} />
        <Stat label="MUF" value={fmt(data.muf)} sub="MHz" />
        <Stat label="foF2" value={fmt(data.fof2)} sub="MHz" />
        <Stat label="Sig Noise" value={fmt(data.signalNoise)} />
        <Stat label="Geomag" value={fmt(data.geomagField)} />
      </div>

      <div className="swx-section-label">HF Band Conditions</div>
      <div className="swx-bands">
        <div className="swx-bands-head">
          <span />
          <span className="label-xs">Day</span>
          <span className="label-xs">Night</span>
        </div>
        {BAND_ROWS.map((band) => {
          const d = dayCond.get(band);
          const n = nightCond.get(band);
          return (
            <div key={band} className="swx-bands-row">
              <span className="swx-band-name mono">{band}</span>
              <span className={`swx-band-cell ${d ? condClass(d) : 'swx--muted'}`}>{d ? fmt(d) : '—'}</span>
              <span className={`swx-band-cell ${n ? condClass(n) : 'swx--muted'}`}>{n ? fmt(n) : '—'}</span>
            </div>
          );
        })}
      </div>

      {data.vhfConditions.length > 0 && (
        <>
          <div className="swx-section-label">VHF Conditions</div>
          <div className="swx-vhf">
            {data.vhfConditions.map((v, i) => (
              <div key={`${v.name}-${v.location}-${i}`} className="swx-vhf-row">
                <span className="swx-vhf-name">{vhfLabel(v)}</span>
                <span className={`swx-vhf-cond ${vhfClass(v.condition)}`}>{fmt(v.condition)}</span>
              </div>
            ))}
          </div>
        </>
      )}
    </div>
  );
}
