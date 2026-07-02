// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus Digital plugin gate + transport. The FT8/FT4 mode buttons light up ONLY
// when the com.kb2uka.digital backend plugin is BOTH:
//
//   installed — its id appears in the installed-plugin list (plugins-store),
//   live      — GET /api/plugins/com.kb2uka.digital/status answers 2xx, i.e.
//               its routes were mapped THIS boot (a fresh install needs one
//               restart; a shut-down instance answers 503 and stays grey).
//
// The probe re-runs at boot (main.tsx), on every app-WS reconnect (the server
// may have restarted under the open tab) and whenever the installed list
// changes (install/uninstall). While the gate is open this module also owns
// the plugin's SSE event stream (decodes / spots / TX status — the old
// 0x38/0x39/0x3A WS frames) and the config SYNC PUSHES: operator identity,
// spotting config and the WSJT-X live-decode subset are re-pushed on every
// SSE open (nothing is replayed across a gap) and on their own store changes.

import { create } from 'zustand';
import {
  DIGITAL_PLUGIN_BASE,
  DIGITAL_PLUGIN_ID,
  openDigitalEvents,
  postDigitalIdentity,
  postDigitalWsjtxLive,
  probeDigitalPlugin,
  type DigitalWsjtxLiveConfig,
} from '../api/digital-plugin';
import { usePluginsStore } from '../plugins/state/plugins-store';
import { useDisplayStore } from './display-store';
import { useOperatorStore } from './operator-store';
import { useSpottingStore } from './spotting-store';
import { useWsjtxStore } from './wsjtx-store';
import { useFt8Store, type Ft8DecodeBatch } from './ft8-store';
import { useWsprStore, type WsprSpotBatch } from './wspr-store';
import { useFt8TxStore, type Ft8TxStatus } from './ft8-tx-store';

interface DigitalPluginState {
  /** com.kb2uka.digital appears in the installed-plugin list. */
  installed: boolean;
  /** GET /status answered 2xx — routes are mapped this boot. */
  live: boolean;
  /** First liveness probe has completed (success or failure). */
  probed: boolean;
  /** The SSE event stream is currently open (decodes/lamps are flowing). */
  sseConnected: boolean;

  /** Run the liveness probe (GET /status). */
  probe: () => Promise<void>;
  /** Full re-check: refresh the installed list, then probe. Boot + reconnect. */
  refresh: () => Promise<void>;
}

export const useDigitalPluginStore = create<DigitalPluginState>((set) => ({
  installed: false,
  live: false,
  probed: false,
  sseConnected: false,

  probe: async () => {
    const live = await probeDigitalPlugin();
    set({ live, probed: true });
  },

  refresh: async () => {
    await usePluginsStore.getState().refreshInstalled();
    const live = await probeDigitalPlugin();
    set({ live, probed: true });
  },
}));

/** Non-hook read for imperative gates (enterDigital / tooltips). */
export function isDigitalPluginReady(): boolean {
  const s = useDigitalPluginStore.getState();
  return s.installed && s.live;
}

// ---------------------------------------------------------------------------
// Sync pushes — the plugin never reads core state; the core UI pushes it.
// All best-effort: a failed push is retried by the next trigger (SSE open,
// store change, pop-out open). Never throws into a subscriber.
// ---------------------------------------------------------------------------

function pushIdentity(): void {
  const op = useOperatorStore.getState();
  void postDigitalIdentity({ call: op.resolvedCall, grid: op.resolvedGrid }).catch(() => {});
}

function pushSpottingConfig(): void {
  // Hydrate only — never auto-save. The plugin persists its own spotting
  // config; auto re-POSTing here would launder the RESOLVED identity (from
  // /spotting/status) into the plugin's persisted override on every SSE open,
  // so a later identity change could leave stale calls in PSK/WSPRnet uploads.
  // Explicit operator SAVE in the Spotting panel is the only config writer.
  void useSpottingStore.getState().refreshStatus();
}

/** Project the core WSJT-X config onto the plugin's live-decode subset. */
export function wsjtxLiveSubset(cfg: {
  enabled: boolean;
  host: string;
  port: number;
  instanceId: string;
  transport: 'unicast' | 'multicast';
  multicastGroup: string;
  multicastTtl: number;
  sendLiveDecodes: boolean;
}): DigitalWsjtxLiveConfig {
  const multicast = cfg.transport === 'multicast';
  return {
    enabled: cfg.enabled && cfg.sendLiveDecodes,
    host: multicast ? cfg.multicastGroup : cfg.host,
    port: cfg.port,
    multicast,
    instanceId: cfg.instanceId,
    multicastTtl: cfg.multicastTtl,
  };
}

function pushWsjtxLive(): void {
  const { config, status } = useWsjtxStore.getState();
  // Until the core /api/wsjtx/status hydrate lands we only hold defaults —
  // nothing authoritative to push.
  if (status == null) return;
  void postDigitalWsjtxLive(wsjtxLiveSubset(config)).catch(() => {});
}

function pushAllConfig(): void {
  pushIdentity();
  pushSpottingConfig();
  pushWsjtxLive();
}

// ---------------------------------------------------------------------------
// SSE lifecycle — open while installed && live, closed otherwise. On EVERY
// open (including auto-reconnects) re-hydrate the REST snapshots and re-push
// config: EventSource replays nothing, so an armed→transmitting→idle cycle in
// a gap would otherwise leave the lamps stale forever.
// ---------------------------------------------------------------------------

