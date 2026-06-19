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
import { WaterfallSurface } from '../../components/WaterfallSurface';
import { ZoomControl } from '../../components/ZoomControl';
import { WaterfallSpeedControl } from '../../components/WaterfallSpeedControl';
import { SpectrumControls } from '../../components/SpectrumControls';
import { LeafletWorldMap } from '../../components/design/LeafletWorldMap';
import { LeafletMapErrorBoundary } from '../../components/design/LeafletMapErrorBoundary';
import { setRx2, type Rx2AudioMode } from '../../api/client';
import { useConnectionStore } from '../../state/connection-store';
import { useRotatorStore } from '../../state/rotator-store';
import { useLayoutStore } from '../../state/layout-store';
import {
  clampSplit,
  mergeInstanceSplit,
  readInitialSplit,
  readInstanceSplit,
  readLegacySplit,
  writeLegacySplit,
} from '../spectrum-split';
import { useWorkspace } from '../WorkspaceContext';
import type { WorkspaceTile } from '../workspace';

interface HeroPanelProps {
  onRemove?: () => void;
  tile?: WorkspaceTile;
  layoutId?: string;
}

const RX_AUDIO_MODES: readonly { mode: Rx2AudioMode; label: string; title: string }[] = [
  { mode: 'rx1', label: 'RX 1', title: 'Hear RX1 only' },
  { mode: 'both', label: 'Both', title: 'Hear RX1 and RX2 together' },
  { mode: 'rx2', label: 'RX 2', title: 'Hear RX2 only' },
];

