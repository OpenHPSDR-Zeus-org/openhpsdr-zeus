/**
 * Thin HTTP client for the broker /admin/* surface. Uses the global `fetch`
 * (Node 18+). Every method maps a non-2xx into a CliError with a useful message,
 * preserving the few status codes the CLI must handle specially (e.g. 503
 * "operator offline" from /admin/request).
 */

import { CliError } from './config.js';
import type {
  LoginResponse,
  MintTokenResponse,
  PresenceResponse,
  RequestResponse,
} from './types.js';

export interface BrokerClientOptions {
  /** Broker base URL, no trailing slash (see resolveBrokerUrl). */
  baseUrl: string;
  /** Admin Bearer token for protected routes (omit for /admin/login). */
  token?: string;
  /** Per-request timeout in ms. */
  timeoutMs?: number;
}

export class BrokerClient {
  private readonly baseUrl: string;
  private readonly token?: string;
  private readonly timeoutMs: number;

  constructor(opts: BrokerClientOptions) {
    this.baseUrl = opts.baseUrl.replace(/\/+$/, '');
    this.token = opts.token;
    this.timeoutMs = opts.timeoutMs ?? 15_000;
  }

  // --- POST /admin/login (no Bearer; QRZ headers + password) ----------------

  async login(args: {
    qrzSession: string;
    callsign: string;
    password: string;
  }): Promise<LoginResponse> {
    const res = await this.fetch('/admin/login', {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'X-QRZ-Session': args.qrzSession,
        'X-QRZ-Callsign': args.callsign,
      },
      body: JSON.stringify({ password: args.password }),
    });
    if (res.status === 401) {
      throw new CliError('login failed: unauthorized (bad QRZ session, callsign, or password).');
    }
    if (res.status === 429) {
      throw new CliError('login rate limited — wait a moment and retry.', 4);
    }
    return this.asJson<LoginResponse>(res, 'login');
  }

  // --- POST /admin/tokens (Bearer session token) ----------------------------

  async mintToken(label = 'agent'): Promise<MintTokenResponse> {
    const res = await this.fetch('/admin/tokens', {
      method: 'POST',
      headers: this.authHeaders({ 'content-type': 'application/json' }),
      body: JSON.stringify({ label }),
    });
    this.assertAuthorized(res, 'mint token');
    return this.asJson<MintTokenResponse>(res, 'mint token');
  }

  // --- GET /admin/presence (Bearer) -----------------------------------------

  async presence(): Promise<PresenceResponse> {
    const res = await this.fetch('/admin/presence', {
      method: 'GET',
      headers: this.authHeaders(),
    });
    this.assertAuthorized(res, 'presence');
    return this.asJson<PresenceResponse>(res, 'presence');
  }

  // --- POST /admin/request (Bearer) -----------------------------------------

  async request(callsign: string): Promise<RequestResponse> {
    const res = await this.fetch('/admin/request', {
      method: 'POST',
      headers: this.authHeaders({ 'content-type': 'application/json' }),
      body: JSON.stringify({ callsign }),
    });
    if (res.status === 503) {
      // The broker returns this when the operator's sidecar is not online.
      throw new CliError(`operator ${callsign} is offline (no support-available sidecar).`, 5);
    }
    this.assertAuthorized(res, 'request');
    return this.asJson<RequestResponse>(res, 'request');
  }

  // --- internals ------------------------------------------------------------

  private authHeaders(extra: Record<string, string> = {}): Record<string, string> {
    if (!this.token) throw new CliError('internal: Bearer token required for this route.', 2);
    return { Authorization: `Bearer ${this.token}`, ...extra };
  }

  private assertAuthorized(res: Response, what: string): void {
    if (res.status === 401) {
      throw new CliError(`${what} failed: unauthorized (token missing, expired, or revoked).`);
    }
    if (res.status === 403) {
      throw new CliError(`${what} failed: forbidden.`);
    }
  }

  private async fetch(path: string, init: RequestInit): Promise<Response> {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), this.timeoutMs);
    try {
      return await fetch(`${this.baseUrl}${path}`, { ...init, signal: controller.signal });
    } catch (err) {
      if (err instanceof Error && err.name === 'AbortError') {
        throw new CliError(`request to ${path} timed out after ${this.timeoutMs}ms.`, 6);
      }
      throw new CliError(`request to ${path} failed: ${(err as Error).message}.`, 6);
    } finally {
      clearTimeout(timer);
    }
  }

  private async asJson<T>(res: Response, what: string): Promise<T> {
    const text = await res.text();
    if (!res.ok) {
      const detail = safeErrorMessage(text);
      throw new CliError(`${what} failed: HTTP ${res.status}${detail ? ` (${detail})` : ''}.`);
    }
    try {
      return JSON.parse(text) as T;
    } catch {
      throw new CliError(`${what} failed: broker returned non-JSON response.`);
    }
  }
}

/** Pull a short `{error:"..."}` message out of a broker error body, if present. */
function safeErrorMessage(text: string): string {
  try {
    const v = JSON.parse(text) as { error?: unknown };
    if (v && typeof v.error === 'string') return v.error;
  } catch {
    /* not JSON */
  }
  return '';
}
