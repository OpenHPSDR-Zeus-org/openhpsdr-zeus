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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { useCallback, useEffect, useMemo, useState, type CSSProperties, type ReactNode } from 'react';
import { RefreshCw } from 'lucide-react';
import { CfcSettingsPanel } from './CfcSettingsPanel';
import { DownloadAudioSuiteButton } from './DownloadAudioSuiteButton';
import { DownloadVstEngineButton } from './DownloadVstEngineButton';
import { usePluginPanels } from '../plugins/runtime/usePluginPanels';
import type { RegisteredPluginPanel } from '../plugins/runtime/pluginRuntime';
import {
  fetchNativeAudioDevices,
  setNativeAudioDevices,
  type NativeAudioDevicesResponse,
} from '../api/audio-devices';
import { getAudioClient } from '../audio/audio-client';
import { restartMicUplinkRunning } from '../audio/mic-uplink-session';
import { useAudioSuiteStore } from '../state/audio-suite-store';
import { useAudioDeviceStore } from '../state/audio-device-store';
import { useCapabilitiesStore } from '../state/capabilities-store';

const CHAIN_SLOT = 'tx-audio-tools.chain';
const RX_CHAIN_SLOT = 'rx-audio-tools.chain';

type AudioRoute = 'tx' | 'rx';
type ChainFlowSlot = { id: string; title: string; installed: boolean };
type DeviceOption = { id: string; name: string; isDefault?: boolean };

function routeColor(route: AudioRoute) {
  return route === 'tx' ? 'var(--tx)' : 'var(--accent)';
}

function RouteRail({
  route,
  title,
  status,
  actions,
  children,
}: {
  route: AudioRoute;
  title: string;
  status: ReactNode;
  actions: ReactNode;
  children: ReactNode;
}) {
  return (
    <section
      aria-label={title}
      style={{
        display: 'flex',
        flexDirection: 'column',
        minWidth: 0,
        background: 'linear-gradient(180deg, var(--panel-top), var(--panel-bot))',
        border: '1px solid var(--line)',
        borderTop: '2px solid ' + routeColor(route),
        borderRadius: 6,
        color: 'var(--fg-2)',
        fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
        overflow: 'hidden',
      }}
    >
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 8,
          minWidth: 0,
          padding: '8px 10px',
          borderBottom: '1px solid var(--line)',
          flexWrap: 'wrap',
        }}
      >
        <span
          style={{
            padding: '2px 7px',
            borderRadius: 3,
            border: '1px solid ' + routeColor(route),
            background: 'var(--bg-2)',
            color: routeColor(route),
            fontSize: 10,
            fontWeight: 800,
            letterSpacing: 0,
            lineHeight: 1.2,
          }}
        >
          {route.toUpperCase()}
        </span>
        <span
          style={{
            color: 'var(--fg-1)',
            fontSize: 12,
            fontWeight: 700,
            letterSpacing: 0,
            whiteSpace: 'nowrap',
          }}
        >
          {title}
        </span>
        {status}
        <div
          style={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'flex-end',
            gap: 8,
            marginLeft: 'auto',
            flexWrap: 'wrap',
          }}
        >
          {actions}
        </div>
      </div>

      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: '8px 12px',
          padding: '8px 10px',
          flexWrap: 'wrap',
        }}
      >
        {children}
      </div>
    </section>
  );
}

function RouteStatusPill({
  route,
  label,
  title,
  muted = false,
}: {
  route: AudioRoute;
  label: string;
  title: string;
  muted?: boolean;
}) {
  return (
    <span
      title={title}
      style={{
        padding: '2px 7px',
        borderRadius: 3,
        border: '1px solid ' + (muted ? 'var(--line)' : routeColor(route)),
        background: 'var(--bg-1)',
        color: muted ? 'var(--fg-3)' : 'var(--fg-1)',
        fontSize: 10,
        fontWeight: 700,
        letterSpacing: 0,
        whiteSpace: 'nowrap',
      }}
    >
      {label}
    </span>
  );
}

