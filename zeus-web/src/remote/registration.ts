// SPDX-License-Identifier: GPL-2.0-or-later
//
// Browser side of SPAKE2+ password registration (ADR-0008): turns the typed
// password + the salt/params delivered by the radio into the (w0, w1) scalars,
// byte-identically to the C# server (Zeus.Server.Hosting/Remote/Spake2PlusRegistration.cs).
// The browser never sends the password; it proves knowledge via SPAKE2+.

import { argon2id } from '@noble/hashes/argon2.js';
import { reduceToScalar } from './spake2plus';

// Must match RemoteAuthConstants on the server.
const ID_PROVER = new Uint8Array(0);
const ID_VERIFIER = new Uint8Array(0);
const ARGON2_OUTPUT_BYTES = 96; // 2 × 48-byte wide scalars → reduce mod n

export interface Argon2Params {
  iterations: number;
  memoryKib: number;
  parallelism: number;
}

/** Derive (w0, w1) from password + salt. Mirror of the C# DeriveScalars. */
export function deriveScalars(
  password: string,
  salt: Uint8Array,
  params: Argon2Params,
): { w0: bigint; w1: bigint } {
  const input = buildPasswordInput(password);
  const wide = argon2id(input, salt, {
    t: params.iterations,
    m: params.memoryKib,
    p: params.parallelism,
    dkLen: ARGON2_OUTPUT_BYTES,
  });
  const half = wide.length / 2;
  return {
    w0: reduceToScalar(wide.slice(0, half)),
    w1: reduceToScalar(wide.slice(half)),
  };
}

// RFC 9383 §3.2: input = len(pw)||pw || len(idProver)||idProver || len(idVerifier)||idVerifier
function buildPasswordInput(password: string): Uint8Array {
  const pw = new TextEncoder().encode(password);
  const parts: Uint8Array[] = [];
  for (const field of [pw, ID_PROVER, ID_VERIFIER]) {
    parts.push(u64le(field.length));
    parts.push(field);
  }
  const total = parts.reduce((n, p) => n + p.length, 0);
  const out = new Uint8Array(total);
  let off = 0;
  for (const p of parts) {
    out.set(p, off);
    off += p.length;
  }
  return out;
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
