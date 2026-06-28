/**
 * Pure (runtime-free) validation for an uploaded crash record body and the
 * per-callsign retention policy. Kept out of index.ts / crash-store.ts — which
 * pull in `cloudflare:workers` globals — so it is unit-testable under plain
 * `node --test` exactly as the admin-auth crypto core is.
 */

/** Hard cap on a single uploaded crash record (~256 KiB). Shared by the edge check. */
export const MAX_CRASH_BYTES = 256 * 1024;

/** Max retained crash records per operator callsign (newest win). */
export const MAX_CRASH_PER_CALLSIGN = 20;

export type CrashBodyVerdict =
  | { ok: true }
  | { ok: false; status: 400 | 413; message: string };

/**
 * Validate the raw request body of a POST /crash: non-empty, within the size
 * cap, and a JSON object (not array/primitive/null). Returns a tagged verdict so
 * the caller can map directly to an HTTP status without re-deriving it.
 */
export function validateCrashBody(body: string): CrashBodyVerdict {
  if (body.length === 0) return { ok: false, status: 400, message: 'empty body' };
  if (body.length > MAX_CRASH_BYTES) return { ok: false, status: 413, message: 'payload too large' };
  let parsed: unknown;
  try {
    parsed = JSON.parse(body);
  } catch {
    return { ok: false, status: 400, message: 'invalid json' };
  }
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
    return { ok: false, status: 400, message: 'expected a JSON object' };
  }
  return { ok: true };
}

/**
 * Apply the bounded-history retention to a per-callsign list after appending a
 * new entry: keep only the newest {@link MAX_CRASH_PER_CALLSIGN}. Mutates and
 * returns the same array for convenience.
 */
export function retainNewest<T>(list: T[]): T[] {
  while (list.length > MAX_CRASH_PER_CALLSIGN) list.shift();
  return list;
}