function selectStyle(disabled: boolean): CSSProperties {
  return {
    flex: '1 1 0',
    minWidth: 0,
    maxWidth: 280,
    width: '100%',
    height: 26,
    padding: '3px 24px 3px 8px',
    textOverflow: 'ellipsis',
    borderRadius: 4,
    border: '1px solid var(--line)',
    background: disabled ? 'var(--bg-1)' : 'var(--bg-2)',
    color: disabled ? 'var(--fg-3)' : 'var(--fg-1)',
    fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
    fontSize: 11,
    fontWeight: 600,
    letterSpacing: 0,
  };
}

const refreshButtonStyle: CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  justifyContent: 'center',
  width: 28,
  height: 26,
  borderRadius: 4,
  border: '1px solid var(--line)',
  background: 'var(--bg-2)',
  color: 'var(--fg-1)',
  cursor: 'pointer',
};

function DeviceSelect({
  label,
  value,
  devices,
  disabled,
  onChange,
}: {
  label: string;
  value: string;
  devices: DeviceOption[];
  disabled: boolean;
  onChange(value: string): void;
}) {
  const selectedMissing = value && !devices.some((device) => device.id === value);
  return (
    <label
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 6,
        flex: '1 1 0',
        minWidth: 0,
        color: 'var(--fg-2)',
        fontSize: 10,
        fontWeight: 800,
        letterSpacing: 0,
        textTransform: 'uppercase',
      }}
    >
      <span style={{ flex: '0 0 auto' }}>{label}</span>
      <select
        aria-label={`Audio ${label.toLowerCase()} device`}
        value={value}
        disabled={disabled}
        onChange={(ev) => onChange(ev.currentTarget.value)}
        style={selectStyle(disabled)}
      >
        <option value="">System default</option>
        {devices.map((device) => (
          <option key={device.id} value={device.id}>
            {device.name}{device.isDefault ? ' (default)' : ''}
          </option>
        ))}
        {selectedMissing && <option value={value}>Missing device</option>}
      </select>
    </label>
  );
}

function AudioDevicesRail() {
  const hostMode = useCapabilitiesStore((s) => s.capabilities?.host ?? null);
  if (hostMode === null) return null;
  return hostMode === 'desktop' ? <NativeAudioDevicesRail /> : <BrowserAudioDevicesRail />;
}

