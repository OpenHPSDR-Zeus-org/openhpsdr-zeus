import { DurableObject } from 'cloudflare:workers';
import type { Env } from './types';
import { retainNewest } from './crash-validate';

/**
 * Single Durable Object holding the most recent auto-shared crash records, keyed
 * by operator callsign.
 *
 * Data flow (Phase 3c "crash auto-share"):
 *   1. An operator opts in to BOTH the L1 "remote diagnostics" master switch AND
 *      the "auto-share crash reports" sub-toggle (default OFF).
 *   2. When the Zeus backend dies WITHOUT a clean-exit marker, the out-of-process
 *      sidecar (Zeus.SupportAgent) — which survives the crash — writes a
 *      SupportCrashRecord to disk and, only if auto-share is on, POSTs it to the
 *      broker's `/crash` endpoint (QRZ-authenticated as the operator's callsign).
 *   3. index.ts verifies the QRZ headers, derives the edge IP/country, then
 *      forwards the body here under the verified callsign. The record is ALREADY
 *      redacted server-side by the backend's diagnostics layer; the broker stores
 *      it verbatim and adds only receipt metadata (receivedAt, ip, country).
 *   4. A maintainer browses the crash-bearing operators via `GET /admin/crashes`
 *      (index) and drills into one via `GET /admin/crashes?callsign=<cs>` — both
 *      admin-Bearer-gated.
 *
 * Storage policy: an in-memory ring per callsign capped at MAX_CRASH_PER_CALLSIGN
 * keeps this free-plan/SQLite-class friendly. Crash records are a best-effort
 * diagnostic convenience, not a system of record, so a DO eviction simply loses
 * the oldest history — acceptable for this use. Each upload is size-capped
 * (MAX_CRASH_BYTES) at the edge before it reaches here.
 */

/** A stored crash entry: the raw record JSON plus broker-side receipt metadata. */
interface StoredCrash {
  /** Broker receive time (unix ms) — authoritative ordering, independent of the record's own clock. */
  receivedAt: number;
  /** Edge-observed client IP at upload time (cf-connecting-ip), or null. */
  ip: string | null;
  /** Edge-observed 2-letter country at upload time (cf.country), or null. */
  country: string | null;
  /** The verbatim SupportCrashRecord JSON as uploaded (already redacted by the backend). */
  record: unknown;
}

export class CrashStore extends DurableObject<Env> {
  /** callsign -> newest-last list of crash entries (bounded to MAX_PER_CALLSIGN). */
  private byCallsign = new Map<string, StoredCrash[]>();

  override async fetch(request: Request): Promise<Response> {
    const url = new URL(request.url);
    const action = url.pathname.replace(/^\/+/, '');
    const callsign = (url.searchParams.get('callsign') ?? '').trim().toUpperCase();
    const now = Date.now();

    switch (action) {
      case 'put': {
        if (!callsign) return new Response('callsign required', { status: 400 });
        let record: unknown;
        try {
          record = await request.json();
        } catch {
          return new Response('invalid json', { status: 400 });
        }
        const ip = (url.searchParams.get('ip') ?? '').trim() || null;
        const country = (url.searchParams.get('country') ?? '').trim().toUpperCase() || null;
        const list = this.byCallsign.get(callsign) ?? [];
        list.push({ receivedAt: now, ip, country, record });
        // Bound the history — drop the oldest beyond the cap.
        retainNewest(list);
        this.byCallsign.set(callsign, list);
        return Response.json({ ok: true, count: list.length });
      }
      case 'list': {
        if (!callsign) return new Response('callsign required', { status: 400 });
        const list = this.byCallsign.get(callsign) ?? [];
        // Newest first for the maintainer view.
        const crashes = [...list]
          .reverse()
          .map((e) => ({ receivedAt: e.receivedAt, ip: e.ip, country: e.country, record: e.record }));
        return Response.json({ callsign, count: crashes.length, crashes });
      }
      case 'index': {
        // Maintainer overview: one row per operator that has shared any crash,
        // so the dashboard can list crash-bearing operators without knowing the
        // callsigns in advance. Newest-crash first.
        const operators = [...this.byCallsign.entries()]
          .map(([cs, list]) => {
            const last = list[list.length - 1];
            return {
              callsign: cs,
              count: list.length,
              lastReceivedAt: last?.receivedAt ?? 0,
              lastIp: last?.ip ?? null,
            };
          })
          .filter((o) => o.count > 0)
          .sort((a, b) => b.lastReceivedAt - a.lastReceivedAt);
        return Response.json({ operators });
      }
      default:
        return new Response('not found', { status: 404 });
    }
  }
}
