// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { create } from 'zustand';
import {
  fetchRadioSelection,
  updateRadioSelection,
  type BoardKind,
  type RadioSelection,
} from '../api/radio';
import {
  fetchBoardCapabilities,
  UNKNOWN_BOARD_CAPABILITIES,
  type BoardCapabilities,
} from '../api/board-capabilities';
import {
  fetchOrionMkIIVariant,
  setOrionMkIIVariant as setVariantApi,
  type OrionMkIIVariant,
} from '../api/orion-mkii-variant';
import { usePaStore } from './pa-store';

type RadioStore = {
  selection: RadioSelection;
  capabilities: BoardCapabilities;
  variant: OrionMkIIVariant;
  loaded: boolean;
  inflight: boolean;
  error: string | null;
  load: () => Promise<void>;
  setPreferred: (preferred: BoardKind, overrideDetection?: boolean) => Promise<void>;
  setOverrideDetection: (enabled: boolean) => Promise<void>;
  setVariant: (variant: OrionMkIIVariant) => Promise<void>;
};

// The radio preference is persisted server-side (LiteDB) rather than in
// browser localStorage because PA defaults / drive math resolve board
// kind on the backend. Local storage would drift from the source of truth
// across tabs.
export const useRadioStore = create<RadioStore>((set, get) => ({
  selection: {
    preferred: 'Auto',
    connected: 'Unknown',
    effective: 'Unknown',
    overrideDetection: false,
  },
  capabilities: UNKNOWN_BOARD_CAPABILITIES,
  variant: 'G2',
  loaded: false,
  inflight: false,
  error: null,

  load: async () => {
    set({ inflight: true, error: null });
    try {
      // Three calls in parallel — selection / capabilities / variant
      // depend on the same backend snapshot but are independent endpoints,
      // and the UI shows a "loaded" gate keyed on the selection arrival.
      const [s, caps, variant] = await Promise.all([
        fetchRadioSelection(),
        fetchBoardCapabilities().catch(() => UNKNOWN_BOARD_CAPABILITIES),
        fetchOrionMkIIVariant().catch(() => 'G2' as OrionMkIIVariant),
      ]);
      set({
        selection: s,
        capabilities: caps,
        variant,
        loaded: true,
        inflight: false,
      });
    } catch (err) {
      // Mark loaded even on failure: `connected` stays 'Unknown', so downstream
      // gates (e.g. App's layout load) fall back to the 'default' radio key
      // rather than blocking forever on an unresolved identity.
      set({
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
        loaded: true,
      });
    }
  },

  setPreferred: async (preferred, overrideDetection) => {
    // Optimistic update so the dropdown feels instant; rollback on failure.
    const prev = get().selection;
    set({
      selection: {
        ...prev,
        preferred,
        ...(overrideDetection !== undefined ? { overrideDetection } : {}),
      },
      inflight: true,
      error: null,
    });
    try {
      const s = await updateRadioSelection(preferred, overrideDetection);
      set({ selection: s, inflight: false });
      // Reload the PA panel with the PREFERRED as the preview override so
      // an operator who explicitly picks G2 while an HL2 is connected sees
      // G2's defaults in empty rows immediately (discovery still wins for
      // actual drive-byte math — that's the MISMATCH badge's job to flag).
      // Auto = no override → server uses the effective board (connected
      // wins over preferred).
      const override = preferred === 'Auto' ? undefined : preferred;
      await usePaStore.getState().load(override);
    } catch (err) {
      set({
        selection: prev,
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },

  setOverrideDetection: async (enabled) => {
    const prev = get().selection;
    set({
      selection: { ...prev, overrideDetection: enabled },
      inflight: true,
      error: null,
    });
    try {
      const s = await updateRadioSelection(prev.preferred, enabled);
      set({ selection: s, inflight: false });
    } catch (err) {
      set({
        selection: prev,
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },

  // Persists the operator-selected variant for the 0x0A wire-byte alias
  // family (issue #218). Optimistic update mirrors setPreferred. Once the
  // variant lands, the PA panel reloads to pick up the new defaults
  // (8000DLE → Anan100 bracket / OrionMkII original → Hermes bracket / G2
  // variants → OrionG2 bracket) — same flow as a board change.
  setVariant: async (variant) => {
    const prev = get().variant;
    set({ variant, inflight: true, error: null });
    try {
      const v = await setVariantApi(variant);
      set({ variant: v, inflight: false });
      // Refresh the PA panel + capabilities — both depend on variant.
      const sel = get().selection;
      const override = sel.preferred === 'Auto' ? undefined : sel.preferred;
      await usePaStore.getState().load(override);
      try {
        const caps = await fetchBoardCapabilities();
        set({ capabilities: caps });
      } catch {
        // Non-fatal — capabilities surface is best-effort UI gating.
      }
    } catch (err) {
      set({
        variant: prev,
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },
}));
