// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN G2 / G2-Ultra hardware front-panel bridge settings store. Backs the
// Radio Settings "Front Panel" card: the master enable, an explicit serial
// device (COM port / device path), and a baud override — plus the live bridge
// status (connected + the device/baud actually opened + the ANDROMEDA panel
// type). Server-authoritative: hydrates from GET /api/radio/front-panel and
// re-syncs on every PUT, never clobbered on connect.
//
// On the G2's internal Pi the defaults auto-detect the panel; on a Windows /
// macOS host the operator points the bridge at the COM port here.

import { create } from 'zustand';

export interface G2PanelSettings {
  /** Master enable. Default ON server-side (auto-detects the panel). */
  enabled: boolean;
  /** Explicit serial device (COM5 / /dev/ttyACM0). Empty = auto-detect. */
  devicePath: string;
  /** Baud override; 0 = auto (9600 for the 8" Mk2 front, 115200 for V1). */
  baud: number;
  /** Live: the bridge has the serial line open. */
  connected: boolean;
  /** Live: the device path the bridge actually opened (may be auto-detected). */
  activeDevicePath: string;
  /** Live: the baud actually in use. */
  activeBaud: number;
  /** Live: ANDROMEDA console type from ZZZS (5 = G2-Ultra). 0 = not identified. */
  panelType: number;
}

const DEFAULTS: G2PanelSettings = {
  enabled: true,
  devicePath: '',
  baud: 0,
  connected: false,
  activeDevicePath: '',
  activeBaud: 0,
  panelType: 0,
};

function parse(raw: unknown): G2PanelSettings {
  const r = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>;
  return {
    enabled: typeof r.enabled === 'boolean' ? r.enabled : true,
    devicePath: typeof r.devicePath === 'string' ? r.devicePath : '',
    baud: typeof r.baud === 'number' ? r.baud : 0,
    connected: typeof r.connected === 'boolean' ? r.connected : false,
    activeDevicePath: typeof r.activeDevicePath === 'string' ? r.activeDevicePath : '',
    activeBaud: typeof r.activeBaud === 'number' ? r.activeBaud : 0,
    panelType: typeof r.panelType === 'number' ? r.panelType : 0,
  };
}

export async function fetchG2PanelSettings(signal?: AbortSignal): Promise<G2PanelSettings> {
  const res = await fetch('/api/radio/front-panel', { signal });
  if (!res.ok) throw new Error(`GET /api/radio/front-panel → ${res.status}`);
  return parse(await res.json());
}

export async function updateG2PanelSettings(
  patch: { enabled?: boolean; devicePath?: string; baud?: number },
  signal?: AbortSignal,
): Promise<G2PanelSettings> {
  const res = await fetch('/api/radio/front-panel', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(patch),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/radio/front-panel → ${res.status}`);
  return parse(await res.json());
}

interface G2PanelState extends G2PanelSettings {
  loaded: boolean;
  inflight: boolean;
  error: string | null;
  /** Hydrate settings + status from the REST snapshot. */
  load: () => Promise<void>;
  /** Update one or more settings (optimistic, server-authoritative). */
  update: (patch: { enabled?: boolean; devicePath?: string; baud?: number }) => Promise<void>;
  __resetForTests: () => void;
}

export const useG2PanelStore = create<G2PanelState>((set, get) => ({
  ...DEFAULTS,
  loaded: false,
  inflight: false,
  error: null,

  load: async () => {
    set({ inflight: true, error: null });
    try {
      const s = await fetchG2PanelSettings();
      set({ ...s, loaded: true, inflight: false });
    } catch (err) {
      set({ error: err instanceof Error ? err.message : String(err), inflight: false });
    }
  },

  update: async (patch) => {
    const prev: G2PanelSettings = {
      enabled: get().enabled,
      devicePath: get().devicePath,
      baud: get().baud,
      connected: get().connected,
      activeDevicePath: get().activeDevicePath,
      activeBaud: get().activeBaud,
      panelType: get().panelType,
    };
    // Optimistic on the operator-settable fields; status fields keep prior
    // values until the server snapshot returns.
    set({ ...patch, inflight: true, error: null });
    try {
      const s = await updateG2PanelSettings(patch);
      set({ ...s, inflight: false });
    } catch (err) {
      set({ ...prev, error: err instanceof Error ? err.message : String(err), inflight: false });
    }
  },

  __resetForTests: () => set({ ...DEFAULTS, loaded: false, inflight: false, error: null }),
}));
