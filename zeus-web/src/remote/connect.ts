// SPDX-License-Identifier: GPL-2.0-or-later
//
// Browser remote-access connect flow (Phase 1/3 client). Opens a WebRTC peer,
// runs the SPAKE2+ password handshake over the control DataChannel, and surfaces
// radio frames once UNLOCKED. Two signalling transports share one handshake core:
//   - connectRemote     — LAN: POST the offer straight to the reachable radio.
//   - connectViaBroker   — internet: relay the offer through the Cloudflare broker
//                          (the radio is behind NAT and not directly reachable).
//
// The crypto is covered by spake2plus.test.ts / registration.test.ts. The live
// WebRTC connection itself is bench-verified (no RTCPeerConnection in vitest).
//
// NOTE: Argon2id runs on the calling thread; move deriveScalars into a Web Worker
// for production so the 64 MiB derivation doesn't jank the UI.

import { Spake2Plus, verifyPeerConfirm, type Spake2Outcome } from './spake2plus';
import { deriveScalars, type Argon2Params } from './registration';

const CONTEXT = new TextEncoder().encode('zeus-remote-access/v1');
const ID_PROVER = new Uint8Array(0);
const ID_VERIFIER = new Uint8Array(0);

const DEFAULT_BROKER = 'https://remote.openhpsdrzeus.com';
const DEFAULT_STUN: RTCIceServer[] = [{ urls: 'stun:stun.cloudflare.com:3478' }];

export interface RemoteConnection {
  pc: RTCPeerConnection;
  frames: RTCDataChannel;
  /**
   * The reliable, ordered control channel. After unlock it also carries the
   * 2-byte stream-request control frames (0x21 audio / 0x22 display) the client
   * sends to enable RX egress — see setRemoteControlSender in ws-client.ts.
   */
  control: RTCDataChannel;
  /**
   * The reliable, ordered API tunnel channel. After unlock it carries
   * read-only (GET/HEAD) `/api/*` request/response pairs correlated by integer
   * `id`, so the SPA's REST chrome (VFO/mode/band/capabilities/…) populates
   * over WebRTC when there is no same-origin backend — see api-tunnel.ts.
   */
  api: RTCDataChannel;
  /**
   * Enable/disable the voice-mic uplink (remote SSB/AM/FM TX). The first
   * `true` lazily prompts for mic permission and swaps the live track into the
   * pre-negotiated sendonly audio m-line (no renegotiation); subsequent calls
   * just toggle it. Driven by MOX in remote-client.ts. Rejects if the mic is
   * denied/unavailable — TX keying still works, just without voice audio.
   */
  setMicEnabled(on: boolean): Promise<void>;
  close(): void;
}

export interface RemoteConnectOptions {
  password: string;
  /** API origin for the LAN path; '' (default) = same-origin. */
  apiBase?: string;
  /** ICE servers; production fills this from broker-minted TURN credentials. */
  iceServers?: RTCIceServer[];
  /** Called with each binary radio frame after unlock. */
  onFrame?: (data: ArrayBuffer) => void;
}

export interface BrokerConnectOptions {
  callsign: string;
  password: string;
  /** Broker origin; defaults to remote.openhpsdrzeus.com. */
  brokerOrigin?: string;
  onFrame?: (data: ArrayBuffer) => void;
}

/** Returns the answer SDP for a given offer (via whatever signalling transport). */
type Signaler = (offerSdp: string) => Promise<string>;

/**
 * LAN: signal directly to a reachable radio (POST /api/remote/connect), then
 * prove the session password over SPAKE2+.
 */
export function connectRemote(opts: RemoteConnectOptions): Promise<RemoteConnection> {
  const signal: Signaler = async (offerSdp) => {
    const resp = await fetch(`${opts.apiBase ?? ''}/api/remote/connect`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ sdp: offerSdp }),
    });
    if (resp.status === 403) throw new Error('Remote access is not enabled (no session password set).');
    if (!resp.ok) throw new Error(`connect failed: HTTP ${resp.status}`);
    return (await resp.json()).sdp as string;
  };
  return establish(
    { password: opts.password, iceServers: opts.iceServers ?? DEFAULT_STUN, onFrame: opts.onFrame },
    signal,
    1500, // LAN: host candidates are immediate
  );
}

