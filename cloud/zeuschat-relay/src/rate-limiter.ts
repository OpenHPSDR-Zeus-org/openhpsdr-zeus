import { DurableObject } from 'cloudflare:workers';
import type { Env } from './types';

/** Connection attempts allowed per IP per window. */
const LIMIT = 30;
const WINDOW_MS = 60_000;

/**
 * Per-IP connection rate limiter. One instance per IP (idFromName(ip)), so the
 * fixed-window counter is strongly consistent for that IP (a DO is
 * single-threaded). In-memory state is fine here: an actively-abused limiter
 * stays warm and keeps counting; an idle one may evict and reset, but idle ==
 * no abuse. Guards the QRZ-verification path from connection-spam.
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
