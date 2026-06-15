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
import { bearingDeg } from './geo';
import { useQrzStore } from '../../state/qrz-store';
import { useRotatorStore } from '../../state/rotator-store';
import { useContactPropagation } from '../../state/use-contact-propagation';
import type { PropagationBand } from '../../api/propagation';

type QrzCardProps = {
  contact: Contact | null;
  enriching: boolean;
  lookupError?: string | null;
  onLogQso?: () => void;
  canLogQso?: boolean;
  onClear?: () => void;
  canClear?: boolean;
};

function fmtBearing(deg: number): string {
  return `${Math.round(((deg % 360) + 360) % 360).toString().padStart(3, '0')}°`;
}

const DAY_NIGHT_GLYPH: Record<string, { icon: string; label: string }> = {
  day: { icon: '☀', label: 'daytime' },
  night: { icon: '☾', label: 'nighttime' },
  grayline: { icon: '◑', label: 'grayline' },
};

/** Map a P.533 status to a token-driven colour class. */
function propClass(status: string): string {
  switch (status.toUpperCase()) {
    case 'GOOD': return 'qrz-prop--good';
    case 'FAIR': return 'qrz-prop--fair';
    case 'POOR': return 'qrz-prop--poor';
    default: return 'qrz-prop--closed';
  }
}

