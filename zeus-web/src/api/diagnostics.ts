// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

// "Report a problem" self-diagnostic REST surface. The backend collects a
// snapshot of radio / DSP / connection state, formats it as a Markdown report,
// and pre-fills a bug-report page URL the operator can open in their browser.
//
//   GET  /api/diagnostics/symptoms -> Symptom[]
//   POST /api/diagnostics/report   -> { report, markdown, githubIssueUrl }

import { ApiError } from './client';

/** A pickable symptom shown in the report form's grouped dropdown. */
export type Symptom = {
  id: string;
  label: string;
  group: string;
};

/** Result of a diagnostic run. `report` is an opaque structured snapshot used
 *  by the backend; the UI only consumes `markdown` (preview + clipboard) and
 *  `githubIssueUrl` (the pre-filled bug-report page). */
export type ReportResult = {
  report: unknown;
  markdown: string;
  githubIssueUrl: string;
};

/** Request body for a diagnostic run. Either field may be null. */
export type ReportRequest = {
  symptomId: string | null;
  freeText: string | null;
};

function toStr(v: unknown): string {
  return typeof v === 'string' ? v : '';
}

function normalizeSymptom(raw: unknown): Symptom {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    id: toStr(r.id),
    label: toStr(r.label),
    group: toStr(r.group),
  };
}

function normalizeReportResult(raw: unknown): ReportResult {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    report: r.report ?? null,
    markdown: toStr(r.markdown),
    githubIssueUrl: toStr(r.githubIssueUrl),
  };
}

async function jsonFetch<T>(
  input: RequestInfo,
  init: RequestInit | undefined,
  parse: (raw: unknown) => T,
): Promise<T> {
  const res = await fetch(input, init);
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const body = (await res.json()) as unknown;
      if (
        body &&
        typeof body === 'object' &&
        'error' in body &&
        typeof (body as { error: unknown }).error === 'string'
      ) {
        message = (body as { error: string }).error;
      }
    } catch {
      /* non-JSON body — keep status text */
    }
    throw new ApiError(res.status, message);
  }
  return parse((await res.json()) as unknown);
}

export function fetchSymptoms(signal?: AbortSignal): Promise<Symptom[]> {
  return jsonFetch('/api/diagnostics/symptoms', { signal }, (raw) =>
    Array.isArray(raw) ? raw.map(normalizeSymptom) : [],
  );
}

export function runReport(
  req: ReportRequest,
  signal?: AbortSignal,
): Promise<ReportResult> {
  return jsonFetch(
    '/api/diagnostics/report',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        symptomId: req.symptomId,
        freeText: req.freeText,
      }),
      signal,
    },
    normalizeReportResult,
  );
}
