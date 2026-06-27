// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// ZeusDigitalSettingsPanel — the "Zeus Digital" section of the MAIN Settings menu
// (alongside Network / HamClock / Spots). It re-homes the former in-workspace
// Ft8SettingsView content into the menu and adds the PER-MODE dimension: a
// FT8 / FT4 / WSPR mode selector at the top picks which mode's config you are
// editing, and every per-mode control reads/writes that mode's server-backed row
// (zeus-prefs.db) so each mode remembers its own behaviour, decode depth and
// waterfall view across desktop restarts.
//
// Operator IDENTITY (My Call / My Grid) stays GLOBAL — it lives in the shared
// operator store (/api/operator) so spotting / FreeDV / TX all read one source.
//
// MESSAGE EDITING (CQ / CQ DX / free-text macros) is NOT here — it stays in the
// digital pop-out (the macros still persist per-mode in the same store). This
// panel only links out to it. Reporting (PSK / WSPRnet / WSJT-X UDP) is SURFACED
// and deep-linked to the Network tab, never duplicated.
//
// Styling is HUD-token-only (ft8-theme.css --hud-*) — no raw hex.

import { useEffect, useState } from 'react';
import { useOperatorStore } from '../state/operator-store';
import { useFt8SettingsStore } from '../state/ft8-settings-store';
import { useFt8Store } from '../state/ft8-store';
import { useSpottingStore } from '../state/spotting-store';
import { useWsjtxStore } from '../state/wsjtx-store';
import { useLayoutStore } from '../state/layout-store';
import {
  DIGITAL_MODES,
  WF_PALETTES,
  WF_SMOOTHING_MAX,
  WF_SMOOTHING_MIN,
  WF_SPAN_MAX_HZ,
  WF_SPAN_MIN_HZ,
  WF_ZOOM_MAX,
  WF_ZOOM_MIN,
  type DigitalMode,
} from '../api/ft8-settings';
import { FT8_MAX_TX_OFFSET_HZ, FT8_MIN_OFFSET_HZ } from '../dsp/ft8-passband';
import {
  Chip,
  NumberRow,
  SegRow,
  TextRow,
  ToggleRow,
} from '../layout/ft8/ft8-settings-controls';

