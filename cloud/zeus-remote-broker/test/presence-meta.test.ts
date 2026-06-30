/**
 * Unit tests for the pure, runtime-free operator-metadata sanitation used by the
 * presence path (POST /presence/register|heartbeat body + edge country code).
 * Exercised under plain Node exactly as deployed.
 *
 *   node --import tsx --test test/presence-meta.test.ts
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  sanitizeOperatorMeta,
  cleanField,
  cleanCountry,
  emptyMeta,
  MAX_META_FIELD,
} from '../src/presence-meta.ts';

test('empty / missing body degrades to all-null meta', () => {
  assert.deepEqual(sanitizeOperatorMeta(''), emptyMeta());
});

test('invalid JSON degrades to empty meta (never throws)', () => {
  assert.deepEqual(sanitizeOperatorMeta('{not json'), emptyMeta());
  assert.deepEqual(sanitizeOperatorMeta('[1,2,3]'), emptyMeta());
  assert.deepEqual(sanitizeOperatorMeta('"a string"'), emptyMeta());
});

test('well-formed body is parsed and typed', () => {
  const m = sanitizeOperatorMeta(
    JSON.stringify({
      platform: 'Microsoft Windows 11 (10.0.26100)',
      appVersion: '0.10.0-dev',
      radioBoard: 'Hermes-Lite 2',
      radioModel: 'G2',
      radioConnected: true,
    }),
  );
  assert.equal(m.platform, 'Microsoft Windows 11 (10.0.26100)');
  assert.equal(m.appVersion, '0.10.0-dev');
  assert.equal(m.radioBoard, 'Hermes-Lite 2');
  assert.equal(m.radioModel, 'G2');
  assert.equal(m.radioConnected, true);
});

test('radioConnected is strictly boolean true', () => {
  assert.equal(sanitizeOperatorMeta(JSON.stringify({ radioConnected: 'true' })).radioConnected, false);
  assert.equal(sanitizeOperatorMeta(JSON.stringify({ radioConnected: 1 })).radioConnected, false);
  assert.equal(sanitizeOperatorMeta(JSON.stringify({ radioConnected: true })).radioConnected, true);
});

test('hyphens and spaces in a radio name are preserved', () => {
  // Regression: the control-char scrub must not eat normal "-"/" ".
  assert.equal(cleanField('Hermes-Lite 2'), 'Hermes-Lite 2');
});

test('control characters are collapsed to spaces and trimmed', () => {
  assert.equal(cleanField('a\nb\tc\r'), 'a b c');
  assert.equal(cleanField('   spaced   '), 'spaced');
});

test('non-strings and empties become null', () => {
  assert.equal(cleanField(123), null);
  assert.equal(cleanField(null), null);
  assert.equal(cleanField('   '), null);
});

test('over-long fields are clamped', () => {
  const long = 'x'.repeat(MAX_META_FIELD + 50);
  assert.equal(cleanField(long)?.length, MAX_META_FIELD);
});

test('country code is normalised; junk/unknown dropped', () => {
  assert.equal(cleanCountry('us'), 'US');
  assert.equal(cleanCountry('DE'), 'DE');
  assert.equal(cleanCountry('XX'), null); // Cloudflare unknown
  assert.equal(cleanCountry('T1'), null); // Tor
  assert.equal(cleanCountry('USA'), null); // not 2-letter
  assert.equal(cleanCountry(''), null);
  assert.equal(cleanCountry(undefined), null);
});
