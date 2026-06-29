import { DurableObject } from 'cloudflare:workers';
import type { Env } from './types';

/**
 * Per-connection state, persisted on the socket so it survives DO hibernation.
 * One `host` (the radio) and any number of `client`s (remote browsers) share a
 * room keyed by callsign. A `support` connection is a maintainer's read-only
 * diagnostics session (remote-diag P3): it behaves like a client but is bound to
 * an operator-approvable `requestId` and identified by the admin callsign.
 */
interface Att {
  role: 'host' | 'client' | 'support';
  callsign: string;
  /** Stable id for a client/support connection, so the host can address its answer. */
  clientId?: string;
  /** (support only) the maintainer request this connection serves. */
  requestId?: string;
  /** (support only) the admin callsign, for the operator's prompt + audit. */
  admin?: string;
}

/** A redeemable support ticket (stored in DO storage so it survives hibernation). */
interface SupportTicket {
  requestId: string;
  admin: string;
  /** unix ms; redemption past this fails. */
  expiresAt: number;
}

/** How long an issued support ticket stays redeemable (the dashboard connects promptly). */
const SUPPORT_TICKET_TTL_MS = 120_000;

/**
 * WebRTC signaling relay for one callsign (idFromName(callsign)). The browser
 * cannot reach the radio directly over the internet, so the radio keeps a
 * persistent `host` socket here and the broker relays SDP/ICE between it and
 * each `client`/`support` connection. The broker never sees media — only
 * signaling — and never the session password or support grant (both are proven
 * end-to-end at the radio, ADR-0008).
 *
 * Maintainer-support flow (remote-diag P3):
 *   1. admin-api POSTs `/support-request` here (after verifying the admin); we
 *      mint a requestId + a single-use ticket, push `support-request` to the host,
 *      and return {requestId, ticket}.
 *   2. the dashboard opens a `role=support` WS with that ticket; we redeem it and
 *      bind the socket to the requestId/admin.
 *   3. the operator approves at the radio; the host sends `support-grant{requestId}`,
 *      which we route to the matching support socket.
 *   4. the support socket sends an `offer`; we stamp `support:true`+`requestId`
 *      (from the trusted attachment, never client-supplied) and relay to the host,
 *      whose `answer` routes back by clientId.
 *
 * Uses the WebSocket Hibernation API so a long-lived-but-idle session costs no
 * GB-s between the bursty exchanges.
 */