function NativeAudioDevicesRail() {
  const [devices, setDevices] = useState<NativeAudioDevicesResponse | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setBusy(true);
    setError(null);
    try {
      setDevices(await fetchNativeAudioDevices());
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const apply = useCallback(
    async (next: { input?: string; output?: string }) => {
      const input = next.input ?? devices?.inputDeviceId ?? '';
      const output = next.output ?? devices?.outputDeviceId ?? '';
      setBusy(true);
      setError(null);
      try {
        setDevices(await setNativeAudioDevices(input || null, output || null));
      } catch (err) {
        setError(err instanceof Error ? err.message : String(err));
      } finally {
        setBusy(false);
      }
    },
    [devices],
  );

  const supported = devices?.supported !== false;
  const effectiveError = error ?? devices?.error ?? null;
  const inputValue = devices?.inputDeviceId ?? '';
  const outputValue = devices?.outputDeviceId ?? '';

  return (
    <RouteRail
      route="rx"
      title="Audio Devices"
      status={
        <RouteStatusPill
          route="rx"
          label={effectiveError ? 'NATIVE ERR' : 'HOST'}
          title={effectiveError ?? 'Native desktop input and output devices'}
          muted={!!effectiveError}
        />
      }
      actions={
        <button
          type="button"
          onClick={() => void load()}
          disabled={busy}
          style={refreshButtonStyle}
          title="Refresh audio device list"
          aria-label="Refresh audio device list"
        >
          <RefreshCw size={13} aria-hidden />
        </button>
      }
    >
      <DeviceSelect
        label="Input"
        value={inputValue}
        devices={devices?.inputs ?? []}
        disabled={busy || !supported}
        onChange={(input) => void apply({ input })}
      />
      <DeviceSelect
        label="Output"
        value={outputValue}
        devices={devices?.outputs ?? []}
        disabled={busy || !supported}
        onChange={(output) => void apply({ output })}
      />
    </RouteRail>
  );
}

function BrowserAudioDevicesRail() {
  const inputs = useAudioDeviceStore((s) => s.browserInputs);
  const outputs = useAudioDeviceStore((s) => s.browserOutputs);
  const inputDeviceId = useAudioDeviceStore((s) => s.browserInputDeviceId);
  const outputDeviceId = useAudioDeviceStore((s) => s.browserOutputDeviceId);
  const outputSupported = useAudioDeviceStore((s) => s.browserOutputSupported);
  const deviceError = useAudioDeviceStore((s) => s.browserDeviceError);
  const refresh = useAudioDeviceStore((s) => s.refreshBrowserDevices);
  const setInputDeviceId = useAudioDeviceStore((s) => s.setBrowserInputDeviceId);
  const setOutputDeviceId = useAudioDeviceStore((s) => s.setBrowserOutputDeviceId);
  const [busy, setBusy] = useState(false);
  const [applyError, setApplyError] = useState<string | null>(null);

  const refreshDevices = useCallback(async () => {
    setBusy(true);
    setApplyError(null);
    try {
      await refresh();
    } finally {
      setBusy(false);
    }
  }, [refresh]);

  useEffect(() => {
    void refreshDevices();
    const mediaDevices = typeof navigator !== 'undefined' ? navigator.mediaDevices : undefined;
    if (!mediaDevices?.addEventListener) return;
    const onChange = () => void refreshDevices();
    mediaDevices.addEventListener('devicechange', onChange);
    return () => mediaDevices.removeEventListener('devicechange', onChange);
  }, [refreshDevices]);

  const onInput = useCallback(
    async (id: string) => {
      setInputDeviceId(id);
      setApplyError(null);
      try {
        await restartMicUplinkRunning();
      } catch (err) {
        setApplyError(err instanceof Error ? err.message : String(err));
      }
    },
    [setInputDeviceId],
  );

  const onOutput = useCallback(
    async (id: string) => {
      setOutputDeviceId(id);
      setApplyError(null);
      try {
        await getAudioClient().setOutputDevice(id);
      } catch (err) {
        setApplyError(err instanceof Error ? err.message : String(err));
      }
    },
    [setOutputDeviceId],
  );

  const effectiveError = applyError ?? deviceError;

  return (
    <RouteRail
      route="rx"
      title="Audio Devices"
      status={
        <RouteStatusPill
          route="rx"
          label={effectiveError ? 'BROWSER ERR' : 'BROWSER'}
          title={effectiveError ?? 'Browser input and output devices'}
          muted={!!effectiveError}
        />
      }
      actions={
        <button
          type="button"
          onClick={() => void refreshDevices()}
          disabled={busy}
          style={refreshButtonStyle}
          title="Refresh audio device list"
          aria-label="Refresh audio device list"
        >
          <RefreshCw size={13} aria-hidden />
        </button>
      }
    >
      <DeviceSelect
        label="Input"
        value={inputDeviceId}
        devices={inputs}
        disabled={busy}
        onChange={(id) => void onInput(id)}
      />
      <DeviceSelect
        label="Output"
        value={outputDeviceId}
        devices={outputs}
        disabled={busy || !outputSupported}
        onChange={(id) => void onOutput(id)}
      />
    </RouteRail>
  );
}

function ChainStageChips({
  slots,
  bypassed,
  emptyText,
  bypassTitle,
}: {
  slots: ChainFlowSlot[];
  bypassed: boolean;
  emptyText: string;
  bypassTitle: string;
}) {
  if (slots.length === 0) {
    return (
      <span
        style={{
          color: 'var(--fg-3)',
          fontSize: 11,
          fontWeight: 500,
          letterSpacing: 0,
        }}
      >
        {emptyText}
      </span>
    );
  }

  return (
    <>
      {slots.map((slot, i) => {
        const dimForBypass = slot.id !== 'cfc' && bypassed;
        return (
          <span
            key={slot.id}
            style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0 }}
          >
            {i > 0 && (
              <span
                aria-hidden
                style={{
                  color: 'var(--fg-3)',
                  fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                }}
              >
                ›
              </span>
            )}
            <span
              style={{
                maxWidth: 190,
                overflow: 'hidden',
                textOverflow: 'ellipsis',
                whiteSpace: 'nowrap',
                padding: '2px 8px',
                borderRadius: 3,
                background: slot.installed ? 'var(--bg-2)' : 'var(--bg-1)',
                border: '1px solid ' + (slot.installed ? 'var(--accent)' : 'var(--line)'),
                color: slot.installed ? 'var(--fg-0)' : 'var(--fg-3)',
                opacity: dimForBypass ? 0.45 : (slot.installed ? 1 : 0.5),
                fontSize: 10,
                fontWeight: 600,
                letterSpacing: 0,
                transition: 'opacity 120ms ease-out',
              }}
              title={
                dimForBypass
                  ? bypassTitle
                  : slot.installed
                    ? 'Installed and active'
                    : 'Not installed — click Download Audio Suite or Settings → Plugins → Install from URL'
              }
            >
              {slot.title}
            </span>
          </span>
        );
      })}
    </>
  );
}