const warnedOnce = new Set<string>();
function warnOnce(key: string, msg: string, err?: unknown): void {
  if (warnedOnce.has(key)) return;
  warnedOnce.add(key);
  console.warn(`[digital-plugin] ${msg}`, err ?? '');
}

async function refreshTxStatus(): Promise<void> {
  try {
    const res = await fetch(`${DIGITAL_PLUGIN_BASE}/ft8/tx`);
    if (!res.ok) return;
    const status = (await res.json()) as Ft8TxStatus;
    useFt8TxStore.getState().ingest(status);
  } catch {
    /* best-effort — the stream push corrects it */
  }
}

function onEventsOpen(): void {
  void useFt8Store.getState().refreshStatus();
  void useWsprStore.getState().refreshStatus();
  void refreshTxStatus();
  pushAllConfig();
}

let closeEvents: (() => void) | null = null;

function syncEventStream(): void {
  // jsdom (vitest) has no EventSource; the stream is production-only.
  if (typeof EventSource === 'undefined') return;
  const s = useDigitalPluginStore.getState();
  const want = s.installed && s.live;
  if (want && closeEvents == null) {
    closeEvents = openDigitalEvents({
      onConnectionChange: (connected) => {
        if (useDigitalPluginStore.getState().sseConnected !== connected) {
          useDigitalPluginStore.setState({ sseConnected: connected });
        }
      },
      onOpen: onEventsOpen,
      onFt8Decode: (json) => {
        try {
          useFt8Store.getState().ingest(JSON.parse(json) as Ft8DecodeBatch);
        } catch (err) {
          warnOnce('sse-ft8-decode-parse', 'ft8decode event parse failed', err);
        }
      },
      onWsprSpot: (json) => {
        try {
          useWsprStore.getState().ingest(JSON.parse(json) as WsprSpotBatch);
        } catch (err) {
          warnOnce('sse-wspr-spot-parse', 'wsprspot event parse failed', err);
        }
      },
      onTxStatus: (json) => {
        try {
          useFt8TxStore.getState().ingest(JSON.parse(json) as Ft8TxStatus);
        } catch (err) {
          warnOnce('sse-tx-status-parse', 'txstatus event parse failed', err);
        }
      },
    });
  } else if (!want && closeEvents != null) {
    closeEvents();
    closeEvents = null;
  }
}

// ---------------------------------------------------------------------------
// Wiring — module-scope subscriptions (mirrors the self-hydrating stores).
// ---------------------------------------------------------------------------

if (typeof window !== 'undefined') {
  // Gate input 1: the installed list. Recompute `installed` on every list
  // change and re-probe when the flag flips (install → probe shows not-live
  // until restart; uninstall → probe confirms the routes 404/503).
  usePluginsStore.subscribe((s) => {
    const installed = s.installed.some((p) => p.id === DIGITAL_PLUGIN_ID);
    if (installed !== useDigitalPluginStore.getState().installed) {
      useDigitalPluginStore.setState({ installed });
      void useDigitalPluginStore.getState().probe();
    }
  });

  // Gate input 2: app-WS reconnect. The server may have restarted (with or
  // without the plugin) under the open tab — re-check both halves of the gate.
  let wasConnected = useDisplayStore.getState().connected;
  useDisplayStore.subscribe((s) => {
    if (s.connected && !wasConnected) {
      void useDigitalPluginStore.getState().refresh();
    }
    wasConnected = s.connected;
  });

  // Gate output: manage the SSE stream off installed/live transitions.
  useDigitalPluginStore.subscribe(() => syncEventStream());

  // Identity push: operator identity is core-owned; forward the RESOLVED
  // values (override else QRZ home — what TX/spotting actually use) whenever
  // they change while the plugin is up.
  let opKey = '';
  useOperatorStore.subscribe((s) => {
    const key = `${s.resolvedCall} ${s.resolvedGrid}`;
    if (key === opKey) return;
    opKey = key;
    if (isDigitalPluginReady()) pushIdentity();
  });

  // WSJT-X live-decode push: the core store keeps owning the full config; the
  // plugin only needs the live-decode subset, re-derived on every change.
  let wsjtxKey = '';
  useWsjtxStore.subscribe((s) => {
    const sub = wsjtxLiveSubset(s.config);
    const key = JSON.stringify(sub);
    if (key === wsjtxKey) return;
    wsjtxKey = key;
    if (s.status != null && isDigitalPluginReady()) {
      void postDigitalWsjtxLive(sub).catch(() => {});
    }
  });

  // Pop-out open: push everything once so a workspace opened right after boot
  // (before any store-change trigger) still seeds the plugin. Starts false —
  // nothing can be open before this module loads (deliberately no getState()
  // at module scope: test mocks of these stores are hoisted and lazy).
  let popOpen = false;
  const onPopMaybeOpen = () => {
    const open = useFt8Store.getState().open || useWsprStore.getState().open;
    if (open && !popOpen && isDigitalPluginReady()) pushAllConfig();
    popOpen = open;
  };
  useFt8Store.subscribe(onPopMaybeOpen);
  useWsprStore.subscribe(onPopMaybeOpen);
}
