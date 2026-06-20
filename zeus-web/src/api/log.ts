// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// Zeus Log API client

export type LogEntry = {
  id: string;
  qsoDateTimeUtc: string;
  callsign: string;
  name: string | null;
  frequencyMhz: number | null;
  band: string;
  mode: string;
  rstSent: string;
  rstRcvd: string;
  grid: string | null;
  country: string | null;
  dxcc: number | null;
  cqZone: number | null;
  ituZone: number | null;
  state: string | null;
  comment: string | null;
  createdUtc: string;
  qrzLogId: string | null;
  qrzUploadedUtc: string | null;
};

export type CreateLogEntryRequest = {
  callsign: string;
  name?: string | null;
  frequencyMhz: number;
  band: string;
  mode: string;
  rstSent: string;
  rstRcvd: string;
  grid?: string | null;
  country?: string | null;
  dxcc?: number | null;
  cqZone?: number | null;
  ituZone?: number | null;
  state?: string | null;
  comment?: string | null;
  qsoDateTimeUtc?: string | null;
};

export type LogEntriesResponse = {
  entries: LogEntry[];
  totalCount: number;
};

export type WorkedCallsignRecentQso = {
  qsoDateTimeUtc: string;
  band: string | null;
  mode: string | null;
  frequencyMhz: number | null;
  rstSent: string | null;
  rstRcvd: string | null;
  name: string | null;
  grid: string | null;
  country: string | null;
  state: string | null;
  comment: string | null;
  qrzLogId: string | null;
};

export type WorkedCallsignSummary = {
  callsign: string;
  workedBefore: boolean;
  totalCount: number;
  lastWorkedUtc: string | null;
  lastBand: string | null;
  lastMode: string | null;
  lastFrequencyMhz: number | null;
  lastRstSent: string | null;
  lastRstRcvd: string | null;
  lastName: string | null;
  lastGrid: string | null;
  lastCountry: string | null;
  lastState: string | null;
  lastComment: string | null;
  bands: string[];
  modes: string[];
  recentQsos: WorkedCallsignRecentQso[];
};

export type QrzPublishRequest = {
  logEntryIds: string[];
};

export type AdifImportError = {
  recordNumber: number;
  message: string;
};

export type AdifImportResponse = {
  totalRecords: number;
  importedCount: number;
  duplicateCount: number;
  skippedCount: number;
  errors: AdifImportError[];
};

export type QrzPublishResponse = {
  totalCount: number;
  successCount: number;
  failedCount: number;
  results: QrzPublishResult[];
};

export type QrzPublishResult = {
  logEntryId: string;
  success: boolean;
  qrzLogId: string | null;
  message: string | null;
};

// API functions

export async function getLogEntries(
  skip = 0,
  take = 100,
  signal?: AbortSignal
): Promise<LogEntriesResponse> {
  const url = `/api/log/entries?skip=${skip}&take=${take}`;
  const response = await fetch(url, { signal });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return await response.json();
}

export async function getWorkedCallsignSummary(
  callsign: string,
  signal?: AbortSignal
): Promise<WorkedCallsignSummary> {
  const url = `/api/log/worked?callsign=${encodeURIComponent(callsign)}&recent=5`;
  const response = await fetch(url, { signal });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return await response.json();
}

export async function createLogEntry(
  request: CreateLogEntryRequest,
  signal?: AbortSignal
): Promise<LogEntry> {
  const response = await fetch('/api/log/entry', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
    signal,
  });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return await response.json();
}

export async function exportToAdif(signal?: AbortSignal): Promise<void> {
  const response = await fetch('/api/log/export/adif', { signal });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);

  const blob = await response.blob();
  const url = window.URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = `zeus-log-${new Date().toISOString().slice(0, 10)}.adi`;
  document.body.appendChild(a);
  a.click();
  window.URL.revokeObjectURL(url);
  document.body.removeChild(a);
}

export async function importAdif(file: File, signal?: AbortSignal): Promise<AdifImportResponse> {
  const adif = await file.text();
  const response = await fetch('/api/log/import/adif', {
    method: 'POST',
    headers: { 'Content-Type': 'text/plain;charset=utf-8' },
    body: adif,
    signal,
  });
  if (!response.ok) {
    const text = await response.text();
    let message = text;
    try {
      const parsed = JSON.parse(text) as { error?: unknown };
      if (typeof parsed.error === 'string') message = parsed.error;
    } catch {
      /* keep the raw response text */
    }
    throw new Error(message || `HTTP ${response.status}`);
  }
  return await response.json();
}

export async function publishToQrz(
  request: QrzPublishRequest,
  signal?: AbortSignal
): Promise<QrzPublishResponse> {
  const response = await fetch('/api/log/publish/qrz', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
    signal,
  });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return await response.json();
}
