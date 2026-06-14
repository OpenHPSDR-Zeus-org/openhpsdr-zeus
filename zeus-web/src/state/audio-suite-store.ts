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

/**
 * A saved Audio Suite profile — a named snapshot of the chain config
 * (active order + parked set + master bypass). Mirrors the server
 * AudioProfileEntry. Chain-level only in v1 (no per-plugin knob state).
 */
export interface AudioProfileSummary {
  name: string;
  order: string[];
  parked: string[];
  masterBypass: boolean;
  createdUtc: string;
  updatedUtc: string;
}

/** Result of a VST directory scan (POST /api/audio-suite/scan-vst-directory). */
export interface VstScanResult {
  ok: boolean;
  error?: string;
  directory?: string;
  registered: Array<{ id: string; name: string }>;
  skipped: Array<{ id: string; name: string }>;
  errors: Array<{ source: string; message: string }>;
}

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

  // Per-slot collapse state for the rack view. Keyed by plugin ID;
  // a true value means the slot is collapsed (header only). Absent /
  // false means expanded (full plugin panel visible). Persisted to
  // localStorage so the operator's "I keep the EQ folded away" choice
  // survives a reload — purely a presentation preference, never sent
  // to the server.
  collapsed: Record<string, boolean>;

  // Chips+detail layout: which chain plugin is loaded in the detail
  // pane. Presentation-only, persisted to localStorage. A null or stale
  // id (plugin parked/removed) falls back to the first chain plugin in
  // the component, so this never needs server validation.
  selectedChainId: string | null;

  // Whether the plugin-browser sidebar is folded to a thin strip.
  // Presentation-only, persisted to localStorage.
  sidebarCollapsed: boolean;

  // Saved profiles (chain-config snapshots). Server-authoritative;
  // loaded on window open, refreshed after save / delete. Not persisted
  // to localStorage.
  profiles: AudioProfileSummary[];

  // Actions
  open(): void;
  close(): void;
  toggle(): void;
  setPosition(x: number, y: number): void;
  setSize(width: number, height: number): void;
  setDragging(on: boolean): void;

  // Rack collapse plumbing.
  toggleCollapsed(pluginId: string): void;
  setAllCollapsed(collapsed: boolean, pluginIds: string[]): void;
  toggleSidebar(): void;

  // Chips+detail selection.
  setSelectedChainId(id: string | null): void;

  // Chain membership — park (active=false) / un-park (active=true) an
  // installed plugin. Parking pulls it out of the active chain (stops
  // processing, drops from the rack) without uninstalling; un-parking
  // slots it back in at its canonical position. Server returns the new
  // active order, which becomes chainOrder.
  setChainMembership(pluginId: string, active: boolean): Promise<void>;

  // Profile plumbing.
  loadProfiles(): Promise<void>;
  saveProfile(name: string): Promise<void>;
  applyProfile(name: string): Promise<void>;
  deleteProfile(name: string): Promise<void>;

  // Scan a directory for VST3 plugins, register each, and refresh the
  // rack. Returns a summary (or an error string on failure).
  scanVstDirectory(directory: string): Promise<VstScanResult>;

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

  // Audio Suite processing route: 'native' (in-process plugin chain, the
  // default) vs 'vst' (out-of-process VST engine). Mutually exclusive; server
  // is authoritative. vstEngineAvailable = an engine is installed;
  // vstEngineActive = the engine is currently live and routing TX audio.
  processingMode: 'native' | 'vst';
  vstEngineAvailable: boolean;
  vstEngineActive: boolean;
  loadProcessingModeFromServer(): Promise<void>;
  setProcessingMode(mode: 'native' | 'vst'): Promise<void>;
}

