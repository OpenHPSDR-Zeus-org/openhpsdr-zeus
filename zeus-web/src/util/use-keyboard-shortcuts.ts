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

import { useEffect } from 'react';
import {
  setMox,
  setVfo,
  setZoom,
  ZOOM_MAX,
  ZOOM_MIN,
  type ZoomLevel,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useToolbarFavoritesStore } from '../state/toolbar-favorites-store';
import { useTxStore } from '../state/tx-store';
import { ACTIVE_MAP_REF } from '../state/active-map-ref';

// The arrow-key tune step follows the operator's TuningStepWidget choice
// (toolbar-favorites-store.stepHz). Read at event time inside bumpTune so
// it picks up changes without re-mounting the hook.
const MAX_HZ = 60_000_000;

function snapHz(hz: number, step: number): number {
  if (!Number.isFinite(hz)) return 0;
  const s = Math.max(1, step);
  const snapped = Math.round(hz / s) * s;
  return Math.min(MAX_HZ, Math.max(0, snapped));
}

function isEditableTarget(el: EventTarget | null): boolean {
  if (!(el instanceof HTMLElement)) return false;
  const tag = el.tagName;
  if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return true;
  return el.isContentEditable;
}

/**
 * Window-scoped arrow-key shortcuts:
 *   ←/→             nudge the VFO down/up by the operator's selected tune step
 *   ↑/↓             step zoom in/out by one level
 *   Option+↑/↓      zoom in/out (Mac muscle-memory alias for plain ↑/↓)
 *   Space (press-and-hold) keys MOX; release drops back to RX.
 *
 * Skips editable targets so typing into an <input> still works, and requires
 * a live connection so arrows don't fire POSTs against a disconnected server.
 * Tune presses coalesce to one POST per animation frame (key autorepeat can
 * fire 30+ Hz); zoom POSTs abort their predecessor the same way ZoomControl
 * does when the user drags the slider. Space uses e.repeat to fire MOX-on
 * exactly once per physical press; releasing always drops MOX off.
 */
export function useKeyboardShortcuts() {
  useEffect(() => {
    let pendingHz: number | null = null;
    let pendingRaf = 0;
    let tuneAbort: AbortController | null = null;
    let zoomAbort: AbortController | null = null;
    let moxAbort: AbortController | null = null;

    const flushTune = () => {
      pendingRaf = 0;
      const hz = pendingHz;
      pendingHz = null;
      if (hz == null) return;
      tuneAbort?.abort();
      const ctrl = new AbortController();
      tuneAbort = ctrl;
      setVfo(hz, ctrl.signal)
        .then((s) => {
          if (!ctrl.signal.aborted) useConnectionStore.getState().applyState(s);
        })
        .catch(() => {});
    };

    const bumpTune = (direction: -1 | 1) => {
      // Accumulate from the queued value so held-down arrows step cleanly
      // rather than re-reading the (potentially stale) store each frame.
      const base = pendingHz ?? useConnectionStore.getState().vfoHz;
      const step = useToolbarFavoritesStore.getState().stepHz;
      const next = snapHz(base + direction * step, step);
      useConnectionStore.setState({ vfoHz: next });
      pendingHz = next;
      if (pendingRaf === 0) pendingRaf = requestAnimationFrame(flushTune);
    };

    const bumpZoom = (direction: -1 | 1) => {
      const store = useConnectionStore.getState();
      const next = Math.min(
        ZOOM_MAX,
        Math.max(ZOOM_MIN, store.zoomLevel + direction),
      ) as ZoomLevel;
      if (next === store.zoomLevel) return;
      // Set local state first for immediate visual feedback
      store.setZoomLevel(next);
      zoomAbort?.abort();
      const ctrl = new AbortController();
      zoomAbort = ctrl;
      setZoom(next, ctrl.signal)
        .then((s) => {
          if (!ctrl.signal.aborted) useConnectionStore.getState().applyState(s);
        })
        .catch(() => {});
    };

    const driveMox = (on: boolean) => {
      const tx = useTxStore.getState();
      if (tx.moxOn === on) return;
      tx.setMoxOn(on);
      tx.setLocalMicArmed(on);
      moxAbort?.abort();
      const ctrl = new AbortController();
      moxAbort = ctrl;
      setMox(on, ctrl.signal).catch(() => {
        if (!ctrl.signal.aborted) {
          const t = useTxStore.getState();
          t.setMoxOn(!on);
          t.setLocalMicArmed(!on);
        }
      });
    };

    const onKeyDown = (e: KeyboardEvent) => {
      if (isEditableTarget(e.target)) return;
      if (useConnectionStore.getState().status !== 'Connected') return;

      // Option/Alt + Up/Down zooms the world map (background overlay), not
      // the panadapter. Handled before the modifier early-return below so
      // the alt path is the only modified shortcut we honour. If the map
      // isn't mounted (mobile shell, no LeafletWorldMap), the shortcut is
      // a no-op rather than falling back to panadapter zoom — keeping the
      // binding's meaning consistent across layouts.
      if (e.altKey && !e.ctrlKey && !e.metaKey) {
        if (e.key === 'ArrowUp') {
          e.preventDefault();
          const m = ACTIVE_MAP_REF.current;
          if (m) m.setZoom(m.getZoom() + 1, { animate: false });
          return;
        }
        if (e.key === 'ArrowDown') {
          e.preventDefault();
          const m = ACTIVE_MAP_REF.current;
          if (m) m.setZoom(m.getZoom() - 1, { animate: false });
          return;
        }
      }
      if (e.ctrlKey || e.metaKey || e.altKey) return;

      switch (e.key) {
        case 'ArrowLeft':
          e.preventDefault();
          bumpTune(-1);
          break;
        case 'ArrowRight':
          e.preventDefault();
          bumpTune(1);
          break;
        case 'ArrowUp':
          e.preventDefault();
          bumpZoom(1);
          break;
        case 'ArrowDown':
          e.preventDefault();
          bumpZoom(-1);
          break;
        case ' ':
        case 'Spacebar':
          // e.repeat filters native autorepeat so we fire MOX-on exactly
          // once per physical press; release-handled MOX-off runs on keyup.
          e.preventDefault();
          if (!e.repeat) driveMox(true);
          break;
      }
    };

    const onKeyUp = (e: KeyboardEvent) => {
      if (isEditableTarget(e.target)) return;
      if (e.key === ' ' || e.key === 'Spacebar') {
        e.preventDefault();
        // Drop MOX regardless of connection state — if we somehow keyed
        // during a brief reconnect window, releasing still clears the latch.
        driveMox(false);
      }
    };

    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('keyup', onKeyUp);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('keyup', onKeyUp);
      if (pendingRaf !== 0) cancelAnimationFrame(pendingRaf);
      tuneAbort?.abort();
      zoomAbort?.abort();
      moxAbort?.abort();
    };
  }, []);
}
