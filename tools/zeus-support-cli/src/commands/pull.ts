/**
 * `pull <callsign>` — the full read-only diagnostics flow:
 *   request → open support WS with the ticket → wait for the operator's Allow →
 *   WebRTC connect → fetch a fixed set of diagnostics over the `api` channel →
 *   capture the `log` backlog + N seconds of live log → write a tidy report.
 *
 * Handles operator-offline (503) and operator-denied/timeout (no grant) cleanly
 * via CliError (the broker/session layers throw those). `--mock` runs the report
 * shaping against synthetic channel data with zero network, so the end-to-end
 * formatting is testable without the deployed broker + a consenting operator.
 */

import { writeFileSync } from 'node:fs';
import { parse, commonOptions } from '../args.js';
import {
  resolveBrokerUrl,
  brokerWsBase,
  requireAdminToken,
  normCallsign,
  CliError,
} from '../config.js';
import { BrokerClient } from '../broker.js';
import { SupportSession } from '../session.js';
import {
  DEFAULT_PULL_PATHS,
  buildReport,
  renderJson,
  renderText,
  type SupportReport,
} from '../report.js';
import type { ApiChannelResponse } from '../types.js';

export const pullHelp = `
zeus-support pull <callsign> — request + connect + collect a diagnostics report

Usage:
  ZEUS_ADMIN_TOKEN=<token> zeus-support pull <CALLSIGN> [options]

Options:
  --out <FILE>        write the report to FILE (default: stdout)
  --format <fmt>      json | text                         (default: json)
  --log-seconds <N>   seconds of live log to capture      (default: 5)
  --paths <list>      comma-separated api paths to fetch
                      (default: ${DEFAULT_PULL_PATHS.join(',')})
  --grant-timeout <N> seconds to wait for the operator's Allow (default: 90)
  --mock              run report shaping on synthetic data (no network)
  --token <T>         Bearer token (or env ZEUS_ADMIN_TOKEN)
  --broker <URL>      broker base url (or env ZEUS_REMOTE_BROKER_URL)
`;

export async function runPull(argv: string[]): Promise<number> {
  const { values, positionals } = parse(argv, {
    ...commonOptions,
    out: { type: 'string' },
    format: { type: 'string' },
    'log-seconds': { type: 'string' },
    paths: { type: 'string' },
    'grant-timeout': { type: 'string' },
    mock: { type: 'boolean' },
  });

  if (values.help) {
    process.stdout.write(`${pullHelp}\n`);
    return 0;
  }

  const callsign = normCallsign(positionals[0] ?? '');
  if (!callsign) throw new CliError('usage: zeus-support pull <callsign>', 2);

  const format = (str(values.format) || 'json').toLowerCase();
  if (format !== 'json' && format !== 'text') {
    throw new CliError(`--format must be json or text (got ${format}).`, 2);
  }
  const paths = parsePaths(str(values.paths));
  const logSeconds = parseNum(str(values['log-seconds']), 5);
  const grantSeconds = parseNum(str(values['grant-timeout']), 90);

  const report = values.mock
    ? mockReport(callsign, paths)
    : await livePull(callsign, paths, logSeconds, grantSeconds, values);

  const rendered = format === 'json' ? renderJson(report) : renderText(report);
  const out = str(values.out);
  if (out) {
    writeFileSync(out, `${rendered}\n`, 'utf8');
    process.stderr.write(`wrote report to ${out}\n`);
  } else {
    process.stdout.write(`${rendered}\n`);
  }
  return 0;
}

async function livePull(
  callsign: string,
  paths: string[],
  logSeconds: number,
  grantSeconds: number,
  values: Record<string, string | boolean | undefined>,
): Promise<SupportReport> {
  const baseUrl = resolveBrokerUrl(str(values.broker));
  const token = requireAdminToken(str(values.token));
  const client = new BrokerClient({ baseUrl, token });

  // 1) request — 503 here means the operator is offline.
  const req = await client.request(callsign);
  process.stderr.write(`requested (requestId=${req.requestId}); awaiting Allow…\n`);

  // 2-5) open WS, wait for grant, WebRTC connect, control handshake.
  const session = await SupportSession.connect({
    wsBase: brokerWsBase(baseUrl),
    callsign,
    ticket: req.ticket,
    requestId: req.requestId,
    grantTimeoutMs: grantSeconds * 1000,
  });

  try {
    // 6) fetch diagnostics over the api channel (best-effort per path).
    const responses: (ApiChannelResponse & { path: string })[] = [];
    for (const path of paths) {
      try {
        const r = await session.apiGet(path);
        responses.push({ ...r, path });
      } catch (err) {
        responses.push({
          id: '',
          status: 0,
          path,
          body: `error: ${(err as Error).message}`,
        });
      }
    }

    // 7) capture log backlog + N seconds of live log.
    const { backlog, live } = await session.collectLogs(logSeconds * 1000);

    return buildReport({
      operator: callsign,
      requestId: session.requestId,
      admin: session.admin,
      responses,
      backlog,
      live,
    });
  } finally {
    session.close();
  }
}

/** Synthetic report so `--mock` exercises the shaping path with no network. */
function mockReport(callsign: string, paths: string[]): SupportReport {
  const responses: (ApiChannelResponse & { path: string })[] = paths.map((path) => ({
    id: `mock-${path}`,
    status: 200,
    contentType: 'application/json',
    path,
    body: JSON.stringify({ mock: true, path }),
  }));
  return buildReport({
    operator: callsign,
    requestId: 'mock-request-id',
    admin: 'MOCK-ADMIN',
    responses,
    backlog: ['[mock] backlog line 1', '[mock] backlog line 2'],
    live: ['[mock] live line 1'],
    now: new Date(0),
  });
}

function parsePaths(raw: string): string[] {
  if (!raw) return [...DEFAULT_PULL_PATHS];
  const paths = raw
    .split(',')
    .map((p) => p.trim())
    .filter(Boolean);
  return paths.length ? paths : [...DEFAULT_PULL_PATHS];
}

function parseNum(raw: string, fallback: number): number {
  const n = Number(raw);
  return Number.isFinite(n) && n >= 0 ? n : fallback;
}

function str(v: string | boolean | undefined): string {
  return typeof v === 'string' ? v : '';
}
