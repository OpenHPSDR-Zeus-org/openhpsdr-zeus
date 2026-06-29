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

import { useState } from 'react';
import { Eye, EyeOff, Search, X } from 'lucide-react';
import { LogbookLive } from '../../components/design/LogbookLive';
import { useLoggerStore } from '../../state/logger-store';
import { useWorkspace } from '../WorkspaceContext';

export function LogbookPanel() {
  const { logbookActions } = useWorkspace();
  const [searchText, setSearchText] = useState('');
  const [hideQrzPublished, setHideQrzPublished] = useState(false);
  const qrzPublishedCount = useLoggerStore((s) =>
    s.entries.reduce((count, entry) => count + (entry.qrzLogId ? 1 : 0), 0),
  );
  const query = searchText.trim();
  const qrzPublishedLabel = qrzPublishedCount === 1
    ? '1 QRZ-published QSO'
    : `${qrzPublishedCount} QRZ-published QSOs`;
  const qrzToggleTitle = hideQrzPublished
    ? `Show ${qrzPublishedLabel}`
    : qrzPublishedCount > 0
      ? `Hide ${qrzPublishedLabel}`
      : 'No QRZ-published QSOs to hide';
  const QrzVisibilityIcon = hideQrzPublished ? EyeOff : Eye;

  return (
    <div className="logbook-panel">
      <div className="logbook-actions">
        <div className="log-search">
          <Search size={14} strokeWidth={2} aria-hidden="true" />
          <input
            type="search"
            value={searchText}
            onChange={(event) => setSearchText(event.target.value)}
            placeholder="Search logbook"
            aria-label="Search logbook"
            spellCheck={false}
          />
          {query && (
            <button
              type="button"
              className="log-search-clear"
              onClick={() => setSearchText('')}
              aria-label="Clear logbook search"
              title="Clear search"
            >
              <X size={13} strokeWidth={2.2} aria-hidden="true" />
            </button>
          )}
        </div>
        <button
          type="button"
          className={`btn ghost sm logbook-visibility-toggle ${hideQrzPublished ? 'active' : ''}`}
          onClick={() => setHideQrzPublished((value) => !value)}
          disabled={qrzPublishedCount === 0 && !hideQrzPublished}
          aria-label={qrzToggleTitle}
          aria-pressed={hideQrzPublished}
          title={qrzToggleTitle}
        >
          <QrzVisibilityIcon size={14} strokeWidth={2.2} aria-hidden="true" />
        </button>
        {logbookActions}
      </div>
      <div className="logbook-panel-body">
        <LogbookLive searchText={searchText} hideQrzPublished={hideQrzPublished} />
      </div>
    </div>
  );
}
