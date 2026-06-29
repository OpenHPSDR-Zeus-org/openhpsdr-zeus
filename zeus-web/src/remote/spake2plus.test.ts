import { describe, it, expect } from 'vitest';
import { Spake2Plus, verifyPeerConfirm } from './spake2plus';

// RFC 9383 Appendix C, Vector 1 (P256-SHA256-HKDF-SHA256-HMAC-SHA256) — the same
// vector the C# verifier is pinned to, so the two implementations interoperate.
const CONTEXT = utf8('SPAKE2+-P256-SHA256-HKDF-SHA256-HMAC-SHA256 Test Vectors');
const ID_PROVER = utf8('client');
const ID_VERIFIER = utf8('server');

const W0 = BigInt('0xbb8e1bbcf3c48f62c08db243652ae55d3e5586053fca77102994f23ad95491b3');
const W1 = BigInt('0x7e945f34d78785b8a3ef44d0df5a1a97d6b3b460409a345ca7830387a74b1dba');
const L = '04eb7c9db3d9a9eb1f8adab81b5794c1f13ae3e225efbe91ea487425854c7fc00f00bfedcbd09b2400142d40a14f2064ef31dfaa903b91d1faea7093d835966efd';
const X = BigInt('0xd1232c8e8693d02368976c174e2088851b8365d0d79a9eee709c6a05a2fad539');
const Y = BigInt('0x717a72348a182085109c8d3917d6c43d59b224dc6a7fc4f0483232fa6516d8b3');
const SHARE_P = '04ef3bd051bf78a2234ec0df197f7828060fe9856503579bb1733009042c15c0c1de127727f418b5966afadfdd95a6e4591d171056b333dab97a79c7193e341727';
const SHARE_V = '04c0f65da0d11927bdf5d560c69e1d7d939a05b0e88291887d679fcadea75810fb5cc1ca7494db39e82ff2f50665255d76173e09986ab46742c798a9a68437b048';
const Z = '04bbfce7dd7f277819c8da21544afb7964705569bdf12fb92aa388059408d50091a0c5f1d3127f56813b5337f9e4e67e2ca633117a4fbd559946ab474356c41839';
const V = '0458bf27c6bca011c9ce1930e8984a797a3419797b936629a5a937cf2f11c8b9514b82b993da8a46e664f23db7c01edc87faa530db01c2ee405230b18997f16b68';
const K_MAIN = '4c59e1ccf2cfb961aa31bd9434478a1089b56cd11542f53d3576fb6c2a438a29';
const K_CONFIRM_P = '871ae3f7b78445e34438fb284504240239031c39d80ac23eb5ab9be5ad6db58a';
const K_CONFIRM_V = 'ccd53c7c1fa37b64a462b40db8be101cedcf838950162902054e644b400f1680';
const K_SHARED = '0c5f8ccd1413423a54f6c1fb26ff01534a87f893779c6e68666d772bfd91f3e7';
const CONFIRM_P = '926cc713504b9b4d76c9162ded04b5493e89109f6d89462cd33adc46fda27527';
const CONFIRM_V = '9747bcc4f8fe9f63defee53ac9b07876d907d55047e6ff2def2e7529089d3e68';

const prover = () => new Spake2Plus('prover', CONTEXT, ID_PROVER, ID_VERIFIER);
const verifier = () => new Spake2Plus('verifier', CONTEXT, ID_PROVER, ID_VERIFIER);

describe('SPAKE2+ browser prover (RFC 9383 Appendix C, Vector 1)', () => {
  it('prover share matches the vector', () => {
    expect(hex(prover().startProver(W0, W1, X))).toBe(SHARE_P);
  });

  it('verifier share matches the vector', () => {
    expect(hex(verifier().startVerifier(W0, bytes(L), Y))).toBe(SHARE_V);
  });

  it('prover derives every intermediate per the RFC', () => {
    const p = prover();
    p.startProver(W0, W1, X);
    const o = p.process(bytes(SHARE_V));

    expect(hex(o.zEnc)).toBe(Z);
    expect(hex(o.vEnc)).toBe(V);
    expect(hex(o.kMain)).toBe(K_MAIN);
    expect(hex(o.kConfirmP)).toBe(K_CONFIRM_P);
    expect(hex(o.kConfirmV)).toBe(K_CONFIRM_V);
    expect(hex(o.sharedKey)).toBe(K_SHARED);
    expect(hex(o.confirmP)).toBe(CONFIRM_P);
    expect(hex(o.confirmV)).toBe(CONFIRM_V);
    expect(hex(o.localConfirm)).toBe(CONFIRM_P); // prover sends confirmP
    expect(hex(o.expectedPeerConfirm)).toBe(CONFIRM_V);
  });

  it('verifier derives the same shared key and confirms', () => {
    const v = verifier();
    v.startVerifier(W0, bytes(L), Y);
    const o = v.process(bytes(SHARE_P));

    expect(hex(o.zEnc)).toBe(Z);
    expect(hex(o.vEnc)).toBe(V);
    expect(hex(o.sharedKey)).toBe(K_SHARED);
    expect(hex(o.localConfirm)).toBe(CONFIRM_V); // verifier sends confirmV
    expect(hex(o.expectedPeerConfirm)).toBe(CONFIRM_P);
  });

  it('full exchange: both sides agree and confirms verify', () => {
    const p = prover();
    const v = verifier();
    const sp = p.startProver(W0, W1);   // random x
    const sv = v.startVerifier(W0, bytes(L)); // random y
    const po = p.process(sv);
    const vo = v.process(sp);

    expect(hex(po.sharedKey)).toBe(hex(vo.sharedKey));
    expect(verifyPeerConfirm(po, vo.localConfirm)).toBe(true);
    expect(verifyPeerConfirm(vo, po.localConfirm)).toBe(true);
  });

  it('wrong password: confirms do not verify', () => {
    const p = prover();
    const v = verifier();
    const sp = p.startProver(W0, W1);
    const sv = v.startVerifier(W0 + 1n, bytes(L)); // verifier's stored secret differs
    const po = p.process(sv);
    const vo = v.process(sp);

    expect(hex(po.sharedKey)).not.toBe(hex(vo.sharedKey));
    expect(verifyPeerConfirm(po, vo.localConfirm)).toBe(false);
    expect(verifyPeerConfirm(vo, po.localConfirm)).toBe(false);
  });

  it('invalid peer share fails closed', () => {
    const v = verifier();
    v.startVerifier(W0, bytes(L));
    expect(() => v.process(new Uint8Array([0x00]))).toThrow();
  });
});

function utf8(s: string): Uint8Array {
  return new TextEncoder().encode(s);
}
function bytes(h: string): Uint8Array {
  const out = new Uint8Array(h.length / 2);
  for (let i = 0; i < out.length; i++) out[i] = parseInt(h.slice(i * 2, i * 2 + 2), 16);
  return out;
}
function hex(b: Uint8Array): string {
  return Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');
}
