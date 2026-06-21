// SPDX-License-Identifier: GPL-2.0-or-later
//
// Browser remote-access connect flow (Phase 1 client). Mirror of the server's
// RemoteWebRtcSession: opens a WebRTC peer, runs the SPAKE2+ password handshake
// over the control DataChannel, and surfaces radio frames once UNLOCKED.
//
// The crypto here is covered by spake2plus.test.ts / registration.test.ts. The
// live WebRTC connection itself needs a real browser (no RTCPeerConnection in
// node/vitest), so it is verified on a bench, not in CI.
//
// NOTE: Argon2id runs on the calling thread; for production move deriveScalars
// into a Web Worker so the 64 MiB derivation doesn't jank the UI.

import { Spake2Plus, verifyPeerConfirm, type Spake2Outcome } from './spake2plus';
import { deriveScalars, type Argon2Params } from './registration';

const CONTEXT = new TextEncoder().encode('zeus-remote-access/v1');
const ID_PROVER = new Uint8Array(0);
const ID_VERIFIER = new Uint8Array(0);

export interface RemoteConnection {
  pc: RTCPeerConnection;
  frames: RTCDataChannel;
  close(): void;
}

export interface RemoteConnectOptions {
  password: string;
  /** API origin; '' (default) = same-origin. */
  apiBase?: string;
  /** ICE servers; production fills this from broker-minted TURN credentials. */
  iceServers?: RTCIceServer[];
  /** Called with each binary radio frame after unlock. */
  onFrame?: (data: ArrayBuffer) => void;
}

/**
 * Connect to the operator's radio: signal via POST /api/remote/connect, then
 * prove the session password over SPAKE2+. Resolves once UNLOCKED; rejects on a
 * wrong password, a disabled remote endpoint, or signalling failure.
 */
export async function connectRemote(opts: RemoteConnectOptions): Promise<RemoteConnection> {
  const pc = new RTCPeerConnection({
    iceServers: opts.iceServers ?? [{ urls: 'stun:stun.cloudflare.com:3478' }],
  });

  const control = pc.createDataChannel('control'); // reliable, ordered
  const frames = pc.createDataChannel('frames', { ordered: false, maxRetransmits: 0 });
  if (opts.onFrame) {
    frames.onmessage = (e: MessageEvent) => opts.onFrame!(e.data as ArrayBuffer);
  }

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
  await iceComplete(pc);

  const resp = await fetch(`${opts.apiBase ?? ''}/api/remote/connect`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ sdp: pc.localDescription!.sdp }),
  });
  if (resp.status === 403) throw new Error('Remote access is not enabled (no session password set).');
  if (!resp.ok) throw new Error(`connect failed: HTTP ${resp.status}`);
  const answer = await resp.json();
  await pc.setRemoteDescription({ type: 'answer', sdp: answer.sdp });

  await unlocked;
  return { pc, frames, close: () => pc.close() };
}

function iceComplete(pc: RTCPeerConnection): Promise<void> {
  return new Promise((resolve) => {
    if (pc.iceGatheringState === 'complete') return resolve();
    const timer = setTimeout(resolve, 750); // vanilla-ICE cap; host candidates suffice on LAN
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