export class SignalRoom extends DurableObject<Env> {
  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    // Keepalive without waking the DO.
    this.ctx.setWebSocketAutoResponse(
      new WebSocketRequestResponsePair(
        JSON.stringify({ t: 'ping' }),
        JSON.stringify({ t: 'pong' }),
      ),
    );
  }

  override async fetch(request: Request): Promise<Response> {
    const url = new URL(request.url);

    // Internal control path (called by admin-api after verifying the admin): mint
    // a request + ticket and notify the host. NOT a WebSocket upgrade.
    if (url.pathname === '/support-request' && request.method === 'POST') {
      return this.createSupportRequest(request);
    }

    const roleParam = url.searchParams.get('role');
    const role: Att['role'] = roleParam === 'host' ? 'host' : roleParam === 'support' ? 'support' : 'client';
    const callsign = (request.headers.get('X-Operator-Callsign') ?? url.searchParams.get('callsign') ?? '')
      .trim()
      .toUpperCase();

    // A support connection must redeem a valid, unexpired, single-use ticket
    // (the admin proved authorisation to admin-api to obtain it). Validate BEFORE
    // accepting the socket so an unauthorised peer never joins the room.
    let ticket: SupportTicket | null = null;
    if (role === 'support') {
      ticket = await this.redeemTicket(url.searchParams.get('ticket') ?? '');
      if (!ticket) return new Response('invalid or expired support ticket', { status: 401 });
    }

    const pair = new WebSocketPair();
    const client = pair[0];
    const server = pair[1];
    this.ctx.acceptWebSocket(server);

    if (role === 'host') {
      // Single host per callsign: evict any stale host (e.g. a reconnect).
      for (const ws of this.ctx.getWebSockets()) {
        const a = ws.deserializeAttachment() as Att | null;
        if (a?.role === 'host') {
          try { ws.close(4000, 'replaced by newer host'); } catch { /* already closing */ }
        }
      }
      server.serializeAttachment({ role: 'host', callsign } satisfies Att);
    } else if (role === 'support') {
      server.serializeAttachment({
        role: 'support',
        callsign,
        clientId: crypto.randomUUID(),
        requestId: ticket!.requestId,
        admin: ticket!.admin,
      } satisfies Att);
    } else {
      server.serializeAttachment({ role: 'client', callsign, clientId: crypto.randomUUID() } satisfies Att);
    }

    return new Response(null, { status: 101, webSocket: client });
  }

  override async webSocketMessage(ws: WebSocket, raw: string | ArrayBuffer): Promise<void> {
    const att = ws.deserializeAttachment() as Att | null;
    if (!att) return;

    let msg: { t?: string; clientId?: string; [k: string]: unknown };
    try {
      msg = JSON.parse(typeof raw === 'string' ? raw : new TextDecoder().decode(raw));
    } catch {
      return;
    }

    if (att.role === 'client' || att.role === 'support') {
      // client/support → host: offer / candidate / bye, tagged with this
      // connection's id. For support we additionally stamp support:true and the
      // requestId FROM THE TRUSTED ATTACHMENT (never from client-supplied fields),
      // so a support peer cannot masquerade as a normal client or forge a grant.
      const host = this.host();
      if (!host) {
        this.send(ws, { t: 'offline' });
        return;
      }
      if (msg.t === 'offer' || msg.t === 'candidate' || msg.t === 'bye') {
        const tagged =
          att.role === 'support'
            ? { ...msg, clientId: att.clientId, support: true, requestId: att.requestId }
            : { ...msg, clientId: att.clientId };
        this.send(host, tagged);
      }
    } else {
      // host → answer / candidate / bye to a specific connection by clientId, or
      // support-grant to the support connection bound to a requestId.
      if (msg.t === 'support-grant') {
        const target = typeof msg.requestId === 'string' ? this.supportByRequestId(msg.requestId) : undefined;
        if (target) this.send(target, { t: 'support-grant', requestId: msg.requestId });
        return;
      }
      const target = msg.clientId ? this.connectionById(msg.clientId) : undefined;
      if (!target) return;
      if (msg.t === 'answer' || msg.t === 'candidate' || msg.t === 'bye') {
        const { clientId: _omit, ...rest } = msg;
        this.send(target, rest);
      }
    }
  }

  override async webSocketClose(ws: WebSocket, code: number, reason: string): Promise<void> {
    const att = ws.deserializeAttachment() as Att | null;
    try { ws.close(code, reason); } catch { /* already closing */ }

    if (att?.role === 'host') {
      // Radio went away — every in-flight client/support connection should know.
      for (const c of this.nonHostConnections()) this.send(c, { t: 'offline' });
    } else if (att?.clientId) {
      const host = this.host();
      if (host) this.send(host, { t: 'bye', clientId: att.clientId });
    }
  }

  override async webSocketError(): Promise<void> {
    /* nothing to reconcile — close handles teardown */
  }

  // -- maintainer-support control --------------------------------------------

  /**
   * Mint a requestId + single-use ticket for a maintainer support request and
   * push `support-request` to the host. Called by admin-api (which has already
   * verified the admin). Returns 503 if the operator (host) is offline.
   */
  private async createSupportRequest(request: Request): Promise<Response> {
    let admin = '';
    try {
      const body = (await request.json()) as { admin?: string };
      admin = (body.admin ?? '').trim().toUpperCase();
    } catch {
      /* empty body → anonymous admin label */
    }

    const host = this.host();
    if (!host) return Response.json({ error: 'operator offline' }, { status: 503 });

    const requestId = crypto.randomUUID();
    const ticket = randomToken();
    await this.ctx.storage.put(
      `ticket:${ticket}`,
      { requestId, admin, expiresAt: Date.now() + SUPPORT_TICKET_TTL_MS } satisfies SupportTicket,
    );
    this.send(host, { t: 'support-request', requestId, admin });
    return Response.json({ requestId, ticket });
  }

  /** Redeem (consume) a support ticket; returns it iff present and unexpired. */
  private async redeemTicket(ticket: string): Promise<SupportTicket | null> {
    if (!ticket) return null;
    const key = `ticket:${ticket}`;
    const stored = await this.ctx.storage.get<SupportTicket>(key);
    if (!stored) return null;
    await this.ctx.storage.delete(key); // single-use, redeemed exactly once
    if (stored.expiresAt <= Date.now()) return null;
    return stored;
  }

  // -- connection lookups -----------------------------------------------------

  private host(): WebSocket | undefined {
    for (const ws of this.ctx.getWebSockets()) {
      if ((ws.deserializeAttachment() as Att | null)?.role === 'host') return ws;
    }
    return undefined;
  }

  private nonHostConnections(): WebSocket[] {
    return this.ctx
      .getWebSockets()
      .filter((ws) => (ws.deserializeAttachment() as Att | null)?.role !== 'host');
  }

  /** Any non-host connection (client or support) addressed by its clientId. */
  private connectionById(id: string): WebSocket | undefined {
    for (const ws of this.ctx.getWebSockets()) {
      const a = ws.deserializeAttachment() as Att | null;
      if (a && a.role !== 'host' && a.clientId === id) return ws;
    }
    return undefined;
  }

  /** The support connection bound to a requestId (for routing support-grant). */
  private supportByRequestId(requestId: string): WebSocket | undefined {
    for (const ws of this.ctx.getWebSockets()) {
      const a = ws.deserializeAttachment() as Att | null;
      if (a?.role === 'support' && a.requestId === requestId) return ws;
    }
    return undefined;
  }

  private send(ws: WebSocket, msg: unknown): void {
    try { ws.send(JSON.stringify(msg)); } catch { /* socket gone */ }
  }
}

/** A URL-safe random token (24 bytes → base64url) for one-time support tickets. */
function randomToken(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(24));
  let bin = '';
  for (const b of bytes) bin += String.fromCharCode(b);
  return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
