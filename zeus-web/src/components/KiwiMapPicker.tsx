// SPDX-License-Identifier: GPL-2.0-or-later
//
// KiwiMapPicker — a world map of public KiwiSDR receivers for Settings → KIWI.
// Each marker is one receiver from the kiwisdr.com directory (proxied + cached
// by the server at /api/kiwi/directory). Clicking a marker passes its address
// up to the panel, which stores it as the Kiwi URL. Reuses the same Leaflet +
// CARTO dark basemap the Lightning map uses; marker colours are hex (SVG stroke
// attributes can't resolve CSS vars) but track the Zeus palette: accent blue =
// free slot, tx red = full, grey = offline.

import { useEffect, useMemo, useRef, useState } from 'react';
import L from 'leaflet';
import 'leaflet/dist/leaflet.css';
import { getKiwiDirectory, type KiwiDirectoryEntry } from '../api/client';

const TILE_URL = 'https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png';
const TILE_ATTRIBUTION =
  '&copy; <a href="https://www.openstreetmap.org/copyright">OSM</a> &copy; ' +
  '<a href="https://carto.com/attributions">CARTO</a> · receivers &copy; ' +
  '<a href="http://kiwisdr.com/public/">kiwisdr.com</a>';

// Palette-matched hex (tokens.css): --accent, --tx, --power, plus a neutral grey.
const COLOR_FREE = '#4a9eff';
const COLOR_FULL = '#e63a2b';
const COLOR_OFFLINE = '#6b7280';
const COLOR_SELECTED = '#ffc93a';

function baseColor(e: KiwiDirectoryEntry): string {
  if (!e.online) return COLOR_OFFLINE;
  if (e.usersMax > 0 && e.users >= e.usersMax) return COLOR_FULL;
  return COLOR_FREE;
}

function escapeHtml(s: string): string {
  return s.replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c] ?? c,
  );
}

export function KiwiMapPicker({
  selectedUrl,
  onSelect,
}: {
  selectedUrl: string;
  onSelect: (url: string) => void;
}) {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const mapRef = useRef<L.Map | null>(null);
  const layerRef = useRef<L.LayerGroup | null>(null);
  const markersRef = useRef<Map<string, L.CircleMarker>>(new Map());

  // Keep the latest onSelect in a ref so the mount-once marker handlers never
  // capture a stale closure.
  const onSelectRef = useRef(onSelect);
  useEffect(() => {
    onSelectRef.current = onSelect;
  }, [onSelect]);

  const [entries, setEntries] = useState<KiwiDirectoryEntry[]>([]);
  const [status, setStatus] = useState<'loading' | 'ready' | 'error'>('loading');

  const counts = useMemo(() => {
    let free = 0;
    let online = 0;
    for (const e of entries) {
      if (e.online) online += 1;
      if (e.online && (e.usersMax === 0 || e.users < e.usersMax)) free += 1;
    }
    return { total: entries.length, online, free };
  }, [entries]);

  // ── Leaflet map init (mount once) ──────────────────────────────────────────
  useEffect(() => {
    const el = hostRef.current;
    if (!el || mapRef.current) return;
    const map = L.map(el, {
      center: [25, 5],
      zoom: 2,
      minZoom: 1,
      maxZoom: 12,
      worldCopyJump: true,
      zoomControl: true,
      attributionControl: true,
      maxBounds: L.latLngBounds([-85, -220], [85, 220]),
      maxBoundsViscosity: 0.7,
    });
    L.tileLayer(TILE_URL, {
      attribution: TILE_ATTRIBUTION,
      subdomains: 'abcd',
      maxZoom: 19,
      detectRetina: true,
    }).addTo(map);
    layerRef.current = L.layerGroup().addTo(map);
    mapRef.current = map;

    const ro = new ResizeObserver(() => map.invalidateSize());
    ro.observe(el);
    return () => {
      ro.disconnect();
      map.remove();
      mapRef.current = null;
      layerRef.current = null;
      markersRef.current.clear();
    };
  }, []);

  // ── Fetch the public directory once ────────────────────────────────────────
  useEffect(() => {
    const ctrl = new AbortController();
    setStatus('loading');
    getKiwiDirectory(ctrl.signal)
      .then((list) => {
        setEntries(list);
        setStatus(list.length > 0 ? 'ready' : 'error');
      })
      .catch(() => setStatus('error'));
    return () => ctrl.abort();
  }, []);

  // ── (Re)build markers when the directory changes ───────────────────────────
  useEffect(() => {
    const layer = layerRef.current;
    if (!layer) return;
    layer.clearLayers();
    markersRef.current.clear();
    for (const e of entries) {
      const color = baseColor(e);
      const m = L.circleMarker([e.lat, e.lon], {
        radius: 5,
        color,
        weight: 1.5,
        fillColor: color,
        fillOpacity: 0.55,
      });
      const usersLabel = e.usersMax > 0 ? `${e.users}/${e.usersMax} users` : `${e.users} users`;
      m.bindTooltip(
        `<b>${escapeHtml(e.name)}</b><br>` +
          (e.location ? `${escapeHtml(e.location)}<br>` : '') +
          `${usersLabel}${e.snr ? ` · SNR ${escapeHtml(e.snr)}` : ''}<br>` +
          `<span style="opacity:.65">${escapeHtml(e.url)}</span>`,
        { direction: 'top', opacity: 0.95 },
      );
      m.on('click', () => onSelectRef.current(e.url));
      m.addTo(layer);
      markersRef.current.set(e.url, m);
    }
  }, [entries]);

  // ── Highlight the currently-selected receiver ──────────────────────────────
  useEffect(() => {
    for (const [url, m] of markersRef.current) {
      const e = entries.find((x) => x.url === url);
      const base = e ? baseColor(e) : COLOR_FREE;
      if (url === selectedUrl) {
        m.setStyle({ radius: 8, weight: 3, color: COLOR_SELECTED, fillColor: base, fillOpacity: 0.85 });
        m.bringToFront();
      } else {
        m.setStyle({ radius: 5, weight: 1.5, color: base, fillColor: base, fillOpacity: 0.55 });
      }
    }
  }, [selectedUrl, entries]);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <div
        ref={hostRef}
        style={{
          height: 360,
          width: '100%',
          borderRadius: 'var(--r-sm)',
          overflow: 'hidden',
          border: '1px solid var(--panel-border)',
          background: 'var(--bg-0)',
        }}
      />
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 12,
          fontSize: 10,
          color: 'var(--fg-3)',
        }}
      >
        {status === 'loading' && <span>Loading public KiwiSDR directory…</span>}
        {status === 'error' && (
          <span style={{ color: 'var(--tx)' }}>
            Directory unavailable — enter a URL manually below.
          </span>
        )}
        {status === 'ready' && (
          <>
            <span>
              {counts.total} receivers · {counts.free} with free slots
            </span>
            <span style={{ flex: 1 }} />
            <Legend color={COLOR_FREE} label="free" />
            <Legend color={COLOR_FULL} label="full" />
            <Legend color={COLOR_OFFLINE} label="offline" />
          </>
        )}
      </div>
    </div>
  );
}

function Legend({ color, label }: { color: string; label: string }) {
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
      <span
        style={{
          width: 8,
          height: 8,
          borderRadius: '50%',
          background: color,
          display: 'inline-block',
        }}
      />
      {label}
    </span>
  );
}
