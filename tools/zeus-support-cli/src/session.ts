/**
 * Support-session client: drives the full P3b flow once a `ticket` is in hand.
 *
 *   1. open  wss://<broker>/signal?role=support&callsign=<OP>&ticket=<ticket>
 *   2. wait  for {t:"support-grant", requestId} (operator pressed Allow)
 *   3. WebRTC: create `control`/`api`/`log` data channels, build an offer,
 *      send {t:"offer", sdp} (broker stamps support+requestId — we never do)
 *   4. recv  {t:"answer", sdp, support:true}; set remote description
 *   5. use   the data channels: control hello → support-ready, api GETs, log stream
 *
 * The broker auto-answers our {t:"ping"} with {t:"pong"}; we heartbeat to keep
 * the (hibernating) signaling socket warm. werift gives us a pure-JS WebRTC stack
 * so this runs headless under Node with no browser / native deps.
 */

import { WebSocket } from 'ws';
import { RTCPeerConnection } from 'werift';
import type { RTCDataChannel } from 'werift';
import { CliError } from './config.js';
import type {
  ApiChannelRequest,
  ApiChannelResponse,
  ControlInbound,
  LogInbound,
  SupportWsInbound,
} from './types.js';

export interface SupportSessionOptions {
  /** ws(s) broker base, no trailing slash (see brokerWsBase). */
  wsBase: string;
  /** Target operator callsign (already normalised). */
  callsign: string;
  /** Single-use ticket from POST /admin/request. */
  ticket: string;
  /** Expected requestId (for sanity-checking the grant); optional. */
  requestId?: string;
  /** Max time to wait for the operator to Allow (support-grant). Default 90s. */
  grantTimeoutMs?: number;
  /** Max time to wait for the WebRTC answer + connection. Default 30s. */
  connectTimeoutMs?: number;
  /** Optional structured logger; defaults to stderr. */
  log?: (msg: string) => void;
}

interface OpenChannels {
  control: RTCDataChannel;
  api: RTCDataChannel;
  log: RTCDataChannel;
}

/** A connected support session: issue API GETs, collect logs, then close(). */
export class SupportSession {
  private constructor(
    private readonly ws: WebSocket,
    private readonly pc: RTCPeerConnection,
    private readonly chans: OpenChannels,
    readonly requestId: string,
    readonly admin: string,
    private readonly heartbeat: ReturnType<typeof setInterval>,
    private readonly logFn: (msg: string) => void,
  ) {}

