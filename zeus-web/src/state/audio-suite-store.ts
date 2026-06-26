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
//     /api/tx-audio-suite/chain/order REST endpoint and the AudioChainOrder
//     (0x1E) WebSocket broadcast frame. The local store is NOT
//     persisted to localStorage — on reload we fetch fresh from the
//     server to avoid drift if the operator reorders on a second
//     client (or a backend restart loaded a different order).
//   - Preview state: whether the Audio Suite is mixing the full TX-monitor
//     output into the operator's RX playback path. Server state
//     (StateDto.TxMonitorEnabled); store mirrors. Not persisted — defaults
//     off on every fresh boot.
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
import { useTxStore } from './tx-store';
import { uninstallPlugin as apiUninstall } from '../plugins/api/plugins';
import { reloadInstalledPluginUis } from '../plugins/runtime/pluginRuntime';

/** Minimum window dimensions enforced on drag-resize. */
export const AUDIO_SUITE_WINDOW_MIN_WIDTH = 480;
export const AUDIO_SUITE_WINDOW_MIN_HEIGHT = 360;

/**
 * A saved Audio Suite profile — a named snapshot of the processing route
 * (native/VST), chain config (active order + parked set + master bypass),
 * and any VST state blobs captured server-side. Mirrors the server
 * AudioProfileEntry summary.
 */
export interface AudioProfileSummary {
  name: string;
  processingMode: 'native' | 'vst';
  order: string[];
  parked: string[];
  masterBypass: boolean;
  createdUtc: string;
  updatedUtc: string;
}

type AudioProfileSummaryResponse = Omit<AudioProfileSummary, 'processingMode'> & {
  processingMode?: string;
};

interface AudioProfilesResponse {
  profiles?: AudioProfileSummaryResponse[];
  selectedProfile?: string | null;
}

function normalizeAudioProfileSummary(profile: AudioProfileSummaryResponse): AudioProfileSummary {
  return {
    ...profile,
    processingMode: profile.processingMode === 'vst' ? 'vst' : 'native',
  };
}

/** Result of a VST directory scan (POST /api/{tx,rx}-audio-suite/scan-vst-directory). */
export interface VstScanResult {
  ok: boolean;
  error?: string;
  directory?: string;
  registered: Array<{ id: string; name: string }>;
  skipped: Array<{ id: string; name: string }>;
  errors: Array<{ source: string; message: string }>;
}

export type VstScanRoute = 'auto' | 'tx' | 'rx' | 'both';
export type AudioSuiteRoute = 'tx' | 'rx';

/** Result of an AU registry scan (POST /api/{tx,rx}-audio-suite/scan-au). */
export interface AuScanResult {
  ok: boolean;
  error?: string;
  /** False off macOS — the scanner is a no-op there. */
  supported: boolean;
  registered: Array<{ id: string; name: string }>;
  skipped: Array<{ id: string; name: string }>;
  errors: Array<{ source: string; message: string }>;
}

export interface AudioProfileMutationResult {
  ok: boolean;
  error?: string;
}

interface AudioSuiteState {
  // Legacy single-window placement. Kept for localStorage migration and
  // older callers; new UI code uses the per-route placement below.
  isOpen: boolean;
  x: number;
  y: number;
  width: number;
  height: number;
  suiteRoute: AudioSuiteRoute;

  // Independent TX/RX Audio Suite windows. This lets an operator keep the
  // transmit chain and receive VST chain open at the same time.
  txOpen: boolean;
  rxOpen: boolean;
  txX: number;
  txY: number;
  txWidth: number;
  txHeight: number;
  rxX: number;
  rxY: number;
  rxWidth: number;
  rxHeight: number;

  // Chain order — head = first in chain (processes mic first).
  // Mirrored from the server; updated by:
  //   (1) loadChainOrderFromServer() on window open
  //   (2) AudioChainOrder WS broadcast (any client's reorder)
  //   (3) reorderChain() local optimistic update before PUT
  chainOrder: string[];
  rxChainOrder: string[];

  // Preview (full TX-monitor path; server reports whether this host can
  // expose it).
  previewSupported: boolean;
  previewEnabled: boolean;

  // Master bypass — single operator-facing toggle that disengages the
  // entire plugin chain. true = chain inert, mic passes bit-identical
  // to WDSP; false = chain hot. Default on first launch is true; the
  // server (AudioChainMasterBypassService) is the source of truth.
  masterBypassed: boolean;
  rxMasterBypassed: boolean;

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
  rxSelectedChainId: string | null;

  // Whether the plugin-browser sidebar is folded to a thin strip.
  // Presentation-only, persisted to localStorage.
  sidebarCollapsed: boolean;
  rxSidebarCollapsed: boolean;

  // Saved profiles (chain-config snapshots). Server-authoritative;
  // loaded on window open, refreshed after save / delete. Not persisted
  // to localStorage.
  profiles: AudioProfileSummary[];
  profilesLoaded: boolean;
  rxProfiles: AudioProfileSummary[];
  rxProfilesLoaded: boolean;

  // The operator's selected profile in the Audio Suite toolbar. This is
  // presentation state, but persisted so the dropdown stays on the profile
  // the operator chose until they pick another one.
  selectedProfile: string;
  rxSelectedProfile: string;
  favoriteVstIds: string[];

  // Actions
  open(route?: AudioSuiteRoute): void;
  openTx(): void;
  openRx(): void;
  close(): void;
  closeTx(): void;
  closeRx(): void;
  toggle(): void;
  setSuiteRoute(route: AudioSuiteRoute): void;
  setWindowPosition(route: AudioSuiteRoute, x: number, y: number): void;
  setWindowSize(route: AudioSuiteRoute, width: number, height: number): void;
  setPosition(x: number, y: number): void;
  setSize(width: number, height: number): void;
  setDragging(on: boolean): void;

  // Rack collapse plumbing.
  toggleCollapsed(pluginId: string): void;
  setAllCollapsed(collapsed: boolean, pluginIds: string[]): void;
  toggleSidebar(): void;
  toggleSidebarForRoute(route: AudioSuiteRoute): void;

