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

import { useCallback, useEffect, useRef, useState } from 'react';
import type { MouseEvent as ReactMouseEvent, PointerEvent as ReactPointerEvent } from 'react';
import { GripVertical, Sliders, Volume2, VolumeX, X } from 'lucide-react';
import { Panadapter } from '../../components/Panadapter';
import { WaterfallSurface } from '../../components/WaterfallSurface';
import { WfDbScale } from '../../components/WfDbScale';
import { ZoomControl } from '../../components/ZoomControl';
import { WaterfallSpeedControl } from '../../components/WaterfallSpeedControl';
import { SpectrumControls } from '../../components/SpectrumControls';
import { LeafletWorldMap } from '../../components/design/LeafletWorldMap';
import { LeafletMapErrorBoundary } from '../../components/design/LeafletMapErrorBoundary';
import { setReceiverMuted } from '../../api/client';
import { useConnectionStore } from '../../state/connection-store';
import { receiverLabel } from '../../state/receiver-state';
import { useTxStore } from '../../state/tx-store';
import { useRotatorStore } from '../../state/rotator-store';
import { useLayoutStore } from '../../state/layout-store';
import { TileLockButton } from '../TileChrome';
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
  tileLocked?: boolean;
  workspaceLocked?: boolean;
  onToggleLock?: () => void;
}

