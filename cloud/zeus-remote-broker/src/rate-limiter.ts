import { DurableObject } from 'cloudflare:workers';
import type { Env } from './types';

/** Connection attempts allowed per IP per window. */
const LIMIT = 30;
const WINDOW_MS = 60_000;

/**
 * Per-IP connection rate limiter (one DO per IP). Single-threaded fixed-window
 * counter; guards the signaling + QRZ-verify paths from connection spam. Same
 * pattern as the chat relay.
 */
export class RateLimiter extends DurableObject<Env> {
  private windowStart = 0;
  private count = 0;

  override async fetch(_request: Request): Promise<Response> {
    const now = Date.now();
    if (now - this.windowStart > WINDOW_MS) {
      this.windowStart = now;
      this.count = 0;
    }
    this.count += 1;
    return new Response(null, { status: this.count <= LIMIT ? 204 : 429 });
  }
}
