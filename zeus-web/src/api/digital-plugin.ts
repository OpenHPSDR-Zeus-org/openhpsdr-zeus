// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.
//
// REST + SSE client for the Zeus Digital plugin (com.kb2uka.digital) — the
// backend plugin that hosts the FT8/FT4/WSPR decoders, TX keyer and spotting
// uploaders after their extraction from core. Everything digital-mode the
// frontend used to reach at /api/ft8|/api/wspr|/api/spotting now lives under
// /api/plugins/com.kb2uka.digital (EXCEPT /api/ft8/settings — the UI-shell
// per-mode prefs stay core). GET /status doubles as the liveness probe the
// mode-gate uses: 2xx means the plugin's routes are mapped THIS boot; 404
// (not installed / not activated) and 503 (shut down — zombie-route guard)
// both read as not-live.

import { getServerBaseUrl } from '../serverUrl';

export const DIGITAL_PLUGIN_ID = 'com.kb2uka.digital';
export const DIGITAL_PLUGIN_BASE = `/api/plugins/${DIGITAL_PLUGIN_ID}`;

/**
 * Liveness probe for the mode gate: true ONLY on a 2xx from GET /status.
 * Installed-but-not-restarted (404) and shut-down-instance (503) are both
 * "not live". Network errors read as not-live too — the next probe trigger
 * (ws reconnect / install refresh) recovers.
 */
export async function probeDigitalPlugin(signal?: AbortSignal): Promise<boolean> {
  try {
    const res = await fetch(`${DIGITAL_PLUGIN_BASE}/status`, { signal });
    return res.ok;
  } catch {
    return false;
  }
}

/** Operator identity pushed to the plugin (spotting + TX message generation).
 *  The CORE owns identity (/api/operator); the plugin only ever receives it. */
export interface DigitalIdentity {
  call: string;
  grid: string;
}

export async function postDigitalIdentity(identity: DigitalIdentity, signal?: AbortSignal): Promise<void> {
  const res = await fetch(`${DIGITAL_PLUGIN_BASE}/config/identity`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(identity),
    signal,
  });
  if (!res.ok) throw new Error(`POST ${DIGITAL_PLUGIN_BASE}/config/identity → ${res.status}`);
}

/**
 * WSJT-X live-decode egress subset pushed to the plugin's WsjtxLiveEmitter.
 * Derived from the CORE WsjtxConfig (which stays the operator-facing source of
 * truth in Settings): `enabled` = enabled && sendLiveDecodes; `host` is the
 * unicast host OR the multicast group depending on `multicast`.
 */
export interface DigitalWsjtxLiveConfig {
  enabled: boolean;
  host: string;
  port: number;
  multicast: boolean;
  /** Operator-configured WSJT-X instance id — third-party loggers correlate the
   *  live stream with core's QSOLogged/LoggedADIF datagrams by this id. */
  instanceId: string;
  /** Multicast hop limit (1..255); ignored for unicast. */
  multicastTtl: number;
}

export async function postDigitalWsjtxLive(cfg: DigitalWsjtxLiveConfig, signal?: AbortSignal): Promise<void> {
  const res = await fetch(`${DIGITAL_PLUGIN_BASE}/config/wsjtx-live`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(cfg),
    signal,
  });
  if (!res.ok) throw new Error(`POST ${DIGITAL_PLUGIN_BASE}/config/wsjtx-live → ${res.status}`);
}

// ---------------------------------------------------------------------------
// SSE event stream — GET /events replaces the old 0x38/0x39/0x3A WS frames.
// Each named event's `data:` is the exact JSON payload of the corresponding
// frame, so the store ingest() logic is unchanged; only the transport moved.
// ---------------------------------------------------------------------------

export interface DigitalEventsHandlers {
  /** Fires on EVERY successful open, including EventSource auto-reconnects —
   *  the caller must re-hydrate (/ft8, /ft8/tx, /wspr) and re-push identity +
   *  config here, because SSE replays nothing that happened in a gap. */
  onOpen?: () => void;
  /** Connection-state edge (true on open, false on error/close). */
  onConnectionChange?: (connected: boolean) => void;
  onFt8Decode?: (json: string) => void;
  onWsprSpot?: (json: string) => void;
  onTxStatus?: (json: string) => void;
}

/** Recreate delay after a HARD EventSource failure (readyState CLOSED — e.g.
 *  the plugin route answered 404/503, which EventSource never retries). */
const SSE_RETRY_MS = 5_000;

/**
 * Open the plugin's SSE stream. EventSource retries transient drops natively;
 * a hard failure (CLOSED) is recreated on a timer so a server restart under an
 * open tab eventually reattaches without a reload. Returns a close function.
 */
export function openDigitalEvents(h: DigitalEventsHandlers): () => void {
  let es: EventSource | null = null;
  let retry: ReturnType<typeof setTimeout> | null = null;
  let closed = false;

  const connect = () => {
    if (closed) return;
    // Relative on web/desktop; Capacitor builds prefix the configured LAN base
    // (EventSource bypasses the fetch interceptor, so resolve it explicitly).
    es = new EventSource(`${getServerBaseUrl()}${DIGITAL_PLUGIN_BASE}/events`);
    es.onopen = () => {
      h.onConnectionChange?.(true);
      h.onOpen?.();
    };
    es.onerror = () => {
      h.onConnectionChange?.(false);
      if (es?.readyState === EventSource.CLOSED && !closed && retry == null) {
        retry = setTimeout(() => {
          retry = null;
          es?.close();
          connect();
        }, SSE_RETRY_MS);
      }
    };
    es.addEventListener('ft8decode', (ev) => h.onFt8Decode?.((ev as MessageEvent<string>).data));
    es.addEventListener('wsprspot', (ev) => h.onWsprSpot?.((ev as MessageEvent<string>).data));
    es.addEventListener('txstatus', (ev) => h.onTxStatus?.((ev as MessageEvent<string>).data));
  };

  connect();

  return () => {
    closed = true;
    if (retry != null) clearTimeout(retry);
    es?.close();
    h.onConnectionChange?.(false);
  };
}
