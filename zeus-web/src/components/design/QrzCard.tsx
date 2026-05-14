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

import type { Contact } from './data';

type QrzCardProps = {
  contact: Contact | null;
  enriching: boolean;
  lookupError?: string | null;
  onLogQso?: () => void;
  canLogQso?: boolean;
  onClear?: () => void;
  canClear?: boolean;
};

export function QrzCard({ contact, enriching, lookupError, onLogQso, canLogQso, onClear, canClear }: QrzCardProps) {
  // Show "Not found" if there's a lookup error
  if (lookupError) {
    return (
      <div className="qrz-empty">
        <div className="label-xs" style={{ color: 'var(--fg-error)', opacity: 0.8 }}>
          Not found: {lookupError}
        </div>
        {onClear && canClear && (
          <button
            type="button"
            onClick={onClear}
            className="btn sm"
            style={{ marginTop: '0.5rem', padding: '0.25rem 0.5rem', fontSize: 10 }}
          >
            Clear
          </button>
        )}
      </div>
    );
  }

  if (!contact) {
    return (
      <div className="qrz-empty">
        <div className="label-xs" style={{ opacity: 0.5 }}>
          No callsign — click "Engage QRZ" or type a callsign
        </div>
      </div>
    );
  }
  const rows: [string, string][] = [
    ['Grid', contact.grid],
    ['Rig', contact.rig],
    ['Lat/Lon', contact.latlon],
    ['Antenna', contact.ant],
    ['CQ·ITU', `${contact.cq} · ${contact.itu}`],
    ['Power', contact.power],
    ['Local', contact.local],
    ['QSL', contact.qsl],
  ];
  return (
    <div className="qrz-card">
      <div className="qrz-header">
        <div className="qrz-portrait">
          <div className="qrz-portrait-bg" aria-hidden>
            <div className="qrz-grid" />
          </div>
          {contact.photoUrl ? (
            <img
              className="qrz-portrait-img"
              src={contact.photoUrl}
              alt={`${contact.callsign} operator portrait`}
              loading="lazy"
              referrerPolicy="no-referrer"
            />
          ) : (
            <div className="qrz-portrait-initials">{contact.initials}</div>
          )}
          <div className="qrz-portrait-flag">{contact.flag}</div>
          {!contact.photoUrl && (
            <div className="qrz-portrait-placeholder label-xs">[ operator photo ]</div>
          )}
          {enriching && <div className="qrz-scan" />}
        </div>
        <div className="qrz-id">
          <div className="qrz-call">{contact.callsign}</div>
          <div className="qrz-name">{contact.name}</div>
          <div className="qrz-loc">
            {contact.flag} {contact.location}
          </div>
          <div className="qrz-tags">
            <span className="qrz-tag">{contact.class}</span>
            <span className="qrz-tag">Lic. {contact.licensed}</span>
            <span className="qrz-tag">Age {contact.age}</span>
          </div>
        </div>
      </div>

      <div className="qrz-section-label">Location · Station</div>
      <div className="qrz-grid-rows">
        {rows.map(([k, v]) => (
          <div key={k} className="qrz-row">
            <span className="k label-xs">{k}</span>
            <span className="v mono">{v}</span>
          </div>
        ))}
      </div>

      <div className="qrz-footer">
        <span className="mono" style={{ color: 'var(--fg-2)', fontSize: 10 }}>
          {contact.email}
        </span>
        <span style={{ flex: 1 }} />
        {onClear && canClear && (
          <button
            type="button"
            onClick={onClear}
            className="btn sm"
            style={{ marginRight: '0.5rem', padding: '0.25rem 0.5rem', fontSize: 10 }}
          >
            Clear
          </button>
        )}
        {onLogQso && canLogQso && (
          <button
            type="button"
            onClick={onLogQso}
            className="btn sm"
            style={{ marginRight: '0.5rem', padding: '0.25rem 0.5rem', fontSize: 10 }}
          >
            Log QSO
          </button>
        )}
        {contact.qrzUrl ? (
          <a
            className="mono"
            href={contact.qrzUrl}
            target="_blank"
            rel="noreferrer"
            style={{ color: 'var(--accent)', fontSize: 10, fontWeight: 700, textDecoration: 'none' }}
          >
            QRZ.COM ↗
          </a>
        ) : (
          <span className="mono" style={{ color: 'var(--accent)', fontSize: 10, fontWeight: 700 }}>
            QRZ.COM ✓
          </span>
        )}
      </div>
    </div>
  );
}
