// SPDX-License-Identifier: GPL-2.0-or-later
//
// Ft8Stats — the FT8/FT4 workspace STATS panel (right column, fixed slot). Pure
// presentation over the EXISTING logbook store (useLoggerStore) via the pure
// `computeFt8Stats` aggregator. HUD tokens only.
//
// DXCC caveat: there is no country/DXCC resolver in-tree (no cty.dat), so a
// native FT8 QSO carries no DXCC entity. The DXCC tile therefore counts only
// QSOs whose entity came from an ADIF import — it is a floor, not a true total,
// and is labelled as such. Distinct GRIDS is the honest native worked-coverage
// proxy. (#1015 follow-up: add a prefix→DXCC resolver.)

import { useMemo } from 'react';
import { useLoggerStore } from '../../state/logger-store';
import { useOperatorStore } from '../../state/operator-store';
import { computeFt8Stats } from '../../dsp/ft8-qso-log';

function Stat({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div className="ft8-stat" title={hint}>
      <div className="ft8-stat__value">{value}</div>
      <div className="ft8-stat__label">{label}</div>
    </div>
  );
}

export function Ft8Stats() {
  const entries = useLoggerStore((s) => s.entries);
  const totalCount = useLoggerStore((s) => s.totalCount);
  // Resolved grid (override else QRZ home) so best-DX distance still computes for
  // a QRZ-home operator who never typed a grid override.
  const myGrid = useOperatorStore((s) => s.resolvedGrid);

  const stats = useMemo(() => computeFt8Stats(entries, myGrid || null), [entries, myGrid]);

  // The store only loads the most-recent 100 QSOs, so computeFt8Stats's window
  // length undercounts the true total — use the server's totalCount for the
  // headline tile so it matches the activity-log bar beside it. (Today/Grids/
  // DXCC/Avg/BestDX still reflect the loaded window — see the note below.)
  const total = Math.max(totalCount, stats.qsosTotal);

  const avgSnr = (() => {
    if (stats.avgSnrRx == null) return '—';
    // Round first so e.g. -0.4 renders "0", never "-0".
    const v = Math.round(stats.avgSnrRx);
    return `${v > 0 ? '+' : ''}${v}`;
  })();

  const bestDx = stats.bestDx
    ? `${Math.round(stats.bestDx.km).toLocaleString()} km`
    : '—';

  return (
    <div className="ft8-stats">
      <div className="ft8-stats__grid">
        <Stat label="QSOs Today" value={String(stats.qsosToday)} />
        <Stat label="Total QSOs" value={String(total)} />
        <Stat
          label="Confirmed"
          value={String(stats.confirmed)}
          hint="QSOs uploaded to QRZ"
        />
        <Stat label="Grids" value={String(stats.distinctGrids)} />
        <Stat
          label="DXCC*"
          value={String(stats.distinctDxcc)}
          hint="DXCC entities — ADIF-import QSOs only (no native resolver yet)"
        />
        <Stat label="Avg SNR RX" value={avgSnr} hint="Average received signal report (dB)" />
      </div>
      <div className="ft8-stats__bestdx">
        <span className="ft8-stats__bestdx-label">Best DX</span>
        <span className="ft8-stats__bestdx-value">
          {stats.bestDx ? `${stats.bestDx.callsign} · ${bestDx}` : bestDx}
        </span>
      </div>
      <div className="ft8-stats__note">
        * DXCC counts ADIF-imported QSOs only — native FT8 QSOs have no country
        resolver yet. Grids is the native coverage measure. Today / Grids / DXCC /
        Avg SNR / Best DX reflect the most-recent 100 QSOs; Total QSOs is the full
        logbook count.
      </div>
    </div>
  );
}
