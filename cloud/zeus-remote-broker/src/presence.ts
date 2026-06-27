import { DurableObject } from 'cloudflare:workers';
import type { Env } from './types';

/**
 * Single Durable Object tracking operators whose sidecar has registered as
 * support-available (the operator's L1 "available for remote diagnostics" switch
 * is on). The broker's /admin/presence reads it; the sidecar registers/heartbeats
 * (wired in P3 — for now only /admin/presence's `list` is reachable).
 *
 * In-memory map only: presence is ephemeral by nature, and a DO restart simply
 * waits for the next heartbeat. Entries whose lastSeen is older than ~90s are
 * treated as offline (a sidecar should heartbeat well inside that window).
 */
interface Entry {
  /** When this operator first registered in the current online streak (unix ms). */
  since: number;
  /** Last heartbeat (unix ms). */
  lastSeen: number;
}

/** An operator is considered offline if we haven't heard from them in this long. */
const PRESENCE_EXPIRY_MS = 90_000;

export class PresenceRoom extends DurableObject<Env> {
  private entries = new Map<string, Entry>();

  override async fetch(request: Request): Promise<Response> {
    const url = new URL(request.url);
    const action = url.pathname.replace(/^\/+/, '');
    const callsign = (url.searchParams.get('callsign') ?? '').trim().toUpperCase();
    const now = Date.now();

    switch (action) {
      case 'register': {
        if (!callsign) return new Response('callsign required', { status: 400 });
        const existing = this.entries.get(callsign);
        this.entries.set(callsign, { since: existing?.since ?? now, lastSeen: now });
        return Response.json({ ok: true });
      }
      case 'heartbeat': {
        if (!callsign) return new Response('callsign required', { status: 400 });
        const existing = this.entries.get(callsign);
        // A heartbeat for an unknown/expired operator implicitly re-registers.
        this.entries.set(callsign, { since: existing?.since ?? now, lastSeen: now });
        return Response.json({ ok: true });
      }
      case 'drop': {
        if (callsign) this.entries.delete(callsign);
        return Response.json({ ok: true });
      }
      case 'list': {
        return Response.json({ operators: this.snapshot(now) });
      }
      default:
        return new Response('not found', { status: 404 });
    }
  }

  /** Live operators (lastSeen within the expiry window); expired ones are pruned. */
  private snapshot(now: number): Array<{ callsign: string; since: number; lastSeen: number }> {
    const out: Array<{ callsign: string; since: number; lastSeen: number }> = [];
    for (const [callsign, e] of this.entries) {
      if (now - e.lastSeen > PRESENCE_EXPIRY_MS) {
        this.entries.delete(callsign);
        continue;
      }
      out.push({ callsign, since: e.since, lastSeen: e.lastSeen });
    }
    return out;
  }
}
