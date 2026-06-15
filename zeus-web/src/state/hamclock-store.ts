// SPDX-License-Identifier: GPL-2.0-or-later
//
// HamClock install/run state + "open as a workspace" helper.
//
// The HamClock panel embeds OpenHamClock (MIT, github.com/accius/openhamclock)
// as an <iframe>. OpenHamClock runs as a Zeus-supervised Node sidecar on its
// own port — see HamClockService on the backend. This store mirrors the
// install/run status from GET /api/hamclock/status and drives the
// install/start/stop endpoints.
//
// "Open as a workspace": rather than a floating window, HamClock gets its own
// named layout in the LeftLayoutBar (a single full-bleed hamclock tile), so it
// behaves like any other Zeus workspace. openWorkspace() creates that layout
// on first use (idempotent by name) and switches to it.

import { create } from 'zustand';
import { useLayoutStore } from './layout-store';
import type { WorkspaceLayout } from '../layout/workspace';

/** Name of the auto-created HamClock layout in the LeftLayoutBar. */
export const HAMCLOCK_LAYOUT_NAME = 'HamClock';
/** Panel registry id for the HamClock iframe tile. */
export const HAMCLOCK_PANEL_ID = 'hamclock';
const HAMCLOCK_TILE_UID = 'tile-hamclock';

const HAMCLOCK_WORKSPACE: WorkspaceLayout = {
  schemaVersion: 8,
  tiles: [
    {
      uid: HAMCLOCK_TILE_UID,
      panelId: HAMCLOCK_PANEL_ID,
      x: 0,
      y: 0,
      w: 24,
      h: 48,
    },
  ],
};

/** Mirror of the backend HamClockStatus record. */
export interface HamClockStatus {
  phase:
    | 'NotInstalled'
    | 'Installing'
    | 'Installed'
    | 'Starting'
    | 'Running'
    | 'Error';
  installed: boolean;
  running: boolean;
  busy: boolean;
  port: number;
  version: string | null;
  nodeAvailable: boolean;
  nodeVersion: string | null;
  error: string | null;
  log: string[];
}

const EMPTY_STATUS: HamClockStatus = {
  phase: 'NotInstalled',
  installed: false,
  running: false,
  busy: false,
  port: 0,
  version: null,
  nodeAvailable: false,
  nodeVersion: null,
  error: null,
  log: [],
};

interface HamClockState {
  status: HamClockStatus;
  loadStatus(): Promise<void>;
  install(): Promise<void>;
  start(): Promise<void>;
  stop(): Promise<void>;
  /** Enable the HamClock workspace: ensure the layout exists and switch to it.
   *  Creates it (single full-bleed hamclock tile) on first use. Idempotent. */
  openWorkspace(): void;
  /** Disable the HamClock workspace: remove its layout tab from the
   *  LeftLayoutBar. The sidecar process is left untouched (use Stop for that).
   *  No-op if the workspace isn't enabled. */
  disableWorkspace(): void;
}

/**
 * Build the iframe src for a running HamClock sidecar. HamClock binds all
 * interfaces, so a LAN client loads it from the same host it reached Zeus on;
 * desktop loopback loads it from localhost. Always plain HTTP (HamClock has no
 * TLS) — under an HTTPS Zeus origin the browser blocks this as mixed content
 * (known limitation of the LAN/HTTPS path; desktop is unaffected).
 */
export function hamclockIframeUrl(port: number): string {
  if (!port) return '';
  const host = window.location.hostname || '127.0.0.1';
  return `http://${host}:${port}/`;
}

export const useHamClockStore = create<HamClockState>()((set) => ({
  status: EMPTY_STATUS,

  loadStatus: async () => {
    try {
      const res = await fetch('/api/hamclock/status');
      if (!res.ok) return;
      set({ status: (await res.json()) as HamClockStatus });
    } catch (err) {
      // eslint-disable-next-line no-console
      console.warn('hamclock status GET threw', err);
    }
  },

  install: async () => {
    try {
      const res = await fetch('/api/hamclock/install', { method: 'POST' });
      if (res.ok || res.status === 202) {
        set({ status: (await res.json()) as HamClockStatus });
      }
    } catch (err) {
      // eslint-disable-next-line no-console
      console.warn('hamclock install POST threw', err);
    }
  },

  start: async () => {
    try {
      const res = await fetch('/api/hamclock/start', { method: 'POST' });
      const body = await res.json();
      if (body?.status) set({ status: body.status as HamClockStatus });
    } catch (err) {
      // eslint-disable-next-line no-console
      console.warn('hamclock start POST threw', err);
    }
  },

  stop: async () => {
    try {
      const res = await fetch('/api/hamclock/stop', { method: 'POST' });
      if (res.ok) set({ status: (await res.json()) as HamClockStatus });
    } catch (err) {
      // eslint-disable-next-line no-console
      console.warn('hamclock stop POST threw', err);
    }
  },

  openWorkspace: () => {
    const ls = useLayoutStore.getState();
    const existing = ls.layouts.find((l) => l.name === HAMCLOCK_LAYOUT_NAME);
    if (existing) {
      ls.setActiveLayout(existing.id);
      return;
    }
    // Create the layout already containing the single full-bleed HamClock
    // tile, so the first persisted layout is the correct one.
    ls.addLayout(HAMCLOCK_LAYOUT_NAME, {
      icon: '🕐',
      description: 'OpenHamClock dashboard',
      workspace: HAMCLOCK_WORKSPACE,
    });
  },

  disableWorkspace: () => {
    const ls = useLayoutStore.getState();
    const existing = ls.layouts.find((l) => l.name === HAMCLOCK_LAYOUT_NAME);
    if (existing) ls.removeLayout(existing.id);
  },
}));
