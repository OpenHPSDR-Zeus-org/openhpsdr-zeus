/**
 * Unit tests for the /admin CORS allowlist. The maintainer dashboard runs at a
 * different origin than the RX SPA, so the allowlist is built from
 * WEB_APP_ORIGIN + ADMIN_ORIGINS and the response echoes back ONLY the exact
 * requesting origin when it matches — never '*', never a list.
 *
 *   node --import tsx --test test/admin-cors.test.ts
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';

import { corsHeaders, allowedAdminOrigins } from '../src/admin-api.ts';
import type { Env } from '../src/types.ts';

// Only the CORS path is exercised, so a partial Env cast is sufficient.
function env(partial: Partial<Env>): Env {
  return partial as Env;
}

function req(origin?: string): Request {
  const headers = new Headers();
  if (origin !== undefined) headers.set('Origin', origin);
  return new Request('https://remote.openhpsdrzeus.com/admin/presence', { headers });
}

test('allowlist combines WEB_APP_ORIGIN + ADMIN_ORIGINS, trims, strips trailing slash', () => {
  const allowed = allowedAdminOrigins(
    env({ WEB_APP_ORIGIN: 'https://app.openhpsdrzeus.com', ADMIN_ORIGINS: ' https://openhpsdrzeus.com/ , https://dash.example.com ' }),
  );
  assert.deepEqual(
    [...allowed].sort(),
    ['https://app.openhpsdrzeus.com', 'https://dash.example.com', 'https://openhpsdrzeus.com'].sort(),
  );
});

test('empty / missing origins are dropped, no entries when unset', () => {
  assert.equal(allowedAdminOrigins(env({})).size, 0);
  assert.deepEqual([...allowedAdminOrigins(env({ ADMIN_ORIGINS: ' , ,, ' }))], []);
});

test('dashboard origin (in ADMIN_ORIGINS) is echoed back exactly', () => {
  const cors = corsHeaders(
    env({ WEB_APP_ORIGIN: 'https://app.openhpsdrzeus.com', ADMIN_ORIGINS: 'https://openhpsdrzeus.com' }),
    req('https://openhpsdrzeus.com'),
  );
  assert.equal(cors['Access-Control-Allow-Origin'], 'https://openhpsdrzeus.com');
  assert.equal(cors['Vary'], 'Origin');
});

test('RX SPA origin (WEB_APP_ORIGIN) still allowed — no regression', () => {
  const cors = corsHeaders(
    env({ WEB_APP_ORIGIN: 'https://app.openhpsdrzeus.com', ADMIN_ORIGINS: 'https://openhpsdrzeus.com' }),
    req('https://app.openhpsdrzeus.com'),
  );
  assert.equal(cors['Access-Control-Allow-Origin'], 'https://app.openhpsdrzeus.com');
});

test('unlisted origin gets NO Allow-Origin header (fails closed, never *)', () => {
  const cors = corsHeaders(
    env({ WEB_APP_ORIGIN: 'https://app.openhpsdrzeus.com', ADMIN_ORIGINS: 'https://openhpsdrzeus.com' }),
    req('https://evil.example.com'),
  );
  assert.equal(cors['Access-Control-Allow-Origin'], undefined);
  // The header value is never the wildcard.
  assert.notEqual(cors['Access-Control-Allow-Origin'], '*');
});

test('request with no Origin header gets no Allow-Origin (non-browser callers)', () => {
  const cors = corsHeaders(env({ WEB_APP_ORIGIN: 'https://app.openhpsdrzeus.com' }), req());
  assert.equal(cors['Access-Control-Allow-Origin'], undefined);
});

test('no allowlist configured → no Allow-Origin even for a real origin', () => {
  const cors = corsHeaders(env({}), req('https://openhpsdrzeus.com'));
  assert.equal(cors['Access-Control-Allow-Origin'], undefined);
});
