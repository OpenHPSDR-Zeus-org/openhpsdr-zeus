/**
 * Typed D1 query wrappers for the `zeus-admin` store. Parameterized queries
 * ONLY — never interpolate caller input into SQL. Rows mirror 0001_admin.sql.
 *
 * This is the broker's full admin data layer; the chat relay carries only a
 * tiny read-only helper (it needs just the enabled-admin callsign list).
 */

/** A row in `admins`. pw_* are NULL for password-less (legacy-seeded) admins. */
export interface AdminRow {
  callsign: string;
  pw_hash: string | null;
  pw_salt: string | null;
  pw_iter: number | null;
  disabled: number;
  created_at: number;
  created_by: string | null;
}

/** A row in `admin_tokens`. token_hash is never returned to clients. */
export interface TokenRow {
  id: string;
  token_hash: string;
  callsign: string;
  label: string | null;
  created_at: number;
  expires_at: number | null; // null = never expires (agent/API token); set for session tokens
  last_used_at: number | null;
  revoked: number;
}

// --- admins ----------------------------------------------------------------

export async function getAdmin(db: D1Database, callsign: string): Promise<AdminRow | null> {
  return db.prepare('SELECT * FROM admins WHERE callsign = ?').bind(callsign).first<AdminRow>();
}

export async function listAdmins(db: D1Database): Promise<AdminRow[]> {
  const res = await db
    .prepare('SELECT * FROM admins ORDER BY callsign')
    .all<AdminRow>();
  return res.results ?? [];
}

/** Callsigns of all enabled admins — the chat relay's only need (also reused here). */
export async function listEnabledAdminCallsigns(db: D1Database): Promise<string[]> {
  const res = await db
    .prepare('SELECT callsign FROM admins WHERE disabled = 0')
    .all<{ callsign: string }>();
  return (res.results ?? []).map((r) => r.callsign);
}

export async function countAdmins(db: D1Database): Promise<number> {
  const row = await db.prepare('SELECT COUNT(*) AS n FROM admins').first<{ n: number }>();
  return row?.n ?? 0;
}

/** Enabled (non-disabled) admin count — used to refuse disabling the last admin. */
export async function countEnabledAdmins(db: D1Database): Promise<number> {
  const row = await db
    .prepare('SELECT COUNT(*) AS n FROM admins WHERE disabled = 0')
    .first<{ n: number }>();
  return row?.n ?? 0;
}

/**
 * Insert an admin if absent (idempotent — never demotes/overwrites an existing
 * row). pw_* may all be null for a password-less seed.
 */
export async function insertAdmin(
  db: D1Database,
  row: {
    callsign: string;
    pw_hash: string | null;
    pw_salt: string | null;
    pw_iter: number | null;
    created_by: string;
  },
): Promise<void> {
  await db
    .prepare(
      'INSERT OR IGNORE INTO admins (callsign, pw_hash, pw_salt, pw_iter, disabled, created_at, created_by) ' +
        'VALUES (?, ?, ?, ?, 0, ?, ?)',
    )
    .bind(row.callsign, row.pw_hash, row.pw_salt, row.pw_iter, Date.now(), row.created_by)
    .run();
}

export async function setAdminPassword(
  db: D1Database,
  callsign: string,
  pw_hash: string,
  pw_salt: string,
  pw_iter: number,
): Promise<void> {
  await db
    .prepare('UPDATE admins SET pw_hash = ?, pw_salt = ?, pw_iter = ? WHERE callsign = ?')
    .bind(pw_hash, pw_salt, pw_iter, callsign)
    .run();
}

/** Disable (soft-delete) an admin; never hard-delete, to keep the audit trail. */
export async function disableAdmin(db: D1Database, callsign: string): Promise<void> {
  await db.prepare('UPDATE admins SET disabled = 1 WHERE callsign = ?').bind(callsign).run();
}

// --- tokens ----------------------------------------------------------------

export async function insertToken(
  db: D1Database,
  row: { id: string; token_hash: string; callsign: string; label: string; expires_at: number | null },
): Promise<void> {
  await db
    .prepare(
      'INSERT INTO admin_tokens (id, token_hash, callsign, label, created_at, expires_at, revoked) ' +
        'VALUES (?, ?, ?, ?, ?, ?, 0)',
    )
    .bind(row.id, row.token_hash, row.callsign, row.label, Date.now(), row.expires_at)
    .run();
}

/**
 * Look a token up by its stored SHA-256 hex. Enforces revoked=0 AND not-expired
 * at the DATA layer (pass the current unix-ms), so expiry holds for every
 * consumer rather than depending on an app-level check. A null expires_at never
 * expires (agent tokens).
 */
export async function getTokenByHash(
  db: D1Database,
  tokenHash: string,
  now: number,
): Promise<TokenRow | null> {
  return db
    .prepare(
      'SELECT * FROM admin_tokens WHERE token_hash = ? AND revoked = 0 ' +
        'AND (expires_at IS NULL OR expires_at > ?)',
    )
    .bind(tokenHash, now)
    .first<TokenRow>();
}

export async function touchToken(db: D1Database, id: string): Promise<void> {
  await db
    .prepare('UPDATE admin_tokens SET last_used_at = ? WHERE id = ?')
    .bind(Date.now(), id)
    .run();
}

export async function revokeToken(db: D1Database, id: string): Promise<void> {
  await db.prepare('UPDATE admin_tokens SET revoked = 1 WHERE id = ?').bind(id).run();
}

/** Token metadata for a callsign — never the secret/hash. */
export async function listTokens(
  db: D1Database,
  callsign: string,
): Promise<Array<Omit<TokenRow, 'token_hash'>>> {
  const res = await db
    .prepare(
      'SELECT id, callsign, label, created_at, expires_at, last_used_at, revoked FROM admin_tokens ' +
        'WHERE callsign = ? ORDER BY created_at DESC',
    )
    .bind(callsign)
    .all<Omit<TokenRow, 'token_hash'>>();
  return res.results ?? [];
}

/** The id of the token referenced by a hash (so we can authorize a revoke). */
export async function getTokenOwner(db: D1Database, id: string): Promise<TokenRow | null> {
  return db.prepare('SELECT * FROM admin_tokens WHERE id = ?').bind(id).first<TokenRow>();
}

// --- audit -----------------------------------------------------------------

export async function insertAudit(
  db: D1Database,
  entry: { actor: string | null; action: string; target?: string | null; detail?: string | null },
): Promise<void> {
  await db
    .prepare('INSERT INTO admin_audit (ts, actor, action, target, detail) VALUES (?, ?, ?, ?, ?)')
    .bind(Date.now(), entry.actor, entry.action, entry.target ?? null, entry.detail ?? null)
    .run();
}