export function QrzCard({ contact, enriching, lookupError, onLogQso, canLogQso, onClear, canClear }: QrzCardProps) {
  const qrzHome = useQrzStore((s) => s.home);
  const rotConnected = useRotatorStore((s) => !!s.status?.connected);
  const setRotatorAz = useRotatorStore((s) => s.setAzimuth);
  // Point-to-point propagation (you → contact). Hook is unconditional so it sits
  // above the early returns; it no-ops when there's no contact or home coords.
  const { data: prop } = useContactPropagation(
    contact ? { lat: contact.lat, lon: contact.lon } : null,
  );

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
            className="rc-btn rc-btn--neutral qrz-footer-rotate-btn"
            style={{ marginTop: '0.5rem' }}
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
  // Layout note: the rig / antenna / power / qsl rows were dropped in the
  // 2× portrait rework — those fields are rarely consulted in-shack and the
  // operator usually wants the portrait + grid + location front-and-centre
  // for a quick "who am I talking to?" read.
  const dn = contact.dayNight ? DAY_NIGHT_GLYPH[contact.dayNight] : null;
  const rows: [string, string][] = [
    ['Grid', contact.grid],
    ['Lat/Lon', contact.latlon],
    ['CQ·ITU', `${contact.cq} · ${contact.itu}`],
  ];
  if (contact.distanceLabel) rows.push(['Distance', contact.distanceLabel]);
  // Short-/long-path beam headings from home → contact (independent of rotator).
  if (qrzHome?.lat != null && qrzHome?.lon != null) {
    const sp = bearingDeg(qrzHome.lat, qrzHome.lon, contact.lat, contact.lon);
    rows.push(['Beam', `SP ${fmtBearing(sp)} · LP ${fmtBearing((sp + 180) % 360)}`]);
  }

  const classTag = contact.class !== '—'
    ? `${contact.class}${contact.licenseCodes ? ` ${contact.licenseCodes}` : ''}`
    : null;
  const licTag = contact.licensed !== '—'
    ? `Lic. ${contact.licensed}${contact.licensedYears != null
      ? ` · ${contact.licensedYears} yr${contact.licensedYears === 1 ? '' : 's'}`
      : ''}`
    : null;

  // QSL channels the operator actually accepts (null flags ⇒ unknown ⇒ hidden).
  const qslBadges: string[] = [];
  if (contact.qslLotw) qslBadges.push('LoTW');
  if (contact.qslEqsl) qslBadges.push('eQSL');
  if (contact.qslMail) qslBadges.push('Direct');
  if (contact.qslManager) qslBadges.push(`via ${contact.qslManager}`);

  return (
    <div className="qrz-card">
      <div className="qrz-card-main">
        <div className="qrz-info-col">
          <div className="qrz-id">
            <div className="qrz-call">{contact.callsign}</div>
            <div className="qrz-name">{contact.name}</div>
            <div className="qrz-loc">
              {contact.flag} {contact.location}
            </div>
            <div className="qrz-tags">
              {classTag && <span className="qrz-tag">{classTag}</span>}
              {licTag && <span className="qrz-tag">{licTag}</span>}
              {dn && (
                <span className="qrz-tag" title={`It is ${dn.label} at ${contact.callsign}`}>
                  {dn.icon} {contact.local !== '—' ? contact.local : dn.label}
                </span>
              )}
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
          {qslBadges.length > 0 && (
            <div className="qrz-qsl-row">
              <span className="k label-xs">QSL</span>
              <span className="qrz-qsl-badges">
                {qslBadges.map((b) => (
                  <span key={b} className="qrz-qsl-badge">{b}</span>
                ))}
              </span>
            </div>
          )}
        </div>
        <div className="qrz-portrait qrz-portrait--large">
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
      </div>

      {prop?.available && (
        <div className="qrz-prop">
          <div className="qrz-prop-head">
            <span className="qrz-section-label">Propagation · you → {contact.callsign}</span>
            <span className="qrz-prop-solar mono">
              SFI {Math.round(prop.sfi)} · K {prop.kIndex} · MUF {prop.muf.toFixed(1)}
            </span>
          </div>
          <div className="qrz-prop-body">
            {prop.currentBand ? (
              <div className={`qrz-prop-current ${propClass(prop.currentBand.status)}`}>
                <div className="qrz-prop-pct">
                  {prop.currentBand.reliability}
                  <span className="qrz-prop-pct-sym">%</span>
                </div>
                <div className="qrz-prop-meta">
                  <div className="qrz-prop-band">
                    {prop.currentBand.band} · {prop.currentBand.status}
                  </div>
                  {prop.currentBand.snr && (
                    <div className="qrz-prop-snr mono">SNR {prop.currentBand.snr}</div>
                  )}
                </div>
              </div>
            ) : (
              <div className="qrz-prop-current qrz-prop--closed">
                <div className="qrz-prop-meta">
                  <div className="qrz-prop-band">Tune to an HF band</div>
                </div>
              </div>
            )}
            <div className="qrz-prop-bands">
              <div className="qrz-prop-bands-label label-xs">Best bands now</div>
              <div className="qrz-prop-chips">
                {(() => {
                  const open = prop.bands.filter((b: PropagationBand) => b.reliability > 0);
                  if (open.length === 0) {
                    return <span className="qrz-prop-chip qrz-prop--closed">No bands open</span>;
                  }
                  return open.slice(0, 4).map((b: PropagationBand) => (
                    <span
                      key={b.band}
                      className={`qrz-prop-chip ${propClass(b.status)}`}
                      title={`${b.reliability}% reliability · ${b.snr}`}
                    >
                      {b.band} <b>{b.reliability}%</b>
                    </span>
                  ));
                })()}
              </div>
            </div>
          </div>
          <div className="qrz-prop-foot label-xs">
            {prop.model} · {prop.distanceKm.toLocaleString()} km path
          </div>
        </div>
      )}

      <div className="qrz-footer">
        <span className="mono" style={{ color: 'var(--fg-2)', fontSize: 10 }}>
          {contact.email}
        </span>
        <span style={{ flex: 1 }} />
        {rotConnected && qrzHome?.lat != null && qrzHome?.lon != null
          && contact.lat != null && contact.lon != null && (() => {
          const sp = bearingDeg(qrzHome.lat, qrzHome.lon, contact.lat, contact.lon);
          const lp = (sp + 180) % 360;
          return (
            <>
              <button
                type="button"
                onClick={() => { void setRotatorAz(Math.round(sp)); }}
                className="rc-btn rc-btn--path qrz-footer-rotate-btn"
                title="Rotate short-path"
              >
                SP {fmtBearing(sp)}
              </button>
              <button
                type="button"
                onClick={() => { void setRotatorAz(Math.round(lp)); }}
                className="rc-btn rc-btn--path qrz-footer-rotate-btn"
                title="Rotate long-path"
              >
                LP {fmtBearing(lp)}
              </button>
            </>
          );
        })()}
        {onClear && canClear && (
          <button
            type="button"
            onClick={onClear}
            className="rc-btn rc-btn--neutral qrz-footer-rotate-btn"
          >
            Clear
          </button>
        )}
        {onLogQso && canLogQso && (
          <button
            type="button"
            onClick={onLogQso}
            className="rc-btn rc-btn--neutral qrz-footer-rotate-btn"
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
