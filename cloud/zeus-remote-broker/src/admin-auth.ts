/**
 * Admin-auth crypto core (WebCrypto only — runs in Workers AND Node 20+, no WASM
 * or native deps). Pure functions so they're unit-testable under `node --test`.
 *
 * Two secret kinds:
 *  - Passwords: PBKDF2-HMAC-SHA256, 100k iters, 16-byte salt, 32-byte key. We
 *    store base64(hash)/base64(salt)/iter and verify with a constant-time
 *    compare of the derived bytes. (Cloudflare Workers' WebCrypto rejects
 *    PBKDF2 iteration counts above 100000 — exceeding it throws at runtime, so
 *    100k is the hard ceiling here, not a tuning choice.)
 *  - API/session tokens: 32 random bytes → base64url, presented once as
 *    `zsa_<...>`. We persist only SHA-256(token) hex and verify by hashing the
 *    presented token and constant-time-comparing to the stored hash.
 *
 * Never log or return secrets; only hashes leave this module.
 */

/**
 * PBKDF2 cost. Stored per-row (pw_iter) so it can be bumped without a flag-day.
 * Capped at 100000: Cloudflare Workers' WebCrypto throws "iteration counts above
 * 100000 are not supported" for anything higher, which would break hash + verify
 * at runtime (Node has no such cap, so unit tests don't catch it).
 */
export const PBKDF2_ITER = 100_000;
const SALT_BYTES = 16;
const KEY_BYTES = 32;
const TOKEN_BYTES = 32;

/** Public prefix on the secret token string. The bytes after it are base64url. */
export const TOKEN_PREFIX = 'zsa_';

/** Session-token lifetime: interactive logins expire; agent tokens do not. */
export const SESSION_TTL_MS = 12 * 60 * 60 * 1000; // ~12h

const enc = new TextEncoder();

// --- base64 helpers (standard + url-safe) ----------------------------------

function bytesToBase64(bytes: Uint8Array): string {
  let bin = '';
  for (const b of bytes) bin += String.fromCharCode(b);
  return btoa(bin);
}

function base64ToBytes(b64: string): Uint8Array {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

function bytesToBase64Url(bytes: Uint8Array): string {
  return bytesToBase64(bytes).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

// --- password (PBKDF2) -----------------------------------------------------

/** A derived password record ready to persist. */
export interface PasswordHash {
  hash: string; // base64(derived key)
  salt: string; // base64(salt)
  iter: number;
}

async function pbkdf2(password: string, salt: Uint8Array, iter: number): Promise<Uint8Array> {
  const key = await crypto.subtle.importKey('raw', enc.encode(password), 'PBKDF2', false, [
    'deriveBits',
  ]);
  const bits = await crypto.subtle.deriveBits(
    { name: 'PBKDF2', hash: 'SHA-256', salt: salt as BufferSource, iterations: iter },
    key,
    KEY_BYTES * 8,
  );
  return new Uint8Array(bits);
}

/** Hash a new password with a fresh random salt at the current cost. */
export async function hashPassword(password: string): Promise<PasswordHash> {
  const salt = crypto.getRandomValues(new Uint8Array(SALT_BYTES));
  const derived = await pbkdf2(password, salt, PBKDF2_ITER);
  return { hash: bytesToBase64(derived), salt: bytesToBase64(salt), iter: PBKDF2_ITER };
}

/**
 * Verify a candidate password against a stored record. Re-derives with the
 * stored salt/iter and constant-time-compares the bytes. Returns false on any
 * malformed input rather than throwing (generic auth failure, no oracle).
 */
export async function verifyPassword(password: string, stored: PasswordHash): Promise<boolean> {
  try {
    const salt = base64ToBytes(stored.salt);
    const expected = base64ToBytes(stored.hash);
    const derived = await pbkdf2(password, salt, stored.iter);
    return constantTimeEqual(derived, expected);
  } catch {
    return false;
  }
}

// --- tokens (random secret → stored SHA-256 hex) ---------------------------

/** A freshly minted token: the secret is returned ONCE, the hash is persisted. */
export interface MintedToken {
  token: string; // secret, shown to the caller once (`zsa_...`)
  hash: string; // SHA-256 hex of the secret, stored
}

/** Mint a new opaque token: 32 random bytes, base64url, `zsa_`-prefixed. */
export async function mintToken(): Promise<MintedToken> {
  const raw = crypto.getRandomValues(new Uint8Array(TOKEN_BYTES));
  const token = TOKEN_PREFIX + bytesToBase64Url(raw);
  const hash = await hashToken(token);
  return { token, hash };
}

/** SHA-256 hex of a token string (the only form we persist). */
export async function hashToken(token: string): Promise<string> {
  const buf = await crypto.subtle.digest('SHA-256', enc.encode(token));
  return [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

/** Cheap shape check before hashing — a presented Bearer must look like ours. */
export function looksLikeToken(token: string): boolean {
  return typeof token === 'string' && token.startsWith(TOKEN_PREFIX) && token.length > TOKEN_PREFIX.length;
}

// --- constant-time compare -------------------------------------------------

/**
 * Constant-time equality for two byte arrays. Length is compared first (lengths
 * are not secret here — both sides are fixed-width digests/keys), then every
 * byte is XOR-accumulated so timing does not leak where a mismatch occurred.
 */
export function constantTimeEqual(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a[i] ^ b[i];
  return diff === 0;
}

/** Constant-time compare of two hex/ascii strings (e.g. stored vs presented token hash). */
export function constantTimeEqualHex(a: string, b: string): boolean {
  return constantTimeEqual(enc.encode(a), enc.encode(b));
}
