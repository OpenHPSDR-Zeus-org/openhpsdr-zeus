// SPDX-License-Identifier: GPL-2.0-or-later
//
// Browser SPAKE2+ (RFC 9383, ciphersuite P256-SHA256-HKDF-SHA256-HMAC-SHA256).
// The prover half of the remote-access password handshake (ADR-0008); the radio
// runs the matching C# verifier (Zeus.Server.Hosting/Remote/Spake2Plus.cs). Both
// are pinned to the RFC 9383 Appendix C test vectors so they interoperate.
//
// EC point math: @noble/curves (audited). Hashing/KDF/MAC: @noble/hashes.

import { p256 } from '@noble/curves/nist.js';
import { sha256 } from '@noble/hashes/sha2.js';
import { hkdf } from '@noble/hashes/hkdf.js';
import { hmac } from '@noble/hashes/hmac.js';

const Point = p256.Point;
const ORDER = Point.Fn.ORDER;

// RFC 9382/9383 constant points (compressed SEC1); discrete log unknown.
const M = Point.fromHex('02886e2f97ace46e55ba9dd7242579f2993b64e16ef3dcab95afd497333d8fa12f');
const N = Point.fromHex('03d8bbd6c639c62937b04d997f38c3770719c629d7014d49a24b4f98baa1292b49');

export type Spake2Role = 'prover' | 'verifier';

export interface Spake2Outcome {
  sharedKey: Uint8Array;
  localConfirm: Uint8Array;
  expectedPeerConfirm: Uint8Array;
  // Intermediates, exposed for RFC-vector tests.
  transcript: Uint8Array;
  kMain: Uint8Array;
  kConfirmP: Uint8Array;
  kConfirmV: Uint8Array;
  confirmP: Uint8Array;
  confirmV: Uint8Array;
  zEnc: Uint8Array;
  vEnc: Uint8Array;
}

export class Spake2Plus {
  private scalar?: bigint;
  private share?: Uint8Array;
  private w0?: bigint;
  private w1?: bigint;
  private bigL?: InstanceType<typeof Point>;

  constructor(
    private readonly role: Spake2Role,
    private readonly context: Uint8Array,
    private readonly idProver: Uint8Array,
    private readonly idVerifier: Uint8Array,
  ) {}

  /** Prover (client) share: shareP = x·P + w0·M. `x` is for tests; omit for random. */
  startProver(w0: bigint, w1: bigint, x?: bigint): Uint8Array {
    if (this.role !== 'prover') throw new Error('role mismatch');
    this.w0 = w0;
    this.w1 = w1;
    this.scalar = x ?? randomScalar();
    this.share = Point.BASE.multiply(this.scalar).add(M.multiply(w0)).toBytes(false);
    return this.share;
  }

  /** Verifier (radio) share: shareV = y·P + w0·N. Mainly for the in-TS round-trip test. */
  startVerifier(w0: bigint, encodedL: Uint8Array, y?: bigint): Uint8Array {
    if (this.role !== 'verifier') throw new Error('role mismatch');
    this.w0 = w0;
    this.bigL = decodePoint(encodedL);
    this.scalar = y ?? randomScalar();
    this.share = Point.BASE.multiply(this.scalar).add(N.multiply(w0)).toBytes(false);
    return this.share;
  }