function TxChainFlow({ chainPanels }: { chainPanels: RegisteredPluginPanel[] }) {
  const v1Slots: ChainFlowSlot[] = useMemo(() => {
    const installedIds = new Set(chainPanels.map((p) => p.pluginId));
    return [
      { id: 'eq', title: 'EQ', installed: installedIds.has('com.openhpsdr.zeus.samples.eq') },
      {
        id: 'comp',
        title: 'COMP',
        installed: installedIds.has('com.openhpsdr.zeus.samples.compressor'),
      },
      {
        id: 'exciter',
        title: 'EXCITER',
        installed: installedIds.has('com.openhpsdr.zeus.samples.exciter'),
      },
      { id: 'bass', title: 'BASS', installed: installedIds.has('com.openhpsdr.zeus.samples.bass') },
      {
        id: 'reverb',
        title: 'REVERB',
        installed: installedIds.has('com.openhpsdr.zeus.samples.reverb'),
      },
      { id: 'cfc', title: 'CFC', installed: true },
    ];
  }, [chainPanels]);

  const masterBypassed = useAudioSuiteStore((s) => s.masterBypassed);
  const processingMode = useAudioSuiteStore((s) => s.processingMode);
  const engineAvailable = useAudioSuiteStore((s) => s.vstEngineAvailable);
  const engineActive = useAudioSuiteStore((s) => s.vstEngineActive);
  const engineSupported = useAudioSuiteStore((s) => s.engineSupported);
  const engineSupportLoaded = useAudioSuiteStore((s) => s.engineSupportLoaded);
  const chainOrder = useAudioSuiteStore((s) => s.chainOrder);
  const loadMasterBypassFromServer = useAudioSuiteStore(
    (s) => s.loadMasterBypassFromServer,
  );
  const loadProcessingModeFromServer = useAudioSuiteStore(
    (s) => s.loadProcessingModeFromServer,
  );

  const vstMode = processingMode === 'vst';
  const vstSlots = useMemo(() => {
    if (!vstMode) return [];
    const orderIndex = new Map(chainOrder.map((id, i) => [id, i] as const));
    return chainPanels
      .filter((p) => p.editorBacked === true && orderIndex.has(p.pluginId))
      .sort((a, b) => orderIndex.get(a.pluginId)! - orderIndex.get(b.pluginId)!)
      .map((p) => ({
        id: p.pluginId,
        title: (p.title || p.pluginId.split('.').pop() || 'VST').toUpperCase(),
        installed: true,
      }));
  }, [vstMode, chainPanels, chainOrder]);

  const slots = vstMode ? vstSlots : v1Slots;
  const statusLabel = !vstMode
    ? 'NATIVE'
    : engineActive
      ? 'VST ON'
      : engineAvailable
        ? 'VST IDLE'
        : 'VST OFF';
  const statusTitle = !vstMode
    ? 'Transmit audio is using the native in-process Audio Suite route.'
    : engineActive
      ? 'Transmit VST route is active.'
      : engineAvailable
        ? 'Transmit VST route is selected but the engine is not routing yet.'
        : 'Transmit VST route is selected but no VST engine is installed.';

  useEffect(() => {
    loadMasterBypassFromServer();
    loadProcessingModeFromServer();
  }, [loadMasterBypassFromServer, loadProcessingModeFromServer]);

  return (
    <RouteRail
      route="tx"
      title="TX Audio"
      status={
        <RouteStatusPill
          route="tx"
          label={statusLabel}
          title={statusTitle}
          muted={!vstMode}
        />
      }
      actions={
        <>
          <SuiteButton route="tx" />
          {!vstMode && <DownloadAudioSuiteButton />}
          {/* VST mode: Windows downloads the out-of-process engine; macOS/Linux
              host plugins in-process (AU/VST3), so the engine-download button —
              which errors off Windows — is replaced by a Scan/Add affordance.
              Wait for the platform DTO before committing, so macOS never flashes
              the (erroring) engine button on the default Windows-shaped state. */}
          {vstMode &&
            engineSupportLoaded &&
            (engineSupported ? <DownloadVstEngineButton /> : <InProcessPluginScanButton route="tx" />)}
        </>
      }
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
        <ProcessingModeButton />
        <MasterBypassButton />
      </div>
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 6,
          flex: '1 1 260px',
          flexWrap: 'wrap',
          minWidth: 0,
        }}
      >
        <span
          style={{
            color: 'var(--fg-1)',
            fontSize: 11,
            fontWeight: 700,
            letterSpacing: 0,
            flex: '0 0 auto',
          }}
        >
          {vstMode ? 'TX VST chain' : 'TX chain'}
        </span>
        <ChainStageChips
          slots={slots}
          bypassed={masterBypassed}
          emptyText="No active TX VST plugins"
          bypassTitle="TX master bypass engaged — this stage is inert. Click BYPASS to engage the TX chain."
        />
      </div>
    </RouteRail>
  );
}