/**
 * Internet: relay the offer through the Cloudflare broker to a radio behind NAT,
 * fetching TURN credentials first (falls back to STUN if TURN is unconfigured).
 */
export async function connectViaBroker(opts: BrokerConnectOptions): Promise<RemoteConnection> {
  const brokerOrigin = opts.brokerOrigin ?? DEFAULT_BROKER;
  const iceServers = await fetchIceServers(brokerOrigin);
  const signal: Signaler = (offerSdp) => brokerSignal(brokerOrigin, opts.callsign, offerSdp);
  return establish(
    { password: opts.password, iceServers, onFrame: opts.onFrame },
    signal,
    5000, // internet: wait for STUN server-reflexive candidates
  );
}

// --- shared handshake core -------------------------------------------------

async function establish(
  opts: { password: string; iceServers: RTCIceServer[]; onFrame?: (d: ArrayBuffer) => void },
  signal: Signaler,
  iceTimeoutMs: number,
): Promise<RemoteConnection> {
  const pc = new RTCPeerConnection({ iceServers: opts.iceServers });
  const control = pc.createDataChannel('control'); // reliable, ordered
  const frames = pc.createDataChannel('frames', { ordered: false, maxRetransmits: 0 });
  const api = pc.createDataChannel('api'); // reliable, ordered — read-write REST tunnel
  if (opts.onFrame) frames.onmessage = (e: MessageEvent) => opts.onFrame!(e.data as ArrayBuffer);

  // Sendonly Opus audio m-line for remote voice TX. Added up front so the offer
  // carries m=audio (the radio answers recvonly), but getUserMedia is deferred
  // until the operator actually keys: replaceTrack swaps the live mic into this
  // sender with NO renegotiation, so an RX-only session never prompts for the
  // mic. The browser encodes Opus natively — low latency, in-band FEC + PLC.
  const audioSender = pc.addTransceiver('audio', { direction: 'sendonly' }).sender;
  let micStream: MediaStream | null = null;
  let micTrack: MediaStreamTrack | null = null;

  const setMicEnabled = async (on: boolean): Promise<void> => {
    if (on) {
      if (!micTrack) {
        // Raw mic — no browser AGC/echo-cancel/noise-suppression. The radio's TX
        // chain (mic gain, leveler, CFC, VST, WDSP TXA) shapes the audio exactly
        // as it does for a local operator; double-processing would muddy it.
        micStream = await navigator.mediaDevices.getUserMedia({
          audio: { channelCount: 1, echoCancellation: false, noiseSuppression: false, autoGainControl: false },
        });
        micTrack = micStream.getAudioTracks()[0] ?? null;
        if (micTrack) await audioSender.replaceTrack(micTrack);
      }
      if (micTrack) micTrack.enabled = true;
    } else if (micTrack) {
      micTrack.enabled = false;
    }
  };

  const stopMic = (): void => {
    micTrack = null;
    if (micStream) {
      for (const t of micStream.getTracks()) t.stop();
      micStream = null;
    }
  };

  const prover = new Spake2Plus('prover', CONTEXT, ID_PROVER, ID_VERIFIER);
  let outcome: Spake2Outcome | undefined;

  const unlocked = new Promise<void>((resolve, reject) => {
    control.onopen = () => control.send(JSON.stringify({ t: 'hello' }));
    control.onmessage = (e: MessageEvent) => {
      try {
        const raw = typeof e.data === 'string' ? e.data : new TextDecoder().decode(e.data);
        const msg = JSON.parse(raw);
        switch (msg.t) {
          case 'auth-params': {
            const params: Argon2Params = {
              iterations: msg.iterations,
              memoryKib: msg.memoryKib,
              parallelism: msg.parallelism,
            };
            const { w0, w1 } = deriveScalars(opts.password, b64decode(msg.salt), params);
            control.send(JSON.stringify({ t: 'auth-share', share: b64encode(prover.startProver(w0, w1)) }));
            break;
          }
          case 'auth-share': {
            outcome = prover.process(b64decode(msg.share));
            control.send(JSON.stringify({ t: 'auth-confirm', confirm: b64encode(outcome.localConfirm) }));
            break;
          }
          case 'auth-ok': {
            if (outcome && verifyPeerConfirm(outcome, b64decode(msg.confirm))) resolve();
            else reject(new Error('server authentication failed'));
            break;
          }
          case 'auth-fail':
            reject(new Error('incorrect password'));
            break;
        }
      } catch (err) {
        reject(err as Error);
      }
    };
  });

  const offer = await pc.createOffer();
  await pc.setLocalDescription(offer);
  await iceComplete(pc, iceTimeoutMs);

  const answerSdp = await signal(pc.localDescription!.sdp);
  await pc.setRemoteDescription({ type: 'answer', sdp: answerSdp });

  await unlocked;
  return {
    pc,
    frames,
    control,
    api,
    setMicEnabled,
    close: () => {
      stopMic();
      pc.close();
    },
  };
}

