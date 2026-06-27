import type { ChatRoom } from './chat-room';
import type { RateLimiter } from './rate-limiter';

/** Worker environment bindings (see wrangler.toml). */
export interface Env {
  /** Durable Object namespace hosting chat rooms. */
  CHAT_ROOM: DurableObjectNamespace<ChatRoom>;

  /** Per-IP connection rate limiter Durable Object. */
  RATE_DO: DurableObjectNamespace<RateLimiter>;
  /**
   * Optional shared secret gating connections to the relay. When set (via
   * `wrangler secret put RELAY_SHARED_SECRET`), backends must present it as a
   * Bearer token or `?token=` query param on the WS upgrade. Unset = open
   * (local dev only).
   */
  RELAY_SHARED_SECRET?: string;

  /**
   * QRZ-login enforcement. Default "on": each connection must present a valid
   * QRZ session (X-QRZ-Session + X-QRZ-Callsign headers) which the relay
   * validates against the QRZ XML API. Set "off" for local dev only — browser
   * WebSocket clients cannot set request headers.
   */
  QRZ_VERIFY?: string;

  /**
   * Shared credential-based admin store (D1 `zeus-admin`, same database the
   * broker owns). ChatRoom repopulates its in-memory admin set from
   * `SELECT callsign FROM admins WHERE disabled = 0` on a ~60s TTL, so dashboard
   * add/disable propagates without a redeploy. Optional in local dev — when
   * absent (or the query throws / returns empty) the ADMINS seed below is used.
   */
  ADMIN_DB?: D1Database;

  /**
   * Fallback seed for moderator callsigns (create/delete private rooms,
   * add/remove members, ban/unban). Defaults to "N9WAR,KB2UKA" when unset.
   * Case-insensitive. Used only when ADMIN_DB is unbound, empty, or unreachable;
   * once the D1 store is populated it is the source of truth.
   */
  ADMINS?: string;
}