export function ZeusDigitalSettingsPanel({
  initialMode = 'FT8',
}: {
  initialMode?: DigitalMode;
}) {
  // Which per-mode config is being edited. The active digital protocol seeds the
  // initial selection so the menu opens on the mode the operator is running; the
  // operator can then switch freely.
  const ft8Protocol = useFt8Store((s) => s.protocol);
  const [mode, setMode] = useState<DigitalMode>(
    initialMode !== 'FT8' ? initialMode : ft8Protocol === 'FT4' ? 'FT4' : 'FT8',
  );

  // Operator identity (server-authoritative, shared/global with spotting/TX).
  const opCall = useOperatorStore((s) => s.call);
  const opGrid = useOperatorStore((s) => s.grid);
  const resolvedCall = useOperatorStore((s) => s.resolvedCall);
  const resolvedGrid = useOperatorStore((s) => s.resolvedGrid);
  const callFromQrz = useOperatorStore((s) => s.callFromQrz);
  const gridFromQrz = useOperatorStore((s) => s.gridFromQrz);
  const setCall = useOperatorStore((s) => s.setCall);
  const setGrid = useOperatorStore((s) => s.setGrid);
  const hydrateOperator = useOperatorStore((s) => s.hydrate);

  // Per-mode persisted preferences for the selected mode.
  const settings = useFt8SettingsStore((s) => s.byMode[mode]);
  const update = useFt8SettingsStore((s) => s.update);
  const hydrateSettings = useFt8SettingsStore((s) => s.hydrate);

  // Decode depth applies to the shared FT8/FT4 engine when the edited mode is the
  // one currently on the air. Always persists per-mode; only seeds the live
  // decoder when relevant (handled inside the store on the next hydrate too).
  const setPasses = useFt8Store((s) => s.setPasses);
  const setDecodeDepth = (passes: number) => {
    if ((mode === 'FT8' || mode === 'FT4') && mode === ft8Protocol) setPasses(passes);
    void update(mode, { decodePasses: passes });
  };

  // Reporting status (read-only chips — owned by the Network tab, surfaced here).
  const spotStatus = useSpottingStore((s) => s.status);
  const refreshSpotting = useSpottingStore((s) => s.refreshStatus);
  const wsjtxStatus = useWsjtxStore((s) => s.status);
  const refreshWsjtx = useWsjtxStore((s) => s.refreshStatus);

  // Freshen identity + reporting + the selected mode's settings on open / mode
  // switch so the panel always reflects the server (QRZ home may resolve late).
  useEffect(() => {
    void hydrateOperator();
    void refreshSpotting();
    void refreshWsjtx();
  }, [hydrateOperator, refreshSpotting, refreshWsjtx]);
  useEffect(() => {
    void hydrateSettings(mode);
  }, [mode, hydrateSettings]);

  // Deep-link to the Network settings tab (PSK / WSPRnet / WSJT-X UDP live there).
  const setSettingsView = useLayoutStore((s) => s.setSettingsView);
  const openNetworkSettings = () => setSettingsView(true, 'network');

  const isWspr = mode === 'WSPR';

  // Decode depth maps to the engine's pass scale: 1 = Normal (floor), >1 = Deep /
  // multi-pass. Default (3) lands on Deep, matching the pre-settings behaviour.
  const depth =
    settings.decodePasses >= 4 ? 'deepest' : settings.decodePasses <= 1 ? 'normal' : 'deep';
  const depthToPasses = (v: 'normal' | 'deep' | 'deepest') =>
    v === 'normal' ? 1 : v === 'deepest' ? 4 : 3;

  return (
    <div className="ft8-settings" aria-label="Zeus Digital settings">
      {/* § Mode selector — which per-mode config is being edited. */}
      <section className="ft8-region ft8-set-section">
        <div className="ft8-region__head">Mode</div>
        <div className="ft8-set-body">
          <SegRow
            label="Editing settings for"
            hint="FT8, FT4 and WSPR each remember their own configuration."
            value={mode}
            options={DIGITAL_MODES.map((m) => ({ value: m, label: m }))}
            onChange={(m) => setMode(m)}
          />
        </div>
      </section>

      {/* § Station / Operator — GLOBAL (shared across all modes). */}
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
            Identity is stored on the server, shared across FT8/FT4/WSPR, and survives restarts.
            Leave a field blank to use your QRZ home station.
          </p>
        </div>
      </section>

      {/* § TX & Auto-sequence — PER-MODE. Not shown for WSPR (beacon, no QSO). */}
      {!isWspr && (
        <section className="ft8-region ft8-set-section">
          <div className="ft8-region__head">TX &amp; Auto-sequence · {mode}</div>
          <div className="ft8-set-body">
            <ToggleRow
              label="Auto-sequence"
              hint="Run the QSO state machine. TX still requires ENABLE/arm — this never keys on its own."
              checked={settings.autoSequence}
              onChange={(v) => void update(mode, { autoSequence: v })}
            />
            <ToggleRow
              label="Call 1st"
              hint="Auto-answer the first decoded CQ while armed."
              checked={settings.callFirst}
              onChange={(v) => void update(mode, { callFirst: v })}
            />
            <ToggleRow
              label="Hold TX freq"
              hint="Lock the TX audio offset against waterfall clicks."
              checked={settings.holdTxFreq}
              onChange={(v) => void update(mode, { holdTxFreq: v })}
            />
            <ToggleRow
              label="Disable TX after 73"
              hint="Auto-disarm once a QSO completes (73 sent)."
              checked={settings.disableTxAfter73}
              onChange={(v) => void update(mode, { disableTxAfter73: v })}
            />
            <SegRow
              label="Default TX slot"
              hint="Which 15 s slot a fresh QSO transmits in."
              value={settings.defaultTxSlot === 0 ? 'even' : 'odd'}
              options={[
                { value: 'even', label: '1ST (even)' },
                { value: 'odd', label: '2ND (odd)' },
              ]}
              onChange={(v) => void update(mode, { defaultTxSlot: v === 'even' ? 0 : 1 })}
            />
            <NumberRow
              label="Default TX audio offset"
              hint="Where a fresh TX tone defaults inside the SSB passband."
              value={settings.defaultTxOffsetHz}
              min={FT8_MIN_OFFSET_HZ}
              max={FT8_MAX_TX_OFFSET_HZ}
              suffix="Hz"
              onChange={(v) => void update(mode, { defaultTxOffsetHz: v })}
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
                onChange={(v) => void update(mode, { rr73InsteadOfRrr: v })}
              />
              <ToggleRow
                label="Skip grid (send report first)"
                hint="JTDX-style opening with the report instead of the grid."
                checked={settings.skipGrid}
                comingSoon
                onChange={(v) => void update(mode, { skipGrid: v })}
              />
              <NumberRow
                label="Caller max retries"
                hint="0 = unlimited."
                value={settings.callerMaxRetries}
                min={0}
                max={30}
                onChange={(v) => void update(mode, { callerMaxRetries: v })}
              />
            </details>
          </div>
        </section>
      )}

      {/* § Decode — PER-MODE. Not shown for WSPR (separate decoder). */}
      {!isWspr && (
        <section className="ft8-region ft8-set-section">
          <div className="ft8-region__head">Decode · {mode}</div>
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
              onChange={(v) => void update(mode, { showOnlyCq: v })}
            />
            <ToggleRow
              label="Hide worked-before"
              hint="Hide stations already in your logbook."
              checked={settings.hideWorkedBefore}
              onChange={(v) => void update(mode, { hideWorkedBefore: v })}
            />
          </div>
        </section>
      )}

      {/* § Waterfall / display — PER-MODE. Persisted per mode (server-backed) and
          forward-looking, but the digital-workspace waterfall that will consume
          these (#1014) is still a placeholder, so every control is surfaced
          "coming soon" (disabled) rather than as a live no-op. When the digital
          waterfall lands it reads byMode[mode] for these fields and the
          comingSoon flags come off. */}
      <section className="ft8-region ft8-set-section">
        <div className="ft8-region__head">Waterfall / display · {mode}</div>
        <div className="ft8-set-body">
          <p className="ft8-set-note">
            The digital waterfall is still in progress (#1014). These views are saved per mode and
            will take effect once it ships.
          </p>
          <NumberRow
            label="Waterfall dB min"
            hint="Bottom of the colour map (noise floor)."
            value={settings.wfDbMin}
            min={-200}
            max={0}
            suffix="dB"
            comingSoon
            onChange={(v) => void update(mode, { wfDbMin: v })}
          />
          <NumberRow
            label="Waterfall dB max"
            hint="Top of the colour map (strong signals)."
            value={settings.wfDbMax}
            min={-200}
            max={200}
            suffix="dB"
            comingSoon
            onChange={(v) => void update(mode, { wfDbMax: v })}
          />
          <SegRow
            label="Palette"
            value={settings.palette}
            options={WF_PALETTES.map((p) => ({
              value: p,
              label: p.charAt(0).toUpperCase() + p.slice(1),
            }))}
            comingSoon
            onChange={(v) => void update(mode, { palette: v })}
          />
          <TextRow
            label="RBW"
            hint='Resolution bandwidth ("auto" or an Hz value).'
            value={settings.rbw}
            maxLength={16}
            comingSoon
            onChange={(v) => void update(mode, { rbw: v })}
          />
          <NumberRow
            label="Smoothing"
            hint="Waterfall averaging frames (0 = none)."
            value={settings.smoothing}
            min={WF_SMOOTHING_MIN}
            max={WF_SMOOTHING_MAX}
            comingSoon
            onChange={(v) => void update(mode, { smoothing: v })}
          />
          <NumberRow
            label="Zoom"
            hint="Display zoom factor (1 = full span)."
            value={settings.zoom}
            min={WF_ZOOM_MIN}
            max={WF_ZOOM_MAX}
            step={0.5}
            suffix="×"
            comingSoon
            onChange={(v) => void update(mode, { zoom: v })}
          />
          <NumberRow
            label="Span"
            hint="Display span."
            value={settings.spanHz}
            min={WF_SPAN_MIN_HZ}
            max={WF_SPAN_MAX_HZ}
            step={100}
            suffix="Hz"
            comingSoon
            onChange={(v) => void update(mode, { spanHz: v })}
          />
        </div>
      </section>

      {/* § Reporting & Logging — surface/deep-link reporting, own logging. */}
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
            PSK Reporter, WSPRnet and WSJT-X UDP push are configured on the Network tab.
          </p>

          {/* QSO logging is a non-WSPR concept — WSPR is a beacon mode with no
              QSO/logbook path, so these toggles are hidden for WSPR (the WSPR
              pop-out consumes none of them). */}
          {!isWspr && (
            <>
              <div className="ft8-set-divider" />

              <ToggleRow
                label="Auto-log QSO"
                hint="Write completed QSOs to the logbook automatically."
                checked={settings.autoLog}
                onChange={(v) => void update(mode, { autoLog: v })}
              />
              <ToggleRow
                label="Prompt before logging"
                hint="Confirm each QSO before it is logged."
                checked={settings.promptBeforeLog}
                onChange={(v) => void update(mode, { promptBeforeLog: v })}
              />
              <ToggleRow
                label="Clear DX call/grid after log"
                hint="Reset the QSO panel once a contact is logged."
                checked={settings.clearDxAfterLog}
                comingSoon
                onChange={(v) => void update(mode, { clearDxAfterLog: v })}
              />
              <ToggleRow
                label="dB report → comment"
                hint="Record the signal report in the QSO comment."
                checked={settings.reportToComment}
                onChange={(v) => void update(mode, { reportToComment: v })}
              />
              <p className="ft8-set-note">
                Edit the CQ / CQ DX / free-text macros in the digital pop-out — they persist per
                mode.
              </p>
            </>
          )}
        </div>
      </section>
    </div>
  );
}