  /**
   * Run the whole connect handshake and resolve with a live SupportSession.
   * Throws CliError on offline/denied/timeout so the CLI can exit cleanly.
   */
  static async connect(opts: SupportSessionOptions): Promise<SupportSession> {
    const log = opts.log ?? ((m: string) => process.stderr.write(`${m}\n`));
    const grantTimeout = opts.grantTimeoutMs ?? 90_000;
    const connectTimeout = opts.connectTimeoutMs ?? 30_000;

    const url =
      `${opts.wsBase}/signal?role=support` +
      `&callsign=${encodeURIComponent(opts.callsign)}` +
      `&ticket=${encodeURIComponent(opts.ticket)}`;

    const ws = new WebSocket(url);
    const pc = new RTCPeerConnection({
      iceServers: [{ urls: 'stun:stun.l.google.com:19302' }],
    });

    // Buffer everything received per-channel before the consumer attaches.
    const logBacklog: string[] = [];
    const logLive: string[] = [];
    let logStarted = false;

    const chans: OpenChannels = {
      control: pc.createDataChannel('control'),
      api: pc.createDataChannel('api'),
      log: pc.createDataChannel('log'),
    };

    const apiWaiters = new Map<string, (r: ApiChannelResponse) => void>();
    let controlReady: ((c: { requestId: string; admin: string }) => void) | null = null;

    chans.control.onMessage.subscribe((data) => {
      const msg = parse<ControlInbound>(data);
      if (msg?.t === 'support-ready' && controlReady) {
        controlReady({ requestId: String(msg.requestId), admin: String(msg.admin) });
        controlReady = null;
      }
    });
    chans.api.onMessage.subscribe((data) => {
      const msg = parse<ApiChannelResponse>(data);
      if (msg && typeof msg.id === 'string') {
        const w = apiWaiters.get(msg.id);
        if (w) {
          apiWaiters.delete(msg.id);
          w(msg);
        }
      }
    });
    chans.log.onMessage.subscribe((data) => {
      const msg = parse<LogInbound>(data);
      if (!msg) return;
      if (msg.t === 'backlog' && Array.isArray(msg.lines)) {
        for (const l of msg.lines) logBacklog.push(String(l));
        logStarted = true;
      } else if (msg.t === 'line' && typeof msg.line === 'string') {
        logLive.push(msg.line);
      }
    });

    try {
      await waitOpen(ws, connectTimeout);
    } catch (err) {
      safeClose(ws, pc);
      throw err;
    }

    // Heartbeat the signaling socket (broker auto-pongs without waking the DO).
    const heartbeat = setInterval(() => {
      if (ws.readyState === ws.OPEN) trySend(ws, { t: 'ping' });
    }, 20_000);

    try {
      // --- 1) wait for the operator to Allow --------------------------------
      log(`waiting up to ${Math.round(grantTimeout / 1000)}s for ${opts.callsign} to Allow…`);
      const grant = await waitForGrant(ws, grantTimeout, opts.requestId);
      const requestId = grant.requestId;
      log(`granted (requestId=${requestId}); negotiating WebRTC…`);

      // --- 2) offer (ICE-gather to completion, then send full SDP) ----------
      const offer = await pc.createOffer();
      await pc.setLocalDescription(offer);
      await waitIceComplete(pc, connectTimeout);
      const localSdp = pc.localDescription?.sdp;
      if (!localSdp) throw new CliError('failed to produce a local SDP offer.', 7);
      // The broker stamps support+requestId; we send ONLY t+sdp.
      trySend(ws, { t: 'offer', sdp: localSdp });

      // --- 3) answer --------------------------------------------------------
      const answer = await waitForAnswer(ws, connectTimeout);
      await pc.setRemoteDescription({ type: 'answer', sdp: answer.sdp });

      // --- 4) data-channel + connection establishment -----------------------
      await waitConnected(pc, connectTimeout);
      await Promise.all([
        waitChannelOpen(chans.control, connectTimeout),
        waitChannelOpen(chans.api, connectTimeout),
        waitChannelOpen(chans.log, connectTimeout),
      ]);

      // --- 5) control handshake → support-ready -----------------------------
      const ready = await new Promise<{ requestId: string; admin: string }>((resolve, reject) => {
        const t = setTimeout(() => reject(new CliError('no support-ready from the radio.', 7)), connectTimeout);
        controlReady = (c) => {
          clearTimeout(t);
          resolve(c);
        };
        trySend(chans.control, { t: 'hello' });
      });

      // Kick the log channel so the radio sends its backlog + live stream.
      trySend(chans.log, { t: 'hello' });

      const session = new SupportSession(ws, pc, chans, requestId, ready.admin, heartbeat, log);
      // expose internal buffers to instance methods via closure-bound getters
      session.attachLogBuffers(() => logBacklog, () => logLive, () => logStarted);
      session.attachApiWaiters(apiWaiters);
      return session;
    } catch (err) {
      clearInterval(heartbeat);
      safeClose(ws, pc);
      throw err;
    }
  }

  // --- buffers wired in by connect() -----------------------------------------

  private getBacklog: () => string[] = () => [];
  private getLive: () => string[] = () => [];
  private apiWaiters: Map<string, (r: ApiChannelResponse) => void> = new Map();

  private attachLogBuffers(
    backlog: () => string[],
    live: () => string[],
    _started: () => boolean,
  ): void {
    this.getBacklog = backlog;
    this.getLive = live;
  }

