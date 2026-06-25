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
// LightningMapPanel — real-time lightning-strike map in the lightningmaps.org /
// Blitzortung.org style. A dark CARTO basemap (Leaflet) with a canvas overlay
// that flashes each incoming strike bright white, then fades it through amber
// to deep orange over ~40 s, exactly like the public viewers. Strikes arrive
// live from Blitzortung's community detection network over a WebSocket; the
// feed is LZW-obfuscated, so we decode each frame before plotting.
//
// Data source: Blitzortung.org is a non-commercial, community-run lightning
// network; lightningmaps.org is its reference viewer. We connect to the same
// public real-time WebSocket the viewer uses. No API key, no backend — the
// browser talks to ws{1,7,8}.blitzortung.org directly with multi-server
// failover and reconnect. NOTE: Blitzortung's data-use policy asks heavy /
// redistributing apps to relay through their own server; if Zeus ever ships
// this widely, move the socket behind a Zeus.Server relay (see HamClock /
// propagation services for the proxy pattern). For a single operator's client
// the direct connection mirrors what every browser opening the viewer does.

import { useEffect, useRef, useState } from 'react';
import L from 'leaflet';
import 'leaflet/dist/leaflet.css';
import './LightningMapPanel.css';
import { useWorkspace } from '../WorkspaceContext';

// CARTO dark basemap — free, no key, near-black land/ocean that lets the
// amber strikes pop. Same family of tiles the public lightning viewers use.
const TILE_URL = 'https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png';
const TILE_ATTRIBUTION =
  'Strikes &copy; <a href="https://www.blitzortung.org/">Blitzortung.org</a> contributors · ' +
  '&copy; <a href="https://www.openstreetmap.org/copyright">OSM</a> &copy; <a href="https://carto.com/attributions">CARTO</a>';

// Public Blitzortung real-time strike servers. We round-robin on disconnect.
const WS_SERVERS = [
  'wss://ws1.blitzortung.org',
  'wss://ws7.blitzortung.org',
  'wss://ws8.blitzortung.org',
];

// How long a strike stays on the map before it has fully faded out (ms).
const STRIKE_TTL_MS = 42_000;
// Cap the live ring so a big storm front can't grow the buffer unbounded.
const MAX_STRIKES = 900;

interface Strike {
  lat: number;
  lon: number;
  /** Wall-clock ms when we received it — drives the fade animation. */
  t: number;
}

// Blitzortung obfuscates each WebSocket frame with an LZW-style dictionary
// compression. This is the inverse, ported from the reference viewer's client
// (the same routine appears in every Blitzortung WS client). It returns the
// JSON text we then parse.
function blitzortungDecode(input: string): string {
  const data = input.split('');
  if (data.length === 0) return '';
  const dict = new Map<number, string>();
  let currChar = data[0]!;
  let oldPhrase = currChar;
  const out: string[] = [currChar];
  let code = 256;
  for (let i = 1; i < data.length; i++) {
    const ch = data[i]!;
    const currCode = ch.charCodeAt(0);
    let phrase: string;
    if (currCode < 256) {
      phrase = ch;
    } else {
      const fromDict = dict.get(currCode);
      phrase = fromDict !== undefined ? fromDict : oldPhrase + currChar;
    }
    out.push(phrase);
    currChar = phrase.charAt(0);
    dict.set(code, oldPhrase + currChar);
    code += 1;
    oldPhrase = phrase;
  }
  return out.join('');
}

type ConnState = 'connecting' | 'live' | 'down';

// Strike colour ramp by normalised age (0 = just struck, 1 = about to expire).
// White-hot flash → amber → deep orange → transparent, matching the energetic
// look of the public viewers while staying in the Zeus amber family.
function strikeStyle(age: number): { core: string; glow: string; r: number; glowR: number } {
  const fade = Math.max(0, 1 - age); // 1 → 0 over the TTL
  // First ~1.5 s reads as a bright flash; after that settles to a steady dot.
  const flash = Math.max(0, 1 - age * (STRIKE_TTL_MS / 1500));
  const a = 0.25 + 0.75 * fade;
  let r: number, g: number, b: number;
  if (age < 0.12) {
    // white-hot core blending to amber
    const k = age / 0.12;
    r = 255;
    g = Math.round(255 - 78 * k);
    b = Math.round(255 - 195 * k);
  } else {
    // amber (#ffb13c) → deep orange (#ff5a18)
    const k = (age - 0.12) / 0.88;
    r = 255;
    g = Math.round(177 - 87 * k);
    b = Math.round(60 - 36 * k);
  }
  const core = `rgba(${r},${g},${b},${a})`;
  const glow = `rgba(${r},${g},${b},${0.16 * fade})`;
  const r0 = 1.6 + 2.6 * flash; // dot swells on the flash, then steadies
  const glowR = 7 + 22 * flash;
  return { core, glow, r: r0, glowR };
}