// Hero panel: Panadapter + Waterfall with optional Leaflet world-map overlay.
// Registered as headerless in panels.ts — this component owns the single
// .workspace-tile-header strip. The strip carries the RGL drag handle, the
// zoom slider, rotator chips (SP/LP/BEAM) when terminator+contact are live,
// the ⌥ map-mode hint, the HZ/PX readout, and the close X. Interactive
// controls inside stop mousedown propagation so a click on a chip / slider /
// input doesn't initiate a tile drag (mirrors the MetersPanel pattern).
export function HeroPanel({ onRemove, tile, layoutId }: HeroPanelProps = {}) {
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
  const applyState = useConnectionStore((s) => s.applyState);
  const rx2Enabled = useConnectionStore((s) => s.rx2Enabled);
  const rx2AudioMode = useConnectionStore((s) => s.rx2AudioMode);
  const rxFocus = useConnectionStore((s) => s.rxFocus);
  const setRxFocus = useConnectionStore((s) => s.setRxFocus);
  const updateTileInstanceConfig = useLayoutStore(
    (s) => s.updateTileInstanceConfigInLayout,
  );
  const layoutLoaded = useLayoutStore((s) => s.isLoaded);
  const layoutRadioKey = useLayoutStore((s) => s.radioKey);
  const activeLayoutId = useLayoutStore((s) => s.activeLayoutId);
  const targetLayoutId = layoutId ?? activeLayoutId;

  const stackRef = useRef<HTMLDivElement | null>(null);
  const tileUid = tile?.uid;
  const tileInstanceConfig = tile?.instanceConfig;
  const [split, setSplit] = useState(() => readInitialSplit(tileInstanceConfig));
  const [splitDragging, setSplitDragging] = useState(false);

  useEffect(() => {
    const persisted = readInstanceSplit(tileInstanceConfig);
    if (persisted === null) return;
    setSplit((current) => (Math.abs(current - persisted) < 0.001 ? current : persisted));
  }, [tileUid, tileInstanceConfig]);

  // One-time migration from the previous localStorage-only split into the
  // server-backed workspace layout config. The localStorage mirror remains as
  // an immediate fallback, but the tile config is the restart-safe source.
  useEffect(() => {
    if (!layoutLoaded) return;
    if (!tileUid) return;
    if (readInstanceSplit(tileInstanceConfig) !== null) return;
    const legacy = readLegacySplit();
    if (legacy === null) return;
    updateTileInstanceConfig(
      targetLayoutId,
      tileUid,
      mergeInstanceSplit(tileInstanceConfig, legacy),
    );
  }, [
    layoutLoaded,
    layoutRadioKey,
    targetLayoutId,
    tileUid,
    tileInstanceConfig,
    updateTileInstanceConfig,
  ]);

  const persistSplit = useCallback(
    (next: number) => {
      const clamped = clampSplit(next);
      writeLegacySplit(clamped);
      if (tile) {
        updateTileInstanceConfig(
          targetLayoutId,
          tile.uid,
          mergeInstanceSplit(tile.instanceConfig, clamped),
        );
      }
    },
    [targetLayoutId, tile, updateTileInstanceConfig],
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
  const chooseRxAudioMode = (mode: Rx2AudioMode) => {
    if (mode === 'rx1') setRxFocus('A');
    if (mode === 'rx2') setRxFocus('B');
    useConnectionStore.setState({ rx2AudioMode: mode });
    setRx2({ audioMode: mode }).then(applyState).catch(() => {});
  };

  const stitchedGridStyle = {
    position: 'relative',
    minHeight: 0,
    height: '100%',
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)',
    gap: 0,
    overflow: 'hidden',
  } as const;

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
        {rx2Enabled && (
          <div
            className="hero-rx-audio-switch"
            onPointerDown={stopDrag}
            onMouseDown={stopDrag}
            role="group"
            aria-label="Select RX audio and VFO target"
          >
            {RX_AUDIO_MODES.map((m) => (
              <button
                key={m.mode}
                type="button"
                className={`hero-rx-audio-switch__key ${rx2AudioMode === m.mode ? 'is-active' : ''}`}
                onClick={() => chooseRxAudioMode(m.mode)}
                aria-pressed={rx2AudioMode === m.mode}
                title={m.title}
              >
                <span>{m.label}</span>
              </button>
            ))}
            <span className="hero-rx-audio-switch__divider" aria-hidden="true" />
            {(['A', 'B'] as const).map((receiver) => (
              <button
                key={receiver}
                type="button"
                className={`hero-rx-audio-switch__key hero-rx-audio-switch__key--vfo ${rxFocus === receiver ? 'is-active' : ''}`}
                onClick={() => setRxFocus(receiver)}
                aria-pressed={rxFocus === receiver}
                title={`Focus VFO ${receiver} for mode, filter, band, keyboard tuning, and meters.`}
              >
                <span>{receiver}</span>
              </button>
            ))}
          </div>
        )}
        <div
          className="hero-tile-controls"
          onPointerDown={stopDrag}
          onMouseDown={stopDrag}
        >
          <ZoomControl />
          <WaterfallSpeedControl />
          <SpectrumControls />
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
          {connected && (
            rx2Enabled ? (
              <div
                style={stitchedGridStyle}
              >
                <div style={{ minWidth: 0, minHeight: 0 }}>
                  <Panadapter
                    receiver="A"
                    stitched
                    foreground={rxFocus === 'A'}
                    tuneReceiver="A"
                  />
                </div>
                <div style={{ minWidth: 0, minHeight: 0 }}>
                  <Panadapter
                    receiver="B"
                    stitched
                    foreground={rxFocus === 'B'}
                    tuneReceiver="B"
                  />
                </div>
              </div>
            ) : (
              <Panadapter receiver="A" />
            )
          )}
          <div
            className={`spectrum-splitter ${splitDragging ? 'dragging' : ''}`}
            role="separator"
            aria-orientation="horizontal"
            aria-label="Resize panadapter and waterfall"
            title="Drag to resize panadapter / waterfall"
            onPointerDown={onSplitterPointerDown}
          />
          {connected && (
            rx2Enabled ? (
              <div style={stitchedGridStyle}>
                <div style={{ minWidth: 0, minHeight: 0 }}>
                  <WaterfallSurface
                    receiver="A"
                    transparent={bgActive}
                    stitched
                    foreground={rxFocus === 'A'}
                    tuneReceiver="A"
                  />
                </div>
                <div style={{ minWidth: 0, minHeight: 0 }}>
                  <WaterfallSurface
                    receiver="B"
                    transparent={bgActive}
                    stitched
                    foreground={rxFocus === 'B'}
                    tuneReceiver="B"
                  />
                </div>
              </div>
            ) : (
              <WaterfallSurface transparent={bgActive} />
            )
          )}
        </div>
      </div>
    </div>
  );
}

