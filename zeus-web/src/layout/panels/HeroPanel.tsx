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

import { useCallback, useEffect, useRef, useState } from 'react';
import type { MouseEvent as ReactMouseEvent, PointerEvent as ReactPointerEvent } from 'react';
import { GripVertical, X } from 'lucide-react';
import { Panadapter } from '../../components/Panadapter';
import { Waterfall } from '../../components/Waterfall';
import { ZoomControl } from '../../components/ZoomControl';
import { WaterfallSpeedControl } from '../../components/WaterfallSpeedControl';
import { LeafletWorldMap } from '../../components/design/LeafletWorldMap';
import { LeafletMapErrorBoundary } from '../../components/design/LeafletMapErrorBoundary';
import { useConnectionStore } from '../../state/connection-store';
import { useRotatorStore } from '../../state/rotator-store';
import { useLayoutStore } from '../../state/layout-store';
import { useWorkspace } from '../WorkspaceContext';
import type { WorkspaceTile } from '../workspace';

// Persisted spectrum/waterfall split: fraction of the stack height given to
// the panadapter (the waterfall gets the remainder). Default 0.4 so the
// waterfall is the larger of the two out of the box; the operator can drag
// the divider to rebalance and the choice survives reloads via localStorage
// (and the server-backed workspace layout when a tile is provided).
const SPLIT_STORAGE_KEY = 'zeus.layout.spectrumSplit';
const SPLIT_CONFIG_KEY = 'spectrumSplit';
const DEFAULT_SPLIT = 0.4;
const MIN_SPLIT = 0.15;
const MAX_SPLIT = 0.85;

function clampSplit(v: number): number {
  return Math.min(MAX_SPLIT, Math.max(MIN_SPLIT, v));
}

function isValidSplit(v: number): boolean {
  return Number.isFinite(v) && v >= MIN_SPLIT && v <= MAX_SPLIT;
}

function readInstanceSplit(raw: unknown): number | null {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return null;
  const v = (raw as Record<string, unknown>)[SPLIT_CONFIG_KEY];
  return typeof v === 'number' && isValidSplit(v) ? v : null;
}

function mergeInstanceSplit(raw: unknown, split: number): Record<string, unknown> {
  const base =
    raw && typeof raw === 'object' && !Array.isArray(raw)
      ? { ...(raw as Record<string, unknown>) }
      : {};
  base[SPLIT_CONFIG_KEY] = clampSplit(split);
  return base;
}

function readLegacySplit(): number | null {
  try {
    if (typeof localStorage === 'undefined') return null;
    const raw = localStorage.getItem(SPLIT_STORAGE_KEY);
    if (raw === null) return null;
    const v = Number.parseFloat(raw);
    if (!isValidSplit(v)) return null;
    return v;
  } catch {
    return null;
  }
}

function readInitialSplit(raw: unknown): number {
  return readInstanceSplit(raw) ?? readLegacySplit() ?? DEFAULT_SPLIT;
}

function writeLegacySplit(v: number): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(SPLIT_STORAGE_KEY, String(clampSplit(v)));
  } catch {
    // quota exceeded / private mode — in-memory state still holds for this session.
  }
}

interface HeroPanelProps {
  onRemove?: () => void;
  tile?: WorkspaceTile;
}