// Hero panel: Panadapter + Waterfall with optional Leaflet world-map overlay.
// Registered as headerless in panels.ts — this component owns the single
// .workspace-tile-header strip. The strip carries the RGL drag handle, the
// zoom slider, rotator chips (SP/LP/BEAM) when terminator+contact are live,
// the ⌥ map-mode hint, the HZ/PX readout, and the close X. Interactive
// controls inside stop mousedown propagation so a click on a chip / slider /
// input doesn't initiate a tile drag (mirrors the MetersPanel pattern).
export function HeroPanel({
  onRemove,
  tile,
  layoutId,
  tileLocked = false,
  workspaceLocked = false,
  onToggleLock,
}: HeroPanelProps = {}) {
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
  // Per-RX listen/mute mixer + focus selector + the multi-DDC spectrum grid all
  // read the exposed-receiver list. RX1/RX2 keep their interactive A/B
  // panadapter/waterfall; RX3+ render read-only monitor + waterfall panes.
  const receivers = useConnectionStore((s) => s.receivers);
  const focusedRxIndex = useConnectionStore((s) => s.focusedRxIndex);
  const setFocusedRxIndex = useConnectionStore((s) => s.setFocusedRxIndex);
  const selectedRxIndices = useConnectionStore((s) => s.selectedRxIndices);
  const toggleRxSelection = useConnectionStore((s) => s.toggleRxSelection);
  // During TX the waterfall region shows the live transmitted spectrum
  // (WDSP TX analyzer pixels, streamed by the server while keyed).
  const keyed = useTxStore((s) => s.moxOn || s.tunOn);
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
  // RX audio mixer popout — small movable window (replaces the cramped inline
  // header switch). Position is null until first opened, then set from the
  // trigger button so it appears next to it; the title bar drags it anywhere.
  const [mixerOpen, setMixerOpen] = useState(false);
  const [mixerPos, setMixerPos] = useState<{ x: number; y: number } | null>(null);
  const mixerTriggerRef = useRef<HTMLButtonElement | null>(null);

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
  // Per-RX listen/mute (Thetis chkMUT/chkRX2Mute). "audible" is the UI sense;
  // the wire/state field is `muted`. Optimistic so the chip reacts immediately.
  const toggleAudible = (index: number, audible: boolean) => {
    const muted = !audible;
    useConnectionStore.setState((s) => ({
      receivers: s.receivers.map((r) => (r.index === index ? { ...r, muted } : r)),
    }));
    setReceiverMuted(index, muted).then(applyState).catch(() => {});
  };
  // Receivers exposed in the mixer/focus row: RX1 always, RX2 when enabled, plus
  // every active extra DDC.
  const exposedReceivers = receivers.filter((r) => r.index === 0 || r.enabled);
  const multiRx = exposedReceivers.length > 1;

  const toggleMixer = () => {
    setMixerOpen((open) => {
      if (!open && mixerPos === null) {
        const rect = mixerTriggerRef.current?.getBoundingClientRect();
        if (rect) setMixerPos({ x: Math.max(8, rect.right - 184), y: rect.bottom + 6 });
      }
      return !open;
    });
  };

  // Drag the mixer popout by its title bar. Window-level listeners (like the
  // splitter) so the drag keeps tracking even if the cursor outruns the bar.
  const onMixerDragStart = (e: ReactPointerEvent) => {
    e.preventDefault();
    e.stopPropagation();
    const base = mixerPos ?? { x: e.clientX - 90, y: e.clientY };
    const originX = e.clientX;
    const originY = e.clientY;
    const onMove = (ev: PointerEvent) => {
      setMixerPos({ x: base.x + (ev.clientX - originX), y: base.y + (ev.clientY - originY) });
    };
    const onEnd = () => {
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onEnd);
      window.removeEventListener('pointercancel', onEnd);
    };
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onEnd);
    window.addEventListener('pointercancel', onEnd);
  };

  // Exposed receivers in DDC order — drives the multi-DDC spectrum grid, keyed
  // by NUMERIC receiver index (0 = RX1, 1 = RX2, >= 2 = RX3+). RX1 always; RX2
  // when enabled; then each active extra DDC. Every pane now renders through the
  // same Panadapter + WaterfallSurface path; only RX1+RX2 (index <= 1) stitch.
  const spectrumPanes: { index: number }[] = [{ index: 0 }];
  if (rx2Enabled) spectrumPanes.push({ index: 1 });
  for (const r of receivers.filter((r) => r.index >= 2 && r.enabled))
    spectrumPanes.push({ index: r.index });
  const multiRxSpectrum = spectrumPanes.length > 1;

  // ≤4 receivers stitched across one row; beyond that the grid wraps so the last
  // receivers stack into a second row (e.g. 8 RX = two rows of 4). The panadapter
  // and waterfall regions share this column count so their cells line up.
  // Multi-RX panes share the grid background through a 1px gap, giving each pane
  // a hairline separator. Single-RX has no adjacent cells so the gap is inert.
  const spectrumGridStyle = {
    position: 'relative',
    minHeight: 0,
    height: '100%',
    display: 'grid',
    gridTemplateColumns: `repeat(${Math.min(spectrumPanes.length, 3)}, minmax(0, 1fr))`,
    gridAutoRows: '1fr',
    gap: multiRxSpectrum ? '1px' : 0,
    background: multiRxSpectrum ? 'var(--line)' : undefined,
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
          title={
            tileLocked || workspaceLocked
              ? 'Panel position is locked'
              : 'Drag to reposition'
          }
        >
          <GripVertical size={12} />
        </span>
        <span className={`dot ${moxOn || tunOn ? 'tx' : 'on'}`} />
        <span className="workspace-tile-title" title={typeof heroTitle === 'string' ? heroTitle : undefined}>
          {heroTitle}
        </span>
        {multiRx && (
          // Centered in the drag bar: absolutely positioned at the header's
          // horizontal midpoint so it sits dead-center regardless of the title
          // and right-side control widths.
          <span
            style={{
              position: 'absolute',
              left: '50%',
              transform: 'translateX(-50%)',
              display: 'inline-flex',
              zIndex: 2,
            }}
            onPointerDown={stopDrag}
            onMouseDown={stopDrag}
          >
            <button
              ref={mixerTriggerRef}
              type="button"
              className={`hero-rx-mixer-trigger ${mixerOpen ? 'is-open' : ''}`}
              onClick={toggleMixer}
              onPointerDown={stopDrag}
              onMouseDown={stopDrag}
              aria-pressed={mixerOpen}
              aria-label="Receiver audio mixer"
              title="Receiver audio mixer — hear/mute and focus each DDC"
            >
              <Sliders size={11} />
              <span>RX MIX</span>
            </button>
          </span>
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
        {onToggleLock ? (
          <TileLockButton
            locked={tileLocked}
            workspaceLocked={workspaceLocked}
            onToggleLock={onToggleLock}
          />
        ) : null}
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
      {multiRx && mixerOpen && (
        <div
          className="hero-rx-mixer-popout"
          style={{ left: mixerPos?.x ?? 80, top: mixerPos?.y ?? 64 }}
          onPointerDown={stopDrag}
          onMouseDown={stopDrag}
          role="dialog"
          aria-label="Receiver audio mixer"
        >
          <div className="hero-rx-mixer-popout__bar" onPointerDown={onMixerDragStart}>
            <span className="hero-rx-mixer-popout__grip" aria-hidden="true">
              <GripVertical size={11} />
            </span>
            <span className="hero-rx-mixer-popout__title">RX Mixer</span>
            <button
              type="button"
              className="hero-rx-mixer-popout__close"
              onClick={() => setMixerOpen(false)}
              aria-label="Close mixer"
              title="Close"
            >
              <X size={12} />
            </button>
          </div>
          <div className="hero-rx-mixer-popout__section">
            <div className="hero-rx-mixer-popout__label">Listen / mute</div>
            <div className="hero-rx-mixer-popout__row">
              {exposedReceivers.map((r) => {
                const audible = !r.muted;
                return (
                  <button
                    key={`hear-${r.index}`}
                    type="button"
                    className={`hero-rx-audio-switch__key ${audible ? 'is-active' : 'is-muted'}`}
                    onClick={() => toggleAudible(r.index, !audible)}
                    aria-pressed={audible}
                    title={audible ? `Mute ${receiverLabel(r)} audio` : `Hear ${receiverLabel(r)} audio`}
                  >
                    {audible ? <Volume2 size={11} /> : <VolumeX size={11} />}
                    <span>{receiverLabel(r)}</span>
                  </button>
                );
              })}
            </div>
          </div>
          <div className="hero-rx-mixer-popout__section">
            <div className="hero-rx-mixer-popout__label">Focus</div>
            <div className="hero-rx-mixer-popout__row">
              {exposedReceivers.map((r) => (
                <button
                  key={`focus-${r.index}`}
                  type="button"
                  className={`hero-rx-audio-switch__key hero-rx-audio-switch__key--vfo ${
                    focusedRxIndex === r.index ? 'is-active' : ''
                  } ${
                    selectedRxIndices.includes(r.index) && focusedRxIndex !== r.index
                      ? 'is-selected'
                      : ''
                  }`}
                  // Plain click focuses (and collapses the selection to) this
                  // receiver; Ctrl/Cmd-click toggles it in the multi-selection so
                  // mode/filter/band/AF act on every selected receiver at once.
                  onClick={(e) =>
                    e.ctrlKey || e.metaKey
                      ? toggleRxSelection(r.index)
                      : setFocusedRxIndex(r.index)
                  }
                  aria-pressed={selectedRxIndices.includes(r.index)}
                  title={`${receiverLabel(r)}: click to focus, Ctrl/⌘-click to add to the multi-selection (ganged mode/filter/band/AF).`}
                >
                  <span>{receiverLabel(r)}</span>
                </button>
              ))}
            </div>
          </div>
        </div>
      )}
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
                contact && contact.lat != null && contact.lon != null
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
          {/* Panadapter region — one cell per exposed receiver, ≤4 per row, the
              rest stacked. Every pane renders the SAME interactive panadapter,
              keyed by numeric receiver index. Only RX1+RX2 (index <= 1) stitch
              into the shared-ADC dual view; RX3+ render standalone. */}
          {connected && (
            <div style={spectrumGridStyle}>
              {spectrumPanes.map((p) => (
                <div
                  key={p.index}
                  style={{ minWidth: 0, minHeight: 0 }}
                  // Any pointer-down anywhere in this RX's pane (panadapter body,
                  // filter overlay, etc.) focuses the receiver, so the global
                  // mode/band/AF toolbar then acts on it. Capture phase so it wins
                  // before child handlers. Fixes "can't change the Kiwi's mode".
                  onPointerDownCapture={() => {
                    if (focusedRxIndex !== p.index) setFocusedRxIndex(p.index);
                  }}
                >
                  <Panadapter
                    receiver={p.index}
                    stitched={multiRxSpectrum && p.index <= 1}
                    foreground={focusedRxIndex === p.index}
                    multiRx={multiRxSpectrum}
                    tuneReceiver={p.index}
                  />
                </div>
              ))}
            </div>
          )}
          <div
            className={`spectrum-splitter ${splitDragging ? 'dragging' : ''}`}
            role="separator"
            aria-orientation="horizontal"
            aria-label="Resize panadapter and waterfall"
            title="Drag to resize panadapter / waterfall"
            onPointerDown={onSplitterPointerDown}
          />
          {/* Waterfall region. While keyed the server feeds the WDSP TX analyzer
              pixels into the main display stream, so a single full-width waterfall
              shows the transmitted spectrum (the TX panafall, issue #81).
              Otherwise: one waterfall cell per exposed receiver, ≤4 per row then
              stacked, matching the panadapter grid above. */}
          {connected && (
            keyed ? (
              <WaterfallSurface transparent={bgActive} />
            ) : (
              <div style={spectrumGridStyle}>
                {spectrumPanes.map((p) => (
                  <div
                    key={p.index}
                    style={{ minWidth: 0, minHeight: 0 }}
                    onPointerDownCapture={() => {
                      if (focusedRxIndex !== p.index) setFocusedRxIndex(p.index);
                    }}
                  >
                    <WaterfallSurface
                      receiver={p.index}
                      transparent={bgActive}
                      stitched={multiRxSpectrum && p.index <= 1}
                      foreground={focusedRxIndex === p.index}
                      tuneReceiver={p.index}
                      dbScale={false}
                    />
                  </div>
                ))}
                {/* One dB scale spanning the whole waterfall grid (left edge of
                    all rows), rather than only the RX1 tile's row. Master mode
                    (multi-RX only): it drives the global window — which every
                    pane follows via its own measured floor offset — and reads
                    out as dB above the shared noise floor, so one drag evens out
                    every band. Single-RX keeps the plain absolute-dBm scale. */}
                <WfDbScale master={multiRxSpectrum} />
              </div>
            )
          )}
        </div>
      </div>
    </div>
  );
}