function RxChainFlow({ chainPanels }: { chainPanels: RegisteredPluginPanel[] }) {
  const rxMasterBypassed = useAudioSuiteStore((s) => s.rxMasterBypassed);
  const rxChainOrder = useAudioSuiteStore((s) => s.rxChainOrder);
  const rxVstEngineAvailable = useAudioSuiteStore((s) => s.rxVstEngineAvailable);
  const rxVstEngineActive = useAudioSuiteStore((s) => s.rxVstEngineActive);
  const rxVstActivePlugins = useAudioSuiteStore((s) => s.rxVstActivePlugins);
  const rxVstDegradedBlocks = useAudioSuiteStore((s) => s.rxVstDegradedBlocks);
  const loadRxChainOrderFromServer = useAudioSuiteStore((s) => s.loadRxChainOrderFromServer);
  const loadRxMasterBypassFromServer = useAudioSuiteStore(
    (s) => s.loadRxMasterBypassFromServer,
  );
  const loadRxProcessingModeFromServer = useAudioSuiteStore(
    (s) => s.loadRxProcessingModeFromServer,
  );

  const engineSupported = useAudioSuiteStore((s) => s.engineSupported);
  const engineSupportLoaded = useAudioSuiteStore((s) => s.engineSupportLoaded);

  const slots = useMemo(() => {
    const orderIndex = new Map(rxChainOrder.map((id, i) => [id, i] as const));
    return chainPanels
      .filter((p) => orderIndex.has(p.pluginId))
      .sort((a, b) => orderIndex.get(a.pluginId)! - orderIndex.get(b.pluginId)!)
      .map((p) => ({
        id: p.pluginId,
        title: (p.title || p.pluginId.split('.').pop() || 'RX VST').toUpperCase(),
        installed: true,
      }));
  }, [chainPanels, rxChainOrder]);

  const statusLabel = rxVstEngineActive
    ? 'VST ON'
    : rxVstEngineAvailable
      ? 'VST IDLE'
      : 'VST OFF';
  const statusTitle = rxVstEngineActive
    ? `Receive VST engine active (${rxVstActivePlugins} plugin${rxVstActivePlugins === 1 ? '' : 's'}, ${rxVstDegradedBlocks} degraded blocks).`
    : rxVstEngineAvailable
      ? 'Receive VST engine is available but idle.'
      : 'Receive VST engine is not installed.';

  useEffect(() => {
    loadRxChainOrderFromServer();
    loadRxMasterBypassFromServer();
    loadRxProcessingModeFromServer();
  }, [loadRxChainOrderFromServer, loadRxMasterBypassFromServer, loadRxProcessingModeFromServer]);

  return (
    <RouteRail
      route="rx"
      title="RX Audio"
      status={
        <RouteStatusPill
          route="rx"
          label={statusLabel}
          title={statusTitle}
          muted={!rxVstEngineActive}
        />
      }
      actions={
        <>
          {/* RX inserts host in-process. On macOS/Linux there is no engine to
              download, so surface the same Scan/Add affordance as TX — this is
              how AU plugins reach the RX path. Windows adds RX VST3s via the
              suite's "Add VST folder"; AU is macOS-only. */}
          {engineSupportLoaded && !engineSupported && <InProcessPluginScanButton route="rx" />}
          <SuiteButton route="rx" />
        </>
      }
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
        <RxMasterBypassButton />
      </div>
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 6,
          flex: '1 1 260px',
          flexWrap: 'wrap',
          minWidth: 0,
        }}
      >
        <span
          style={{
            color: 'var(--fg-1)',
            fontSize: 11,
            fontWeight: 700,
            letterSpacing: 0,
            flex: '0 0 auto',
          }}
        >
          RX VST chain
        </span>
        <ChainStageChips
          slots={slots}
          bypassed={rxMasterBypassed}
          emptyText="No active RX VST plugins"
          bypassTitle="RX master bypass engaged — this stage is inert. Click BYPASS to engage the RX chain."
        />
      </div>
    </RouteRail>
  );
}

