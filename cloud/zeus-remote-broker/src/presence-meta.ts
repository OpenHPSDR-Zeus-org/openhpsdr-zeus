/**
 * Pure (runtime-free) parsing + sanitation for the optional operator-metadata
 * body a sidecar POSTs to /presence/register and /presence/heartbeat, plus the
 * edge-derived network fields (IP / country). Kept out of index.ts / presence.ts
 * — which pull in `cloudflare:workers` globals — so it is unit-testable under
 * plain `node --test` exactly as the crash-validate core is.
 *
 * The sidecar is QRZ-authenticated as the operator, but the metadata it sends is
 * still untrusted free-form text (platform string, app version, radio name), so
 * every field is length-clamped and type-checked here before it is stored and
 * later shown to a maintainer in the dashboard. Never throws.
 */

/** Defensive clamp on any single operator metadata string. */
export const MAX_META_FIELD = 120;

/** A sanitised operator-metadata snapshot stored in the presence DO. */
export interface OperatorMeta {
  /** Operator's reported OS/platform string (e.g. "Microsoft Windows 11 ..."). */
  platform: string | null;
  /** Operator's reported Zeus app version (e.g. "0.10.0-dev"). */
  appVersion: string | null;
  /** Human-readable connected radio board (e.g. "Hermes-Lite 2"), or null. */
  radioBoard: string | null;
  /** Variant/model refinement of the board (e.g. "G2"), or null. */
  radioModel: string | null;
  /** Whether a radio is currently connected at the operator's station. */
  radioConnected: boolean;
}

/** An OperatorMeta with every field empty (older operators send no body). */
export function emptyMeta(): OperatorMeta {
  return {
    platform: null,
    appVersion: null,
    radioBoard: null,
    radioModel: null,
    radioConnected: false,
  };
}

/**
 * Clean a single untrusted field to a trimmed, length-clamped non-empty string,
 * or null. Drops non-strings, empty/whitespace, and collapses control characters
 * (so a maintainer's dashboard never renders a stray newline/escape from the
 * wire). Exported for the IP/country edge fields too.
 */
export function cleanField(value: unknown): string | null {
  if (typeof value !== 'string') return null;
  // Collapse ASCII control chars (incl. CR/LF/tab) to spaces so stored metadata
  // is single-line, then trim.
  const cleaned = value.replace(/[\x00-\x1f\x7f]/g, ' ').trim();
  if (!cleaned) return null;
  return cleaned.length > MAX_META_FIELD ? cleaned.slice(0, MAX_META_FIELD) : cleaned;
}

/**
 * Parse + sanitise the raw request body of a POST /presence/register|heartbeat.
 * An empty body, invalid JSON, a non-object, or any missing field all degrade
 * gracefully to {@link emptyMeta} / null fields — the sidecar metadata is a
 * best-effort convenience, never a hard requirement for presence.
 */
export function sanitizeOperatorMeta(rawBody: string): OperatorMeta {
  const meta = emptyMeta();
  if (!rawBody) return meta;
  let parsed: unknown;
  try {
    parsed = JSON.parse(rawBody);
  } catch {
    return meta;
  }
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) return meta;
  const o = parsed as Record<string, unknown>;
  meta.platform = cleanField(o.platform);
  meta.appVersion = cleanField(o.appVersion);
  meta.radioBoard = cleanField(o.radioBoard);
  meta.radioModel = cleanField(o.radioModel);
  meta.radioConnected = o.radioConnected === true;
  return meta;
}

/**
 * Normalise a Cloudflare 2-letter country code (cf.country) to an upper-case
 * A–Z pair, or null. Cloudflare uses "XX"/"T1" for unknown/Tor — those are
 * dropped so the dashboard shows nothing rather than a meaningless badge.
 */
export function cleanCountry(value: unknown): string | null {
  if (typeof value !== 'string') return null;
  const cc = value.trim().toUpperCase();
  if (!/^[A-Z]{2}$/.test(cc) || cc === 'XX' || cc === 'T1') return null;
  return cc;
}