  private attachApiWaiters(m: Map<string, (r: ApiChannelResponse) => void>): void {
    this.apiWaiters = m;
  }

  // --- public API ------------------------------------------------------------

  /** Issue one GET over the `api` data channel and await the radio's response. */
  async apiGet(path: string, timeoutMs = 15_000): Promise<ApiChannelResponse> {
    const id = randomId();
    const req: ApiChannelRequest = { id, method: 'GET', path };
    return await new Promise<ApiChannelResponse>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.apiWaiters.delete(id);
        reject(new CliError(`api GET ${path} timed out.`, 7));
      }, timeoutMs);
      this.apiWaiters.set(id, (r) => {
        clearTimeout(timer);
        resolve(r);
      });
      trySend(this.chans.api, req);
    });
  }

  /** Snapshot the log backlog received so far. */
  logBacklog(): string[] {
    return [...this.getBacklog()];
  }

  /** Collect live log lines for `ms`, then return everything captured. */
  async collectLogs(ms: number): Promise<{ backlog: string[]; live: string[] }> {
    if (ms > 0) {
      this.logFn(`capturing ${Math.round(ms / 1000)}s of live log…`);
      await delay(ms);
    }
    return { backlog: [...this.getBacklog()], live: [...this.getLive()] };
  }

  /** Tear the session down: bye on control, close channels, peer, socket. */
  close(): void {
    clearInterval(this.heartbeat);
    trySend(this.ws, { t: 'bye' });
    safeClose(this.ws, this.pc);
  }
}

// --- WebSocket / WebRTC wait helpers ------------------------------------------

function waitOpen(ws: WebSocket, timeoutMs: number): Promise<void> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      cleanup();
      reject(new CliError('timed out opening the support WebSocket.', 6));
    }, timeoutMs);
    const onOpen = () => {
      cleanup();
      resolve();
    };
    const onErr = (err: Error) => {
      cleanup();
      reject(new CliError(`support WebSocket error: ${err.message}.`, 6));
    };
    const onClose = () => {
      cleanup();
      reject(new CliError('support WebSocket closed before opening (bad ticket?).', 6));
    };
    function cleanup() {
      clearTimeout(timer);
      ws.off('open', onOpen);
      ws.off('error', onErr);
      ws.off('close', onClose);
    }
    ws.on('open', onOpen);
    ws.on('error', onErr);
    ws.on('close', onClose);
  });
}

function waitForGrant(
  ws: WebSocket,
  timeoutMs: number,
  expectRequestId?: string,
): Promise<{ requestId: string }> {
  return waitForFrame(
    ws,
    timeoutMs,
    (m) => {
      if (m.t === 'offline') throw new CliError('operator went offline before granting.', 5);
      if (m.t === 'support-grant') {
        const requestId = String((m as { requestId?: unknown }).requestId ?? '');
        if (expectRequestId && requestId && requestId !== expectRequestId) return undefined;
        return { requestId };
      }
      return undefined;
    },
    'operator did not Allow in time (denied or timed out).',
    8,
  );
}

function waitForAnswer(ws: WebSocket, timeoutMs: number): Promise<{ sdp: string }> {
  return waitForFrame(
    ws,
    timeoutMs,
    (m) => {
      if (m.t === 'offline') throw new CliError('operator went offline during negotiation.', 5);
      if (m.t === 'answer' && typeof (m as { sdp?: unknown }).sdp === 'string') {
        return { sdp: (m as { sdp: string }).sdp };
      }
      return undefined;
    },
    'no WebRTC answer received.',
    7,
  );
}

/**
 * Generic single-frame waiter over the support WS: resolves with the first
 * frame for which `match` returns a value; rejects on timeout. `match` may throw
 * a CliError to fail fast (e.g. an `offline` frame).
 */