function MasterBypassButton() {
  const masterBypassed = useAudioSuiteStore((s) => s.masterBypassed);
  const setMasterBypassed = useAudioSuiteStore((s) => s.setMasterBypassed);
  return (
    <button
      type="button"
      onClick={() => setMasterBypassed(!masterBypassed)}
      aria-pressed={masterBypassed}
      title={
        masterBypassed
          ? 'TX master bypass ENGAGED — transmit plugin chain is inert; mic passes through untouched. CFC is unaffected. Click to engage the TX chain.'
          : 'TX plugin chain ACTIVE — installed plugins are processing your mic. Click to bypass the TX chain.'
      }
      style={routeButtonStyle(masterBypassed)}
    >
      Bypass
    </button>
  );
}

function RxMasterBypassButton() {
  const rxMasterBypassed = useAudioSuiteStore((s) => s.rxMasterBypassed);
  const setRxMasterBypassed = useAudioSuiteStore((s) => s.setRxMasterBypassed);
  return (
    <button
      type="button"
      onClick={() => setRxMasterBypassed(!rxMasterBypassed)}
      aria-pressed={rxMasterBypassed}
      title={
        rxMasterBypassed
          ? 'RX Audio Suite bypass ENGAGED — receive VST inserts are inert. Click to engage the RX chain.'
          : 'RX Audio Suite ACTIVE — receive VST inserts are processing audio. Click to bypass the RX chain.'
      }
      style={routeButtonStyle(rxMasterBypassed)}
    >
      Bypass
    </button>
  );
}

function routeButtonStyle(active: boolean): CSSProperties {
  return {
    padding: '4px 12px',
    borderRadius: 4,
    border: '1px solid ' + (active ? 'var(--tx)' : 'var(--accent)'),
    background: active ? 'var(--tx)' : 'var(--bg-2)',
    color: active ? '#fff' : 'var(--fg-2)',
    cursor: 'pointer',
    fontSize: 10,
    fontWeight: 700,
    letterSpacing: 0,
    textTransform: 'uppercase',
    fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
    minWidth: 72,
    transition: 'background 120ms ease-out, color 120ms ease-out, border-color 120ms ease-out',
  };
}

