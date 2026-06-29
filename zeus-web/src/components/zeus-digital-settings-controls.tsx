// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// House-styled settings rows for the main-Settings "Zeus Digital" tab. These
// mirror the API of the digital pop-out's HUD controls (layout/ft8/
// ft8-settings-controls.tsx) one-for-one — ToggleRow / SegRow / SelectRow /
// TextRow / NumberRow / Chip with identical props — but render in the SAME
// surface family as every other Settings tab: a `.ps-card` ▸ `.ps-field`
// (name + <em> hint, control on the right), `btn sm` toggles & segmented
// buttons, and tokenised inputs. That way the Zeus Digital menu reads as part
// of the Settings menu (PsSettings / DSP / Radio …) instead of the dark-HUD
// operating pop-out.
//
// The pop-out keeps its own HUD controls — these are deliberately a separate,
// house-skinned set so changing the menu never disturbs the live operating
// view. Styling is global-token only (tokens.css + zeus-digital-settings.css);
// no raw hex, no --hud-* layer.

import '../styles/zeus-digital-settings.css';

function Soon() {
  return <span className="zd-soon">soon</span>;
}

export function ToggleRow(props: {
  label: string;
  hint?: string;
  checked: boolean;
  /** Render disabled with a "coming soon" badge — persisted but not yet wired. */
  comingSoon?: boolean;
  onChange: (v: boolean) => void;
}) {
  return (
    <div className={`ps-field${props.comingSoon ? ' is-disabled' : ''}`}>
      <div className="ps-name">
        {props.label}
        {props.comingSoon && <Soon />}
        {props.hint && <em>{props.hint}</em>}
      </div>
      <button
        type="button"
        role="switch"
        aria-checked={props.checked}
        aria-label={props.label}
        aria-disabled={props.comingSoon}
        disabled={props.comingSoon}
        className={`btn sm${props.checked ? ' active' : ''}`}
        onClick={() => !props.comingSoon && props.onChange(!props.checked)}
      >
        {props.checked ? 'ON' : 'OFF'}
      </button>
    </div>
  );
}

export function SegRow<T extends string>(props: {
  label: string;
  hint?: string;
  value: T;
  options: ReadonlyArray<{ value: T; label: string }>;
  /** Render disabled with a "coming soon" badge — persisted but not yet wired. */
  comingSoon?: boolean;
  onChange: (v: T) => void;
}) {
  return (
    <div className={`ps-field${props.comingSoon ? ' is-disabled' : ''}`}>
      <div className="ps-name">
        {props.label}
        {props.comingSoon && <Soon />}
        {props.hint && <em>{props.hint}</em>}
      </div>
      <div className="btn-row" role="group" aria-label={props.label}>
        {props.options.map((o) => (
          <button
            key={o.value}
            type="button"
            aria-pressed={props.value === o.value}
            aria-disabled={props.comingSoon}
            disabled={props.comingSoon}
            className={`btn sm${props.value === o.value ? ' active' : ''}`}
            onClick={() => !props.comingSoon && props.onChange(o.value)}
          >
            {o.label}
          </button>
        ))}
      </div>
    </div>
  );
}

export function SelectRow<T extends string>(props: {
  label: string;
  hint?: string;
  value: T;
  options: ReadonlyArray<{ value: T; label: string }>;
  disabled?: boolean;
  onChange: (v: T) => void;
}) {
  return (
    <div className={`ps-field${props.disabled ? ' is-disabled' : ''}`}>
      <div className="ps-name">
        {props.label}
        {props.hint && <em>{props.hint}</em>}
      </div>
      <select
        className="ps-select-mini"
        aria-label={props.label}
        value={props.value}
        disabled={props.disabled}
        onChange={(e) => props.onChange(e.target.value as T)}
      >
        {props.options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>
    </div>
  );
}

export function TextRow(props: {
  label: string;
  hint?: string;
  value: string;
  placeholder?: string;
  maxLength?: number;
  upper?: boolean;
  /** Render disabled with a "coming soon" badge — persisted but not yet wired. */
  comingSoon?: boolean;
  resolved?: { value: string; fromQrz: boolean };
  onChange: (v: string) => void;
}) {
  return (
    <div className={`ps-field${props.comingSoon ? ' is-disabled' : ''}`}>
      <div className="ps-name">
        {props.label}
        {props.comingSoon && <Soon />}
        {props.hint && <em>{props.hint}</em>}
        {props.resolved && props.resolved.fromQrz && props.resolved.value && (
          <em className="zd-resolved">
            Using QRZ home: <strong>{props.resolved.value}</strong>
          </em>
        )}
      </div>
      <input
        className="zd-input"
        value={props.value}
        placeholder={props.placeholder}
        maxLength={props.maxLength}
        spellCheck={false}
        disabled={props.comingSoon}
        style={props.upper ? { textTransform: 'uppercase' } : undefined}
        onChange={(e) => props.onChange(e.target.value)}
      />
    </div>
  );
}

export function NumberRow(props: {
  label: string;
  hint?: string;
  value: number;
  min?: number;
  max?: number;
  step?: number;
  suffix?: string;
  disabled?: boolean;
  /** Render disabled with a "coming soon" badge — persisted but not yet wired. */
  comingSoon?: boolean;
  onChange: (v: number) => void;
}) {
  const disabled = props.disabled || props.comingSoon;
  return (
    <div className={`ps-field${props.comingSoon ? ' is-disabled' : ''}`}>
      <div className="ps-name">
        {props.label}
        {props.comingSoon && <Soon />}
        {props.hint && <em>{props.hint}</em>}
      </div>
      <span className="ps-ninput">
        <input
          type="number"
          className={`zd-num${props.suffix ? '' : ' no-unit'}`}
          value={props.value}
          min={props.min}
          max={props.max}
          step={props.step ?? 1}
          disabled={disabled}
          onChange={(e) => props.onChange(Number(e.target.value))}
        />
        {props.suffix && <span className="ps-unit">{props.suffix}</span>}
      </span>
    </div>
  );
}

export function Chip(props: { label: string; on: boolean }) {
  return (
    <span className={`zd-chip${props.on ? ' is-on' : ''}`}>
      <span className="zd-chip__dot" />
      {props.label} {props.on ? 'ON' : 'OFF'}
    </span>
  );
}