// Hero panel: Panadapter + Waterfall with optional Leaflet world-map overlay.
// Registered as headerless in panels.ts — this component owns the single
// .workspace-tile-header strip. The strip carries the RGL drag handle, the
// zoom slider, rotator chips (SP/LP/BEAM) when terminator+contact are live,
// the ⌥ map-mode hint, the HZ/PX readout, and the close X. Interactive
// controls inside stop mousedown propagation so a click on a chip / slider /
// input doesn't initiate a tile drag (mirrors the MetersPanel pattern).
export function HeroPanel({ onRemove, tile }: HeroPanelProps = {}) {
  const {
    terminatorActive,
    imageMode,
    bgActive,
    backgroundImage,
    backgroundImageFit,
    moxOn,
    tunOn,
    contact,
    mapAvailable,
    setMapAvailable,
    mapInteractive,
    effectiveHome,
    beamOverrideDeg,
    setBeamOverrideDeg,
    beamInputStr,
    setBeamInputStr,
    rotLiveAz,
    sp,
    lp,
    heroTitle,
    submitBeam,
  } = useWorkspace();
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const updateTileInstanceConfig = useLayoutStore((s) => s.updateTileInstanceConfig);
  const layoutLoaded = useLayoutStore((s) => s.isLoaded);
  const layoutRadioKey = useLayoutStore((s) => s.radioKey);
  const activeLayoutId = useLayoutStore((s) => s.activeLayoutId);

  const stackRef = useRef<HTMLDivElement | null>(null);
  const [split, setSplit] = useState(() => readInitialSplit(tile?.instanceConfig));
  const [splitDragging, setSplitDragging] = useState(false);

  useEffect(() => {
    const persisted = readInstanceSplit(tile?.instanceConfig);
    if (persisted === null) return;
    setSplit((current) => (Math.abs(current - persisted) < 0.001 ? current : persisted));
  }, [tile?.uid, tile?.instanceConfig]);

  // One-time migration from the previous localStorage-only split into the
  // server-backed workspace layout config. The localStorage mirror remains as
  // an immediate fallback, but the tile config is the restart-safe source.
  useEffect(() => {
    if (!layoutLoaded) return;
    if (!tile) return;
    if (readInstanceSplit(tile.instanceConfig) !== null) return;
    const legacy = readLegacySplit();
    if (legacy === null) return;
    updateTileInstanceConfig(tile.uid, mergeInstanceSplit(tile.instanceConfig, legacy));
  }, [
    activeLayoutId,
    layoutLoaded,
    layoutRadioKey,
    tile?.uid,
    tile?.instanceConfig,
    updateTileInstanceConfig,
  ]);

  const persistSplit = useCallback(
    (next: number) => {
      const clamped = clampSplit(next);
      writeLegacySplit(clamped);
      if (tile) {
        updateTileInstanceConfig(tile.uid, mergeInstanceSplit(tile.instanceConfig, clamped));
      }
    },
    [tile, updateTileInstanceConfig],
  );

  // Drag the divider to rebalance the panadapter/waterfall split. We attach
  // window-level move/up listeners (rather than relying on the divider's own
  // pointer events) so the drag keeps tracking even when the cursor outruns
  // the slim hit area. stopPropagation keeps RGL from treating it as a tile
  // drag; preventDefault suppresses text selection during the gesture.
  const onSplitterPointerDown = (e: ReactPointerEvent) => {
    e.preventDefault();
    e.stopPropagation();
    const stack = stackRef.current;
    if (!stack) return;
    const rect = stack.getBoundingClientRect();
    if (rect.height <= 0) return;
    setSplitDragging(true);
    let latest = split;
    const onMove = (ev: PointerEvent) => {
      const frac = (ev.clientY - rect.top) / rect.height;
      latest = clampSplit(frac);
      setSplit(latest);
    };
    const onEnd = () => {
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onEnd);
      window.removeEventListener('pointercancel', onEnd);
      setSplitDragging(false);
      persistSplit(latest);
    };
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onEnd);
    window.addEventListener('pointercancel', onEnd);
  };

  const handleRotateToBearing = (brg: number) => {
    const rot = useRotatorStore.getState();
    const normalized = ((brg % 360) + 360) % 360;
    setBeamOverrideDeg(normalized);
    setBeamInputStr(normalized.toFixed(0));
    if (rot.config.enabled && rot.status?.connected) {
      void rot.setAzimuth(normalized);
    }
  };

  // Stop pointerdown/mousedown bubbling so RGL doesn't treat a click on
  // the zoom slider, an SP/LP chip, the BEAM input, or the close X as a
  // tile-drag start. The .workspace-tile-header strip itself stays the
  // drag handle.
  const stopDrag = (e: ReactPointerEvent | ReactMouseEvent) => e.stopPropagation();

  return (
    <div
      className={`hero ${bgActive ? 'bg-active' : ''} ${mapInteractive ? 'map-mode' : ''}`}
      style={{ display: 'flex', flexDirection: 'column', height: '100%' }}
    >
      <div className="workspace-tile-header hero-tile-header">
        <span
          className="workspace-tile-drag-handle"
          aria-hidden="true"
          title="Drag to reposition"
        >
          <GripVertical size={12} />
        </span>
        <span className={`dot ${moxOn || tunOn ? 'tx' : 'on'}`} />
        <span className="workspace-tile-title" title={typeof heroTitle === 'string' ? heroTitle : undefined}>
          {heroTitle}
        </span>
        <div
          className="hero-tile-controls"
          onPointerDown={stopDrag}
          onMouseDown={stopDrag}
        >
          <ZoomControl />
          <WaterfallSpeedControl />
          {terminatorActive && contact && mapAvailable && (
            <>
              <button
                type="button"
                className="chip mono"
                onClick={() => handleRotateToBearing(sp)}
                title="Short path — click to rotate"
              >
                <span className="k">SP</span>
                <span className="v">{sp.toFixed(0)}°</span>
              </button>
              <button
                type="button"
                className="chip mono"
                onClick={() => handleRotateToBearing(lp)}
                title="Long path — click to rotate"
              >
                <span className="k">LP</span>
                <span className="v">{lp.toFixed(0)}°</span>
              </button>
              <form onSubmit={submitBeam} className="chip mono" style={{ gap: 4 }}>
                <span className="k">BEAM</span>
                <input
                  type="text"
                  inputMode="decimal"
                  value={beamInputStr}
                  onChange={(e) => setBeamInputStr(e.target.value)}
                  placeholder={(((rotLiveAz ?? beamOverrideDeg ?? sp) % 360 + 360) % 360).toFixed(0)}
                  style={{
                    width: 40,
                    background: 'transparent',
                    border: '1px solid var(--line)',
                    color: 'inherit',
                    fontFamily: 'inherit',
                    fontSize: 'inherit',
                    padding: '0 2px',
                  }}
                />
                <button type="submit" className="btn sm" style={{ padding: '0 6px' }}>
                  Go
                </button>
              </form>
            </>
          )}
          {terminatorActive && mapAvailable && (
            <span
              className={`chip mono ${mapInteractive ? 'accent' : ''}`}
              title="Hold ⌥ (Alt) to zoom and pan the map (click-to-tune paused)"
            >
              <span className="k">⌥</span>
              <span className="v">+ −</span>
            </span>
          )}
        </div>
        {onRemove ? (
          <button
            type="button"
            className="workspace-tile-close"
            aria-label="Remove panel"
            title="Remove panel"
            onClick={(e) => {
              e.stopPropagation();
              onRemove();
            }}
            onPointerDown={(e) => e.stopPropagation()}
            onMouseDown={(e) => e.stopPropagation()}
          >
            <X size={12} />
          </button>
        ) : null}
      </div>
      <div className="hero-body" style={{ flex: 1, position: 'relative' }}>
        {imageMode && (
          <div
            className={`image-layer ${backgroundImageFit}`}
            style={{ backgroundImage: `url(${backgroundImage})` }}
          />
        )}
        <div className={`map-layer ${terminatorActive ? 'visible' : ''}`}>
          <LeafletMapErrorBoundary
            onError={(error) => {
              console.warn('Leaflet map unavailable:', error.message);
              setMapAvailable(false);
            }}
            fallback={null}
          >
            {effectiveHome && (
            <LeafletWorldMap
              home={{
                call: effectiveHome.call,
                lat: effectiveHome.lat,
                lon: effectiveHome.lon,
                grid: effectiveHome.grid,
                imageUrl: effectiveHome.imageUrl,
              }}
              target={
                contact
                  ? {
                      call: contact.callsign,
                      lat: contact.lat,
                      lon: contact.lon,
                      grid: contact.grid,
                      imageUrl: contact.photoUrl ?? null,
                    }
                  : null
              }
              beamBearing={rotLiveAz ?? beamOverrideDeg ?? undefined}
              active={terminatorActive}
              interactive={mapInteractive}
              onRotateToBearing={handleRotateToBearing}
            />
            )}
          </LeafletMapErrorBoundary>
        </div>
        <div
          ref={stackRef}
          data-spectrum-stack
          style={{
            position: 'absolute',
            inset: 0,
            display: 'grid',
            gridTemplateRows: `${split}fr 8px ${1 - split}fr`,
            zIndex: 1,
          }}
        >
          {connected && <Panadapter />}
          <div
            className={`spectrum-splitter ${splitDragging ? 'dragging' : ''}`}
            role="separator"
            aria-orientation="horizontal"
            aria-label="Resize panadapter and waterfall"
            title="Drag to resize panadapter / waterfall"
            onPointerDown={onSplitterPointerDown}
          />
          {connected && <Waterfall transparent={bgActive} />}
        </div>
      </div>
    </div>
  );
}

