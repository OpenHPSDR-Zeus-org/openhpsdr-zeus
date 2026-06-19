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
}
