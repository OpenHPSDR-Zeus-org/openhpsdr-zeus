/**
 * Unit tests for the crash-share validation + retention core. These are the pure,
 * runtime-free pieces of the POST /crash + CrashStore path, exercised under plain
 * Node (no Workers/miniflare) exactly as deployed.
 *
 *   node --import tsx --test test/crash-validate.test.ts
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  validateCrashBody,
  retainNewest,
  MAX_CRASH_BYTES,
  MAX_CRASH_PER_CALLSIGN,
} from '../src/crash-validate.ts';

test('accepts a well-formed JSON object body', () => {
  const v = validateCrashBody(JSON.stringify({ schemaVersion: 1, pid: 4321, crashed: true }));
  assert.equal(v.ok, true);
});

test('rejects an empty body', () => {
  const v = validateCrashBody('');
  assert.equal(v.ok, false);
  if (!v.ok) assert.equal(v.status, 400);
});

test('rejects a body over the size cap with 413', () => {
  // A JSON string whose serialised length exceeds the cap.
  const big = JSON.stringify({ note: 'x'.repeat(MAX_CRASH_BYTES + 10) });
  assert.ok(big.length > MAX_CRASH_BYTES);
  const v = validateCrashBody(big);
  assert.equal(v.ok, false);
  if (!v.ok) assert.equal(v.status, 413);
});

test('rejects invalid JSON', () => {
  const v = validateCrashBody('{ not json ');
  assert.equal(v.ok, false);
  if (!v.ok) assert.equal(v.status, 400);
});

test('rejects a JSON array (must be an object)', () => {
  const v = validateCrashBody(JSON.stringify([1, 2, 3]));
  assert.equal(v.ok, false);
  if (!v.ok) assert.equal(v.status, 400);
});

test('rejects a bare JSON primitive / null', () => {
  assert.equal(validateCrashBody('null').ok, false);
  assert.equal(validateCrashBody('42').ok, false);
  assert.equal(validateCrashBody('"a string"').ok, false);
});

test('retainNewest keeps only the newest N, dropping the oldest', () => {
  const list: number[] = [];
  for (let i = 0; i < MAX_CRASH_PER_CALLSIGN + 5; i++) {
    list.push(i);
    retainNewest(list);
  }
  assert.equal(list.length, MAX_CRASH_PER_CALLSIGN);
  // The five oldest (0..4) were dropped; the list ends at the latest pushed value.
  assert.equal(list[0], 5);
  assert.equal(list[list.length - 1], MAX_CRASH_PER_CALLSIGN + 4);
});

test('retainNewest is a no-op below the cap', () => {
  const list = [1, 2, 3];
  retainNewest(list);
  assert.deepEqual(list, [1, 2, 3]);
});
