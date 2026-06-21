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

import { useEffect, useRef, type MutableRefObject } from 'react';
import L from 'leaflet';
import 'leaflet/dist/leaflet.css';
import { ACTIVE_MAP_REF } from '../../state/active-map-ref';
import {
  bearingDeg,
  destinationPoint,
  distanceKm,
  greatCirclePath,
  greatCircleSegments,
} from './geo';

type MapStation = {
  call: string;
  lat: number;
  lon: number;
  grid?: string | null;
  /** QRZ portrait URL. When present the marker renders as a circular photo
   *  avatar (Log4YM MapPlugin style); otherwise falls back to a radio emoji. */
  imageUrl?: string | null;
};

type LeafletWorldMapProps = {
  home: MapStation;
  target: MapStation | null;
  /** Beam bearing (deg, 0=N, CW). Defaults to initial great-circle bearing when target is set. */
  beamBearing?: number;
  /** Beam range in km — Log4YM uses 5000 km to reach across oceans. */
  beamRangeKm?: number;
  /** Half-angle of the beam span rendered as side-lobe lines either side of
   *  the centre bearing, in degrees. Default 10.5° (≈ 21° total span) —
   *  a narrower hint than a textbook 3 dB yagi lobe so the wedge doesn't
   *  overpower the map; set to 0 to draw just the single centre beam. */
  beamHalfWidthDeg?: number;
  /** When true, arcs and markers are drawn; otherwise the map renders empty-ish. */
  active: boolean;
  /** When true, user can drag/zoom the map. Off by default — the spectrum
   *  above owns pointer events for click-to-tune. */
  interactive?: boolean;
  /** When true, render Leaflet's +/- zoom control in the corner. Defaults to
   *  the value of `interactive`. The rotator compass keeps interactive=true
   *  (wheel/drag/pinch) but turns this off so the buttons don't overlap its
   *  own NOW/SP/LP overlay. */
  showZoomControl?: boolean;
  /** When true (default), this map claims the shared `ACTIVE_MAP_REF`
   *  singleton on mount so global Alt+Up/Alt+Down shortcuts drive it. The
   *  rotator compass map sets this false so the hero-panel background map
   *  stays the keyboard-zoom target even when the rotator panel is mounted. */
  claimActiveMapRef?: boolean;
  /** If present, the target popup shows a "Rotate to NNN°" button that calls
   *  this with the current great-circle bearing. Wire to rotator-store. */
  onRotateToBearing?: (bearingDeg: number) => void;
  /** Optional out-ref: populated with the L.Map instance once mounted, null
   *  after unmount. Lets a parent drive pan/zoom imperatively (wheel bindings
   *  on the spectrum canvas above call panBy/setZoom through this). */
  mapRef?: MutableRefObject<L.Map | null>;
};

// Esri World Imagery — free satellite photo tiles, no API key. Dark oceans
// blend with the hero backdrop and continents carry enough colour to read
// clearly through the translucent spectrum/waterfall above. Matches the
// WebSDR-style reference the operator is used to.
const TILE_URL =
  'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}';
const TILE_ATTRIBUTION =
  'Tiles &copy; Esri &mdash; Source: Esri, i-cubed, USDA, USGS, AEX, GeoEye, Getmapping, Aerogrid, IGN, IGP, UPR-EGP, and the GIS User Community';

// Colour system ported from Log4YM's MapPlugin (amber target, cyan home +
// beam, dark-navy popup chrome). Kept as string constants so any future
// theme rework only needs to touch one block.
const COLOR_AMBER = '#ffb432';
const COLOR_CYAN = '#00ddff';
const COLOR_RED = '#ff4466';
const COLOR_BG_DARK = '#1a1e26';
const COLOR_TEXT_MUTED = '#a5b4c8';

