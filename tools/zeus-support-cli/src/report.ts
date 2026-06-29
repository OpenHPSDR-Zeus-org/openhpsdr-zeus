/**
 * Report shaping for `pull`: turns the raw api-channel responses + captured log
 * into a tidy JSON or text report. Kept pure (no I/O, no network) so it is unit
 * testable and reused by the `--mock` path.
 */

import type { ApiChannelResponse } from './types.js';

/** The default set of read-only diagnostics endpoints `pull` fetches. */
export const DEFAULT_PULL_PATHS = [
  '/api/version',
  '/api/state',
  '/api/diagnostics/v2',
] as const;

export interface DiagnosticEntry {
  path: string;
  status: number;
  contentType?: string;
  /** Parsed JSON body if the contentType was JSON and it parsed; else raw string. */
  body: unknown;
}

export interface SupportReport {
  schema: 'zeus-support-pull/1';
  generatedAt: string;
  operator: string;
  requestId: string;
  admin: string;
  diagnostics: DiagnosticEntry[];
  log: {
    backlog: string[];
    live: string[];
  };
}

export interface BuildReportArgs {
  operator: string;
  requestId: string;
  admin: string;
  responses: ApiChannelResponse[];
  backlog: string[];
  live: string[];
  now?: Date;
}

/** Build the structured report object from raw channel data. */
export function buildReport(args: BuildReportArgs): SupportReport {
  return {
    schema: 'zeus-support-pull/1',
    generatedAt: (args.now ?? new Date()).toISOString(),
    operator: args.operator,
    requestId: args.requestId,
    admin: args.admin,
    diagnostics: args.responses.map(toEntry),
    log: { backlog: args.backlog, live: args.live },
  };
}

function toEntry(r: ApiChannelResponse & { path?: string }): DiagnosticEntry {
  return {
    path: r.path ?? '(unknown)',
    status: r.status,
    contentType: r.contentType,
    body: maybeJson(r.body, r.contentType),
  };
}

/** Parse a body as JSON when the contentType says JSON; otherwise keep the string. */
function maybeJson(body: string, contentType?: string): unknown {
  if (contentType && /json/i.test(contentType)) {
    try {
      return JSON.parse(body);
    } catch {
      return body;
    }
  }
  return body;
}

/** Render the report as pretty JSON. */
export function renderJson(report: SupportReport): string {
  return JSON.stringify(report, null, 2);
}

/** Render the report as a human-readable text summary. */
export function renderText(report: SupportReport): string {
  const lines: string[] = [];
  lines.push(`Zeus support pull — ${report.operator}`);
  lines.push(`  generated : ${report.generatedAt}`);
  lines.push(`  admin     : ${report.admin}`);
  lines.push(`  requestId : ${report.requestId}`);
  lines.push('');
  lines.push('Diagnostics');
  for (const d of report.diagnostics) {
    lines.push(`  [${d.status}] ${d.path}${d.contentType ? ` (${d.contentType})` : ''}`);
    const rendered = typeof d.body === 'string' ? d.body : JSON.stringify(d.body, null, 2);
    for (const l of rendered.split('\n')) lines.push(`      ${l}`);
  }
  lines.push('');
  lines.push(`Log backlog (${report.log.backlog.length} lines)`);
  for (const l of report.log.backlog) lines.push(`  ${l}`);
  lines.push('');
  lines.push(`Live log (${report.log.live.length} lines)`);
  for (const l of report.log.live) lines.push(`  ${l}`);
  return lines.join('\n');
}
