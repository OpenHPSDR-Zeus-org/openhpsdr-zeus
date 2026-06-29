// SPDX-License-Identifier: GPL-2.0-or-later
//
// Ft8DecodeLegend — the colour key for the decode table. Each swatch matches the
// row class it documents (CQ / directed-at-me / worked-before / new grid / your
// TX). Tokens only — the swatch colours reference the same --hud-* / --tx
// variables the table rows use (ft8-theme.css), so the legend can never drift
// from the actual row colouring.

const LEGEND: ReadonlyArray<{ cls: string; label: string }> = [
  { cls: 'dw-legend__sw--cq', label: 'CQ' },
  { cls: 'dw-legend__sw--me', label: 'Calling you' },
  { cls: 'dw-legend__sw--new', label: 'New grid' },
  { cls: 'dw-legend__sw--worked', label: 'Worked' },
  { cls: 'dw-legend__sw--tx', label: 'Your TX' },
];

export function Ft8DecodeLegend() {
  return (
    <div className="dw-legend" aria-label="Decode colour key">
      {LEGEND.map((item) => (
        <span key={item.label} className="dw-legend__item">
          <span className={`dw-legend__sw ${item.cls}`} aria-hidden="true" />
          {item.label}
        </span>
      ))}
    </div>
  );
}