function waitForFrame<T>(
  ws: WebSocket,
  timeoutMs: number,
  match: (m: SupportWsInbound) => T | undefined,
  timeoutMsg: string,
  timeoutCode: number,
): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => {
      cleanup();
      reject(new CliError(timeoutMsg, timeoutCode));
    }, timeoutMs);
    const onMsg = (raw: WsData) => {
      const m = parse<SupportWsInbound>(raw);
      if (!m || typeof m.t !== 'string') return;
      try {
        const v = match(m);
        if (v !== undefined) {
          cleanup();
          resolve(v);
        }
      } catch (err) {
        cleanup();
        reject(err);
      }
    };
    const onClose = () => {
      cleanup();
      reject(new CliError('support WebSocket closed unexpectedly.', 6));
    };
    function cleanup() {
      clearTimeout(timer);
      ws.off('message', onMsg);
      ws.off('close', onClose);
    }
    ws.on('message', onMsg);
    ws.on('close', onClose);
  });
}

function waitIceComplete(pc: RTCPeerConnection, timeoutMs: number): Promise<void> {
  if (pc.iceGatheringState === 'complete') return Promise.resolve();
  return new Promise((resolve) => {
    const timer = setTimeout(() => resolve(), timeoutMs); // proceed with what we have
    const sub = pc.iceGatheringStateChange.subscribe((state) => {
      if (state === 'complete') {
        clearTimeout(timer);
        sub.unSubscribe();
        resolve();
      }
    });
  });
}

function waitConnected(pc: RTCPeerConnection, timeoutMs: number): Promise<void> {
  if (pc.connectionState === 'connected') return Promise.resolve();
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      sub.unSubscribe();
      reject(new CliError('WebRTC peer connection did not establish.', 7));
    }, timeoutMs);
    const sub = pc.connectionStateChange.subscribe((state) => {
      if (state === 'connected') {
        clearTimeout(timer);
        sub.unSubscribe();
        resolve();
      } else if (state === 'failed' || state === 'closed') {
        clearTimeout(timer);
        sub.unSubscribe();
        reject(new CliError(`WebRTC connection ${state}.`, 7));
      }
    });
  });
}

function waitChannelOpen(dc: RTCDataChannel, timeoutMs: number): Promise<void> {
  if (dc.readyState === 'open') return Promise.resolve();
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      sub.unSubscribe();
      reject(new CliError(`data channel "${dc.label}" did not open.`, 7));
    }, timeoutMs);
    const sub = dc.stateChanged.subscribe((state) => {
      if (state === 'open') {
        clearTimeout(timer);
        sub.unSubscribe();
        resolve();
      } else if (state === 'closed') {
        clearTimeout(timer);
        sub.unSubscribe();
        reject(new CliError(`data channel "${dc.label}" closed before opening.`, 7));
      }
    });
  });
}

// --- small utilities ----------------------------------------------------------

type WsData = string | Buffer | ArrayBuffer | Buffer[];

function parse<T>(raw: WsData | string | Buffer): T | null {
  try {
    const text =
      typeof raw === 'string'
        ? raw
        : Buffer.isBuffer(raw)
          ? raw.toString('utf8')
          : Array.isArray(raw)
            ? Buffer.concat(raw).toString('utf8')
            : Buffer.from(raw as ArrayBuffer).toString('utf8');
    return JSON.parse(text) as T;
  } catch {
    return null;
  }
}

function trySend(target: WebSocket | RTCDataChannel, msg: unknown): void {
  try {
    target.send(JSON.stringify(msg));
  } catch {
    /* socket/channel gone */
  }
}

function safeClose(ws: WebSocket, pc: RTCPeerConnection): void {
  try {
    ws.close();
  } catch {
    /* already closing */
  }
  try {
    void pc.close();
  } catch {
    /* already closing */
  }
}

function delay(ms: number): Promise<void> {
  return new Promise((r) => setTimeout(r, ms));
}

function randomId(): string {
  return `req_${Math.random().toString(36).slice(2)}${Date.now().toString(36)}`;
}
