// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Ft8SettingsView — the FT8/FT4 workspace SETTINGS page (the curated WSJT-X +
// JTDX KEEP set, with everything a native SDR already owns SCRAPPED: rig/CAT/PTT
// selection, sound-card in/out, split, the frequencies table, power/attenuator
// dials — Zeus owns all of those). Five grouped sections in one scrollable
// column, fixed (non-closable) per the locked-workspace design.
//
// THE UNBLOCK lives in § Station / Operator: My Call + My Grid are persisted
// SERVER-side (shared operator identity) so FT8 TX ungates and the desktop no
// longer loses the call on restart. Reporting/logging that another subsystem
// already owns (PSK Reporter / WSPRnet / WSJT-X UDP) is SURFACED and deep-linked,
// never duplicated.
//
// Styling is HUD-token-only (ft8-theme.css --hud-*) — no raw hex.

import { useEffect } from 'react';
import { useOperatorStore } from '../../state/operator-store';
import { useFt8SettingsStore } from '../../state/ft8-settings-store';
import { useFt8Store } from '../../state/ft8-store';
import { useSpottingStore } from '../../state/spotting-store';
import { useWsjtxStore } from '../../state/wsjtx-store';
import { useLayoutStore } from '../../state/layout-store';
import { FT8_MAX_TX_OFFSET_HZ, FT8_MIN_OFFSET_HZ } from '../../dsp/ft8-passband';

// ---- small token-styled controls ------------------------------------------