  // Chips+detail selection.
  setSelectedChainId(id: string | null): void;
  setSelectedChainIdForRoute(route: AudioSuiteRoute, id: string | null): void;

  // Profile selection.
  setSelectedProfile(name: string): void;
  setSelectedProfileForRoute(route: AudioSuiteRoute, name: string): void;
  toggleFavoriteVst(pluginId: string): void;

  // Chain membership — park (active=false) / un-park (active=true) an
  // installed plugin. Parking pulls it out of the active chain (stops
  // processing, drops from the rack) without uninstalling; un-parking
  // slots it back in at its canonical position. Server returns the new
  // active order, which becomes chainOrder.
  setChainMembership(pluginId: string, active: boolean): Promise<void>;
  setRxChainMembership(pluginId: string, active: boolean): Promise<void>;

  // Profile plumbing.
  loadProfiles(route?: AudioSuiteRoute): Promise<void>;
  saveProfile(name: string, route?: AudioSuiteRoute): Promise<AudioProfileMutationResult>;
  applyProfile(name: string, route?: AudioSuiteRoute): Promise<AudioProfileMutationResult>;
  deleteProfile(name: string, route?: AudioSuiteRoute): Promise<AudioProfileMutationResult>;

  // Scan a directory for VST3 plugins, register each, and refresh the
  // rack. Returns a summary (or an error string on failure).
  scanVstDirectory(directory: string, route?: VstScanRoute): Promise<VstScanResult>;

  // Scan the OS AudioComponent registry for AUv2 effects (macOS only),
  // register each, and refresh the rack — the AU sibling of
  // scanVstDirectory. No directory: AUs are resolved from the system
  // registry. Returns supported=false (and an empty list) off macOS.
  scanAuComponents(route?: VstScanRoute): Promise<AuScanResult>;

  // Permanently uninstall a plugin (DELETE /api/plugins/{id}) and refresh
  // the Audio Suite so its rack / sidebar panel disappears. Unlike parking
  // (setChainMembership(id, false)), this deletes the plugin from Zeus —
  // used to remove unwanted scanned VSTs. `deferred` is true when the host
  // could only detach it (assembly still loaded) and a restart is needed to
  // delete the files.
  uninstallPlugin(
    pluginId: string,
  ): Promise<{ ok: boolean; deferred: boolean; message?: string; error?: string }>;

  // Chain order plumbing.
  setChainOrderFromServer(ids: string[]): void;
  reorderChain(fromIndex: number, toIndex: number): Promise<void>;
  loadChainOrderFromServer(): Promise<void>;
  setRxChainOrderFromServer(ids: string[]): void;
  reorderRxChain(fromIndex: number, toIndex: number): Promise<void>;
  loadRxChainOrderFromServer(): Promise<void>;

  // Preview plumbing. `meterOnly` (Auto Tune) runs the TX-monitor chain for
  // metering without broadcasting the audible monitor — the operator hears
  // nothing while the sample is captured in the background.
  loadPreviewState(): Promise<void>;
  setPreviewEnabled(enabled: boolean, meterOnly?: boolean): Promise<void>;

  // Master bypass plumbing.
  setMasterBypassedFromServer(bypassed: boolean): void;
  setRxMasterBypassedFromServer(bypassed: boolean): void;
  loadMasterBypassFromServer(): Promise<void>;
  setMasterBypassed(bypassed: boolean): Promise<void>;
  loadRxMasterBypassFromServer(): Promise<void>;
  setRxMasterBypassed(bypassed: boolean): Promise<void>;

  // Audio Suite processing route: 'native' (in-process plugin chain, the
  // default) vs 'vst' (out-of-process VST engine). Mutually exclusive; server
  // is authoritative. vstEngineAvailable = an engine is installed;
  // vstEngineActive = the engine is currently live and routing TX audio.
  processingMode: 'native' | 'vst';
  vstEngineAvailable: boolean;
  vstEngineActive: boolean;
  // True when VST mode is selected and an engine is installed but it keeps
  // crashing on startup (Faulted) rather than just still warming up. Drives the
  // "Repair engine" affordance; the server also auto-repairs once on crash-loop.
  vstEngineCrashLooping: boolean;
  rxVstEngineAvailable: boolean;
  rxVstEngineActive: boolean;
  rxVstActivePlugins: number;
  rxVstDegradedBlocks: number;
  loadProcessingModeFromServer(): Promise<void>;
  loadRxProcessingModeFromServer(): Promise<void>;
  setProcessingMode(mode: 'native' | 'vst'): Promise<void>;

  // "Get VST Engine" provisioning. The out-of-process VST engine is fetched
  // from its upstream release and staged by the server (never bundled). The
  // operator triggers it from the Audio Suite when VST mode is selected but no
  // engine is installed; this store polls the server install status until it
  // finishes, then refreshes the processing-mode so the new engine is picked up.
  // phase lifecycle: idle → downloading → extracting → staging (server) →
  // configuring (client switches to VST mode) → done (installed AND usable).
  vstEngineInstall: {
    phase:
      | 'idle'
      | 'downloading'
      | 'extracting'
      | 'staging'
      | 'configuring'
      | 'done'
      | 'failed';
    percent: number;
    message: string | null;
  };
  // postUrl is an internal seam (install vs repair endpoint); callers use the
  // no-arg form. Default is the install endpoint.
  installVstEngine(postUrl?: string): Promise<void>;
  // Force a re-download of the verified engine, replacing a stale/corrupt or
  // crash-looping binary. Reuses the install status/polling lifecycle.
  repairVstEngine(): Promise<void>;

