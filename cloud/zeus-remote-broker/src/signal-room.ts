import { DurableObject } from 'cloudflare:workers';
import type { Env } from './types';

/**
 * Per-connection state, persisted on the socket so it survives DO hibernation.
 * One `host` (the radio) and any number of `client`s (remote browsers) share a
 * room keyed by callsign.
 */
interface Att {
  role: 'host' | 'client';
  callsign: string;
  /** Stable id for a client connection, so the host can address its answer. */
  clientId?: string;
}

/**
 * WebRTC signaling relay for one callsign (idFromName(callsign)). The browser
 * cannot reach the radio directly over the internet, so the radio keeps a
 * persistent `host` socket here and the broker relays SDP/ICE between it and
 * each `client`. The broker never sees media — only signaling — and never the
 * session password (that is proven end-to-end at the radio, ADR-0008).
 *
 * Uses the WebSocket Hibernation API so a long-lived-but-idle session costs no
 * GB-s between the bursty offer/answer/candidate exchanges.
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
    const role = url.searchParams.get('role') === 'host' ? 'host' : 'client';
    const callsign = (request.headers.get('X-Operator-Callsign') ?? url.searchParams.get('callsign') ?? '')
      .trim()
      .toUpperCase();

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

    if (att.role === 'client') {
      // client → host: offer / candidate / bye. Tag with this client's id.
      const host = this.host();
      if (!host) {
        this.send(ws, { t: 'offline' });
        return;
      }
      if (msg.t === 'offer' || msg.t === 'candidate' || msg.t === 'bye') {
        this.send(host, { ...msg, clientId: att.clientId });
      }
    } else {
      // host → a specific client by clientId: answer / candidate / bye.
      const target = msg.clientId ? this.clientById(msg.clientId) : undefined;
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
      // Radio went away — every in-flight client should know.
      for (const c of this.clients()) this.send(c, { t: 'offline' });
    } else if (att?.clientId) {
      const host = this.host();
      if (host) this.send(host, { t: 'bye', clientId: att.clientId });
    }
  }

  override async webSocketError(): Promise<void> {
    /* nothing to reconcile — close handles teardown */
  }

  private host(): WebSocket | undefined {
    for (const ws of this.ctx.getWebSockets()) {
      if ((ws.deserializeAttachment() as Att | null)?.role === 'host') return ws;
    }
    return undefined;
  }

  private clients(): WebSocket[] {
    return this.ctx
      .getWebSockets()
      .filter((ws) => (ws.deserializeAttachment() as Att | null)?.role === 'client');
  }

  private clientById(id: string): WebSocket | undefined {
    for (const ws of this.ctx.getWebSockets()) {
      const a = ws.deserializeAttachment() as Att | null;
      if (a?.role === 'client' && a.clientId === id) return ws;
    }
    return undefined;
  }

  private send(ws: WebSocket, msg: unknown): void {
    try { ws.send(JSON.stringify(msg)); } catch { /* socket gone */ }
  }
}
