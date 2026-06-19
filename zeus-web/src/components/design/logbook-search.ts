// SPDX-License-Identifier: GPL-2.0-or-later

import type { LogEntry } from '../../api/log';
import { formatQsoDateUtc, formatQsoTimeUtc } from './logbook-formatters';

function normalizeSearchText(value: string): string {
  return value.trim().toLocaleLowerCase();
}

function compactSearchParts(parts: Array<string | number | null | undefined>): string {
  return parts
    .filter((part): part is string | number => part !== null && part !== undefined && part !== '')
    .join(' ')
    .toLocaleLowerCase();
}

export type LogEntryFilterOptions = {
  hideQrzPublished?: boolean;
};

export function isQrzPublished(entry: LogEntry): boolean {
  return !!entry.qrzLogId;
}

export function logEntrySearchText(entry: LogEntry): string {
  return compactSearchParts([
    entry.callsign,
    formatQsoDateUtc(entry.qsoDateTimeUtc),
    formatQsoTimeUtc(entry.qsoDateTimeUtc),
    entry.qsoDateTimeUtc,
    entry.frequencyMhz.toFixed(3),
    entry.frequencyMhz.toString(),
    entry.band,
    entry.mode,
    entry.rstSent,
    entry.rstRcvd,
    entry.name,
    entry.grid,
    entry.country,
    entry.dxcc,
    entry.cqZone,
    entry.ituZone,
    entry.state,
    entry.comment,
    entry.qrzLogId,
  ]);
}

export function filterLogEntries(
  entries: LogEntry[],
  query: string,
  options: LogEntryFilterOptions = {},
): LogEntry[] {
  const candidates = options.hideQrzPublished
    ? entries.filter((entry) => !isQrzPublished(entry))
    : entries;
  const terms = normalizeSearchText(query).split(/\s+/).filter(Boolean);
  if (terms.length === 0) return candidates;

  return candidates.filter((entry) => {
    const haystack = logEntrySearchText(entry);
    return terms.every((term) => haystack.includes(term));
  });
}