  /** Process the peer share → session key + confirmation MACs. Throws (fails closed) on bad input. */
  process(peerShare: Uint8Array): Spake2Outcome {
    if (this.w0 === undefined || this.scalar === undefined || this.share === undefined)
      throw new Error('start* not called');

    // Capture into locals before any call — TS drops `this.field` narrowing across calls.
    const w0 = this.w0;
    const scalar = this.scalar;
    const myShare = this.share;
    const peer = decodePoint(peerShare);

    let z: InstanceType<typeof Point>;
    let v: InstanceType<typeof Point>;
    let shareP: Uint8Array;
    let shareV: Uint8Array;

    if (this.role === 'prover') {
      const t = peer.subtract(N.multiply(w0)); // Y − w0·N
      z = t.multiply(scalar); // x·(Y − w0·N)
      v = t.multiply(this.w1!); // w1·(Y − w0·N)
      shareP = myShare;
      shareV = peer.toBytes(false);
    } else {
      const t = peer.subtract(M.multiply(w0)); // X − w0·M
      z = t.multiply(scalar); // y·(X − w0·M)
      v = this.bigL!.multiply(scalar); // y·L
      shareP = peer.toBytes(false);
      shareV = myShare;
    }

    const zEnc = z.toBytes(false);
    const vEnc = v.toBytes(false);

    const transcript = buildTranscript(
      this.context, this.idProver, this.idVerifier,
      M.toBytes(false), N.toBytes(false),
      shareP, shareV, zEnc, vEnc, scalarTo32(w0),
    );
    const kMain = sha256(transcript);

    const confirmKeys = hkdf(sha256, kMain, undefined, utf8('ConfirmationKeys'), 64);
    const kConfirmP = confirmKeys.slice(0, 32);
    const kConfirmV = confirmKeys.slice(32);
    const sharedKey = hkdf(sha256, kMain, undefined, utf8('SharedKey'), 32);

    // RFC 9383 §3.4: confirmP = MAC(K_confirmP, shareV); confirmV = MAC(K_confirmV, shareP).
    const confirmP = hmac(sha256, kConfirmP, shareV);
    const confirmV = hmac(sha256, kConfirmV, shareP);

    const [localConfirm, expectedPeerConfirm] =
      this.role === 'prover' ? [confirmP, confirmV] : [confirmV, confirmP];

    return {
      sharedKey, localConfirm, expectedPeerConfirm,
      transcript, kMain, kConfirmP, kConfirmV, confirmP, confirmV, zEnc, vEnc,
    };
  }
}

/** Constant-time confirmation-MAC check. */
export function verifyPeerConfirm(outcome: Spake2Outcome, received: Uint8Array): boolean {
  const a = outcome.expectedPeerConfirm;
  if (a.length !== received.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a[i]! ^ received[i]!;
  return diff === 0;
}

// --- helpers ---------------------------------------------------------------

function decodePoint(bytes: Uint8Array): InstanceType<typeof Point> {
  const p = Point.fromBytes(bytes);
  p.assertValidity(); // reject off-curve / identity — mandatory for PAKE security
  return p;
}

// TT = len(Context)||Context || len(idProver)||idProver || len(idVerifier)||idVerifier
//   || len(M)||M || len(N)||N || len(shareP)||shareP || len(shareV)||shareV
//   || len(Z)||Z || len(V)||V || len(w0)||w0     (RFC 9383 §3.3; 8-byte LE lengths)
function buildTranscript(...fields: Uint8Array[]): Uint8Array {
  const parts: Uint8Array[] = [];
  for (const f of fields) {
    parts.push(u64le(f.length));
    parts.push(f);
  }
  return concat(parts);
}

function u64le(n: number): Uint8Array {
  const out = new Uint8Array(8);
  let v = BigInt(n);
  for (let i = 0; i < 8; i++) {
    out[i] = Number(v & 0xffn);
    v >>= 8n;
  }
  return out;
}

function scalarTo32(w: bigint): Uint8Array {
  const out = new Uint8Array(32);
  let v = w;
  for (let i = 31; i >= 0; i--) {
    out[i] = Number(v & 0xffn);
    v >>= 8n;
  }
  if (v !== 0n) throw new Error('scalar too large');
  return out;
}

export function reduceToScalar(wide: Uint8Array): bigint {
  let v = 0n;
  for (const b of wide) v = (v << 8n) | BigInt(b);
  return v % ORDER;
}

function randomScalar(): bigint {
  const buf = new Uint8Array(48); // oversample → reduce, low bias
  while (true) {
    crypto.getRandomValues(buf);
    const s = reduceToScalar(buf);
    if (s > 0n) return s;
  }
}

function utf8(s: string): Uint8Array {
  return new TextEncoder().encode(s);
}

function concat(parts: Uint8Array[]): Uint8Array {
  const total = parts.reduce((n, p) => n + p.length, 0);
  const out = new Uint8Array(total);
  let off = 0;
  for (const p of parts) {
    out.set(p, off);
    off += p.length;
  }
  return out;
}
