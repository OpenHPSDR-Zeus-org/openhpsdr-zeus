// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Shared token-styled settings controls for the digital (FT8/FT4/WSPR) config.
// Extracted from the former in-workspace Ft8SettingsView so the same widgets can
// be reused by the main-Settings "Zeus Digital" section AND the pop-out's compact
// message editor (DRY). Styling is HUD-token-only (ft8-theme.css --hud-*).

import '../../styles/ft8-theme.css';

export function ToggleRow(props: {
  label: string;
  hint?: string;
  checked: boolean;
  /** Render disabled with a "coming soon" badge — persisted but not yet wired. */
  comingSoon?: boolean;
  onChange: (v: boolean) => void;
}) {
  return (
    <label className={`ft8-set-row ft8-set-row--toggle${props.comingSoon ? ' is-disabled' : ''}`}>
      <span className="ft8-set-row__text">
        <span className="ft8-set-row__label">
          {props.label}
          {props.comingSoon && <span className="ft8-set-soon">soon</span>}
        </span>
        {props.hint && <span className="ft8-set-row__hint">{props.hint}</span>}
      </span>
      <button
        type="button"
        role="switch"
        aria-checked={props.checked}
        aria-label={props.label}
        aria-disabled={props.comingSoon}
        disabled={props.comingSoon}
        className={`ft8-set-switch${props.checked ? ' is-on' : ''}`}
        onClick={() => !props.comingSoon && props.onChange(!props.checked)}
      >
        {props.checked ? 'ON' : 'OFF'}
      </button>
    </label>
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
    <div className={`ft8-set-row${props.comingSoon ? ' is-disabled' : ''}`}>
      <span className="ft8-set-row__text">
        <span className="ft8-set-row__label">
          {props.label}
          {props.comingSoon && <span className="ft8-set-soon">soon</span>}
        </span>
        {props.hint && <span className="ft8-set-row__hint">{props.hint}</span>}
      </span>
      <div className="ft8-set-seg" role="group" aria-label={props.label}>
        {props.options.map((o) => (
          <button
            key={o.value}
            type="button"
            aria-pressed={props.value === o.value}
            aria-disabled={props.comingSoon}
            disabled={props.comingSoon}
            className={`ft8-set-seg__btn${props.value === o.value ? ' is-on' : ''}`}
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
    <div className={`ft8-set-row${props.disabled ? ' is-disabled' : ''}`}>
      <span className="ft8-set-row__text">
        <span className="ft8-set-row__label">{props.label}</span>
        {props.hint && <span className="ft8-set-row__hint">{props.hint}</span>}
      </span>
      <select
        className="ft8-set-input"
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
    <div className={`ft8-set-row${props.comingSoon ? ' is-disabled' : ''}`}>
      <span className="ft8-set-row__text">
        <span className="ft8-set-row__label">
          {props.label}
          {props.comingSoon && <span className="ft8-set-soon">soon</span>}
        </span>
        {props.hint && <span className="ft8-set-row__hint">{props.hint}</span>}
        {props.resolved && props.resolved.fromQrz && props.resolved.value && (
          <span className="ft8-set-row__resolved">
            Using QRZ home: <strong>{props.resolved.value}</strong>
          </span>
        )}
      </span>
      <input
        className="ft8-set-input"
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
    <div className={`ft8-set-row${props.comingSoon ? ' is-disabled' : ''}`}>
      <span className="ft8-set-row__text">
        <span className="ft8-set-row__label">
          {props.label}
          {props.comingSoon && <span className="ft8-set-soon">soon</span>}
        </span>
        {props.hint && <span className="ft8-set-row__hint">{props.hint}</span>}
      </span>
      <span className="ft8-set-num">
        <input
          type="number"
          className="ft8-set-input ft8-set-input--num"
          value={props.value}
          min={props.min}
          max={props.max}
          step={props.step ?? 1}
          disabled={disabled}
          onChange={(e) => props.onChange(Number(e.target.value))}
        />
        {props.suffix && <span className="ft8-set-num__suffix">{props.suffix}</span>}
      </span>
    </div>
  );
}

export function Chip(props: { label: string; on: boolean }) {
  return (
    <span className={`ft8-set-chip${props.on ? ' is-on' : ''}`}>
      <span className="ft8-set-chip__dot" />
      {props.label} {props.on ? 'ON' : 'OFF'}
    </span>
  );
}