function ProcessingModeButton() {
  const mode = useAudioSuiteStore((s) => s.processingMode);
  const engineAvailable = useAudioSuiteStore((s) => s.vstEngineAvailable);
  const engineActive = useAudioSuiteStore((s) => s.vstEngineActive);
  const setProcessingMode = useAudioSuiteStore((s) => s.setProcessingMode);

  const isVst = mode === 'vst';
  const vstWarn = isVst && !engineActive;

  const border = isVst ? (vstWarn ? 'var(--tx)' : 'var(--accent)') : 'var(--line)';
  const background = isVst && !vstWarn ? 'var(--accent)' : 'var(--bg-2)';
  const color = isVst ? (vstWarn ? 'var(--tx)' : '#fff') : 'var(--fg-2)';

  const title = !isVst
    ? 'Processing route: NATIVE — the in-process Audio Suite chain. Click to route TX mic audio through the out-of-process VST engine instead.'
    : engineActive
      ? 'Processing route: VST — TX mic audio runs through the out-of-process VST engine. Click to return to the native chain.'
      : engineAvailable
        ? 'VST route selected, but the engine is not routing yet. TX audio passes through clean meanwhile. Click to return to the native chain.'
        : 'VST route selected, but no VST engine is installed. TX audio passes through clean. Click to return to the native chain.';

  return (
    <button
      type="button"
      onClick={() => setProcessingMode(isVst ? 'native' : 'vst')}
      aria-pressed={isVst}
      title={title}
      style={{
        padding: '4px 12px',
        borderRadius: 4,
        border: '1px solid ' + border,
        background,
        color,
        cursor: 'pointer',
        fontSize: 10,
        fontWeight: 700,
        letterSpacing: 0,
        whiteSpace: 'nowrap',
        minWidth: 72,
        transition: 'background 120ms ease-out, color 120ms ease-out, border-color 120ms ease-out',
      }}
    >
      {isVst ? 'VST' : 'Native'}
    </button>
  );
}

/**
 * In-process plugin affordance for platforms where the out-of-process VST
 * engine is unavailable (macOS / Linux). On macOS this scans the OS
 * AudioComponent registry for AUv2 effects and registers them into the given
 * route's insert chain in-process; on every platform it surfaces a path to
 * the suite where VST3 folders can be added (also in-process via the native
 * VST3 bridge). No engine download — that path errors off Windows.
 */
function InProcessPluginScanButton({ route }: { route: AudioRoute }) {
  const auSupported = useAudioSuiteStore((s) => s.auSupported);
  const scanAuComponents = useAudioSuiteStore((s) => s.scanAuComponents);
  const openTx = useAudioSuiteStore((s) => s.openTx);
  const openRx = useAudioSuiteStore((s) => s.openRx);
  const [scanning, setScanning] = useState(false);
  const [notice, setNotice] = useState<{ tone: 'ok' | 'warn' | 'err'; text: string } | null>(
    null,
  );

  const openSuite = route === 'tx' ? openTx : openRx;

  const runScan = useCallback(async () => {
    setScanning(true);
    setNotice(null);
    try {
      const result = await scanAuComponents(route);
      if (!result.ok) {
        setNotice({ tone: 'err', text: result.error ?? 'AU scan failed' });
        return;
      }
      if (!result.supported) {
        setNotice({ tone: 'warn', text: 'Audio Units are macOS-only.' });
        return;
      }
      const added = result.registered.length;
      const present = result.skipped.length;
      const failed = result.errors.length;
      setNotice({
        tone: failed > 0 ? 'warn' : 'ok',
        text: `Added ${added}, already present ${present}${failed > 0 ? `, failed ${failed}` : ''}.`,
      });
    } finally {
      setScanning(false);
    }
  }, [route, scanAuComponents]);

  const scanLabel = scanning ? 'Scanning…' : auSupported ? 'Scan AU' : 'Scan plugins';
  const scanTitle = auSupported
    ? `Scan installed Audio Units and add them to the ${route.toUpperCase()} insert chain (in-process, no engine download)`
    : `Open the ${route.toUpperCase()} Audio Suite to add VST3 plugin folders (hosted in-process)`;

  const noticeColor =
    notice?.tone === 'err' ? 'var(--tx)' : notice?.tone === 'warn' ? 'var(--power)' : 'var(--fg-2)';

  return (
    <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-end', gap: 4 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
        <button
          type="button"
          onClick={() => (auSupported ? void runScan() : openSuite())}
          disabled={scanning}
          style={inProcessScanButtonStyle(scanning)}
          title={scanTitle}
        >
          {scanLabel}
        </button>
        {auSupported && (
          <button
            type="button"
            onClick={openSuite}
            style={inProcessScanButtonStyle(false)}
            title={`Open the ${route.toUpperCase()} Audio Suite to add VST3 plugin folders (hosted in-process)`}
          >
            + VST3
          </button>
        )}
      </div>
      {notice && (
        <span
          role="status"
          style={{ color: noticeColor, fontSize: 10, fontWeight: 600, letterSpacing: 0 }}
        >
          {notice.text}
        </span>
      )}
    </div>
  );
}

