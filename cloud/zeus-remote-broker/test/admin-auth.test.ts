/**
 * Unit tests for the admin-auth crypto core. Runs under Node 20+ using the
 * global WebCrypto (crypto.subtle) — the same primitive the Worker uses — so the
 * functions are exercised exactly as deployed, no mocks.
 *
 *   node --import tsx --test test/admin-auth.test.ts
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  hashPassword,
  verifyPassword,
  mintToken,
  hashToken,
  looksLikeToken,
  constantTimeEqual,
  TOKEN_PREFIX,
  PBKDF2_ITER,
} from '../src/admin-auth.ts';

test('password hash + verify round-trip', async () => {
  const rec = await hashPassword('correct horse battery staple');
  assert.equal(rec.iter, PBKDF2_ITER);
  assert.ok(rec.hash.length > 0);
  assert.ok(rec.salt.length > 0);
  assert.equal(await verifyPassword('correct horse battery staple', rec), true);
});

test('wrong password fails', async () => {
  const rec = await hashPassword('s3cret-password');
  assert.equal(await verifyPassword('s3cret-passwor', rec), false);
  assert.equal(await verifyPassword('S3cret-password', rec), false);
  assert.equal(await verifyPassword('', rec), false);
});

test('distinct salts produce distinct hashes for the same password', async () => {
  const a = await hashPassword('same-password');
  const b = await hashPassword('same-password');
  assert.notEqual(a.salt, b.salt);
  assert.notEqual(a.hash, b.hash);
  // ...but both still verify.
  assert.equal(await verifyPassword('same-password', a), true);
  assert.equal(await verifyPassword('same-password', b), true);
});

test('token generate -> hash -> verify round-trip', async () => {
  const minted = await mintToken();
  assert.ok(minted.token.startsWith(TOKEN_PREFIX));
  assert.equal(looksLikeToken(minted.token), true);
  // The stored hash equals SHA-256 of the presented token.
  assert.equal(await hashToken(minted.token), minted.hash);
});

test('tampered token fails to match its stored hash', async () => {
  const minted = await mintToken();
  const tampered = minted.token.slice(0, -1) + (minted.token.endsWith('A') ? 'B' : 'A');
  assert.notEqual(tampered, minted.token);
  assert.notEqual(await hashToken(tampered), minted.hash);
});

test('looksLikeToken rejects non-prefixed / empty values', () => {
  assert.equal(looksLikeToken('zsa_abc'), true);
  assert.equal(looksLikeToken('abc'), false);
  assert.equal(looksLikeToken(TOKEN_PREFIX), false); // prefix only, no body
  assert.equal(looksLikeToken(''), false);
});

test('constantTimeEqual returns correct booleans', () => {
  const a = new Uint8Array([1, 2, 3, 4]);
  const b = new Uint8Array([1, 2, 3, 4]);
  const c = new Uint8Array([1, 2, 3, 5]);
  const d = new Uint8Array([1, 2, 3]);
  assert.equal(constantTimeEqual(a, b), true);
  assert.equal(constantTimeEqual(a, c), false); // same length, last byte differs
  assert.equal(constantTimeEqual(a, d), false); // different length
});