export function LightningMapPanel() {
  const { effectiveHome } = useWorkspace();
  const hostRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const mapRef = useRef<L.Map | null>(null);
  const strikesRef = useRef<Strike[]>([]);
  // Receipt timestamps for the rolling strikes-per-minute rate, independent of
  // the plotted ring (which is capped + expires).
  const rateRef = useRef<number[]>([]);
  const homeLayerRef = useRef<L.LayerGroup | null>(null);

  const [conn, setConn] = useState<ConnState>('connecting');
  const [rate, setRate] = useState(0); // strikes/min over the last 60 s
  const [shown, setShown] = useState(0); // strikes currently on the map

  // Home QTH for the initial view + a marker dot. Captured in a ref so the WS /
  // map init effect stays mount-once and doesn't tear down when home resolves.
  const homeRef = useRef(effectiveHome);
  useEffect(() => {
    homeRef.current = effectiveHome;
  }, [effectiveHome]);

  // ── Leaflet map init (mount once) ──────────────────────────────────────────
  useEffect(() => {
    const el = hostRef.current;
    if (!el || mapRef.current) return;

    const home = homeRef.current;
    const map = L.map(el, {
      center: home ? [home.lat, home.lon] : [30, 5],
      zoom: home ? 5 : 3,
      minZoom: 2,
      maxZoom: 11,
      // Zoom control lives bottom-left so it clears the strikes/min HUD card
      // pinned top-left.
      zoomControl: false,
      attributionControl: true,
      worldCopyJump: true,
      maxBounds: L.latLngBounds([-85, -200], [85, 200]),
      maxBoundsViscosity: 0.7,
    });

    L.tileLayer(TILE_URL, {
      attribution: TILE_ATTRIBUTION,
      subdomains: 'abcd',
      maxZoom: 19,
      detectRetina: true,
    })
      .on('tileerror', (e) => console.warn('Lightning map tile error:', e))
      .addTo(map);

    L.control.zoom({ position: 'bottomleft' }).addTo(map);

    homeLayerRef.current = L.layerGroup().addTo(map);
    mapRef.current = map;

    const ro = new ResizeObserver(() => map.invalidateSize());
    ro.observe(el);

    return () => {
      ro.disconnect();
      map.remove();
      mapRef.current = null;
      homeLayerRef.current = null;
    };
  }, []);

  // ── Home QTH marker — small cyan ring, redrawn when home changes ───────────
  useEffect(() => {
    const layer = homeLayerRef.current;
    if (!layer) return;
    layer.clearLayers();
    if (!effectiveHome) return;
    L.circleMarker([effectiveHome.lat, effectiveHome.lon], {
      radius: 5,
      color: '#00ddff',
      weight: 2,
      fillColor: '#00ddff',
      fillOpacity: 0.5,
    })
      .bindTooltip(effectiveHome.call || effectiveHome.grid || 'Home', {
        direction: 'top',
        offset: [0, -4],
      })
      .addTo(layer);
  }, [effectiveHome]);

  // ── Blitzortung WebSocket — multi-server failover + reconnect ──────────────
  useEffect(() => {
    let ws: WebSocket | null = null;
    let serverIdx = 0;
    let reconnectTimer: number | undefined;
    let closed = false;

    const ingest = (lat: number, lon: number) => {
      if (!Number.isFinite(lat) || !Number.isFinite(lon)) return;
      const now = Date.now();
      const ring = strikesRef.current;
      ring.push({ lat, lon, t: now });
      if (ring.length > MAX_STRIKES) ring.splice(0, ring.length - MAX_STRIKES);
      rateRef.current.push(now);
    };

    const handleFrame = (raw: string) => {
      let text: string;
      try {
        text = blitzortungDecode(raw);
      } catch {
        return;
      }
      let msg: unknown;
      try {
        msg = JSON.parse(text);
      } catch {
        return;
      }
      if (!msg || typeof msg !== 'object') return;
      const m = msg as Record<string, unknown>;
      // A strike frame carries lat/lon at the top level; ignore keep-alive /
      // station-status frames that don't.
      if (typeof m.lat === 'number' && typeof m.lon === 'number') {
        ingest(m.lat, m.lon);
      }
    };

    const connect = () => {
      if (closed) return;
      setConn('connecting');
      const url = WS_SERVERS[serverIdx % WS_SERVERS.length] ?? WS_SERVERS[0]!;
      let sock: WebSocket;
      try {
        sock = new WebSocket(url);
      } catch {
        scheduleReconnect();
        return;
      }
      ws = sock;

      sock.onopen = () => {
        if (closed) return;
        setConn('live');
        // Subscribe to the global real-time strike stream.
        try {
          sock.send(JSON.stringify({ a: 111 }));
        } catch {
          /* will surface via onclose */
        }
      };
      sock.onmessage = (ev) => {
        if (typeof ev.data === 'string') handleFrame(ev.data);
      };
      sock.onerror = () => {
        try {
          sock.close();
        } catch {
          /* ignore */
        }
      };
      sock.onclose = () => {
        if (closed) return;
        setConn('down');
        serverIdx += 1; // try the next server next time
        scheduleReconnect();
      };
    };

    const scheduleReconnect = () => {
      if (closed) return;
      window.clearTimeout(reconnectTimer);
      reconnectTimer = window.setTimeout(connect, 2500);
    };

    connect();

    return () => {
      closed = true;
      window.clearTimeout(reconnectTimer);
      if (ws) {
        ws.onopen = ws.onmessage = ws.onerror = ws.onclose = null;
        try {
          ws.close();
        } catch {
          /* ignore */
        }
      }
    };
  }, []);

  // ── Render loop — reproject + draw strikes every frame, expire the old ─────
  // A single rAF loop both animates the fade and re-projects strikes during
  // pan/zoom (latLngToContainerPoint is correct per-frame), so panning the map
  // keeps the strikes pinned to their geography.
  useEffect(() => {
    let raf = 0;
    const canvas = canvasRef.current;
    const host = hostRef.current;
    if (!canvas || !host) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    let dpr = window.devicePixelRatio || 1;
    const sizeCanvas = () => {
      dpr = window.devicePixelRatio || 1;
      const w = host.clientWidth;
      const h = host.clientHeight;
      canvas.width = Math.max(1, Math.round(w * dpr));
      canvas.height = Math.max(1, Math.round(h * dpr));
      canvas.style.width = `${w}px`;
      canvas.style.height = `${h}px`;
    };
    sizeCanvas();
    const ro = new ResizeObserver(sizeCanvas);
    ro.observe(host);

    let lastStat = 0;

    const frame = () => {
      raf = window.requestAnimationFrame(frame);
      const map = mapRef.current;
      if (!map) return;
      const now = Date.now();

      // Expire faded strikes from the plotted ring.
      const ring = strikesRef.current;
      let firstLive = 0;
      while (firstLive < ring.length && now - ring[firstLive]!.t > STRIKE_TTL_MS) firstLive += 1;
      if (firstLive > 0) ring.splice(0, firstLive);

      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.clearRect(0, 0, canvas.width / dpr, canvas.height / dpr);
      ctx.globalCompositeOperation = 'lighter'; // additive — overlapping strikes glow

      const size = map.getSize();
      for (let i = 0; i < ring.length; i++) {
        const s = ring[i]!;
        const age = (now - s.t) / STRIKE_TTL_MS;
        const p = map.latLngToContainerPoint([s.lat, s.lon]);
        // Cull off-screen (with a small margin for glow bleed).
        if (p.x < -40 || p.y < -40 || p.x > size.x + 40 || p.y > size.y + 40) continue;
        const { core, glow, r, glowR } = strikeStyle(age);
        // Soft glow halo.
        const grad = ctx.createRadialGradient(p.x, p.y, 0, p.x, p.y, glowR);
        grad.addColorStop(0, glow);
        grad.addColorStop(1, 'rgba(0,0,0,0)');
        ctx.fillStyle = grad;
        ctx.beginPath();
        ctx.arc(p.x, p.y, glowR, 0, Math.PI * 2);
        ctx.fill();
        // Bright core.
        ctx.fillStyle = core;
        ctx.beginPath();
        ctx.arc(p.x, p.y, r, 0, Math.PI * 2);
        ctx.fill();
      }
      ctx.globalCompositeOperation = 'source-over';

      // Throttle React state updates (rate / count HUD) to ~4 Hz.
      if (now - lastStat > 250) {
        lastStat = now;
        const cutoff = now - 60_000;
        const stamps = rateRef.current;
        let drop = 0;
        while (drop < stamps.length && stamps[drop]! < cutoff) drop += 1;
        if (drop > 0) stamps.splice(0, drop);
        setRate(stamps.length);
        setShown(ring.length);
      }
    };
    raf = window.requestAnimationFrame(frame);

    return () => {
      window.cancelAnimationFrame(raf);
      ro.disconnect();
    };
  }, []);

  const recenter = () => {
    const map = mapRef.current;
    if (!map) return;
    const home = homeRef.current;
    if (home) map.setView([home.lat, home.lon], 5, { animate: true });
    else map.setView([30, 5], 3, { animate: true });
  };

  const statusText = conn === 'live' ? 'Live feed' : conn === 'connecting' ? 'Connecting…' : 'Reconnecting…';

  return (
    <div className="lightning-map">
      <div ref={hostRef} className="lm-host" />
      <canvas ref={canvasRef} className="lm-strikes" />

      <div className="lm-hud">
        <div className="lm-hud-row">
          <span className="lm-hud-label">Strikes/min</span>
          <span className="lm-hud-value">{rate}</span>
        </div>
        <div className="lm-hud-row">
          <span className="lm-hud-label">On map</span>
          <span className="lm-hud-value sub">{shown}</span>
        </div>
        <div className="lm-status">
          <span className={`lm-dot ${conn}`} />
          <span className="lm-status-text">{statusText}</span>
        </div>
      </div>

      <button type="button" className="lm-recenter" onClick={recenter} title="Re-center on your QTH">
        ⌖ Recenter
      </button>
    </div>
  );
}
