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

import { useEffect } from 'react';
import { useLoggerStore } from '../../state/logger-store';

// QSO timestamps are stored / exported / uploaded to QRZ in UTC throughout
// the server stack (Zeus.Server.Hosting/LogService.cs writes DateTime.UtcNow
// when the client omits a value; QrzService.cs and the ADIF export both
// pull QsoDateTimeUtc directly). The renderer used to drop into
// browser-local time via Date.toLocale*String() with no timezone option —
// which silently shifted the displayed clock for any operator outside
// UTC. Ham-radio convention is to log + display QSO times in UTC always,
// so both formatters pin timeZone:'UTC' and the column label carries a
// "UTC" tag so the operator never has to guess.
//
// Exported so the test (LogbookLive.formatters.test.ts) can assert the
// timezone behaviour without rendering the whole component.
export function formatQsoTimeUtc(isoString: string): string {
  const date = new Date(isoString);
  return date.toLocaleTimeString([], {
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
    timeZone: 'UTC',
  });
}

export function formatQsoDateUtc(isoString: string): string {
  const date = new Date(isoString);
  return date.toLocaleDateString([], {
    month: 'short',
    day: 'numeric',
    timeZone: 'UTC',
  });
}

export function LogbookLive() {
  const entries = useLoggerStore((s) => s.entries);
  const totalCount = useLoggerStore((s) => s.totalCount);
  const loading = useLoggerStore((s) => s.loading);
  const lastPublishResult = useLoggerStore((s) => s.lastPublishResult);
  const publishError = useLoggerStore((s) => s.publishError);
  const clearPublishResult = useLoggerStore((s) => s.clearPublishResult);
  const selectedIds = useLoggerStore((s) => s.selectedIds);
  const toggleSelected = useLoggerStore((s) => s.toggleSelected);

  useEffect(() => {
    // Self-clear publish feedback (shown in the Logbook header) after a few seconds.
    if (lastPublishResult || publishError) {
      const timer = setTimeout(() => {
        clearPublishResult();
      }, 4000);
      return () => clearTimeout(timer);
    }
  }, [lastPublishResult, publishError, clearPublishResult]);

<<<<<<< HEAD
  const formatTime = (isoString: string) => {
    const date = new Date(isoString);
    return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false, timeZone: 'UTC' });
  };

  const formatDate = (isoString: string) => {
    const date = new Date(isoString);
    return date.toLocaleDateString([], { month: 'short', day: 'numeric', timeZone: 'UTC' });
  };

=======
>>>>>>> origin/develop
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
        <span style={{ width: '2rem' }}>✓</span>
<<<<<<< HEAD
        <span>Date</span>
        <span>Time (UTC)</span>
=======
        <span title="QSO date in UTC">Date·UTC</span>
        <span title="QSO time in UTC">Time·UTC</span>
>>>>>>> origin/develop
        <span>Call</span>
        <span>Freq</span>
        <span>Mode</span>
        <span>RST</span>
        <span>Name</span>
      </div>
      <div className="log-rows">
        {entries.map((entry) => (
          <button
            key={entry.id}
            type="button"
            className={`log-row mono ${selectedIds.has(entry.id) ? 'selected' : ''}`}
            onClick={() => toggleSelected(entry.id)}
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
            <span>{entry.frequencyMhz.toFixed(3)}</span>
            <span className="t-mode">{entry.mode}</span>
            <span>{entry.rstSent}/{entry.rstRcvd}</span>
            <span className="t-name" style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', minWidth: 0 }}>
              <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {entry.name ?? '—'}
              </span>
              {entry.qrzLogId && (
                <span style={{ color: 'var(--accent)', fontSize: '0.7em', flexShrink: 0 }}>
                  ✓ QRZ
                </span>
              )}
            </span>
          </button>
        ))}
      </div>
      <div className="log-foot">
        <span style={{ flex: 1 }} />
        <span className="label-xs">{entries.length} of {totalCount}</span>
      </div>
    </div>
  );
}