// Default window placement — top-left quadrant, room for plugin panels.
const DEFAULT_X = 80;
const DEFAULT_Y = 80;
const DEFAULT_WIDTH = 860;
const DEFAULT_HEIGHT = 760;

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
      processingMode: 'native',
      vstEngineAvailable: false,
      vstEngineActive: false,
      isDragging: false,
      collapsed: {},
      selectedChainId: null,
      sidebarCollapsed: false,
      profiles: [],

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

      // Slots default to collapsed (absent === collapsed), so the
      // toggle flips relative to that default: an untouched slot opens
      // on first click instead of needing two.
      toggleCollapsed: (pluginId) =>
        set((s) => ({
          collapsed: {
            ...s.collapsed,
            [pluginId]: !(s.collapsed[pluginId] ?? true),
          },
        })),

      setAllCollapsed: (collapsed, pluginIds) =>
        set((s) => {
          const next = { ...s.collapsed };
          for (const id of pluginIds) next[id] = collapsed;
          return { collapsed: next };
        }),

      toggleSidebar: () =>
        set((s) => ({ sidebarCollapsed: !s.sidebarCollapsed })),

      setSelectedChainId: (id) => set({ selectedChainId: id }),

      setChainMembership: async (pluginId, active) => {
        const prev = get().chainOrder;
        // Optimistic only for park (we know the result: drop the ID).
        // Un-park's position is server-decided, so we wait for the
        // response and adopt the returned active order.
        if (!active) {
          set({ chainOrder: prev.filter((id) => id !== pluginId) });
        }
        try {
          const res = await fetch(
            `/api/plugins/${encodeURIComponent(pluginId)}/chain-membership`,
            {
              method: 'PUT',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ active }),
            },
          );
          if (!res.ok) {
            set({ chainOrder: prev });
            // eslint-disable-next-line no-console
            console.warn(
              `audio-suite chain-membership PUT rejected: ${res.status} ${res.statusText}`,
            );
            return;
          }
          const body = (await res.json()) as { pluginIds?: string[] };
          if (Array.isArray(body.pluginIds)) {
            set({ chainOrder: body.pluginIds });
          }
        } catch (err) {
          set({ chainOrder: prev });
          // eslint-disable-next-line no-console
          console.warn('audio-suite chain-membership PUT threw', err);
        }
      },

      loadProfiles: async () => {
        try {
          const res = await fetch('/api/audio-suite/profiles');
          if (!res.ok) return;
          const body = (await res.json()) as { profiles?: AudioProfileSummary[] };
          if (Array.isArray(body.profiles)) set({ profiles: body.profiles });
        } catch (err) {
          // eslint-disable-next-line no-console
          console.warn('audio-suite profiles GET threw', err);
        }
      },

      saveProfile: async (name) => {
        const trimmed = name.trim();
        if (!trimmed) return;
        try {
          const res = await fetch(
            `/api/audio-suite/profiles/${encodeURIComponent(trimmed)}`,
            { method: 'PUT' },
          );
          if (!res.ok) {
            // eslint-disable-next-line no-console
            console.warn(`audio-suite profile save rejected: ${res.status}`);
            return;
          }
        } catch (err) {
          // eslint-disable-next-line no-console
          console.warn('audio-suite profile save threw', err);
        }
        await get().loadProfiles();
      },

      applyProfile: async (name) => {
        try {
          const res = await fetch(
            `/api/audio-suite/profiles/${encodeURIComponent(name)}/apply`,
            { method: 'POST' },
          );
          if (!res.ok) {
            // eslint-disable-next-line no-console
            console.warn(`audio-suite profile apply rejected: ${res.status}`);
            return;
          }
          // Server returns the new active order; master-bypass + order
          // also arrive via WS broadcast, but adopt the response so the
          // rack updates instantly.
          const body = (await res.json()) as { pluginIds?: string[] };
          if (Array.isArray(body.pluginIds)) set({ chainOrder: body.pluginIds });
          // Master bypass may have changed; refresh it from the server.
          await get().loadMasterBypassFromServer();
        } catch (err) {
          // eslint-disable-next-line no-console
          console.warn('audio-suite profile apply threw', err);
        }
      },

      deleteProfile: async (name) => {
        try {
          await fetch(`/api/audio-suite/profiles/${encodeURIComponent(name)}`, {
            method: 'DELETE',
          });
        } catch (err) {
          // eslint-disable-next-line no-console
          console.warn('audio-suite profile delete threw', err);
        }
        await get().loadProfiles();
      },

      scanVstDirectory: async (directory) => {
        const empty: VstScanResult = {
          ok: false,
          registered: [],
          skipped: [],
          errors: [],
        };
        try {
          const res = await fetch('/api/audio-suite/scan-vst-directory', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ directory }),
          });
          const body = await res.json();
          if (!res.ok) {
            return { ...empty, error: body?.error ?? `scan failed (${res.status})` };
          }
          // New plugins were installed + activated server-side. Re-register
          // their UI panels and refresh the chain order so the rack updates.
          const { reloadInstalledPluginUis } = await import(
            '../plugins/runtime/pluginRuntime'
          );
          await reloadInstalledPluginUis();
          await get().loadChainOrderFromServer();
          return {
            ok: true,
            directory: body.directory,
            registered: body.registered ?? [],
            skipped: body.skipped ?? [],
            errors: body.errors ?? [],
          };
        } catch (err) {
          // eslint-disable-next-line no-console
          console.warn('audio-suite scan-vst-directory threw', err);
          return { ...empty, error: String(err) };
        }
      },

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

      // Processing-mode plumbing (Native in-process chain vs out-of-process
      // VST engine). Server is authoritative; fetched on Audio Suite mount.
      loadProcessingModeFromServer: async () => {
        try {
          const res = await fetch('/api/audio-suite/processing-mode');
          if (!res.ok) return;
          const body = (await res.json()) as {
            mode?: string;
            engineAvailable?: boolean;
            engineActive?: boolean;
          };
          set({
            processingMode: body.mode === 'vst' ? 'vst' : 'native',
            vstEngineAvailable: body.engineAvailable === true,
            vstEngineActive: body.engineActive === true,
          });
        } catch (err) {
          // eslint-disable-next-line no-console
          console.warn('audio-suite processing-mode GET threw', err);
        }
      },

      setProcessingMode: async (mode) => {
        const prev = get().processingMode;
        if (prev === mode) return;
        // Optimistic so the toggle feels instant; reconcile from the response.
        set({ processingMode: mode });
        try {
          const res = await fetch('/api/audio-suite/processing-mode', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ mode }),
          });
          if (!res.ok) {
            set({ processingMode: prev });
            // eslint-disable-next-line no-console
            console.warn(
              `audio-suite processing-mode PUT rejected: ${res.status} ${res.statusText}`,
            );
            return;
          }
          const body = (await res.json()) as {
            mode?: string;
            engineAvailable?: boolean;
            engineActive?: boolean;
          };
          set({
            processingMode: body.mode === 'vst' ? 'vst' : 'native',
            vstEngineAvailable: body.engineAvailable === true,
            vstEngineActive: body.engineActive === true,
          });
        } catch (err) {
          set({ processingMode: prev });
          // eslint-disable-next-line no-console
          console.warn('audio-suite processing-mode PUT threw', err);
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
        collapsed: s.collapsed,
        selectedChainId: s.selectedChainId,
        sidebarCollapsed: s.sidebarCollapsed,
      }),
    },
  ),
);
