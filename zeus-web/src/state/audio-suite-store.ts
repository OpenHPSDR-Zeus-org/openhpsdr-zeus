// SPDX-License-Identifier: GPL-2.0-or-later
//
// Audio Suite window state — operator UI for the audio-plugin chain.
//
// What lives here:
//   - Window open / closed flag (persisted across reloads).
//   - Window position (x, y) + size — also persisted so the operator's
//     "I put it over here" choice survives a refresh.
//   - Chain order: the canonical ordered list of plugin IDs in the
//     audio chain. Server is the source of truth (ChainOrderService);
//     this store mirrors what the server publishes via the
//     /api/plugins/chain/order REST endpoint and the AudioChainOrder
//     (0x1E) WebSocket broadcast frame. The local store is NOT
//     persisted to localStorage — on reload we fetch fresh from the
//     server to avoid drift if the operator reorders on a second
//     client (or a backend restart loaded a different order).
//   - Audition state: whether the Audio Suite is mixing the plugin
//     chain's output into the operator's RX playback path. Server
//     state (IAuditionAudioSink.IsEnabled); store mirrors. Not
//     persisted — defaults off on every fresh boot.
//   - Master bypass: single operator-facing toggle that disengages
//     the entire plugin chain (NoiseGate / EQ / Comp / Exciter / Bass
//     / Reverb). Server-side default on first install is true
//     (chain inert). State persists across server restarts via
//     AudioChainSettingsStore. WS broadcast: AudioMasterBypassFrame
//     (0x1F). Local store is NOT persisted to localStorage — server
//     is authoritative; fetched on mount.
//
// What does NOT live here (handled elsewhere):
//   - Per-plugin settings (bypass, knob positions) — owned by each
//     plugin's own panel via its REST endpoint.
//   - Chain meter values (IN/OUT/GR per plugin) — polled from each
//     plugin's /meters endpoint inside its panel component.

import { create } from 'zustand';
import { persist } from 'zustand/middleware';

/** Minimum window dimensions enforced on drag-resize. */
export const AUDIO_SUITE_WINDOW_MIN_WIDTH = 480;
export const AUDIO_SUITE_WINDOW_MIN_HEIGHT = 360;

interface AudioSuiteState {
  // Window placement
  isOpen: boolean;
  x: number;
  y: number;
  width: number;
  height: number;

  // Chain order — head = first in chain (processes mic first).
  // Mirrored from the server; updated by:
  //   (1) loadChainOrderFromServer() on window open
  //   (2) AudioChainOrder WS broadcast (any client's reorder)
  //   (3) reorderChain() local optimistic update before PUT
  chainOrder: string[];

  // Audition (desktop-only feature; server returns supported=false on
  // browser mode and the toggle is disabled).
  auditionSupported: boolean;
  auditionEnabled: boolean;

  // Master bypass — single operator-facing toggle that disengages the
  // entire plugin chain. true = chain inert, mic passes bit-identical
  // to WDSP; false = chain hot. Default on first launch is true; the
  // server (AudioChainMasterBypassService) is the source of truth.
  masterBypassed: boolean;

  // Drag state — transient, not persisted.
  isDragging: boolean;

  // Actions
  open(): void;
  close(): void;
  toggle(): void;
  setPosition(x: number, y: number): void;
  setSize(width: number, height: number): void;
  setDragging(on: boolean): void;

  // Chain order plumbing.
  setChainOrderFromServer(ids: string[]): void;
  reorderChain(fromIndex: number, toIndex: number): Promise<void>;
  loadChainOrderFromServer(): Promise<void>;

  // Audition plumbing.
  loadAuditionState(): Promise<void>;
  setAuditionEnabled(enabled: boolean): Promise<void>;

  // Master bypass plumbing.
  setMasterBypassedFromServer(bypassed: boolean): void;
  loadMasterBypassFromServer(): Promise<void>;
  setMasterBypassed(bypassed: boolean): Promise<void>;
}

// Default window placement — top-left quadrant, room for plugin panels.
const DEFAULT_X = 80;
const DEFAULT_Y = 80;
const DEFAULT_WIDTH = 640;
const DEFAULT_HEIGHT = 720;

