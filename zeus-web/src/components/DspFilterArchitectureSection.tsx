// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

import { useCallback, useEffect, useState, type CSSProperties } from 'react';
import {
  fetchHardwareDiagnostics,
  type HardwareDspFilterActiveDto,
  type HardwareDspFilterGeometryDto,
} from '../api/client';

function dash(v: string | number | null | undefined): string {
  if (v === null || v === undefined || v === '') return '-';
  return String(v);
}

function boolLabel(v: boolean | null | undefined): string {
  if (v === null || v === undefined) return '-';
  return v ? 'ON' : 'OFF';
}

function hz(v: number | null | undefined): string {
  if (v === null || v === undefined || !Number.isFinite(v)) return '-';
  if (Math.abs(v) >= 1000) return `${v / 1000} kHz`;
  return `${v} Hz`;
}

function pct(v: number | null | undefined): string {
  if (v === null || v === undefined || !Number.isFinite(v)) return '-';
  return `${v.toFixed(1)}%`;
}

function time(v: string | null | undefined): string {
  if (!v) return '-';
  const d = new Date(v);
  return Number.isNaN(d.getTime()) ? v : d.toLocaleTimeString();
}

type Field = {
  label: string;
  value: string | number | null | undefined;
};

function FieldGrid({ fields }: { fields: Field[] }) {
  return (
    <div
      style={{
        display: 'grid',
        gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))',
        gap: 7,
      }}
    >
      {fields.map((field) => (
        <div key={field.label} style={fieldStyle}>
          <div style={fieldLabelStyle}>{field.label}</div>
          <div className="mono" style={fieldValueStyle}>{dash(field.value)}</div>
        </div>
      ))}
    </div>
  );
}

function ActivePathFields({
  label,
  path,
}: {
  label: string;
  path: HardwareDspFilterActiveDto;
}) {
  return (
    <div style={{ display: 'grid', gap: 7 }}>
      <span style={{ fontSize: 11, fontWeight: 800, color: 'var(--fg-0)' }}>{label}</span>
      <FieldGrid
        fields={[
          { label: 'Mode', value: path.mode },
          { label: 'Low Edge', value: hz(path.filterLowHz) },
          { label: 'High Edge', value: hz(path.filterHighHz) },
          { label: 'Preset', value: path.filterPresetName },
          { label: 'IQ Buffer', value: path.inputBufferSize },
          { label: 'DSP Buffer', value: path.dspBufferSize },
          { label: 'IQ Out', value: path.outputBufferSize },
          { label: 'Window', value: `${path.filterWindow} (${path.filterWindowId})` },
          { label: 'Type', value: path.filterType },
          { label: 'Taps', value: path.filterTaps },
          { label: 'CFIR', value: path.cfirCompensation === undefined ? null : boolLabel(path.cfirCompensation) },
          { label: 'Status', value: path.status },
        ]}
      />
    </div>
  );
}

