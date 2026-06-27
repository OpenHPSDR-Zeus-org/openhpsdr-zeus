import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  resolveBrokerUrl,
  brokerWsBase,
  normCallsign,
  requireAdminToken,
  CliError,
  DEFAULT_BROKER_URL,
} from '../src/config.js';

test('resolveBrokerUrl prefers the flag, strips trailing slashes', () => {
  assert.equal(resolveBrokerUrl('https://example.com/'), 'https://example.com');
  assert.equal(resolveBrokerUrl('https://example.com///'), 'https://example.com');
});

test('resolveBrokerUrl falls back to env then default', () => {
  const prev = process.env.ZEUS_REMOTE_BROKER_URL;
  try {
    process.env.ZEUS_REMOTE_BROKER_URL = 'https://env.example.com';
    assert.equal(resolveBrokerUrl(), 'https://env.example.com');
    delete process.env.ZEUS_REMOTE_BROKER_URL;
    assert.equal(resolveBrokerUrl(), DEFAULT_BROKER_URL);
  } finally {
    if (prev === undefined) delete process.env.ZEUS_REMOTE_BROKER_URL;
    else process.env.ZEUS_REMOTE_BROKER_URL = prev;
  }
});

test('resolveBrokerUrl rejects non-http(s) and garbage', () => {
  assert.throws(() => resolveBrokerUrl('ftp://x'), CliError);
  assert.throws(() => resolveBrokerUrl('not a url'), CliError);
});

test('brokerWsBase maps http(s) → ws(s)', () => {
  assert.equal(brokerWsBase('https://remote.example.com'), 'wss://remote.example.com');
  assert.equal(brokerWsBase('http://localhost:8787'), 'ws://localhost:8787');
});

test('normCallsign trims and uppercases', () => {
  assert.equal(normCallsign('  n9war '), 'N9WAR');
});

test('requireAdminToken prefers flag, then env, else throws', () => {
  const prev = process.env.ZEUS_ADMIN_TOKEN;
  try {
    assert.equal(requireAdminToken('zsa_flag'), 'zsa_flag');
    delete process.env.ZEUS_ADMIN_TOKEN;
    assert.throws(() => requireAdminToken(), CliError);
    process.env.ZEUS_ADMIN_TOKEN = 'zsa_env';
    assert.equal(requireAdminToken(), 'zsa_env');
  } finally {
    if (prev === undefined) delete process.env.ZEUS_ADMIN_TOKEN;
    else process.env.ZEUS_ADMIN_TOKEN = prev;
  }
});