export const useAudioSuiteStore = create<AudioSuiteState>()(
  persist(
    (set, get) => ({
      isOpen: false,
      x: DEFAULT_X,
      y: DEFAULT_Y,
      width: DEFAULT_WIDTH,
      height: DEFAULT_HEIGHT,
      chainOrder: [],
      auditionSupported: false,
      auditionEnabled: false,
      // Default to true (bypassed) so the UI starts in the inert state
      // that matches the server's first-run default. The server load
      // on Audio Suite window mount overrides this with the persisted
      // value (if any) and any WS broadcast keeps it in sync after.
      masterBypassed: true,
      isDragging: false,

      open: () => set({ isOpen: true }),
      close: () => set({ isOpen: false }),
      toggle: () => set((s) => ({ isOpen: !s.isOpen })),

      setPosition: (x, y) => set({ x, y }),
      setSize: (width, height) =>
        set({
          width: Math.max(AUDIO_SUITE_WINDOW_MIN_WIDTH, width),
          height: Math.max(AUDIO_SUITE_WINDOW_MIN_HEIGHT, height),
        }),
      setDragging: (on) => set({ isDragging: on }),

      setChainOrderFromServer: (ids) => set({ chainOrder: ids }),

      reorderChain: async (fromIndex, toIndex) => {
        const current = get().chainOrder;
        if (
          fromIndex < 0 ||
          fromIndex >= current.length ||
          toIndex < 0 ||
          toIndex >= current.length ||
          fromIndex === toIndex
        ) {
          return;
        }
        // Optimistic local update — the WS broadcast handler will
        // reconcile if the server's persisted order ends up different
        // (shouldn't happen for valid permutations). The non-null
        // assertion on splice's return is safe because we already
        // bounds-checked fromIndex above.
        const next = current.slice();
        const moved = next.splice(fromIndex, 1)[0]!;
        next.splice(toIndex, 0, moved);
        set({ chainOrder: next });

        try {
          const res = await fetch('/api/plugins/chain/order', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ pluginIds: next }),
          });
          if (!res.ok) {
            // Roll back on server-side validation failure (e.g. set
            // membership changed between our GET and PUT).
            set({ chainOrder: current });
            // eslint-disable-next-line no-console
            console.warn(
              `audio-suite chain-order PUT rejected: ${res.status} ${res.statusText}`,
            );
          }
        } catch (err) {
          set({ chainOrder: current });
          // eslint-disable-next-line no-console
          console.warn('audio-suite chain-order PUT threw', err);
        }
      },

      loadChainOrderFromServer: async () => {
        try {
          const res = await fetch('/api/plugins/chain/order');
          if (!res.ok) return;
          const body = (await res.json()) as { pluginIds?: string[] };
          if (Array.isArray(body.pluginIds)) {
            set({ chainOrder: body.pluginIds });
          }
        } catch (err) {
          // eslint-disable-next-line no-console
          console.warn('audio-suite chain-order GET threw', err);
        }
      },

      loadAuditionState: async () => {
        try {
          const res = await fetch('/api/audio-suite/audition');
          if (!res.ok) {
            set({ auditionSupported: false, auditionEnabled: false });
            return;
          }
          const body = (await res.json()) as {
            supported?: boolean;
            enabled?: boolean;
          };
          set({
            auditionSupported: body.supported ?? false,
            auditionEnabled: body.enabled ?? false,
          });
        } catch {
          set({ auditionSupported: false, auditionEnabled: false });
        }
      },

      setAuditionEnabled: async (enabled) => {
        const prev = get().auditionEnabled;
        // Optimistic update so the toggle feels instant.
        set({ auditionEnabled: enabled });
        try {
          const res = await fetch('/api/audio-suite/audition', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ enabled }),
          });
          if (!res.ok) {
            set({ auditionEnabled: prev });
            // eslint-disable-next-line no-console
            console.warn(
              `audio-suite audition PUT rejected: ${res.status} ${res.statusText}`,
            );
          }
        } catch (err) {
          set({ auditionEnabled: prev });
          // eslint-disable-next-line no-console
          console.warn('audio-suite audition PUT threw', err);
        }
      },

      setMasterBypassedFromServer: (bypassed) => set({ masterBypassed: bypassed }),

      loadMasterBypassFromServer: async () => {
        try {
          const res = await fetch('/api/audio-suite/master-bypass');
          if (!res.ok) return;
          const body = (await res.json()) as { bypassed?: boolean };
          if (typeof body.bypassed === 'boolean') {
            set({ masterBypassed: body.bypassed });
          }
        } catch (err) {
          // eslint-disable-next-line no-console
          console.warn('audio-suite master-bypass GET threw', err);
        }
      },

      setMasterBypassed: async (bypassed) => {
        const prev = get().masterBypassed;
        if (prev === bypassed) return;
        // Optimistic update so the toggle feels instant.
        set({ masterBypassed: bypassed });
        try {
          const res = await fetch('/api/audio-suite/master-bypass', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ bypassed }),
          });
          if (!res.ok) {
            set({ masterBypassed: prev });
            // eslint-disable-next-line no-console
            console.warn(
              `audio-suite master-bypass PUT rejected: ${res.status} ${res.statusText}`,
            );
          }
        } catch (err) {
          set({ masterBypassed: prev });
          // eslint-disable-next-line no-console
          console.warn('audio-suite master-bypass PUT threw', err);
        }
      },
    }),
    {
      name: 'zeus-audio-suite',
      // Persist only window placement + open flag. Chain order and
      // audition state come from the server on every mount.
      partialize: (s) => ({
        isOpen: s.isOpen,
        x: s.x,
        y: s.y,
        width: s.width,
        height: s.height,
      }),
    },
  ),
);
