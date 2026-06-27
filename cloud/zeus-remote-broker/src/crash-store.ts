import { DurableObject } from 'cloudflare:workers';
import type { Env } from './types';
import { MAX_CRASH_PER_CALLSIGN, retainNewest } from './crash-validate';

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
 *   3. index.ts verifies the QRZ headers, then forwards the body here under the
 *      verified callsign. The record is ALREADY redacted server-side by the
 *      backend's diagnostics layer; the broker stores it verbatim and adds nothing.
 *   4. A maintainer lists/fetches a consented operator's crashes via the
 *      admin-only `GET /admin/crashes?callsign=<cs>` route (Bearer admin auth).
 *
 * Storage policy: in-DO transactional SQLite-free map persisted via the DO storage
 * API would be ideal, but to stay free-plan/SQLite-class friendly and simple we
 * keep an in-memory ring per callsign capped at MAX_CRASH_PER_CALLSIGN. Crash
 * records are a best-effort diagnostic convenience, not a system of record, so a
 * DO eviction simply loses the oldest unsent history — acceptable for this use.
 * Each upload is size-capped (MAX_CRASH_BYTES) at the edge before it reaches here.
 */

/** A stored crash entry: the raw record JSON plus broker-side receipt metadata. */
interface StoredCrash {
  /** Broker receive time (unix ms) — authoritative ordering, independent of the record's own clock. */
  receivedAt: number;
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
        const list = this.byCallsign.get(callsign) ?? [];
        list.push({ receivedAt: now, record });
        // Bound the history — drop the oldest beyond the cap.
        retainNewest(list);
        this.byCallsign.set(callsign, list);
        return Response.json({ ok: true, count: list.length });
      }
      case 'list': {
        if (!callsign) return new Response('callsign required', { status: 400 });
        const list = this.byCallsign.get(callsign) ?? [];
        // Newest first for the maintainer view.
        const crashes = [...list].reverse().map((e) => ({ receivedAt: e.receivedAt, record: e.record }));
        return Response.json({ callsign, count: crashes.length, crashes });
      }
      default:
        return new Response('not found', { status: 404 });
    }
  }
}
