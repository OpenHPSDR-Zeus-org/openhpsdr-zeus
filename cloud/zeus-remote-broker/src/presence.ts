import { DurableObject } from 'cloudflare:workers';
import type { Env } from './types';
import { emptyMeta, type OperatorMeta } from './presence-meta';

/**
 * Single Durable Object tracking operators whose sidecar has registered as
 * support-available (the operator's L1 "available for remote diagnostics" switch
 * is on). The broker's /admin/presence reads it; the sidecar registers/heartbeats
 * (wired in P3) and now also carries a metadata snapshot (platform, app version,
 * connected radio) plus the edge-derived network fields (IP, country) so a
 * maintainer can triage at a glance before opening a live session.
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
  /** Edge-observed client IP (cf-connecting-ip), or null. */
  ip: string | null;
  /** Edge-observed 2-letter country (cf.country), or null. */
  country: string | null;
  /** Sidecar-reported metadata (platform / app version / radio). */
  meta: OperatorMeta;
}

/** An operator is considered offline if we haven't heard from them in this long. */
const PRESENCE_EXPIRY_MS = 90_000;

/** The wire shape of a register/heartbeat body forwarded by index.ts. */
interface PresenceUpsert extends Partial<OperatorMeta> {
  callsign?: string;
  ip?: string | null;
  country?: string | null;
}

/** A single operator row in the /admin/presence response. */
interface OperatorView extends OperatorMeta {
  callsign: string;
  since: number;
  lastSeen: number;
  ip: string | null;
  country: string | null;
}

export class PresenceRoom extends DurableObject<Env> {
  private entries = new Map<string, Entry>();

  override async fetch(request: Request): Promise<Response> {
    const url = new URL(request.url);
    const action = url.pathname.replace(/^\/+/, '');
    const now = Date.now();

    switch (action) {
      case 'register':
      case 'heartbeat': {
        const body = await this.readBody(request);
        const callsign = (body.callsign ?? '').trim().toUpperCase();
        if (!callsign) return new Response('callsign required', { status: 400 });
        const existing = this.entries.get(callsign);
        // A heartbeat for an unknown/expired operator implicitly re-registers.
        this.entries.set(callsign, {
          since: existing?.since ?? now,
          lastSeen: now,
          ip: body.ip ?? existing?.ip ?? null,
          country: body.country ?? existing?.country ?? null,
          meta: this.mergeMeta(existing?.meta, body),
        });
        return Response.json({ ok: true });
      }
      case 'drop': {
        const body = await this.readBody(request);
        const callsign = (body.callsign ?? '').trim().toUpperCase();
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

  /** Parse the forwarded JSON upsert body; never throws (degrades to {}). */
  private async readBody(request: Request): Promise<PresenceUpsert> {
    try {
      const v = await request.json();
      return v && typeof v === 'object' ? (v as PresenceUpsert) : {};
    } catch {
      return {};
    }
  }

  /**
   * Carry forward known metadata across heartbeats: a heartbeat may legitimately
   * omit fields, so prefer the freshly-sent value, else keep the last known one,
   * rather than blanking a radio that is still connected.
   */
  private mergeMeta(prev: OperatorMeta | undefined, body: PresenceUpsert): OperatorMeta {
    const base = prev ?? emptyMeta();
    return {
      platform: body.platform ?? base.platform ?? null,
      appVersion: body.appVersion ?? base.appVersion ?? null,
      radioBoard: body.radioBoard ?? base.radioBoard ?? null,
      radioModel: body.radioModel ?? base.radioModel ?? null,
      // radioConnected is authoritative per-beat (a radio can disconnect), so take
      // the body value whenever it is a boolean; only fall back when absent.
      radioConnected:
        typeof body.radioConnected === 'boolean' ? body.radioConnected : base.radioConnected,
    };
  }

  /** Live operators (lastSeen within the expiry window); expired ones are pruned. */
  private snapshot(now: number): OperatorView[] {
    const out: OperatorView[] = [];
    for (const [callsign, e] of this.entries) {
      if (now - e.lastSeen > PRESENCE_EXPIRY_MS) {
        this.entries.delete(callsign);
        continue;
      }
      out.push({
        callsign,
        since: e.since,
        lastSeen: e.lastSeen,
        ip: e.ip,
        country: e.country,
        ...e.meta,
      });
    }
    return out;
  }
}
