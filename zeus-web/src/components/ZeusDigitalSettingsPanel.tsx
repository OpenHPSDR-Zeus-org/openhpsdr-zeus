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
// panel only links out to it. Spotting (PSK Reporter / WSPRnet) is SURFACED and
// deep-linked to the Network tab, never duplicated. LOGGING, by contrast, is
// owned here: the Zeus internal logbook is the always-on default and an ADDITIVE
// external logger (WSJT-X UDP — the universal contract Log4OM / N1MM+ /
// GridTracker / JTAlert all speak) plus QRZ cloud upload only ADD copies. The
// external-logger form reuses the same wsjtx-store/api the Network tab uses (not
// a fork); egress is OFF until the operator opts in and SAVES.
//
// Styling: this tab renders in the SAME surface family as every other Settings
// tab — `.ps-shell` ▸ `.ps-card` ▸ `.ps-field`, shared with PsSettings / DSP /
// Radio — using the house-skinned rows in zeus-digital-settings-controls. The
// dark-HUD pop-out keeps its own controls; this menu reads as Settings, not as
// the operating view. Global tokens only (tokens.css), no raw hex.

import { useEffect, useState } from 'react';
import { useOperatorStore } from '../state/operator-store';
import { useFt8SettingsStore } from '../state/ft8-settings-store';
import { useFt8Store } from '../state/ft8-store';
import { useDigitalPluginStore } from '../state/digital-plugin-store';
import { useSpottingStore } from '../state/spotting-store';
import { useWsjtxStore } from '../state/wsjtx-store';
import { useQrzStore } from '../state/qrz-store';
import { useLayoutStore } from '../state/layout-store';
import { useN1mmStore } from '../state/n1mm-store';
import { useCloudLogStore } from '../state/cloud-log-store';
import type { WsjtxConfig } from '../api/wsjtx';
import type { N1mmConfig } from '../api/n1mm';
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
  SelectRow,
  TextRow,
  ToggleRow,
} from './zeus-digital-settings-controls';

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
  const refreshWsjtx = useWsjtxStore((s) => s.refreshStatus);
  // QRZ logbook (cloud) availability — surfaced as a logging option. Publishing
  // QSOs to QRZ needs a logbook API key; the QRZ tab owns the credential.
  const qrzHasApiKey = useQrzStore((s) => s.hasApiKey);

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
  const openQrzSettings = () => setSettingsView(true, 'qrz');
  const openPluginSettings = () => setSettingsView(true, 'plugins');

  // Zeus Digital plugin gate. Identity, auto-sequence, macros, decode depth and
  // the logging groups are CORE-stored and stay editable without the plugin;
  // decode/TX themselves and the plugin-hosted surfaces (spotting, WSJT-X live
  // decodes) need it installed AND activated (one restart after install).
  const pluginInstalled = useDigitalPluginStore((s) => s.installed);
  const pluginReady = useDigitalPluginStore((s) => s.installed && s.live);

  const isWspr = mode === 'WSPR';

  // Decode depth maps to the engine's pass scale: 1 = Normal (floor), >1 = Deep /
  // multi-pass. Default (3) lands on Deep, matching the pre-settings behaviour.
  const depth =
    settings.decodePasses >= 4 ? 'deepest' : settings.decodePasses <= 1 ? 'normal' : 'deep';
  const depthToPasses = (v: 'normal' | 'deep' | 'deepest') =>
    v === 'normal' ? 1 : v === 'deepest' ? 4 : 3;

  return (
    <div className="ps-shell zd-settings" aria-label="Zeus Digital settings">
      {/* § Plugin banner — FT8/FT4 decode/TX live in the Zeus Digital plugin.
          Settings below stay editable (they persist core-side); the modes
          themselves stay greyed in the mode pickers until the gate opens. */}
      {!pluginReady && (
        <div className="ps-card" role="alert">
          <h4>Zeus Digital plugin</h4>
          <p className="zd-note">
            {pluginInstalled
              ? 'The Zeus Digital plugin is installed but not active yet — restart Zeus to activate it. FT8/FT4 stay greyed out until then.'
              : 'FT8/FT4 decode, TX and spotting are provided by the Zeus Digital plugin, which is not installed. Settings edited here are kept and apply once it is running.'}
          </p>
          {!pluginInstalled && (
            <button type="button" className="btn sm" onClick={openPluginSettings}>
              Open Plugins →
            </button>
          )}
        </div>
      )}

      {/* § Mode selector — which per-mode config is being edited. */}
      <div className="ps-card">
        <h4>Mode</h4>
        <SegRow
          label="Editing settings for"
          hint="FT8, FT4 and WSPR each remember their own configuration."
          value={mode}
          options={DIGITAL_MODES.map((m) => ({ value: m, label: m }))}
          onChange={(m) => setMode(m)}
        />
      </div>

      {/* § Station / Operator — GLOBAL (shared across all modes). */}
      <div className="ps-card">
        <h4>Station / Operator</h4>
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
        <p className="zd-note">
          Identity is stored on the server, shared across FT8/FT4/WSPR, and survives restarts.
          Leave a field blank to use your QRZ home station.
        </p>
      </div>

      {/* § TX & Auto-sequence — PER-MODE. Not shown for WSPR (beacon, no QSO). */}
      {!isWspr && (
        <div className="ps-card">
          <h4>
            TX &amp; Auto-sequence
            <span className="ps-card-hint">{mode}</span>
          </h4>
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
          <div className="ps-field">
            <div className="ps-name">
              TX watchdog
              <em>Backend hard cap on unattended TX. Fixed for safety.</em>
            </div>
            <span className="zd-readonly-val">10 min</span>
          </div>

          <details className="zd-advanced">
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
      )}

      {/* § Decode — PER-MODE. Not shown for WSPR (separate decoder). */}
      {!isWspr && (
        <div className="ps-card">
          <h4>
            Decode
            <span className="ps-card-hint">{mode}</span>
          </h4>
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
      )}

      {/* § Waterfall / display — PER-MODE. Persisted per mode (server-backed) and
          forward-looking, but the digital-workspace waterfall that will consume
          these (#1014) is still a placeholder, so every control is surfaced
          "coming soon" (disabled) rather than as a live no-op. When the digital
          waterfall lands it reads byMode[mode] for these fields and the
          comingSoon flags come off. */}
      <div className="ps-card">
        <h4>
          Waterfall / display
          <span className="ps-card-hint">{mode}</span>
        </h4>
        <p className="zd-note">
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

      {/* § Reporting — spotting networks (owned by the Network tab, surfaced). */}
      <div className="ps-card">
        <h4>Reporting</h4>
        <div className="zd-chips">
          <Chip label="PSK Reporter" on={!!spotStatus?.pskReporterEnabled} />
          <Chip label="WSPRnet" on={!!spotStatus?.wsprnetEnabled} />
        </div>
        <button type="button" className="btn sm" onClick={openNetworkSettings}>
          Open Network settings →
        </button>
        <p className="zd-note">
          {pluginReady
            ? 'PSK Reporter and WSPRnet automatic spotting are configured on the Network tab.'
            : 'Spotting is hosted by the Zeus Digital plugin — the Network-tab form activates once the plugin is installed and Zeus has restarted.'}
        </p>
      </div>

      {/* § Logging — Zeus internal logbook (ALWAYS on) + ADDITIVE external copies.
          The internal logbook is the default and is never bypassed; the external
          logger (WSJT-X UDP) and QRZ cloud upload only ADD copies. */}
      <div className="ps-card">
        <h4>Logging</h4>
        <div className="zd-chips">
          <Chip label="Zeus internal logbook" on />
        </div>
        <p className="zd-note">
          Every QSO is always saved to the Zeus internal logbook. The options below ADD
          external copies — they never replace it.
        </p>

        {/* Internal QSO-logging behaviour — non-WSPR (WSPR is a beacon mode
            with no QSO/logbook path, so these toggles are hidden for it). */}
        {!isWspr && (
          <>
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
            <p className="zd-note">
              Edit the CQ / CQ DX / free-text macros in the digital pop-out — they persist per
              mode.
            </p>
          </>
        )}

        <div className="zd-divider" />

        {/* Additive external logger over the WSJT-X UDP protocol. */}
        <ExternalLoggingGroup />

        <div className="zd-divider" />

        {/* Additive N1MM-format UDP logger (HRD Logbook / DXKeeper gateway). */}
        <N1mmLoggingGroup />

        <div className="zd-divider" />

        {/* Additive per-QSO HTTP cloud loggers (Wavelog/Cloudlog + Club Log). */}
        <CloudLoggingGroup />

        <div className="zd-divider" />

        {/* QRZ cloud logbook — credential lives on the QRZ tab. */}
        <div className="zd-chips">
          <Chip label="QRZ Logbook (cloud)" on={qrzHasApiKey} />
        </div>
        <button type="button" className="btn sm" onClick={openQrzSettings}>
          Open QRZ settings →
        </button>
        <p className="zd-note">
          Push logged QSOs to your QRZ.com logbook from the Logbook panel. Requires a QRZ
          logbook API key, set on the QRZ tab.
        </p>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// External logging over the WSJT-X UDP protocol — the universal contract every
// modern logger speaks (Log4OM, N1MM+, GridTracker, JTAlert, …). This is the
// SAME wsjtx-store/api the Network tab's WsjtxSettingsPanel uses (not a fork):
// the additive fields (transport, multicast group/TTL, instance-id, type-5,
// live decodes) are edited here, basic enable/host/port can be edited in either
// place, and both POST through saveConfig so they stay coherent. Egress is OFF
// by default and nothing leaves the machine until the operator hits SAVE.
//
// SEND-ONLY: Zeus never opens an inbound WSJT-X listener, so external loggers
// cannot key the radio — there is no Reply/HaltTx/FreeText path. (Mirrored from
// the backend safety note.)
// ---------------------------------------------------------------------------

type LoggerPreset = 'log4om' | 'n1mm' | 'gridtracker' | 'jtalert' | 'custom';

const LOGGER_PRESETS: ReadonlyArray<{ value: LoggerPreset; label: string }> = [
  { value: 'log4om', label: 'Log4OM' },
  { value: 'n1mm', label: 'N1MM+' },
  { value: 'gridtracker', label: 'GridTracker' },
  { value: 'jtalert', label: 'JTAlert' },
  { value: 'custom', label: 'Custom (raw WSJT-X UDP)' },
];

// Per-preset sensible defaults. Host/port match the WSJT-X convention
// (127.0.0.1:2237) for every preset and stay fully editable; the meaningful
// difference is which extra streams a tool wants — GridTracker needs the live
// decode/status stream for its map & roster, Log4OM prefers the structured
// type-5 QSOLogged. All additive, all overridable.
const PRESET_DEFAULTS: Record<
  Exclude<LoggerPreset, 'custom'>,
  Pick<WsjtxConfig, 'host' | 'port' | 'transport' | 'sendQsoLogged' | 'sendLiveDecodes'>
> = {
  log4om: { host: '127.0.0.1', port: 2237, transport: 'unicast', sendQsoLogged: true, sendLiveDecodes: false },
  n1mm: { host: '127.0.0.1', port: 2237, transport: 'unicast', sendQsoLogged: false, sendLiveDecodes: false },
  gridtracker: { host: '127.0.0.1', port: 2237, transport: 'unicast', sendQsoLogged: false, sendLiveDecodes: true },
  jtalert: { host: '127.0.0.1', port: 2237, transport: 'unicast', sendQsoLogged: false, sendLiveDecodes: false },
};

export function ExternalLoggingGroup() {
  const config = useWsjtxStore((s) => s.config);
  const status = useWsjtxStore((s) => s.status);
  const saveConfig = useWsjtxStore((s) => s.saveConfig);

  // Live decodes/status stream from the Zeus Digital plugin's emitter — the
  // type-12 logged-ADIF push stays core (log-driven, any mode), so only THIS
  // toggle greys out when the plugin is absent.
  const pluginReady = useDigitalPluginStore((s) => s.installed && s.live);

  // Local form state — nothing is committed (no egress) until SAVE. Seeded from
  // the server-backed config and re-seeded whenever it changes.
  const [form, setForm] = useState<WsjtxConfig>(config);
  const [preset, setPreset] = useState<LoggerPreset>('custom');
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    setForm(config);
  }, [config]);

  const patch = (p: Partial<WsjtxConfig>) => setForm((f) => ({ ...f, ...p }));
  // Any manual field edit drops the preset back to Custom — the values no longer
  // match a named tool's profile.
  const editField = (p: Partial<WsjtxConfig>) => {
    patch(p);
    setPreset('custom');
  };
  const applyPreset = (p: LoggerPreset) => {
    setPreset(p);
    if (p !== 'custom') patch(PRESET_DEFAULTS[p]);
  };

  async function onSave() {
    setSaving(true);
    try {
      const port =
        Number.isFinite(form.port) && form.port > 0 && form.port < 65536 ? form.port : 2237;
      await saveConfig({
        ...form,
        port,
        host: form.host.trim() || '127.0.0.1',
        instanceId: form.instanceId.trim() || 'WSJT-X',
        multicastGroup: form.multicastGroup.trim() || '224.0.0.73',
      });
    } finally {
      setSaving(false);
    }
  }

  const live = status?.enabled ?? false;
  const isMulticast = form.transport === 'multicast';

  return (
    <div aria-label="External logging (WSJT-X UDP)">
      <ToggleRow
        label="Also send to an external logger"
        hint="Mirror each logged QSO out over the WSJT-X UDP protocol. Additive — Zeus keeps the internal copy."
        checked={form.enabled}
        onChange={(v) => patch({ enabled: v })}
      />

      {form.enabled && (
        <>
          <SelectRow<LoggerPreset>
            label="Logger preset"
            hint="Sets sensible host/port and stream defaults — all fields stay editable."
            value={preset}
            options={LOGGER_PRESETS}
            onChange={applyPreset}
          />
          <TextRow
            label="Host"
            hint="127.0.0.1 for a logger on this machine; otherwise the logger's LAN IP."
            value={form.host}
            placeholder="127.0.0.1"
            onChange={(v) => editField({ host: v })}
          />
          <NumberRow
            label="Port"
            hint="WSJT-X default is 2237 — match your logger's UDP input."
            value={form.port}
            min={1}
            max={65535}
            onChange={(v) => editField({ port: v })}
          />
          <SegRow
            label="Transport"
            hint="Multicast lets several loggers receive the same stream at once."
            value={form.transport}
            options={[
              { value: 'unicast', label: 'Unicast' },
              { value: 'multicast', label: 'Multicast' },
            ]}
            onChange={(v) => editField({ transport: v })}
          />
          {isMulticast && (
            <>
              <TextRow
                label="Multicast group"
                hint="IPv4 multicast address (224.0.0.0–239.255.255.255). WSJT-X uses 224.0.0.73."
                value={form.multicastGroup}
                placeholder="224.0.0.73"
                onChange={(v) => editField({ multicastGroup: v })}
              />
              <NumberRow
                label="Multicast TTL"
                hint="Hop limit. 1 keeps the stream on the local subnet."
                value={form.multicastTtl}
                min={1}
                max={255}
                onChange={(v) => editField({ multicastTtl: v })}
              />
            </>
          )}
          <TextRow
            label="Instance id"
            hint='Identifies this sender to the logger. Leave "WSJT-X" for maximum compatibility.'
            value={form.instanceId}
            placeholder="WSJT-X"
            onChange={(v) => editField({ instanceId: v })}
          />
          <ToggleRow
            label="Send structured QSO (type 5) too"
            hint="Emit QSOLogged alongside the ADIF record. Some loggers (e.g. Log4OM) prefer it."
            checked={form.sendQsoLogged}
            onChange={(v) => editField({ sendQsoLogged: v })}
          />
          <ToggleRow
            label="Send live decodes & status (for GridTracker)"
            hint={
              pluginReady
                ? 'Stream Decode/WSPRDecode/Status/Heartbeat so map & roster tools stay live.'
                : 'Streamed by the Zeus Digital plugin — install/activate it to enable live decodes.'
            }
            checked={form.sendLiveDecodes}
            disabled={!pluginReady}
            onChange={(v) => editField({ sendLiveDecodes: v })}
          />
        </>
      )}

      <div className="ps-field">
        <div className="ps-name">
          <span className={`zd-dot${live ? ' is-on' : ''}`} />
          External logger
          <em>
            {live
              ? status?.transport === 'multicast'
                ? `Sending to multicast ${status?.multicastGroup}:${status?.port}`
                : `Sending to ${status?.host}:${status?.port}`
              : 'Disabled — no QSO data leaves this machine.'}
          </em>
        </div>
        <button type="button" className="btn sm active" disabled={saving} onClick={() => void onSave()}>
          {saving ? 'SAVING…' : 'SAVE'}
        </button>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// N1MM-format UDP logging — the "N1MM Logger+ Broadcasts" contactinfo datagram
// (a DIFFERENT wire format from the WSJT-X type-12 ADIF datagram above). HRD
// Logbook's QSO-Forwarding and DXKeeper-via-Gateway listen for THIS, on its own
// configurable port (default 2333). Server-backed (useN1mmStore), SEND-ONLY,
// egress OFF until the operator opts in and SAVES.
// ---------------------------------------------------------------------------

function N1mmLoggingGroup() {
  const config = useN1mmStore((s) => s.config);
  const saveConfig = useN1mmStore((s) => s.saveConfig);
  const refreshConfig = useN1mmStore((s) => s.refreshConfig);

  const [form, setForm] = useState<N1mmConfig>(config);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    void refreshConfig();
  }, [refreshConfig]);
  useEffect(() => {
    setForm(config);
  }, [config]);

  const patch = (p: Partial<N1mmConfig>) => setForm((f) => ({ ...f, ...p }));

  async function onSave() {
    setSaving(true);
    try {
      const port = Number.isFinite(form.port) && form.port > 0 && form.port < 65536 ? form.port : 2333;
      await saveConfig({ ...form, port, host: form.host.trim() || '127.0.0.1' });
    } finally {
      setSaving(false);
    }
  }

  return (
    <div aria-label="N1MM-format logging (HRD / DXKeeper)">
      <ToggleRow
        label="Also send to an HRD / N1MM-format logger"
        hint="Mirror each logged QSO out as the N1MM contactinfo UDP datagram. HRD Logbook & DXKeeper-via-Gateway speak this. Additive — Zeus keeps the internal copy."
        checked={form.enabled}
        onChange={(v) => patch({ enabled: v })}
      />

      {form.enabled && (
        <>
          <TextRow
            label="Host"
            hint="127.0.0.1 for a logger on this machine; otherwise the logger's LAN IP."
            value={form.host}
            placeholder="127.0.0.1"
            onChange={(v) => patch({ host: v })}
          />
          <NumberRow
            label="Port"
            hint="N1MM-broadcast default is 2333 — match your logger's UDP input."
            value={form.port}
            min={1}
            max={65535}
            onChange={(v) => patch({ port: v })}
          />
        </>
      )}

      <div className="ps-field">
        <div className="ps-name">
          <span className={`zd-dot${config.enabled ? ' is-on' : ''}`} />
          N1MM-format logger
          <em>
            {config.enabled
              ? `Sending to ${config.host}:${config.port}`
              : 'Disabled — no QSO data leaves this machine.'}
          </em>
        </div>
        <button type="button" className="btn sm active" disabled={saving} onClick={() => void onSave()}>
          {saving ? 'SAVING…' : 'SAVE'}
        </button>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// HTTP cloud loggers — per-QSO realtime ADIF push to Wavelog/Cloudlog and Club
// Log. Server-backed (useCloudLogStore), SEND-ONLY, egress OFF by default.
// SECRETS ARE WRITE-ONLY: the API key / application password are typed into a
// password field and POSTed to a separate credentials endpoint; they never come
// back down, so the form only shows a "key saved" indicator from the status.
// ---------------------------------------------------------------------------

// Minimal masked input for write-only secrets (the shared TextRow does not mask).
function SecretRow(props: {
  label: string;
  hint?: string;
  value: string;
  placeholder?: string;
  onChange: (v: string) => void;
}) {
  return (
    <div className="ps-field">
      <div className="ps-name">
        {props.label}
        {props.hint && <em>{props.hint}</em>}
      </div>
      <input
        className="zd-input"
        type="password"
        autoComplete="off"
        value={props.value}
        placeholder={props.placeholder}
        spellCheck={false}
        aria-label={props.label}
        onChange={(e) => props.onChange(e.target.value)}
      />
    </div>
  );
}

function CloudLoggingGroup() {
  const status = useCloudLogStore((s) => s.status);
  const refreshStatus = useCloudLogStore((s) => s.refreshStatus);
  const saveConfig = useCloudLogStore((s) => s.saveConfig);
  const saveCredentials = useCloudLogStore((s) => s.saveCredentials);

  // Non-secret form state, seeded from the server status. Secrets stay in their
  // own local state and are cleared after a save (never echoed back).
  const [wlEnabled, setWlEnabled] = useState(false);
  const [wlBaseUrl, setWlBaseUrl] = useState('');
  const [wlProfile, setWlProfile] = useState('');
  const [wlKey, setWlKey] = useState('');

  const [clEnabled, setClEnabled] = useState(false);
  const [clEmail, setClEmail] = useState('');
  const [clCall, setClCall] = useState('');
  const [clPassword, setClPassword] = useState('');
  const [clApiKey, setClApiKey] = useState('');

  const [saving, setSaving] = useState(false);

  useEffect(() => {
    void refreshStatus();
  }, [refreshStatus]);
  useEffect(() => {
    setWlEnabled(status.wavelog.enabled);
    setWlBaseUrl(status.wavelog.baseUrl);
    setWlProfile(status.wavelog.stationProfileId);
    setClEnabled(status.clubLog.enabled);
    setClEmail(status.clubLog.email);
    setClCall(status.clubLog.callsign);
  }, [status]);

  async function onSave() {
    setSaving(true);
    try {
      await saveConfig({
        wavelog: {
          enabled: wlEnabled,
          baseUrl: wlBaseUrl.trim(),
          stationProfileId: wlProfile.trim(),
        },
        clubLog: {
          enabled: clEnabled,
          email: clEmail.trim(),
          callsign: clCall.trim().toUpperCase(),
        },
      });
      // Only push secrets the operator actually typed this session.
      const creds: { wavelogApiKey?: string; clubLogPassword?: string; clubLogApiKey?: string } = {};
      if (wlKey) creds.wavelogApiKey = wlKey;
      if (clPassword) creds.clubLogPassword = clPassword;
      if (clApiKey) creds.clubLogApiKey = clApiKey;
      if (Object.keys(creds).length > 0) await saveCredentials(creds);
      setWlKey('');
      setClPassword('');
      setClApiKey('');
    } finally {
      setSaving(false);
    }
  }

  return (
    <div aria-label="Cloud logging (Wavelog / Club Log)">
      {/* Wavelog / Cloudlog */}
      <ToggleRow
        label="Also push to Wavelog / Cloudlog"
        hint="Upload each logged QSO to a Wavelog or Cloudlog instance over HTTPS. Additive — Zeus keeps the internal copy."
        checked={wlEnabled}
        onChange={setWlEnabled}
      />
      {wlEnabled && (
        <>
          <TextRow
            label="Base URL"
            hint="Your instance root, e.g. https://log.example.com (Zeus adds /api/qso)."
            value={wlBaseUrl}
            placeholder="https://log.example.com"
            onChange={setWlBaseUrl}
          />
          <TextRow
            label="Station profile id"
            hint="The Wavelog station-location / profile id this log feeds."
            value={wlProfile}
            placeholder="1"
            onChange={setWlProfile}
          />
          <SecretRow
            label="API key"
            hint={
              status.wavelog.hasApiKey
                ? 'A key is saved — type a new one to replace it.'
                : 'Wavelog account → API keys. Stored on the server, never shown again.'
            }
            value={wlKey}
            placeholder={status.wavelog.hasApiKey ? '•••••• (saved)' : 'paste API key'}
            onChange={setWlKey}
          />
        </>
      )}

      <div className="zd-divider" />

      {/* Club Log */}
      <ToggleRow
        label="Also push to Club Log"
        hint="Upload each logged QSO to Club Log's realtime API over HTTPS. Additive — Zeus keeps the internal copy."
        checked={clEnabled}
        onChange={setClEnabled}
      />
      {clEnabled && (
        <>
          <TextRow
            label="Email"
            hint="The email of your Club Log account."
            value={clEmail}
            placeholder="you@example.com"
            onChange={setClEmail}
          />
          <TextRow
            label="Callsign"
            hint="The logging callsign for this Club Log account."
            value={clCall}
            placeholder="MYCALL"
            upper
            onChange={setClCall}
          />
          <SecretRow
            label="Password"
            hint={
              status.clubLog.hasPassword
                ? 'A password is saved — type a new one to replace it.'
                : 'Your Club Log account password. Stored on the server, never shown again.'
            }
            value={clPassword}
            placeholder={status.clubLog.hasPassword ? '•••••• (saved)' : 'account password'}
            onChange={setClPassword}
          />
          <SecretRow
            label="API key"
            hint={
              status.clubLog.hasApiKey
                ? 'A key is saved — type a new one to replace it.'
                : 'Club Log application API key (clublog.org/api.php).'
            }
            value={clApiKey}
            placeholder={status.clubLog.hasApiKey ? '•••••• (saved)' : 'application API key'}
            onChange={setClApiKey}
          />
        </>
      )}

      <div className="ps-field">
        <div className="ps-name">
          <span
            className={`zd-dot${status.wavelog.enabled || status.clubLog.enabled ? ' is-on' : ''}`}
          />
          Cloud loggers
          <em>
            {status.wavelog.enabled || status.clubLog.enabled
              ? `Active: ${[status.wavelog.enabled ? 'Wavelog' : null, status.clubLog.enabled ? 'Club Log' : null]
                  .filter(Boolean)
                  .join(' + ')}`
              : 'Disabled — no QSO data leaves this machine.'}
          </em>
        </div>
        <button type="button" className="btn sm active" disabled={saving} onClick={() => void onSave()}>
          {saving ? 'SAVING…' : 'SAVE'}
        </button>
      </div>
    </div>
  );
}
