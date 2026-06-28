import type { SignalRoom } from './signal-room';
import type { RateLimiter } from './rate-limiter';
import type { PresenceRoom } from './presence';
import type { CrashStore } from './crash-store';

/** Worker environment bindings (see wrangler.toml). */
export interface Env {
  /** Per-callsign WebRTC signaling room (WebSocket Hibernation DO). */
  SIGNAL_ROOM: DurableObjectNamespace<SignalRoom>;

  /** Per-IP connection rate limiter DO (shared pattern with the chat relay). */
  RATE_DO: DurableObjectNamespace<RateLimiter>;

  /**
   * Credential-based admin store (D1 `zeus-admin`), shared with the chat relay.
   * Single source of truth for who is an admin: callsign ownership (QRZ) AND a
   * stored credential. See ADMIN_AUTH_DESIGN.md and migrations/0001_admin.sql.
   */
  ADMIN_DB: D1Database;

  /**
   * Presence DO: a single object tracking operators whose sidecar has registered
   * as support-available (heartbeats, ~90s expiry). Listed via /admin/presence.
   */
  PRESENCE: DurableObjectNamespace<PresenceRoom>;

  /**
   * Crash-share store DO: a single object holding the most-recent auto-shared
   * crash records per operator callsign. Written by /crash (operator, QRZ-gated),
   * read by /admin/crashes (maintainer, Bearer-gated). See crash-store.ts.
   */
  CRASH_STORE: DurableObjectNamespace<CrashStore>;

  /**
   * Bootstrap admin seeded on first /admin request when `admins` is empty
   * (created_by 'bootstrap'). Set BOTH via `wrangler secret put`. Optional —
   * absent means no bootstrap admin is created (legacy env.ADMINS still migrates).
   */
  ADMIN_BOOTSTRAP_CALLSIGN?: string;
  /** Bootstrap admin's initial password (see ADMIN_BOOTSTRAP_CALLSIGN). */
  ADMIN_BOOTSTRAP_PASSWORD?: string;

  /**
   * QRZ-login enforcement for the HOST (radio) side. Default "on": only the
   * operator who can prove the callsign on QRZ may register as that callsign's
   * radio. The CLIENT side is never QRZ-gated — access is gated end-to-end by
   * the session password at the radio (ADR-0008), so the broker only relays.
   */
  QRZ_VERIFY?: string;

  /** Origin of the web client (Cloudflare Pages) that /go/<callsign> redirects to. */
  WEB_APP_ORIGIN?: string;

  /** Cloudflare Realtime TURN key id (set via `wrangler secret put`). */
  TURN_KEY_ID?: string;
  /** Cloudflare Realtime TURN API token (set via `wrangler secret put`). */
  TURN_API_TOKEN?: string;

  /**
   * Legacy comma-separated admin callsigns. Only used at bootstrap: any callsign
   * here is seeded as a password-less admin row the first time `admins` is empty,
   * so chat-admin keeps working pre-migration. Not consulted after that.
   */
  ADMINS?: string;
}
