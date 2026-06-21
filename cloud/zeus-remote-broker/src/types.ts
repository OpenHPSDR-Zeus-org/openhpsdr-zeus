import type { SignalRoom } from './signal-room';
import type { RateLimiter } from './rate-limiter';

/** Worker environment bindings (see wrangler.toml). */
export interface Env {
  /** Per-callsign WebRTC signaling room (WebSocket Hibernation DO). */
  SIGNAL_ROOM: DurableObjectNamespace<SignalRoom>;

  /** Per-IP connection rate limiter DO (shared pattern with the chat relay). */
  RATE_DO: DurableObjectNamespace<RateLimiter>;

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
}