function inProcessScanButtonStyle(busy: boolean): CSSProperties {
  return {
    padding: '4px 12px',
    borderRadius: 4,
    border: '1px solid var(--accent)',
    background: 'var(--bg-2)',
    color: 'var(--fg-0)',
    cursor: busy ? 'progress' : 'pointer',
    opacity: busy ? 0.6 : 1,
    fontSize: 10,
    fontWeight: 700,
    letterSpacing: 0,
    textTransform: 'uppercase',
    fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
    whiteSpace: 'nowrap',
  };
}

function SuiteButton({ route }: { route: AudioRoute }) {
  const openTx = useAudioSuiteStore((s) => s.openTx);
  const openRx = useAudioSuiteStore((s) => s.openRx);
  const buttonStyle: CSSProperties = {
    padding: '4px 12px',
    borderRadius: 4,
    border: '1px solid var(--accent)',
    background: 'var(--bg-2)',
    color: 'var(--fg-0)',
    cursor: 'pointer',
    fontSize: 10,
    fontWeight: 700,
    letterSpacing: 0,
    textTransform: 'uppercase',
    fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
    whiteSpace: 'nowrap',
  };
  return (
    <button
      type="button"
      onClick={route === 'tx' ? openTx : openRx}
      style={buttonStyle}
      title={
        route === 'tx'
          ? 'Open the TX Audio Suite window to reorder, preview, and tune transmit plugins'
          : 'Open the RX Audio Suite window to reorder and tune receive VST inserts'
      }
    >
      {route === 'tx' ? 'TX Suite' : 'RX Suite'}
    </button>
  );
}

export function TxAudioToolsPanel() {
  const allPanels = usePluginPanels();
  const loadEngineSupportFromServer = useAudioSuiteStore(
    (s) => s.loadEngineSupportFromServer,
  );
  const chainPanels = useMemo(
    () => allPanels.filter((p) => p.slot === CHAIN_SLOT),
    [allPanels],
  );
  const rxChainPanels = useMemo(
    () => allPanels.filter((p) => p.slot === RX_CHAIN_SLOT),
    [allPanels],
  );

  // Pull the platform affordance flags once on mount so TX/RX render the
  // right path (Windows engine download vs macOS/Linux in-process scan).
  useEffect(() => {
    loadEngineSupportFromServer();
  }, [loadEngineSupportFromServer]);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <div
        style={{
          display: 'grid',
          gridTemplateColumns: 'repeat(auto-fit, minmax(min(100%, 360px), 1fr))',
          gap: 12,
          alignItems: 'stretch',
        }}
      >
        <AudioDevicesRail />
        <TxChainFlow chainPanels={chainPanels} />
        <RxChainFlow chainPanels={rxChainPanels} />
      </div>

      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 8,
          color: 'var(--fg-2)',
          fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
          fontSize: 11,
          fontWeight: 800,
          letterSpacing: 0,
        }}
      >
        <span
          style={{
            padding: '2px 7px',
            borderRadius: 3,
            border: '1px solid var(--tx)',
            background: 'var(--bg-2)',
            color: 'var(--tx)',
          }}
        >
          TX
        </span>
        <span>Transmit tools</span>
      </div>

      <CfcSettingsPanel />
    </div>
  );
}