export function DspFilterArchitectureSection() {
  const [diag, setDiag] = useState<HardwareDspFilterGeometryDto | null>(null);
  const [generatedUtc, setGeneratedUtc] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    setBusy(true);
    try {
      const next = await fetchHardwareDiagnostics(signal);
      setDiag(next.dsp.filterGeometry);
      setGeneratedUtc(next.generatedUtc);
      setError(null);
    } catch (err) {
      if ((err as DOMException).name !== 'AbortError') {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setBusy(false);
    }
  }, []);

  useEffect(() => {
    const ac = new AbortController();
    void load(ac.signal);
    const id = window.setInterval(() => void load(), 2_000);
    return () => {
      ac.abort();
      window.clearInterval(id);
    };
  }, [load]);

  if (!diag) {
    return (
      <div className="dsp-cfg">
        <div className="dsp-cfg-row">
          <span className="dsp-cfg-label">Status</span>
          <span className="dsp-cfg-hint" style={{ flex: 1 }}>
            {error ?? 'Waiting for WDSP filter architecture diagnostics.'}
          </span>
          <button type="button" className="btn sm" onClick={() => void load()} disabled={busy}>
            REFRESH
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="dsp-cfg">
      <div className="dsp-cfg-row">
        <span className="dsp-cfg-label">Status</span>
        <span className="dsp-cfg-hint" style={{ flex: 1 }}>
          {diag.status} · {diag.runtimeSampleRateControl.writable ? 'rate writable' : 'diagnostic only'} · {time(generatedUtc)}
        </span>
        <button type="button" className="btn sm" onClick={() => void load()} disabled={busy}>
          REFRESH
        </button>
      </div>
      {error && <div style={{ fontSize: 11, color: 'var(--tx)' }}>{error}</div>}

      <FieldGrid
        fields={[
          { label: 'RX ADCs', value: diag.hardwareLimits.rxAdcCount },
          { label: 'Active DDC Rate', value: hz(diag.hardwareLimits.activeSampleRateHz) },
          { label: 'Board Max DDC', value: hz(diag.hardwareLimits.maxRxSampleRateHz) },
          { label: 'IQ Buffer Sizes', value: diag.optionCatalog.iqBufferSizes.join(' / ') },
          { label: 'Tap Sizes', value: diag.optionCatalog.filterTapSizes.join(' / ') },
          { label: 'Filter Types', value: diag.optionCatalog.filterTypes.join(' / ') },
          { label: 'Windows', value: diag.optionCatalog.filterWindows.map((w) => `${w.label}=${w.id}`).join(' / ') },
          { label: 'Config Surface', value: diag.operatorConfigurable ? 'rate writable / DSP fixed' : 'diagnostic only' },
        ]}
      />

      <FieldGrid
        fields={[
          { label: 'Rate Control', value: diag.runtimeSampleRateControl.status },
          { label: 'Writable', value: boolLabel(diag.runtimeSampleRateControl.writable) },
          { label: 'Reconnect', value: diag.runtimeSampleRateControl.requiresReconnect ? 'required' : 'live' },
          { label: 'Max Writable', value: hz(diag.runtimeSampleRateControl.maxWritableSampleRateHz) },
          { label: 'Wideband Write', value: boolLabel(diag.runtimeSampleRateControl.widebandWritable) },
          { label: 'Surface', value: diag.runtimeSampleRateControl.settingsSurface },
          { label: 'API', value: diag.runtimeSampleRateControl.apiRoute },
        ]}
      />

      <FieldGrid
        fields={[
          { label: 'Bandwidth Status', value: diag.receiverBandwidth.status },
          { label: 'Tone', value: diag.receiverBandwidth.tone },
          { label: 'P2 Active', value: boolLabel(diag.receiverBandwidth.protocol2Active) },
          { label: 'Wideband Active', value: boolLabel(diag.receiverBandwidth.widebandActive) },
          { label: 'Active Nyquist', value: hz(diag.receiverBandwidth.activeNyquistHz) },
          { label: 'Max Nyquist', value: hz(diag.receiverBandwidth.maxNyquistHz) },
          { label: 'Utilization', value: pct(diag.receiverBandwidth.utilizationPct) },
          { label: 'Unused DDC Rate', value: hz(diag.receiverBandwidth.unusedSampleRateHz) },
          { label: 'Unused Span', value: hz(diag.receiverBandwidth.unusedNyquistHz) },
          { label: 'Active RX', value: diag.receiverBandwidth.activeSoftwareReceivers },
          { label: 'Manual Capacity', value: diag.receiverBandwidth.manualReceiverCapacity },
          { label: 'Unexposed RX', value: diag.receiverBandwidth.unexposedReceiverCount },
          { label: 'User DDC', value: diag.receiverBandwidth.activeUserDdcIndex },
        ]}
      />

      {(diag.receiverBandwidth.activeSlots.length > 0 || diag.receiverBandwidth.reservedSlots.length > 0) && (
        <div style={{ overflowX: 'auto' }}>
          <table style={tableStyle}>
            <thead>
              <tr>
                <th style={thStyle}>DDC Slot</th>
                <th style={thStyle}>Purpose</th>
                <th style={thStyle}>Status</th>
                <th style={thStyle}>Notes</th>
              </tr>
            </thead>
            <tbody>
              {[...diag.receiverBandwidth.activeSlots, ...diag.receiverBandwidth.reservedSlots].map((slot) => (
                <tr key={`${slot.slot}-${slot.purpose}`}>
                  <td style={tdStyle} className="mono">{slot.slot}</td>
                  <td style={tdStyle}>{slot.purpose}</td>
                  <td style={tdStyle} className="mono">{slot.status}</td>
                  <td style={tdStyle}>{slot.notes}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div style={{ overflowX: 'auto' }}>
        <table style={tableStyle}>
          <thead>
            <tr>
              <th style={thStyle}>DDC Rate</th>
              <th style={thStyle}>Board</th>
              <th style={thStyle}>Protocol</th>
              <th style={thStyle}>Active</th>
              <th style={thStyle}>Status</th>
            </tr>
          </thead>
          <tbody>
            {diag.hardwareLimits.sampleRates.map((rate) => (
              <tr key={rate.sampleRateHz}>
                <td style={tdStyle} className="mono">{rate.label}</td>
                <td style={tdStyle} className="mono">{rate.boardSupported ? 'supported' : 'locked'}</td>
                <td style={tdStyle}>{rate.protocol2Required ? 'P2 only' : 'P1/P2'}</td>
                <td style={tdStyle} className="mono">{boolLabel(rate.active)}</td>
                <td style={tdStyle}>{rate.status}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <ActivePathFields label="Active RXA Filter" path={diag.activeRx} />
      <ActivePathFields label="Active TXA Filter" path={diag.activeTx} />

      <div style={{ overflowX: 'auto' }}>
        <table style={tableStyle}>
          <thead>
            <tr>
              <th style={thStyle}>Mode Family</th>
              <th style={thStyle}>Path</th>
              <th style={thStyle}>IQ Buffer</th>
              <th style={thStyle}>Taps</th>
              <th style={thStyle}>Type</th>
              <th style={thStyle}>Window</th>
              <th style={thStyle}>Status</th>
            </tr>
          </thead>
          <tbody>
            {diag.thetisMatrix.map((row) => (
              <tr key={`${row.modeFamily}-${row.direction}`}>
                <td style={tdStyle}>{row.modeFamily}</td>
                <td style={tdStyle} className="mono">{row.direction}</td>
                <td style={tdStyle} className="mono">{dash(row.iqBufferSize)}</td>
                <td style={tdStyle} className="mono">{dash(row.filterTaps)}</td>
                <td style={tdStyle}>{row.filterType}</td>
                <td style={tdStyle} className="mono">{row.filterWindow}</td>
                <td style={tdStyle}>{row.status}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <FieldGrid
        fields={[
          { label: 'FFTW Wisdom', value: boolLabel(diag.impulseCache.fftwWisdomCache) },
          { label: 'Wisdom Phase', value: diag.impulseCache.fftwWisdomPhase },
          { label: 'Wisdom Status', value: diag.impulseCache.fftwWisdomStatus },
          { label: 'Impulse Cache', value: boolLabel(diag.impulseCache.filterImpulseCache) },
          { label: 'Cache File', value: boolLabel(diag.impulseCache.saveRestoreImpulseCacheFile) },
          { label: 'Cache Status', value: diag.impulseCache.status },
          { label: 'High-Res Display', value: boolLabel(diag.highResolutionFilterDisplay.enabled) },
          { label: 'High-Res Status', value: diag.highResolutionFilterDisplay.status },
        ]}
      />

      {diag.optionCatalog.filterWindows.length > 0 && (
        <div style={{ overflowX: 'auto' }}>
          <table style={tableStyle}>
            <thead>
              <tr>
                <th style={thStyle}>Window</th>
                <th style={thStyle}>WDSP Id</th>
                <th style={thStyle}>Notes</th>
              </tr>
            </thead>
            <tbody>
              {diag.optionCatalog.filterWindows.map((window) => (
                <tr key={window.id}>
                  <td style={tdStyle}>{window.label}</td>
                  <td style={tdStyle} className="mono">{window.id}</td>
                  <td style={tdStyle}>{window.notes}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div style={noteStyle}>{diag.optionCatalog.slowModeChangeWarning}</div>
      <div style={noteStyle}>{diag.receiverBandwidth.diagnosticRecommendation}</div>
      <div style={noteStyle}>{diag.runtimeSampleRateControl.diagnosticRecommendation}</div>
      <div className="mono" style={{ ...noteStyle, color: 'var(--fg-3)' }}>{diag.receiverBandwidth.source}</div>
      <div className="mono" style={{ ...noteStyle, color: 'var(--fg-3)' }}>{diag.optionCatalog.source}</div>
      <div style={noteStyle}>{diag.impulseCache.notes}</div>
      <div style={noteStyle}>{diag.highResolutionFilterDisplay.notes}</div>
      <div style={noteStyle}>{diag.diagnosticRecommendation}</div>
      <div className="mono" style={{ ...noteStyle, color: 'var(--fg-3)' }}>{diag.source}</div>
    </div>
  );
}

const fieldStyle: CSSProperties = {
  padding: '7px 8px',
  background: 'var(--bg-0)',
  border: '1px solid var(--panel-border)',
  borderRadius: 'var(--r-sm)',
};

const fieldLabelStyle: CSSProperties = {
  fontSize: 9,
  fontWeight: 700,
  letterSpacing: '0.08em',
  textTransform: 'uppercase',
  color: 'var(--fg-3)',
};

const fieldValueStyle: CSSProperties = {
  marginTop: 3,
  fontSize: 12,
  color: 'var(--fg-0)',
  overflowWrap: 'anywhere',
};

const tableStyle: CSSProperties = {
  width: '100%',
  borderCollapse: 'collapse',
  fontSize: 11,
};

const thStyle: CSSProperties = {
  padding: '5px 6px',
  textAlign: 'left',
  borderBottom: '1px solid var(--panel-border)',
  color: 'var(--fg-3)',
  fontSize: 9,
  letterSpacing: '0.08em',
  textTransform: 'uppercase',
};

const tdStyle: CSSProperties = {
  padding: '5px 6px',
  borderBottom: '1px solid var(--panel-border)',
  verticalAlign: 'top',
};

const noteStyle: CSSProperties = {
  fontSize: 11,
  lineHeight: 1.35,
  color: 'var(--fg-2)',
};