  // Platform affordance flags, mirrored from the engine-install GET DTO.
  //   engineSupported        — the out-of-process VST engine can be
  //                            downloaded/used (Windows only). When false,
  //                            the "Download VST Engine" button is hidden
  //                            because the installer throws on this OS.
  //   inProcessHostSupported — the native in-process bridges (VST3, and AU
  //                            on macOS) host plugins without any engine
  //                            download. True on every platform.
  //   auSupported            — Audio Units can be scanned/hosted (macOS only).
  // engineSupportLoaded gates first-paint so the panel doesn't flash the
  // wrong affordance before the DTO arrives.
  engineSupported: boolean;
  inProcessHostSupported: boolean;
  auSupported: boolean;
  engineSupportLoaded: boolean;
  loadEngineSupportFromServer(): Promise<void>;
}

type AudioSuitePersistedState = Pick<
  AudioSuiteState,
  | 'isOpen'
  | 'x'
  | 'y'
  | 'width'
  | 'height'
  | 'suiteRoute'
  | 'txOpen'
  | 'rxOpen'
  | 'txX'
  | 'txY'
  | 'txWidth'
  | 'txHeight'
  | 'rxX'
  | 'rxY'
  | 'rxWidth'
  | 'rxHeight'
  | 'collapsed'
  | 'selectedChainId'
  | 'rxSelectedChainId'
  | 'sidebarCollapsed'
  | 'rxSidebarCollapsed'
  | 'selectedProfile'
  | 'rxSelectedProfile'
  | 'favoriteVstIds'
>;

// Default window placement — top-left quadrant, room for plugin panels.
const DEFAULT_X = 80;
const DEFAULT_Y = 80;
const DEFAULT_WIDTH = 860;
const DEFAULT_HEIGHT = 760;
const DEFAULT_RX_X = DEFAULT_X + 44;
const DEFAULT_RX_Y = DEFAULT_Y + 52;

async function profileErrorMessage(res: Response): Promise<string> {
  try {
    const text = await res.text();
    if (text.trim()) return text.trim();
  } catch {
    // Fall through to status text.
  }
  return `${res.status} ${res.statusText}`.trim();
}

