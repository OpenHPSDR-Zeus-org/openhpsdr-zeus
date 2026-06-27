import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  buildReport,
  renderJson,
  renderText,
  DEFAULT_PULL_PATHS,
} from '../src/report.js';
import type { ApiChannelResponse } from '../src/types.js';

const sampleResponses: (ApiChannelResponse & { path: string })[] = [
  {
    id: 'a',
    status: 200,
    contentType: 'application/json',
    path: '/api/version',
    body: JSON.stringify({ version: '1.2.3' }),
  },
  {
    id: 'b',
    status: 500,
    contentType: 'text/plain',
    path: '/api/state',
    body: 'boom',
  },
];

test('buildReport parses JSON bodies and keeps text bodies raw', () => {
  const report = buildReport({
    operator: 'N9WAR',
    requestId: 'rq1',
    admin: 'KB2UKA',
    responses: sampleResponses,
    backlog: ['line a'],
    live: ['line b'],
    now: new Date('2026-01-01T00:00:00.000Z'),
  });

  assert.equal(report.schema, 'zeus-support-pull/1');
  assert.equal(report.operator, 'N9WAR');
  assert.equal(report.admin, 'KB2UKA');
  assert.equal(report.requestId, 'rq1');
  assert.equal(report.generatedAt, '2026-01-01T00:00:00.000Z');

  // JSON body parsed into an object…
  assert.deepEqual(report.diagnostics[0].body, { version: '1.2.3' });
  assert.equal(report.diagnostics[0].path, '/api/version');
  // …text body kept as a string.
  assert.equal(report.diagnostics[1].body, 'boom');
  assert.equal(report.diagnostics[1].status, 500);

  assert.deepEqual(report.log.backlog, ['line a']);
  assert.deepEqual(report.log.live, ['line b']);
});

test('buildReport tolerates malformed JSON bodies (keeps the raw string)', () => {
  const responses: (ApiChannelResponse & { path: string })[] = [
    { id: 'x', status: 200, contentType: 'application/json', path: '/api/diagnostics/v2', body: '{not json' },
  ];
  const report = buildReport({
    operator: 'OP',
    requestId: 'r',
    admin: 'A',
    responses,
    backlog: [],
    live: [],
  });
  assert.equal(report.diagnostics[0].body, '{not json');
});

test('renderJson round-trips to a valid object', () => {
  const report = buildReport({
    operator: 'OP',
    requestId: 'r',
    admin: 'A',
    responses: sampleResponses,
    backlog: [],
    live: [],
  });
  const parsed = JSON.parse(renderJson(report));
  assert.equal(parsed.schema, 'zeus-support-pull/1');
  assert.equal(parsed.diagnostics.length, 2);
});

test('renderText includes diagnostics + log section headers', () => {
  const report = buildReport({
    operator: 'N9WAR',
    requestId: 'rq1',
    admin: 'KB2UKA',
    responses: sampleResponses,
    backlog: ['backlog-1'],
    live: ['live-1'],
  });
  const text = renderText(report);
  assert.match(text, /Zeus support pull — N9WAR/);
  assert.match(text, /\[200\] \/api\/version/);
  assert.match(text, /\[500\] \/api\/state/);
  assert.match(text, /Log backlog \(1 lines\)/);
  assert.match(text, /Live log \(1 lines\)/);
});

test('DEFAULT_PULL_PATHS covers the spec-required endpoints', () => {
  for (const p of ['/api/version', '/api/state', '/api/diagnostics/v2']) {
    assert.ok(DEFAULT_PULL_PATHS.includes(p as (typeof DEFAULT_PULL_PATHS)[number]), `${p} missing`);
  }
});
