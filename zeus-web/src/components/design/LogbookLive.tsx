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

import { useEffect, useMemo, useRef } from 'react';
import { useLoggerStore } from '../../state/logger-store';
import type { LogEntry } from '../../api/log';
import { formatQsoDateUtc, formatQsoTimeUtc } from './logbook-formatters';
import { filterLogEntries } from './logbook-search';

function compactList(parts: Array<string | null | undefined>): string {
  return parts.filter((p): p is string => !!p).join(' · ');
}

function logLocation(entry: LogEntry): string {
  return compactList([entry.grid, entry.state, entry.country]) || '—';
}

function logMeta(entry: LogEntry): string {
  return compactList([
    entry.band,
    logLocation(entry) !== '—' ? logLocation(entry) : null,
    entry.comment,
  ]) || '—';
}

function logRowTitle(entry: LogEntry): string {
  return compactList([
    `${entry.callsign} ${formatQsoDateUtc(entry.qsoDateTimeUtc)} ${formatQsoTimeUtc(entry.qsoDateTimeUtc)}Z`,
    `${entry.frequencyMhz.toFixed(3)} MHz`,
    entry.band,
    entry.mode,
    `RST ${entry.rstSent}/${entry.rstRcvd}`,
    entry.name,
    logLocation(entry) !== '—' ? logLocation(entry) : null,
    entry.comment,
    entry.qrzLogId ? `QRZ ${entry.qrzLogId}` : null,
  ]);
}

type LogbookLiveProps = {
  searchText: string;
  hideQrzPublished: boolean;
};

export function LogbookLive({ searchText, hideQrzPublished }: LogbookLiveProps) {
  const entries = useLoggerStore((s) => s.entries);
  const totalCount = useLoggerStore((s) => s.totalCount);
  const loading = useLoggerStore((s) => s.loading);
  const lastPublishResult = useLoggerStore((s) => s.lastPublishResult);
  const publishError = useLoggerStore((s) => s.publishError);
  const clearPublishResult = useLoggerStore((s) => s.clearPublishResult);
  const selectedIds = useLoggerStore((s) => s.selectedIds);
  const toggleSelected = useLoggerStore((s) => s.toggleSelected);
  const setSelectedIds = useLoggerStore((s) => s.setSelectedIds);
  const selectAllRef = useRef<HTMLInputElement>(null);
  const query = searchText.trim();
  const filteredEntries = useMemo(
    () => filterLogEntries(entries, searchText, { hideQrzPublished }),
    [entries, hideQrzPublished, searchText],
  );
  const filtersActive = query.length > 0 || hideQrzPublished;
  const visibleIds = useMemo(() => new Set(filteredEntries.map((entry) => entry.id)), [filteredEntries]);
  const selectedVisibleCount = filteredEntries.reduce(
    (count, entry) => count + (selectedIds.has(entry.id) ? 1 : 0),
    0,
  );
  const allVisibleSelected = filteredEntries.length > 0 && selectedVisibleCount === filteredEntries.length;
  const someVisibleSelected = selectedVisibleCount > 0 && !allVisibleSelected;

  useEffect(() => {
    // Self-clear publish feedback (shown in the Logbook header) after a few seconds.
    if (lastPublishResult || publishError) {
      const timer = setTimeout(() => {
        clearPublishResult();
      }, 4000);
      return () => clearTimeout(timer);
    }
  }, [lastPublishResult, publishError, clearPublishResult]);

  useEffect(() => {
    if (selectAllRef.current) {
      selectAllRef.current.indeterminate = someVisibleSelected;
    }
  }, [someVisibleSelected]);

  if (loading && entries.length === 0) {
    return (
      <div className="logbook">
        <div className="log-rows" style={{ padding: '2rem', textAlign: 'center', opacity: 0.5 }}>
          Loading log entries...
        </div>
      </div>
    );
  }

  if (entries.length === 0) {
    return (
      <div className="logbook">
        <div className="log-rows" style={{ padding: '2rem', textAlign: 'center', opacity: 0.5 }}>
          No log entries yet. Log a QSO from the QRZ panel to get started.
        </div>
      </div>
    );
  }

  return (
    <div className="logbook">
      <div className="log-head mono">
        <span className="log-select-cell">
          <input
            ref={selectAllRef}
            type="checkbox"
            checked={allVisibleSelected}
            disabled={filteredEntries.length === 0}
            onChange={() => {
              setSelectedIds(
                allVisibleSelected
                  ? [...selectedIds].filter((id) => !visibleIds.has(id))
                  : [...selectedIds, ...filteredEntries.map((entry) => entry.id)],
              );
            }}
            aria-label={allVisibleSelected ? 'Clear selected visible log entries' : 'Select visible log entries'}
            title={allVisibleSelected ? 'Clear selected visible log entries' : 'Select visible log entries'}
          />
        </span>
        <span title="QSO date in UTC">Date·UTC</span>
        <span title="QSO time in UTC">Time·UTC</span>
        <span>Call</span>
        <span>Freq·Band</span>
        <span>Mode</span>
        <span>RST</span>
        <span>Name · QTH · Notes</span>
      </div>
      <div className="log-rows">
        {filteredEntries.length === 0 && (
          <div className="log-empty">
            {query
              ? `No log entries match "${query}".`
              : 'No unpublished log entries to show.'}
          </div>
        )}
        {filteredEntries.map((entry) => (
          <button
            key={entry.id}
            type="button"
            className={`log-row mono ${selectedIds.has(entry.id) ? 'selected' : ''}`}
            onClick={() => toggleSelected(entry.id)}
            title={logRowTitle(entry)}
          >
            <span>
              <input
                type="checkbox"
                checked={selectedIds.has(entry.id)}
                readOnly
                tabIndex={-1}
                style={{ cursor: 'pointer', pointerEvents: 'none' }}
              />
            </span>
            <span className="t-date" title={entry.qsoDateTimeUtc}>
              {formatQsoDateUtc(entry.qsoDateTimeUtc)}
            </span>
            <span className="t-time" title={entry.qsoDateTimeUtc}>
              {formatQsoTimeUtc(entry.qsoDateTimeUtc)}
            </span>
            <span className="t-call">{entry.callsign}</span>
            <span className="t-freq log-cell-stack">
              <span>{entry.frequencyMhz.toFixed(3)}</span>
              <span className="log-sub">{entry.band || '—'}</span>
            </span>
            <span className="t-mode">{entry.mode}</span>
            <span className="log-cell-stack">
              <span>{entry.rstSent}/{entry.rstRcvd}</span>
              <span className="log-sub">{entry.grid || '—'}</span>
            </span>
            <span className="t-name log-cell-stack">
              <span className="log-name-line">
                <span className="log-name-text">
                  {entry.name ?? '—'}
                </span>
                {entry.qrzLogId && (
                  <span className="log-sync-pill">✓ QRZ</span>
                )}
              </span>
              <span className="log-meta-line">{logMeta(entry)}</span>
            </span>
          </button>
        ))}
      </div>
      <div className="log-foot">
        <span style={{ flex: 1 }} />
        <span className="label-xs">
          {filtersActive
            ? `${filteredEntries.length} of ${entries.length} visible · ${totalCount} total`
            : `${entries.length} of ${totalCount}`}
        </span>
      </div>
    </div>
  );
}