export const useAudioSuiteStore = create<AudioSuiteState>()(
  persist(
    (set, get) => ({
      isOpen: false,
      x: DEFAULT_X,
      y: DEFAULT_Y,
      width: DEFAULT_WIDTH,
      height: DEFAULT_HEIGHT,
      suiteRoute: 'tx',
      txOpen: false,
      rxOpen: false,
      txX: DEFAULT_X,
      txY: DEFAULT_Y,
      txWidth: DEFAULT_WIDTH,
      txHeight: DEFAULT_HEIGHT,
      rxX: DEFAULT_RX_X,
      rxY: DEFAULT_RX_Y,
      rxWidth: DEFAULT_WIDTH,
      rxHeight: DEFAULT_HEIGHT,
      chainOrder: [],
      rxChainOrder: [],
      previewSupported: false,
      previewEnabled: useTxStore.getState().txMonitorEnabled,
      // Default to true (bypassed) so the UI starts in the inert state
      // that matches the server's first-run default. The server load
      // on Audio Suite window mount overrides this with the persisted
      // value (if any) and any WS broadcast keeps it in sync after.
      masterBypassed: true,
      rxMasterBypassed: true,
      processingMode: 'native',
      vstEngineAvailable: false,
      vstEngineActive: false,
      vstEngineCrashLooping: false,
      rxVstEngineAvailable: false,
      rxVstEngineActive: false,
      rxVstActivePlugins: 0,
      rxVstDegradedBlocks: 0,
      vstEngineInstall: { phase: 'idle', percent: 0, message: null },
      // Default to the Windows shape (engine supported) until the server DTO
      // arrives, so a never-loaded state never accidentally hides the Windows
      // path. engineSupportLoaded keeps the panel from committing to an
      // affordance before that first load resolves.
      engineSupported: true,
      inProcessHostSupported: true,
      auSupported: false,
      engineSupportLoaded: false,
      isDragging: false,
      collapsed: {},
      selectedChainId: null,
      rxSelectedChainId: null,
      sidebarCollapsed: false,
      rxSidebarCollapsed: false,
      profiles: [],
      profilesLoaded: false,
      rxProfiles: [],
      rxProfilesLoaded: false,
      selectedProfile: '',
      rxSelectedProfile: '',
      favoriteVstIds: [],

      open: (route = 'tx') => {
        if (route === 'rx') get().openRx();
        else get().openTx();
      },
      openTx: () =>
        set((s) => ({
          isOpen: true,
          txOpen: true,
          suiteRoute: 'tx',
          x: s.txX,
          y: s.txY,
          width: s.txWidth,
          height: s.txHeight,
        })),
      openRx: () =>
        set((s) => ({
          isOpen: true,
          rxOpen: true,
          suiteRoute: 'rx',
          x: s.rxX,
          y: s.rxY,
          width: s.rxWidth,
          height: s.rxHeight,
        })),
      close: () => set({ isOpen: false, txOpen: false, rxOpen: false }),
      closeTx: () =>
        set((s) => ({
          txOpen: false,
          isOpen: s.rxOpen,
          suiteRoute: s.rxOpen ? 'rx' : s.suiteRoute,
        })),
      closeRx: () =>
        set((s) => ({
          rxOpen: false,
          isOpen: s.txOpen,
          suiteRoute: s.txOpen ? 'tx' : s.suiteRoute,
        })),
      toggle: () => {
        if (get().txOpen) get().closeTx();
        else get().openTx();
      },
      setSuiteRoute: (route) =>
        set((s) =>
          route === 'rx'
            ? {
                suiteRoute: 'rx',
                isOpen: true,
                rxOpen: true,
                x: s.rxX,
                y: s.rxY,
                width: s.rxWidth,
                height: s.rxHeight,
              }
            : {
                suiteRoute: 'tx',
                isOpen: true,
                txOpen: true,
                x: s.txX,
                y: s.txY,
                width: s.txWidth,
                height: s.txHeight,
              },
        ),

      setWindowPosition: (route, x, y) =>
        set((s) =>
          route === 'rx'
            ? {
                rxX: x,
                rxY: y,
                ...(s.suiteRoute === 'rx' ? { x, y } : {}),
              }
            : {
                txX: x,
                txY: y,
                ...(s.suiteRoute === 'tx' ? { x, y } : {}),
              },
        ),
      setWindowSize: (route, width, height) => {
        const nextWidth = Math.max(AUDIO_SUITE_WINDOW_MIN_WIDTH, width);
        const nextHeight = Math.max(AUDIO_SUITE_WINDOW_MIN_HEIGHT, height);
        set((s) =>
          route === 'rx'
            ? {
                rxWidth: nextWidth,
                rxHeight: nextHeight,
                ...(s.suiteRoute === 'rx'
                  ? { width: nextWidth, height: nextHeight }
                  : {}),
              }
            : {
                txWidth: nextWidth,
                txHeight: nextHeight,
                ...(s.suiteRoute === 'tx'
                  ? { width: nextWidth, height: nextHeight }
                  : {}),
              },
        );
      },

      setPosition: (x, y) => get().setWindowPosition(get().suiteRoute, x, y),
      setSize: (width, height) =>
        get().setWindowSize(get().suiteRoute, width, height),
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
      toggleSidebarForRoute: (route) =>
        set((s) =>
          route === 'rx'
            ? { rxSidebarCollapsed: !s.rxSidebarCollapsed }
            : { sidebarCollapsed: !s.sidebarCollapsed },
        ),

      setSelectedChainId: (id) => set({ selectedChainId: id }),
      setSelectedChainIdForRoute: (route, id) =>
        set(route === 'rx' ? { rxSelectedChainId: id } : { selectedChainId: id }),

      setSelectedProfile: (name) => set({ selectedProfile: name.trim() }),
      setSelectedProfileForRoute: (route, name) =>
        set(route === 'rx' ? { rxSelectedProfile: name.trim() } : { selectedProfile: name.trim() }),
      toggleFavoriteVst: (pluginId) =>
        set((s) => ({
          favoriteVstIds: s.favoriteVstIds.includes(pluginId)
            ? s.favoriteVstIds.filter((id) => id !== pluginId)
            : [...s.favoriteVstIds, pluginId],
        })),

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
            `/api/tx-audio-suite/plugins/${encodeURIComponent(pluginId)}/chain-membership`,
            {
              method: 'PUT',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ active }),
            },
          );
          if (!res.ok) {
            set({ chainOrder: prev });

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

          console.warn('audio-suite chain-membership PUT threw', err);
        }
      },

      setRxChainMembership: async (pluginId, active) => {
        const prev = get().rxChainOrder;
        if (!active) {
          set({ rxChainOrder: prev.filter((id) => id !== pluginId) });
        }
        try {
          const res = await fetch(
            `/api/rx-audio-suite/plugins/${encodeURIComponent(pluginId)}/chain-membership`,
            {
              method: 'PUT',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ active }),
            },
          );
          if (!res.ok) {
            set({ rxChainOrder: prev });

            console.warn(
              `rx-audio-suite chain-membership PUT rejected: ${res.status} ${res.statusText}`,
            );
            return;
          }
          const body = (await res.json()) as { pluginIds?: string[] };
          if (Array.isArray(body.pluginIds)) {
            set({ rxChainOrder: body.pluginIds });
          }
          await get().loadRxProcessingModeFromServer();
        } catch (err) {
          set({ rxChainOrder: prev });

          console.warn('rx-audio-suite chain-membership PUT threw', err);
        }
      },

      loadProfiles: async (route = 'tx') => {
        try {
          const res = await fetch(`/api/${route}-audio-suite/profiles`);
          if (!res.ok) return;
          const body = (await res.json()) as AudioProfilesResponse;
          if (Array.isArray(body.profiles)) {
            const nextProfiles = body.profiles.map(normalizeAudioProfileSummary);
            const serverSelected =
              typeof body.selectedProfile === 'string'
                ? body.selectedProfile.trim()
                : '';
            const profileExists = (name: string) =>
              nextProfiles.some((p) => p.name === name);
            const selectedFromServer =
              serverSelected && profileExists(serverSelected)
                ? serverSelected
                : '';
            set((s) =>
              route === 'rx'
                ? {
                    rxProfiles: nextProfiles,
                    rxProfilesLoaded: true,
                    rxSelectedProfile:
                      selectedFromServer ||
                      (s.rxSelectedProfile && profileExists(s.rxSelectedProfile)
                        ? s.rxSelectedProfile
                        : ''),
                  }
                : {
                    profiles: nextProfiles,
                    profilesLoaded: true,
                    selectedProfile:
                      s.selectedProfile &&
                      !nextProfiles.some((p) => p.name === s.selectedProfile)
                        ? ''
                        : s.selectedProfile,
                  },
            );
          }
        } catch (err) {

          console.warn(`${route}-audio-suite profiles GET threw`, err);
        }
      },

      saveProfile: async (name, route = 'tx') => {
        const trimmed = name.trim();
        if (!trimmed) return { ok: false, error: 'Profile name is required.' };
        try {
          const res = await fetch(
            `/api/${route}-audio-suite/profiles/${encodeURIComponent(trimmed)}`,
            { method: 'PUT' },
          );
          if (!res.ok) {
            const error = await profileErrorMessage(res);

            console.warn(`${route}-audio-suite profile save rejected: ${error}`);
            return { ok: false, error };
          }
        } catch (err) {

          console.warn(`${route}-audio-suite profile save threw`, err);
          return { ok: false, error: err instanceof Error ? err.message : String(err) };
        }
        await get().loadProfiles(route);
        return { ok: true };
      },

      applyProfile: async (name, route = 'tx') => {
        const trimmed = name.trim();
        if (!trimmed) return { ok: false, error: 'Profile name is required.' };
        try {
          const res = await fetch(
            `/api/${route}-audio-suite/profiles/${encodeURIComponent(trimmed)}/apply`,
            { method: 'POST' },
          );
          if (!res.ok) {
            const error = await profileErrorMessage(res);

            console.warn(`${route}-audio-suite profile apply rejected: ${error}`);
            return { ok: false, error };
          }
          const body = (await res.json()) as {
            pluginIds?: string[];
            processingMode?: string;
            engineAvailable?: boolean;
            engineActive?: boolean;
            activePlugins?: number;
            degradedBlocks?: number;
            masterBypass?: boolean;
          };

          if (route === 'rx') {
            if (Array.isArray(body.pluginIds)) set({ rxChainOrder: body.pluginIds });
            set({
              rxSelectedProfile: trimmed,
              rxVstEngineAvailable: body.engineAvailable === true,
              rxVstEngineActive: body.engineActive === true,
              rxVstActivePlugins:
                typeof body.activePlugins === 'number' ? body.activePlugins : 0,
              rxVstDegradedBlocks:
                typeof body.degradedBlocks === 'number' ? body.degradedBlocks : 0,
            });
            if (typeof body.masterBypass === 'boolean') {
              set({ rxMasterBypassed: body.masterBypass });
            } else {
              await get().loadRxMasterBypassFromServer();
            }
          } else {
            if (Array.isArray(body.pluginIds)) set({ chainOrder: body.pluginIds });
            if (body.processingMode === 'vst' || body.processingMode === 'native') {
              set({
                processingMode: body.processingMode,
                vstEngineAvailable: body.engineAvailable === true,
                vstEngineActive: body.engineActive === true,
              });
            }
            if (typeof body.masterBypass === 'boolean') {
              set({ masterBypassed: body.masterBypass });
            }
            set({ selectedProfile: trimmed });
            if (
              body.processingMode !== 'vst' &&
              body.processingMode !== 'native'
            ) {
              await get().loadProcessingModeFromServer();
            }
            if (typeof body.masterBypass !== 'boolean') {
              await get().loadMasterBypassFromServer();
            }
          }
          return { ok: true };
        } catch (err) {

          console.warn(`${route}-audio-suite profile apply threw`, err);
          return { ok: false, error: err instanceof Error ? err.message : String(err) };
        }
      },

      deleteProfile: async (name, route = 'tx') => {
        try {
          const res = await fetch(`/api/${route}-audio-suite/profiles/${encodeURIComponent(name)}`, {
            method: 'DELETE',
          });
          if (!res.ok) {
            const error = await profileErrorMessage(res);

            console.warn(`${route}-audio-suite profile delete rejected: ${error}`);
            return { ok: false, error };
          }
        } catch (err) {

          console.warn(`${route}-audio-suite profile delete threw`, err);
          return { ok: false, error: err instanceof Error ? err.message : String(err) };
        }
        if (route === 'rx') {
          if (get().rxSelectedProfile === name) {
            set({ rxSelectedProfile: '' });
          }
        } else if (get().selectedProfile === name) {
          set({ selectedProfile: '' });
        }
        await get().loadProfiles(route);
        return { ok: true };
      },

      scanVstDirectory: async (directory, route = 'auto') => {
        const empty: VstScanResult = {
          ok: false,
          registered: [],
          skipped: [],
          errors: [],
        };
        try {
          const scanUrl =
            route === 'tx'
              ? '/api/tx-audio-suite/scan-vst-directory'
              : route === 'rx'
                ? '/api/rx-audio-suite/scan-vst-directory'
                : '/api/audio-suite/scan-vst-directory';
          const res = await fetch(scanUrl, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ directory, route }),
          });
          const body = await res.json();
          if (!res.ok) {
            return { ...empty, error: body?.error ?? `scan failed (${res.status})` };
          }
          // New plugins were installed server-side. Re-register their UI
          // panels and refresh both chain orders so TX/RX parked racks update.
          await reloadInstalledPluginUis();
          await get().loadChainOrderFromServer();
          await get().loadRxChainOrderFromServer();
          await get().loadRxProcessingModeFromServer();
          return {
            ok: true,
            directory: body.directory,
            registered: body.registered ?? [],
            skipped: body.skipped ?? [],
            errors: body.errors ?? [],
          };
        } catch (err) {

          console.warn('audio-suite scan-vst-directory threw', err);
          return { ...empty, error: String(err) };
        }
      },

      scanAuComponents: async (route = 'auto') => {
        const empty: AuScanResult = {
          ok: false,
          supported: false,
          registered: [],
          skipped: [],
          errors: [],
        };
        try {
          const scanUrl =
            route === 'tx'
              ? '/api/tx-audio-suite/scan-au'
              : route === 'rx'
                ? '/api/rx-audio-suite/scan-au'
                : '/api/audio-suite/scan-au';
          const res = await fetch(scanUrl, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ route }),
          });
          const body = await res.json();
          if (!res.ok) {
            return { ...empty, error: body?.error ?? `AU scan failed (${res.status})` };
          }
          // Newly-registered AUs are parked server-side; re-register their UI
          // panels and refresh both chain orders so TX/RX racks update.
          await reloadInstalledPluginUis();
          await get().loadChainOrderFromServer();
          await get().loadRxChainOrderFromServer();
          await get().loadRxProcessingModeFromServer();
          return {
            ok: true,
            supported: body.supported === true,
            registered: body.registered ?? [],
            skipped: body.skipped ?? [],
            errors: body.errors ?? [],
          };
        } catch (err) {

          console.warn('audio-suite scan-au threw', err);
          return { ...empty, error: String(err) };
        }
      },

      uninstallPlugin: async (pluginId) => {
        try {
          const result = await apiUninstall(pluginId);
          // The server has already detached the plugin from the chain
          // (ChainOrderService.OnPluginDetached). Re-register the UI panels
          // (which now prunes the gone plugin's panel) and re-pull the chain
          // order so the rack + sidebar update without a page reload.
          await reloadInstalledPluginUis();
          await get().loadChainOrderFromServer();
          await get().loadRxChainOrderFromServer();
          // If the detail pane was showing this plugin, drop the selection so
          // it falls back to the first remaining chain plugin.
          if (get().selectedChainId === pluginId) {
            set({ selectedChainId: null });
          }
          if (get().rxSelectedChainId === pluginId) {
            set({ rxSelectedChainId: null });
          }
          await get().loadRxProcessingModeFromServer();
          return {
            ok: true,
            deferred: result.status === 202,
            message: result.message,
          };
        } catch (err) {

          console.warn('audio-suite uninstall threw', err);
          return { ok: false, deferred: false, error: String(err) };
        }
      },

      setChainOrderFromServer: (ids) => set({ chainOrder: ids }),
      setRxChainOrderFromServer: (ids) => set({ rxChainOrder: ids }),

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
          const res = await fetch('/api/tx-audio-suite/chain/order', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ pluginIds: next }),
          });
          if (!res.ok) {
            // Roll back on server-side validation failure (e.g. set
            // membership changed between our GET and PUT).
            set({ chainOrder: current });

            console.warn(
              `audio-suite chain-order PUT rejected: ${res.status} ${res.statusText}`,
            );
          }
        } catch (err) {
          set({ chainOrder: current });

          console.warn('audio-suite chain-order PUT threw', err);
        }
      },

      loadChainOrderFromServer: async () => {
        try {
          const res = await fetch('/api/tx-audio-suite/chain/order');
          if (!res.ok) return;
          const body = (await res.json()) as { pluginIds?: string[] };
          if (Array.isArray(body.pluginIds)) {
            set({ chainOrder: body.pluginIds });
          }
        } catch (err) {

          console.warn('audio-suite chain-order GET threw', err);
        }
      },

      reorderRxChain: async (fromIndex, toIndex) => {
        const current = get().rxChainOrder;
        if (
          fromIndex < 0 ||
          fromIndex >= current.length ||
          toIndex < 0 ||
          toIndex >= current.length ||
          fromIndex === toIndex
        ) {
          return;
        }
        const next = current.slice();
        const moved = next.splice(fromIndex, 1)[0]!;
        next.splice(toIndex, 0, moved);
        set({ rxChainOrder: next });

        try {
          const res = await fetch('/api/rx-audio-suite/chain/order', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ pluginIds: next }),
          });
          if (!res.ok) {
            set({ rxChainOrder: current });

            console.warn(
              `rx-audio-suite chain-order PUT rejected: ${res.status} ${res.statusText}`,
            );
            return;
          }
          await get().loadRxProcessingModeFromServer();
        } catch (err) {
          set({ rxChainOrder: current });

          console.warn('rx-audio-suite chain-order PUT threw', err);
        }
      },

      loadRxChainOrderFromServer: async () => {
        try {
          const res = await fetch('/api/rx-audio-suite/chain/order');
          if (!res.ok) return;
          const body = (await res.json()) as { pluginIds?: string[] };
          if (Array.isArray(body.pluginIds)) {
            set({ rxChainOrder: body.pluginIds });
          }
        } catch (err) {

          console.warn('rx-audio-suite chain-order GET threw', err);
        }
      },

      loadPreviewState: async () => {
        try {
          const res = await fetch('/api/tx-audio-suite/preview');
          if (!res.ok) {
            set({ previewSupported: false, previewEnabled: false });
            return;
          }
          const body = (await res.json()) as {
            supported?: boolean;
            enabled?: boolean;
          };
          set({
            previewSupported: body.supported ?? false,
            previewEnabled: body.enabled ?? false,
          });
          useTxStore.getState().setTxMonitorEnabled(body.enabled ?? false);
        } catch {
          set({ previewSupported: false, previewEnabled: false });
          useTxStore.getState().setTxMonitorEnabled(false);
        }
      },

      setPreviewEnabled: async (enabled, meterOnly = false) => {
        const prev = get().previewEnabled;
        // Optimistic update so the toggle feels instant.
        set({ previewEnabled: enabled });
        useTxStore.getState().setTxMonitorEnabled(enabled);
        try {
          const res = await fetch('/api/tx-audio-suite/preview', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ enabled, meterOnly }),
          });
          if (!res.ok) {
            set({ previewEnabled: prev });
            useTxStore.getState().setTxMonitorEnabled(prev);

            console.warn(
              `audio-suite preview PUT rejected: ${res.status} ${res.statusText}`,
            );
            return;
          }
          const body = (await res.json()) as { enabled?: boolean };
          const serverEnabled = body.enabled ?? enabled;
          set({ previewEnabled: serverEnabled });
          useTxStore.getState().setTxMonitorEnabled(serverEnabled);
        } catch (err) {
          set({ previewEnabled: prev });
          useTxStore.getState().setTxMonitorEnabled(prev);

          console.warn('audio-suite preview PUT threw', err);
        }
      },

      setMasterBypassedFromServer: (bypassed) => set({ masterBypassed: bypassed }),
      setRxMasterBypassedFromServer: (bypassed) =>
        set({ rxMasterBypassed: bypassed }),

      loadMasterBypassFromServer: async () => {
        try {
          const res = await fetch('/api/tx-audio-suite/master-bypass');
          if (!res.ok) return;
          const body = (await res.json()) as { bypassed?: boolean };
          if (typeof body.bypassed === 'boolean') {
            set({ masterBypassed: body.bypassed });
          }
        } catch (err) {

          console.warn('audio-suite master-bypass GET threw', err);
        }
      },

      setMasterBypassed: async (bypassed) => {
        const prev = get().masterBypassed;
        if (prev === bypassed) return;
        // Optimistic update so the toggle feels instant.
        set({ masterBypassed: bypassed });
        try {
          const res = await fetch('/api/tx-audio-suite/master-bypass', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ bypassed }),
          });
          if (!res.ok) {
            set({ masterBypassed: prev });

            console.warn(
              `audio-suite master-bypass PUT rejected: ${res.status} ${res.statusText}`,
            );
          }
        } catch (err) {
          set({ masterBypassed: prev });

          console.warn('audio-suite master-bypass PUT threw', err);
        }
      },

      loadRxMasterBypassFromServer: async () => {
        try {
          const res = await fetch('/api/rx-audio-suite/master-bypass');
          if (!res.ok) return;
          const body = (await res.json()) as { bypassed?: boolean };
          if (typeof body.bypassed === 'boolean') {
            set({ rxMasterBypassed: body.bypassed });
          }
        } catch (err) {

          console.warn('rx-audio-suite master-bypass GET threw', err);
        }
      },

      setRxMasterBypassed: async (bypassed) => {
        const prev = get().rxMasterBypassed;
        if (prev === bypassed) return;
        set({ rxMasterBypassed: bypassed });
        try {
          const res = await fetch('/api/rx-audio-suite/master-bypass', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ bypassed }),
          });
          if (!res.ok) {
            set({ rxMasterBypassed: prev });

            console.warn(
              `rx-audio-suite master-bypass PUT rejected: ${res.status} ${res.statusText}`,
            );
          }
        } catch (err) {
          set({ rxMasterBypassed: prev });

          console.warn('rx-audio-suite master-bypass PUT threw', err);
        }
      },

      // Processing-mode plumbing (Native in-process chain vs out-of-process
      // VST engine). Server is authoritative; fetched on Audio Suite mount.
      loadProcessingModeFromServer: async () => {
        try {
          const res = await fetch('/api/tx-audio-suite/processing-mode');
          if (!res.ok) return;
          const body = (await res.json()) as {
            mode?: string;
            engineAvailable?: boolean;
            engineActive?: boolean;
            engineCrashLooping?: boolean;
          };
          set({
            processingMode: body.mode === 'vst' ? 'vst' : 'native',
            vstEngineAvailable: body.engineAvailable === true,
            vstEngineActive: body.engineActive === true,
            vstEngineCrashLooping: body.engineCrashLooping === true,
          });
        } catch (err) {

          console.warn('audio-suite processing-mode GET threw', err);
        }
      },

      loadRxProcessingModeFromServer: async () => {
        try {
          const res = await fetch('/api/rx-audio-suite/processing-mode');
          if (!res.ok) return;
          const body = (await res.json()) as {
            engineAvailable?: boolean;
            engineActive?: boolean;
            activePlugins?: number;
            degradedBlocks?: number;
          };
          set({
            rxVstEngineAvailable: body.engineAvailable === true,
            rxVstEngineActive: body.engineActive === true,
            rxVstActivePlugins:
              typeof body.activePlugins === 'number' ? body.activePlugins : 0,
            rxVstDegradedBlocks:
              typeof body.degradedBlocks === 'number' ? body.degradedBlocks : 0,
          });
        } catch (err) {

          console.warn('rx-audio-suite processing-mode GET threw', err);
        }
      },

      setProcessingMode: async (mode) => {
        const prev = get().processingMode;
        if (prev === mode) return;
        // Optimistic so the toggle feels instant; reconcile from the response.
        set({ processingMode: mode });
        try {
          const res = await fetch('/api/tx-audio-suite/processing-mode', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ mode }),
          });
          if (!res.ok) {
            set({ processingMode: prev });

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

          console.warn('audio-suite processing-mode PUT threw', err);
        }
      },

      installVstEngine: async (postUrl = '/api/tx-audio-suite/vst-engine/install') => {
        const phase = get().vstEngineInstall.phase;
        if (phase === 'downloading' || phase === 'extracting' || phase === 'staging') {
          return; // already running
        }
        set({ vstEngineInstall: { phase: 'downloading', percent: 0, message: 'Starting…' } });
        try {
          const res = await fetch(postUrl, {
            method: 'POST',
          });
          if (!res.ok) {
            set({
              vstEngineInstall: {
                phase: 'failed',
                percent: 0,
                message: `Install request rejected (${res.status}).`,
              },
            });
            return;
          }

          // Poll the server install status until it finishes. The engine
          // download is multi-MB, so allow a generous ceiling before giving up
          // on the poll loop (the server keeps working regardless).
          for (let i = 0; i < 600; i += 1) {
            await new Promise((r) => setTimeout(r, 1000));
            let body: {
              phase?: string;
              percent?: number;
              message?: string | null;
            };
            try {
              const poll = await fetch('/api/tx-audio-suite/vst-engine/install');
              if (!poll.ok) continue;
              body = await poll.json();
            } catch {
              continue;
            }
            const p = (body.phase ?? 'idle') as
              | 'idle'
              | 'downloading'
              | 'extracting'
              | 'staging'
              | 'done'
              | 'failed';
            if (p === 'done') {
              // Configure: switch the TX route to VST so the freshly-staged
              // engine activates and audio runs through it — the operator gets
              // working VST without a second click. A direct PUT (not
              // setProcessingMode, which short-circuits when the mode is
              // unchanged) so the server re-activates even if VST was already
              // the selected route when the engine was missing.
              set({
                vstEngineInstall: {
                  phase: 'configuring',
                  percent: 100,
                  message: 'Configuring VST mode…',
                },
              });
              try {
                const put = await fetch('/api/tx-audio-suite/processing-mode', {
                  method: 'PUT',
                  headers: { 'Content-Type': 'application/json' },
                  body: JSON.stringify({ mode: 'vst' }),
                });
                if (put.ok) {
                  const pm = (await put.json()) as {
                    mode?: string;
                    engineAvailable?: boolean;
                    engineActive?: boolean;
                  };
                  set({
                    processingMode: pm.mode === 'vst' ? 'vst' : 'native',
                    vstEngineAvailable: pm.engineAvailable === true,
                    vstEngineActive: pm.engineActive === true,
                  });
                } else {
                  await get().loadProcessingModeFromServer();
                }
              } catch {
                await get().loadProcessingModeFromServer();
              }
              set({
                vstEngineInstall: {
                  phase: 'done',
                  percent: 100,
                  message: 'VST engine ready — TX audio now routes through VST.',
                },
              });
              return;
            }
            set({
              vstEngineInstall: {
                phase: p,
                percent: typeof body.percent === 'number' ? body.percent : 0,
                message: body.message ?? null,
              },
            });
            if (p === 'failed') return;
          }
        } catch (err) {
          set({
            vstEngineInstall: {
              phase: 'failed',
              percent: 0,
              message: 'Install failed (network?).',
            },
          });
          console.warn('vst-engine install threw', err);
        }
      },

      repairVstEngine: async () => {
        await get().installVstEngine('/api/tx-audio-suite/vst-engine/repair');
      },

      // Read the platform affordance flags from the engine-install GET DTO.
      // Server is authoritative; fetched on Audio Tools mount. On failure the
      // flags keep their last (Windows-shaped) defaults — fail safe toward
      // showing the engine path rather than hiding a working affordance.
      loadEngineSupportFromServer: async () => {
        try {
          const res = await fetch('/api/tx-audio-suite/vst-engine/install');
          if (!res.ok) return;
          const body = (await res.json()) as {
            engineSupported?: boolean;
            inProcessHostSupported?: boolean;
            auSupported?: boolean;
          };
          set({
            engineSupported: body.engineSupported !== false,
            inProcessHostSupported: body.inProcessHostSupported !== false,
            auSupported: body.auSupported === true,
            engineSupportLoaded: true,
          });
        } catch (err) {

          console.warn('audio-suite engine-support GET threw', err);
        }
      },
    }),
    {
      name: 'zeus-audio-suite',
      version: 1,
      migrate: (persisted) => {
        const state = persisted as Partial<AudioSuitePersistedState>;
        const suiteRoute: AudioSuiteRoute = state.suiteRoute === 'rx' ? 'rx' : 'tx';
        let txOpen = state.txOpen === true;
        let rxOpen = state.rxOpen === true;
        if (state.isOpen === true && !txOpen && !rxOpen) {
          if (suiteRoute === 'rx') rxOpen = true;
          else txOpen = true;
        }

        const legacyX = typeof state.x === 'number' ? state.x : DEFAULT_X;
        const legacyY = typeof state.y === 'number' ? state.y : DEFAULT_Y;
        const legacyWidth = typeof state.width === 'number' ? state.width : DEFAULT_WIDTH;
        const legacyHeight = typeof state.height === 'number' ? state.height : DEFAULT_HEIGHT;
        const txX =
          typeof state.txX === 'number'
            ? state.txX
            : suiteRoute === 'tx'
              ? legacyX
              : DEFAULT_X;
        const txY =
          typeof state.txY === 'number'
            ? state.txY
            : suiteRoute === 'tx'
              ? legacyY
              : DEFAULT_Y;
        const txWidth =
          typeof state.txWidth === 'number'
            ? state.txWidth
            : suiteRoute === 'tx'
              ? legacyWidth
              : DEFAULT_WIDTH;
        const txHeight =
          typeof state.txHeight === 'number'
            ? state.txHeight
            : suiteRoute === 'tx'
              ? legacyHeight
              : DEFAULT_HEIGHT;
        const rxX =
          typeof state.rxX === 'number'
            ? state.rxX
            : suiteRoute === 'rx'
              ? legacyX
              : DEFAULT_RX_X;
        const rxY =
          typeof state.rxY === 'number'
            ? state.rxY
            : suiteRoute === 'rx'
              ? legacyY
              : DEFAULT_RX_Y;
        const rxWidth =
          typeof state.rxWidth === 'number'
            ? state.rxWidth
            : suiteRoute === 'rx'
              ? legacyWidth
              : DEFAULT_WIDTH;
        const rxHeight =
          typeof state.rxHeight === 'number'
            ? state.rxHeight
            : suiteRoute === 'rx'
              ? legacyHeight
              : DEFAULT_HEIGHT;

        return {
          isOpen: txOpen || rxOpen,
          suiteRoute,
          x: suiteRoute === 'rx' ? rxX : txX,
          y: suiteRoute === 'rx' ? rxY : txY,
          width: suiteRoute === 'rx' ? rxWidth : txWidth,
          height: suiteRoute === 'rx' ? rxHeight : txHeight,
          txOpen,
          rxOpen,
          txX,
          txY,
          txWidth,
          txHeight,
          rxX,
          rxY,
          rxWidth,
          rxHeight,
          collapsed: state.collapsed ?? {},
          selectedChainId: state.selectedChainId ?? null,
          rxSelectedChainId: state.rxSelectedChainId ?? null,
          sidebarCollapsed: state.sidebarCollapsed === true,
          rxSidebarCollapsed: state.rxSidebarCollapsed === true,
          selectedProfile: state.selectedProfile ?? '',
          rxSelectedProfile: state.rxSelectedProfile ?? '',
          favoriteVstIds: Array.isArray(state.favoriteVstIds)
            ? state.favoriteVstIds.filter((id): id is string => typeof id === 'string')
            : [],
        } satisfies AudioSuitePersistedState;
      },
      // Persist only presentation state. Chain order and preview state
      // come from the server on every mount.
      partialize: (s) => ({
        isOpen: s.isOpen,
        x: s.x,
        y: s.y,
        width: s.width,
        height: s.height,
        suiteRoute: s.suiteRoute,
        txOpen: s.txOpen,
        rxOpen: s.rxOpen,
        txX: s.txX,
        txY: s.txY,
        txWidth: s.txWidth,
        txHeight: s.txHeight,
        rxX: s.rxX,
        rxY: s.rxY,
        rxWidth: s.rxWidth,
        rxHeight: s.rxHeight,
        collapsed: s.collapsed,
        selectedChainId: s.selectedChainId,
        rxSelectedChainId: s.rxSelectedChainId,
        sidebarCollapsed: s.sidebarCollapsed,
        rxSidebarCollapsed: s.rxSidebarCollapsed,
        selectedProfile: s.selectedProfile,
        rxSelectedProfile: s.rxSelectedProfile,
        favoriteVstIds: s.favoriteVstIds,
      }),
    },
  ),
);

useTxStore.subscribe((state, prev) => {
  if (state.txMonitorEnabled === prev.txMonitorEnabled) return;
  useAudioSuiteStore.setState({ previewEnabled: state.txMonitorEnabled });
});
