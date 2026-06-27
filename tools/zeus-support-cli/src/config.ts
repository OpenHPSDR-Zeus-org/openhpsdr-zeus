/** Shared config + small helpers (broker URL resolution, env tokens, errors). */

export const DEFAULT_BROKER_URL = 'https://remote.openhpsdrzeus.com';

/** A user-facing failure: printed cleanly to stderr, exits non-zero, no stack. */
export class CliError extends Error {
  constructor(
    message: string,
    /** Process exit code. */
    public readonly code = 1,
  ) {
    super(message);
    this.name = 'CliError';
  }
}

/**
 * Resolve the broker base URL from (in priority order) an explicit `--broker`
 * flag, the `ZEUS_REMOTE_BROKER_URL` env var, then the default. Returns it with
 * any trailing slashes stripped so callers can append `/admin/...` safely.
 */
export function resolveBrokerUrl(flag?: string): string {
  const raw = flag || process.env.ZEUS_REMOTE_BROKER_URL || DEFAULT_BROKER_URL;
  let url: URL;
  try {
    url = new URL(raw);
  } catch {
    throw new CliError(`invalid broker URL: ${raw}`, 2);
  }
  if (url.protocol !== 'https:' && url.protocol !== 'http:') {
    throw new CliError(`broker URL must be http(s): ${raw}`, 2);
  }
  return raw.replace(/\/+$/, '');
}

/** Map an http(s) broker base to its ws(s) origin for the /signal WebSocket. */
export function brokerWsBase(httpBase: string): string {
  return httpBase.replace(/^http/i, 'ws');
}

/**
 * Read the admin Bearer token from `--token` or `ZEUS_ADMIN_TOKEN`. Throws a
 * CliError (rather than returning empty) so every Bearer command fails the same
 * clear way when no token is configured.
 */
export function requireAdminToken(flag?: string): string {
  const token = flag || process.env.ZEUS_ADMIN_TOKEN || '';
  if (!token) {
    throw new CliError(
      'no admin token: pass --token or set ZEUS_ADMIN_TOKEN (mint one with `zeus-support token mint`).',
      2,
    );
  }
  return token;
}

/** Normalise a callsign the way the broker does (trim + uppercase). */
export function normCallsign(callsign: string): string {
  return callsign.trim().toUpperCase();
}