// --- signalling transports -------------------------------------------------

function brokerSignal(brokerOrigin: string, callsign: string, offerSdp: string): Promise<string> {
  const wsOrigin = brokerOrigin.replace(/^http/, 'ws');
  const url = `${wsOrigin}/signal?role=client&callsign=${encodeURIComponent(callsign.toUpperCase())}`;
  return new Promise<string>((resolve, reject) => {
    const ws = new WebSocket(url);
    const timer = setTimeout(() => {
      reject(new Error('Broker signalling timed out.'));
      try { ws.close(); } catch { /* ignore */ }
    }, 20000);
    ws.onopen = () => ws.send(JSON.stringify({ t: 'offer', sdp: offerSdp }));
    ws.onmessage = (e: MessageEvent) => {
      let msg: { t?: string; sdp?: string };
      try { msg = JSON.parse(typeof e.data === 'string' ? e.data : ''); } catch { return; }
      if (msg.t === 'answer' && msg.sdp) {
        clearTimeout(timer);
        resolve(msg.sdp);
        try { ws.close(); } catch { /* ignore */ }
      } else if (msg.t === 'offline') {
        clearTimeout(timer);
        reject(new Error('That radio is offline.'));
        try { ws.close(); } catch { /* ignore */ }
      }
    };
    ws.onerror = () => {
      clearTimeout(timer);
      reject(new Error('Could not reach the broker.'));
    };
  });
}

async function fetchIceServers(brokerOrigin: string): Promise<RTCIceServer[]> {
  try {
    const r = await fetch(`${brokerOrigin}/turn`, { method: 'POST' });
    if (r.ok) {
      const j = await r.json();
      if (Array.isArray(j.iceServers) && j.iceServers.length) return j.iceServers as RTCIceServer[];
    }
  } catch {
    /* TURN unconfigured or unreachable — STUN-only still works for most home networks */
  }
  return DEFAULT_STUN;
}

// --- helpers ---------------------------------------------------------------

function iceComplete(pc: RTCPeerConnection, timeoutMs: number): Promise<void> {
  return new Promise((resolve) => {
    if (pc.iceGatheringState === 'complete') return resolve();
    const timer = setTimeout(resolve, timeoutMs);
    pc.onicegatheringstatechange = () => {
      if (pc.iceGatheringState === 'complete') {
        clearTimeout(timer);
        resolve();
      }
    };
  });
}

function b64encode(b: Uint8Array): string {
  let s = '';
  for (const x of b) s += String.fromCharCode(x);
  return btoa(s);
}

function b64decode(s: string): Uint8Array {
  const bin = atob(s);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}