function ToggleRow(props: {
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

function SegRow<T extends string>(props: {
  label: string;
  hint?: string;
  value: T;
  options: ReadonlyArray<{ value: T; label: string }>;
  onChange: (v: T) => void;
}) {
  return (
    <div className="ft8-set-row">
      <span className="ft8-set-row__text">
        <span className="ft8-set-row__label">{props.label}</span>
        {props.hint && <span className="ft8-set-row__hint">{props.hint}</span>}
      </span>
      <div className="ft8-set-seg" role="group" aria-label={props.label}>
        {props.options.map((o) => (
          <button
            key={o.value}
            type="button"
            aria-pressed={props.value === o.value}
            className={`ft8-set-seg__btn${props.value === o.value ? ' is-on' : ''}`}
            onClick={() => props.onChange(o.value)}
          >
            {o.label}
          </button>
        ))}
      </div>
    </div>
  );
}

function TextRow(props: {
  label: string;
  hint?: string;
  value: string;
  placeholder?: string;
  maxLength?: number;
  upper?: boolean;
  resolved?: { value: string; fromQrz: boolean };
  onChange: (v: string) => void;
}) {
  return (
    <div className="ft8-set-row">
      <span className="ft8-set-row__text">
        <span className="ft8-set-row__label">{props.label}</span>
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
        style={props.upper ? { textTransform: 'uppercase' } : undefined}
        onChange={(e) => props.onChange(e.target.value)}
      />
    </div>
  );
}

function NumberRow(props: {
  label: string;
  hint?: string;
  value: number;
  min?: number;
  max?: number;
  step?: number;
  suffix?: string;
  disabled?: boolean;
  onChange: (v: number) => void;
}) {
  return (
    <div className="ft8-set-row">
      <span className="ft8-set-row__text">
        <span className="ft8-set-row__label">{props.label}</span>
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
          disabled={props.disabled}
          onChange={(e) => props.onChange(Number(e.target.value))}
        />
        {props.suffix && <span className="ft8-set-num__suffix">{props.suffix}</span>}
      </span>
    </div>
  );
}

function Chip(props: { label: string; on: boolean }) {
  return (
    <span className={`ft8-set-chip${props.on ? ' is-on' : ''}`}>
      <span className="ft8-set-chip__dot" />
      {props.label} {props.on ? 'ON' : 'OFF'}
    </span>
  );
}

// ---- the view -------------------------------------------------------------

export function Ft8SettingsView() {
  // Operator identity (server-authoritative, shared with spotting/FreeDV/TX).
  const opCall = useOperatorStore((s) => s.call);
  const opGrid = useOperatorStore((s) => s.grid);
  const resolvedCall = useOperatorStore((s) => s.resolvedCall);
  const resolvedGrid = useOperatorStore((s) => s.resolvedGrid);
  const callFromQrz = useOperatorStore((s) => s.callFromQrz);
  const gridFromQrz = useOperatorStore((s) => s.gridFromQrz);
  const setCall = useOperatorStore((s) => s.setCall);
  const setGrid = useOperatorStore((s) => s.setGrid);

  // Persisted FT8 behaviour preferences.
  const settings = useFt8SettingsStore((s) => s.settings);
  const update = useFt8SettingsStore((s) => s.update);

  // Live decode depth (applies immediately + persists).
  const setPasses = useFt8Store((s) => s.setPasses);
  const setDecodeDepth = (passes: number) => {
    setPasses(passes);
    void update({ decodePasses: passes });
  };

  // Reporting status (read-only chips — owned by the Network tab, surfaced here).
  const spotStatus = useSpottingStore((s) => s.status);
  const refreshSpotting = useSpottingStore((s) => s.refreshStatus);
  const wsjtxStatus = useWsjtxStore((s) => s.status);
  const refreshWsjtx = useWsjtxStore((s) => s.refreshStatus);
  useEffect(() => {
    void refreshSpotting();
    void refreshWsjtx();
  }, [refreshSpotting, refreshWsjtx]);

  // Deep-link to the Network settings tab. The FT8 workspace is a full-screen
  // overlay, so to reveal the Network tab behind it we leave the workspace.
  const setSettingsView = useLayoutStore((s) => s.setSettingsView);
  const closeWorkspace = useFt8Store((s) => s.closeWorkspace);
  const openNetworkSettings = () => {
    closeWorkspace();
    setSettingsView(true, 'network');
  };

  // Decode depth maps to the engine's pass scale: 1 = Normal (floor), >1 = Deep /
  // multi-pass (there is no lighter-than-normal mode, so no bogus "Fast"). The
  // default (3) lands on Deep, matching the pre-settings engine behaviour.
  const depth =
    settings.decodePasses >= 4 ? 'deepest' : settings.decodePasses <= 1 ? 'normal' : 'deep';
  const depthToPasses = (v: 'normal' | 'deep' | 'deepest') =>
    v === 'normal' ? 1 : v === 'deepest' ? 4 : 3;

  return (
    <div className="ft8-settings" aria-label="FT8 settings">
      {/* § Station / Operator — THE unblock. */}
      <section className="ft8-region ft8-set-section">
        <div className="ft8-region__head">Station / Operator</div>
        <div className="ft8-set-body">
          <TextRow
            label="My Call"
            hint="Your station callsign — required to transmit. Shared with spotting & FreeDV."
            value={opCall}
            placeholder={resolvedCall && callFromQrz ? resolvedCall : 'MYCALL'}
            upper
            resolved={{ value: resolvedCall, fromQrz: callFromQrz }}
            onChange={setCall}
          />
          <TextRow
            label="My Grid"
            hint="Maidenhead locator (4 or 6 chars). Falls back to your QRZ home grid."
            value={opGrid}
            placeholder={resolvedGrid && gridFromQrz ? resolvedGrid : 'FN42'}
            maxLength={6}
            upper
            resolved={{ value: resolvedGrid, fromQrz: gridFromQrz }}
            onChange={setGrid}
          />
          <p className="ft8-set-note">
            Identity is stored on the server and survives restarts. Leave a field blank to use your
            QRZ home station.
          </p>
        </div>
      </section>

      {/* § TX & Auto-sequence. */}
      <section className="ft8-region ft8-set-section">
        <div className="ft8-region__head">TX &amp; Auto-sequence</div>
        <div className="ft8-set-body">
          <ToggleRow
            label="Auto-sequence"
            hint="Run the QSO state machine. TX still requires ENABLE/arm — this never keys on its own."
            checked={settings.autoSequence}
            onChange={(v) => void update({ autoSequence: v })}
          />
          <ToggleRow
            label="Call 1st"
            hint="Auto-answer the first decoded CQ while armed."
            checked={settings.callFirst}
            onChange={(v) => void update({ callFirst: v })}
          />
          <ToggleRow
            label="Hold TX freq"
            hint="Lock the TX audio offset against waterfall clicks."
            checked={settings.holdTxFreq}
            onChange={(v) => void update({ holdTxFreq: v })}
          />
          <ToggleRow
            label="Disable TX after 73"
            hint="Auto-disarm once a QSO completes (73 sent)."
            checked={settings.disableTxAfter73}
            onChange={(v) => void update({ disableTxAfter73: v })}
          />
          <SegRow
            label="Default TX slot"
            hint="Which 15 s slot a fresh QSO transmits in."
            value={settings.defaultTxSlot === 0 ? 'even' : 'odd'}
            options={[
              { value: 'even', label: '1ST (even)' },
              { value: 'odd', label: '2ND (odd)' },
            ]}
            onChange={(v) => void update({ defaultTxSlot: v === 'even' ? 0 : 1 })}
          />
          <NumberRow
            label="Default TX audio offset"
            hint="Where a fresh TX tone defaults inside the SSB passband."
            value={settings.defaultTxOffsetHz}
            min={FT8_MIN_OFFSET_HZ}
            max={FT8_MAX_TX_OFFSET_HZ}
            suffix="Hz"
            onChange={(v) => void update({ defaultTxOffsetHz: v })}
          />
          <div className="ft8-set-row ft8-set-row--readonly">
            <span className="ft8-set-row__text">
              <span className="ft8-set-row__label">TX watchdog</span>
              <span className="ft8-set-row__hint">
                Backend hard cap on unattended TX. Fixed for safety.
              </span>
            </span>
            <span className="ft8-set-readonly-value">10 min</span>
          </div>

          <details className="ft8-set-advanced">
            <summary>Advanced sequence (JTDX) — default off</summary>
            <ToggleRow
              label="Send RR73 instead of RRR"
              checked={settings.rr73InsteadOfRrr}
              onChange={(v) => void update({ rr73InsteadOfRrr: v })}
            />
            <ToggleRow
              label="Skip grid (send report first)"
              hint="JTDX-style opening with the report instead of the grid."
              checked={settings.skipGrid}
              comingSoon
              onChange={(v) => void update({ skipGrid: v })}
            />
            <NumberRow
              label="Caller max retries"
              hint="0 = unlimited."
              value={settings.callerMaxRetries}
              min={0}
              max={30}
              onChange={(v) => void update({ callerMaxRetries: v })}
            />
          </details>
        </div>
      </section>

      {/* § Macros. */}
      <section className="ft8-region ft8-set-section">
        <div className="ft8-region__head">Macros</div>
        <div className="ft8-set-body">
          <TextRow
            label="CQ message"
            hint="The CQ button text (e.g. CQ or CQ TEST)."
            value={settings.cqMessage}
            maxLength={32}
            onChange={(v) => void update({ cqMessage: v })}
          />
          <TextRow
            label="CQ DX message"
            value={settings.cqDxMessage}
            maxLength={32}
            onChange={(v) => void update({ cqDxMessage: v })}
          />
          <TextRow
            label="Free-text macro"
            hint="A reusable 13-char free-text message."
            value={settings.freeTextMacro}
            maxLength={13}
            upper
            onChange={(v) => void update({ freeTextMacro: v })}
          />
        </div>
      </section>

      {/* § Decode. */}
      <section className="ft8-region ft8-set-section">
        <div className="ft8-region__head">Decode</div>
        <div className="ft8-set-body">
          <SegRow
            label="Decode depth"
            hint="Deeper digs out weaker signals at higher CPU cost."
            value={depth}
            options={[
              { value: 'normal', label: 'Normal' },
              { value: 'deep', label: 'Deep' },
              { value: 'deepest', label: 'Deepest' },
            ]}
            onChange={(v) => setDecodeDepth(depthToPasses(v))}
          />
          <ToggleRow
            label="Show only CQ"
            hint="Hide non-CQ rows (still shows stations calling you)."
            checked={settings.showOnlyCq}
            onChange={(v) => void update({ showOnlyCq: v })}
          />
          <ToggleRow
            label="Hide worked-before"
            hint="Hide stations already in your logbook."
            checked={settings.hideWorkedBefore}
            onChange={(v) => void update({ hideWorkedBefore: v })}
          />
        </div>
      </section>

      {/* § Reporting & Logging — surface/deep-link, don't rebuild. */}
      <section className="ft8-region ft8-set-section">
        <div className="ft8-region__head">Reporting &amp; Logging</div>
        <div className="ft8-set-body">
          <div className="ft8-set-chips">
            <Chip label="PSK Reporter" on={!!spotStatus?.pskReporterEnabled} />
            <Chip label="WSPRnet" on={!!spotStatus?.wsprnetEnabled} />
            <Chip label="WSJT-X UDP" on={!!wsjtxStatus?.enabled} />
          </div>
          <button type="button" className="ft8-set-link" onClick={openNetworkSettings}>
            Open Network settings →
          </button>
          <p className="ft8-set-note">
            PSK Reporter, WSPRnet and WSJT-X UDP push are configured on the Network tab. The link
            leaves the FT8 workspace.
          </p>

          <div className="ft8-set-divider" />

          <ToggleRow
            label="Auto-log QSO"
            hint="Write completed QSOs to the logbook automatically."
            checked={settings.autoLog}
            onChange={(v) => void update({ autoLog: v })}
          />
          <ToggleRow
            label="Prompt before logging"
            hint="Confirm each QSO before it is logged."
            checked={settings.promptBeforeLog}
            onChange={(v) => void update({ promptBeforeLog: v })}
          />
          <ToggleRow
            label="Clear DX call/grid after log"
            hint="Reset the QSO panel once a contact is logged."
            checked={settings.clearDxAfterLog}
            comingSoon
            onChange={(v) => void update({ clearDxAfterLog: v })}
          />
          <ToggleRow
            label="dB report → comment"
            hint="Record the signal report in the QSO comment."
            checked={settings.reportToComment}
            onChange={(v) => void update({ reportToComment: v })}
          />
        </div>
      </section>
    </div>
  );
}
