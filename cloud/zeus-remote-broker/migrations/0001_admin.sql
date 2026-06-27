-- Phase 2 of the remote-diagnostics epic (beads zeus-87ca / issue zeus-d0yr).
-- One D1 database `zeus-admin`, bound to BOTH Workers (broker + chat relay), so
-- "admin" has a single credential-based source of truth. See ADMIN_AUTH_DESIGN.md.

CREATE TABLE admins (
  callsign     TEXT PRIMARY KEY,        -- uppercased
  pw_hash      TEXT,                    -- PBKDF2 derived key, base64; NULL = no interactive login yet
  pw_salt      TEXT,                    -- base64; NULL iff pw_hash NULL
  pw_iter      INTEGER,                 -- PBKDF2 iterations used (for future cost bumps)
  disabled     INTEGER NOT NULL DEFAULT 0,
  created_at   INTEGER NOT NULL,        -- unix ms
  created_by   TEXT                     -- callsign of the admin who added them, or 'bootstrap'
);

CREATE TABLE admin_tokens (
  id           TEXT PRIMARY KEY,        -- random public id (safe to list)
  token_hash   TEXT NOT NULL,           -- SHA-256 hex of the secret token; the secret is shown once
  callsign     TEXT NOT NULL,
  label        TEXT,                    -- e.g. 'agent', 'dashboard', 'session'
  created_at   INTEGER NOT NULL,
  expires_at   INTEGER,                 -- unix ms; NULL = never expires (agent token). Enforced in getTokenByHash.
  last_used_at INTEGER,
  revoked      INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE admin_audit (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  ts           INTEGER NOT NULL,
  actor        TEXT,                    -- who did it
  action       TEXT NOT NULL,           -- login | token.mint | token.revoke | admin.add | admin.disable | ...
  target       TEXT,                    -- subject callsign / token id
  detail       TEXT
);

CREATE INDEX admin_tokens_callsign ON admin_tokens(callsign);
-- Fast token auth lookups (hash equality) and expiry-aware filtering.
CREATE INDEX admin_tokens_hash ON admin_tokens(token_hash);