function escapeHtml(s: string): string {
  return s.replace(/[&<>"']/g, (c) =>
    c === '&' ? '&amp;' : c === '<' ? '&lt;' : c === '>' ? '&gt;' : c === '"' ? '&quot;' : '&#39;',
  );
}

// divIcon factories — matches Log4YM's MapPlugin `createCallsignImageIcon`
// pattern: circular photo avatar with a callsign chip underneath. Home uses
// cyan borders; target uses amber. If the operator has no QRZ portrait the
// inner image falls back to a radio emoji.
const AVATAR_SIZE = 56;
const CHIP_OFFSET = 4; // gap between avatar and callsign chip
const CHIP_HEIGHT = 18;

function escapeAttr(s: string): string {
  return s.replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

function callsignAvatarIcon(station: MapStation, tone: 'home' | 'target'): L.DivIcon {
  const isHome = tone === 'home';
  const ring = isHome ? COLOR_CYAN : COLOR_AMBER;
  const glow = isHome ? 'rgba(0, 221, 255, 0.55)' : 'rgba(168, 221, 255, 0.55)';
  const chipBg = isHome ? 'rgba(0, 221, 255, 0.15)' : 'rgba(255, 180, 50, 0.15)';
  const chipBorder = isHome ? 'rgba(0, 221, 255, 0.6)' : 'rgba(255, 180, 50, 0.6)';
  const chipText = isHome ? COLOR_CYAN : COLOR_AMBER;

  const img = station.imageUrl
    ? `<img src="${escapeAttr(station.imageUrl)}"
        alt="" loading="lazy" referrerpolicy="no-referrer"
        onerror="this.style.display='none';this.parentNode.querySelector('.lf-avatar-fallback').style.display='flex';"
        style="
          position: absolute; inset: 3px; width: ${AVATAR_SIZE - 6}px; height: ${AVATAR_SIZE - 6}px;
          border-radius: 50%; object-fit: cover;
        " />`
    : '';
  const fallbackDisplay = station.imageUrl ? 'none' : 'flex';
  const call = escapeAttr(station.call);

  const html = `
    <div style="position: relative; width: ${AVATAR_SIZE}px; height: ${AVATAR_SIZE + CHIP_OFFSET + CHIP_HEIGHT}px;">
      <div style="
        position: absolute; top: 0; left: 0;
        width: ${AVATAR_SIZE}px; height: ${AVATAR_SIZE}px;
        border-radius: 50%;
        background: ${COLOR_BG_DARK};
        border: 3px solid ${ring};
        box-shadow: 0 0 10px ${glow};
      ">
        ${img}
        <div class="lf-avatar-fallback" style="
          position: absolute; inset: 3px; border-radius: 50%;
          display: ${fallbackDisplay};
          align-items: center; justify-content: center;
          font-size: 26px; line-height: 1;
        ">📻</div>
      </div>
      <div style="
        position: absolute;
        top: ${AVATAR_SIZE + CHIP_OFFSET}px;
        left: 50%;
        transform: translateX(-50%);
        padding: 1px 6px;
        border-radius: 0;
        background: ${chipBg};
        border: 1px solid ${chipBorder};
        color: ${chipText};
        font: 700 10px/1.3 ui-monospace, SFMono-Regular, monospace;
        letter-spacing: 0.06em;
        text-shadow: 0 1px 2px rgba(0,0,0,0.6);
        white-space: nowrap;
      ">${call}</div>
    </div>`;

  return L.divIcon({
    className: 'lf-marker',
    html,
    iconSize: [AVATAR_SIZE, AVATAR_SIZE + CHIP_OFFSET + CHIP_HEIGHT],
    iconAnchor: [AVATAR_SIZE / 2, AVATAR_SIZE / 2],
  });
}

function buildTargetPopup(
  target: { call: string; grid?: string | null },
  bearing: number,
  dist: number,
  hasRotator: boolean,
): string {
  const call = escapeHtml(target.call);
  const grid = target.grid ? escapeHtml(target.grid) : '';
  const brg = `${bearing.toFixed(0)}°`;
  const km = `${Math.round(dist).toLocaleString()} km`;
  return `
    <div style="
      min-width: 180px; padding: 4px 2px;
      font-family: ui-sans-serif, system-ui, sans-serif;
      color: #e0e7ee; background: ${COLOR_BG_DARK};
    ">
      <div style="
        font-size: 15px; font-weight: 700; font-family: ui-monospace, SFMono-Regular, monospace;
        color: ${COLOR_AMBER}; letter-spacing: 0.05em;
      ">${call}</div>
      ${grid ? `<div style="font-size: 11px; font-family: ui-monospace, monospace; color: ${COLOR_TEXT_MUTED}; margin-top: 2px;">${grid}</div>` : ''}
      <div style="font-size: 11px; font-family: ui-monospace, monospace; color: ${COLOR_CYAN}; margin-top: 6px;">
        ${brg} &middot; ${km}
      </div>
      <div style="margin-top: 8px; display: flex; gap: 6px; align-items: center;">
        ${hasRotator
          ? `<button type="button" data-lf-action="rotate" data-lf-bearing="${bearing.toFixed(1)}"
              style="
                padding: 3px 8px; font-size: 11px; border-radius: 0;
                border: 1px solid ${COLOR_CYAN}; background: rgba(0,221,255,0.15);
                color: ${COLOR_CYAN}; cursor: pointer; font-family: inherit;
              ">Rotate ${brg}</button>`
          : ''}
        <a href="https://www.qrz.com/db/${call}" target="_blank" rel="noreferrer"
          style="font-size: 11px; color: ${COLOR_AMBER}; text-decoration: none; font-weight: 600;">
          QRZ.COM &#8599;
        </a>
      </div>
    </div>`;
}

export function LeafletWorldMap({
  home,
  target,
  beamBearing,
  beamRangeKm = 10000,
  beamHalfWidthDeg = 10.5,
  active,
  interactive = false,
  showZoomControl,
  claimActiveMapRef = true,
  onRotateToBearing,
  mapRef: externalMapRef,
}: LeafletWorldMapProps) {
  const wantZoomControl = showZoomControl ?? interactive;
  // Wrapper owns our dynamic className (`interactive`, aria-hidden). Leaflet
  // mounts into an inner div whose className we never touch, so the
  // `leaflet-container`/`leaflet-grab`/etc classes Leaflet writes directly to
  // the DOM survive React re-renders. Flattening the two into one element
  // makes React overwrite Leaflet's class additions on every prop change and
  // the tiles disappear after the first toggle.
  const wrapperRef = useRef<HTMLDivElement | null>(null);
  const containerRef = useRef<HTMLDivElement | null>(null);
  const mapRef = useRef<L.Map | null>(null);
  // Two layer groups so beam updates (rotator turning live) don't tear down
  // the marker layer — see issue #244. `markerLayerRef` holds home/target
  // avatars + great-circle arc; `beamLayerRef` holds the beam wedge + rays.
  const markerLayerRef = useRef<L.LayerGroup | null>(null);
  const beamLayerRef = useRef<L.LayerGroup | null>(null);
  const zoomCtrlRef = useRef<L.Control.Zoom | null>(null);
  // Stash the callback in a ref so the marker effect doesn't tear down on
  // every parent re-render — we want the popup to pick up the latest handler
  // without invalidating the marker itself.
  const onRotateRef = useRef(onRotateToBearing);
  useEffect(() => { onRotateRef.current = onRotateToBearing; }, [onRotateToBearing]);

  // One-time init: Leaflet map, tile layer, attribution control in the corner.
  useEffect(() => {
    const el = containerRef.current;
    if (!el || mapRef.current) return;

    const map = L.map(el, {
      center: [30, -30],
      zoom: 2,
      minZoom: 2,
      maxZoom: 6,
      zoomControl: false,
      attributionControl: true,
      // Clamp to the world rectangle so the 'M'-modifier drag can't pan past
      // the ±85° tile cap or off the horizontal edge into empty space. Mercator
      // tiles don't exist above ~85° of latitude; viscosity 1 makes the pan
      // hit a hard wall rather than accelerate into the void.
      maxBounds: L.latLngBounds([-85, -180], [85, 180]),
      maxBoundsViscosity: 1.0,
      // Map is purely decorative background by default — the spectrum above
      // owns pointer events for click-to-tune. Handlers below are disabled at
      // init and the `interactive` effect enables them while 'M' is held.
      dragging: false,
      doubleClickZoom: false,
      scrollWheelZoom: false,
      boxZoom: false,
      keyboard: false,
      touchZoom: false,
      worldCopyJump: false,
      fadeAnimation: false,
      zoomAnimation: false,
    });

    L.tileLayer(TILE_URL, {
      attribution: TILE_ATTRIBUTION,
      maxZoom: 19,
    }).on('tileerror', (err) => {
      console.warn('Leaflet tile load error:', err);
      // Tile errors are non-fatal; map continues to work with cached tiles or
      // at lower zoom levels. Error boundary handles catastrophic init failures.
    }).addTo(map);

    markerLayerRef.current = L.layerGroup().addTo(map);
    beamLayerRef.current = L.layerGroup().addTo(map);
    mapRef.current = map;
    if (externalMapRef) externalMapRef.current = map;
    // Populate the shared ACTIVE_MAP_REF singleton so global keyboard
    // shortcuts (Alt+Up/Down) can drive zoom regardless of which layout
    // tree mounted us. The rotator compass passes claimActiveMapRef=false
    // so it doesn't steal the keyboard shortcut from the hero background.
    if (claimActiveMapRef) ACTIVE_MAP_REF.current = map;

    const ro = new ResizeObserver(() => map.invalidateSize());
    ro.observe(el);

    return () => {
      ro.disconnect();
      map.remove();
      mapRef.current = null;
      markerLayerRef.current = null;
      beamLayerRef.current = null;
      if (externalMapRef) externalMapRef.current = null;
      // Only clear the shared ref if it still points at OUR map — defensive
      // against React 18 strict-mode double-mounts where a fresh instance
      // could have set the singleton between our mount and unmount.
      if (ACTIVE_MAP_REF.current === map) ACTIVE_MAP_REF.current = null;
    };
  }, [externalMapRef, claimActiveMapRef]);

  // Toggle pan/zoom handlers in response to the `interactive` prop. Keeping
  // the map mounted (rather than recreating it) preserves the current pan
  // position and tile cache across M-key toggles.
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    const handlers = [
      map.dragging,
      map.scrollWheelZoom,
      map.doubleClickZoom,
      map.touchZoom,
      map.boxZoom,
      map.keyboard,
    ] as const;
    for (const h of handlers) {
      if (!h) continue;
      if (interactive) h.enable();
      else h.disable();
    }
    if (wantZoomControl && !zoomCtrlRef.current) {
      zoomCtrlRef.current = L.control.zoom({ position: 'topleft' }).addTo(map);
    } else if (!wantZoomControl && zoomCtrlRef.current) {
      zoomCtrlRef.current.remove();
      zoomCtrlRef.current = null;
    }
  }, [interactive, wantZoomControl]);

  // Redraw markers + great-circle arc when home/target change.
  //
  // Deps are primitive on purpose: parents (App.tsx, HeroPanel) build the
  // `home` / `target` objects inline, so a fresh reference arrives on every
  // render — and App re-renders many times per second while streaming
  // (vfoHz updates, rotator status polls). Depending on the object identity
  // would tear down the marker layer ~10×/s, re-mount the avatar `<img>`,
  // and flash the fallback emoji while the browser re-decoded — that is the
  // macOS "blinking map" reported in issue #244. Primitive deps make the
  // effect re-run only when something the user can see has actually changed.
  // Beam lines live in a separate layer / effect so rotator-azimuth updates
  // don't disturb these markers.
  useEffect(() => {
    const map = mapRef.current;
    const layer = markerLayerRef.current;
    if (!map || !layer) return;
    layer.clearLayers();
    if (!active) return;

    // Home marker — circular photo avatar (cyan ring) with callsign chip.
    const homeLabel = home.grid ? `${home.call} · ${home.grid}` : home.call;
    L.marker([home.lat, home.lon], { icon: callsignAvatarIcon(home, 'home') })
      .bindTooltip(homeLabel, { direction: 'top', className: 'lf-tt lf-tt-home' })
      .addTo(layer);

    if (target) {
      const dist = distanceKm(home.lat, home.lon, target.lat, target.lon);
      const bear = bearingDeg(home.lat, home.lon, target.lat, target.lon);

      // Great-circle path — amber dashed, antimeridian-safe.
      const segments = greatCircleSegments(
        { lat: home.lat, lon: home.lon },
        { lat: target.lat, lon: target.lon },
      );
      for (const seg of segments) {
        L.polyline(seg, {
          color: COLOR_AMBER,
          weight: 2,
          opacity: 0.8,
          dashArray: '5, 10',
          lineCap: 'round',
        }).addTo(layer);
      }

      // Target marker — circular photo avatar (amber ring) with callsign chip
      // + click-to-reveal popup carrying grid / bearing / distance / Rotate.
      const marker = L.marker([target.lat, target.lon], { icon: callsignAvatarIcon(target, 'target') })
        .bindPopup(buildTargetPopup(target, bear, dist, !!onRotateRef.current), {
          closeButton: true,
          className: 'lf-popup-wrap',
          maxWidth: 260,
        })
        .addTo(layer);

      // Attach the Rotate-to-bearing button handler on popup open. Leaflet
      // rebuilds the popup DOM each open, so we re-query the button each time
      // rather than holding a stale element reference.
      marker.on('popupopen', (ev) => {
        const el = (ev as L.PopupEvent).popup.getElement();
        if (!el) return;
        const btn = el.querySelector<HTMLButtonElement>('button[data-lf-action="rotate"]');
        if (!btn) return;
        btn.onclick = () => {
          const val = Number(btn.dataset.lfBearing);
          if (Number.isFinite(val)) onRotateRef.current?.(val);
          marker.closePopup();
        };
      });
    }
    /* eslint-disable-next-line react-hooks/exhaustive-deps */
  }, [
    home.call, home.lat, home.lon, home.grid, home.imageUrl,
    target?.call, target?.lat, target?.lon, target?.grid, target?.imageUrl,
    active,
  ]);

  // Beam lines — render whenever the caller supplies a bearing (rotator
  // connected + pointing, manual Go override, or derived from the current
  // target). Great-circle paths so the beam follows Earth's curvature;
  // three parallel rays (centre + ±halfWidth) visualise the antenna's
  // approximate 3 dB span at long range. Cyan centre + red sides keeps the
  // semantic separate from the amber home→target arc above.
  //
  // Lives in its own layer so live rotator-azimuth updates only redraw the
  // beam wedge, not the avatar markers (issue #244).
  useEffect(() => {
    const map = mapRef.current;
    const layer = beamLayerRef.current;
    if (!map || !layer) return;
    layer.clearLayers();
    if (!active) return;

    const implicitBeam = target
      ? bearingDeg(home.lat, home.lon, target.lat, target.lon)
      : null;
    const beam = beamBearing ?? implicitBeam;
    if (beam == null) return;

    // Shaded wedge between the two edge beams — unwrapped continuous paths
    // keep the ring closed across the antimeridian. Rendered first so the
    // centre/edge polylines draw on top.
    if (beamHalfWidthDeg > 0) {
      const edgeLeft = destinationPoint(home.lat, home.lon, beam - beamHalfWidthDeg, beamRangeKm);
      const edgeRight = destinationPoint(home.lat, home.lon, beam + beamHalfWidthDeg, beamRangeKm);
      const pathLeft = greatCirclePath(
        { lat: home.lat, lon: home.lon },
        { lat: edgeLeft[0], lon: edgeLeft[1] },
      );
      const pathRight = greatCirclePath(
        { lat: home.lat, lon: home.lon },
        { lat: edgeRight[0], lon: edgeRight[1] },
      );
      const ring: [number, number][] = [...pathLeft, ...pathRight.slice().reverse()];
      L.polygon(ring, {
        stroke: false,
        fill: true,
        fillColor: COLOR_RED,
        fillOpacity: 0.22,
        interactive: false,
      }).addTo(layer);
    }

    const offsets = beamHalfWidthDeg > 0
      ? [-beamHalfWidthDeg, 0, beamHalfWidthDeg]
      : [0];
    for (const offset of offsets) {
      const bearingForLine = beam + offset;
      const endpoint = destinationPoint(home.lat, home.lon, bearingForLine, beamRangeKm);
      const beamSegments = greatCircleSegments(
        { lat: home.lat, lon: home.lon },
        { lat: endpoint[0], lon: endpoint[1] },
      );
      const isCentre = offset === 0;
      for (const seg of beamSegments) {
        L.polyline(seg, {
          color: isCentre ? COLOR_CYAN : COLOR_RED,
          weight: isCentre ? 3 : 2,
          opacity: isCentre ? 0.75 : 0.55,
          dashArray: '10, 5',
          lineCap: 'round',
        }).addTo(layer);
      }
    }
    /* eslint-disable-next-line react-hooks/exhaustive-deps */
  }, [
    home.lat, home.lon,
    target?.lat, target?.lon,
    beamBearing, beamRangeKm, beamHalfWidthDeg, active,
  ]);

  return (
    <div
      ref={wrapperRef}
      className={`leaflet-world-map${interactive ? ' interactive' : ''}`}
      aria-hidden={interactive ? undefined : true}
    >
      <div ref={containerRef} className="leaflet-host" />
    </div>
  );
}
