/**
 * Wire shapes for the broker /admin/* HTTP surface and the support-session
 * WebSocket + data-channel protocol. The CLI is a pure CONSUMER of these — they
 * are defined by the deployed broker (cloud/zeus-remote-broker, ADMIN_AUTH_DESIGN.md)
 * and the P3b support-session protocol. We never mutate them, only model them.
 */

// --- /admin/login -----------------------------------------------------------

export interface LoginResponse {
  /** Short-lived (~12h) session token, prefix `zsa_`. */
  token: string;
  callsign: string;
  /** Unix ms expiry of the session token. */
  expiresAt: number;
}

// --- /admin/tokens (POST) ---------------------------------------------------

export interface MintTokenResponse {
  /** Public, listable token id. */
  id: string;
  /** The secret agent token (shown ONCE), prefix `zsa_`. */
  token: string;
}

// --- /admin/presence --------------------------------------------------------

export interface PresenceOperator {
  callsign: string;
  /** Unix ms the operator registered as support-available. */
  since: number;
  /** Unix ms of the last heartbeat. */
  lastSeen: number;
}

export interface PresenceResponse {
  operators: PresenceOperator[];
}

// --- /admin/request ---------------------------------------------------------

export interface RequestResponse {
  ok: boolean;
  /** Correlates the request with the later `support-grant` over the WS. */
  requestId: string;
  /** Single-use ticket to open the support WebSocket. */
  ticket: string;
  callsign: string;
}

// --- support WebSocket frames (JSON text) -----------------------------------

/** Frames the CLI may receive on the support WebSocket. */
export type SupportWsInbound =
  | { t: 'pong' }
  | { t: 'support-grant'; requestId: string }
  | { t: 'answer'; sdp: string; support?: boolean }
  | { t: 'candidate'; candidate?: unknown }
  | { t: 'offline' }
  | { t: 'bye' }
  | { t: string; [k: string]: unknown };

/** Frames the CLI may send on the support WebSocket. */
export type SupportWsOutbound =
  | { t: 'ping' }
  | { t: 'offer'; sdp: string }
  | { t: 'candidate'; candidate: unknown }
  | { t: 'bye' };

// --- data-channel messages --------------------------------------------------

/** `control` channel. */
export type ControlInbound = { t: 'support-ready'; requestId: string; admin: string } | { t: string; [k: string]: unknown };

/** `api` channel request (GET-only allowlist enforced at the radio). */
export interface ApiChannelRequest {
  id: string;
  method: 'GET';
  path: string;
}

export interface ApiChannelResponse {
  id: string;
  status: number;
  contentType?: string;
  body: string;
}

/** `log` channel. */
export type LogInbound =
  | { t: 'backlog'; lines: string[] }
  | { t: 'line'; line: string }
  | { t: string; [k: string]: unknown };
