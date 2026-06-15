// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

import { useCallback, useEffect, useState, type CSSProperties } from 'react';
import {
  createHardwareDiagnosticsMarker,
  fetchHardwareDiagnostics,
  fetchRadios,
  resetHardwareDiagnosticsMap,
  type HardwareByteStreamMapDto,
  type HardwareDiagnosticItemDto,
  type HardwareDiagnosticsDto,
  type HardwareFeatureSurfaceDto,
  type HardwareMappingMarkerDto,
  type HardwareP1MapDto,
  type RadioInfoDto,
} from '../api/client';

const INVENTORY_INTERVAL_MS = 10_000;

type Field = {
  label: string;
  value: string | number | null | undefined;
};

function dash(v: string | number | null | undefined): string {
  if (v === null || v === undefined || v === '') return '-';
  return String(v);
}

function boolLabel(v: boolean | null | undefined): string {
  if (v === null || v === undefined) return '-';
  return v ? 'ON' : 'OFF';
}

function hex(v: number | null | undefined, width = 2): string {
  if (v === null || v === undefined) return '-';
  return `0x${Math.max(0, v).toString(16).toUpperCase().padStart(width, '0')}`;
}

function adc(v: number | null | undefined): string {
  if (v === null || v === undefined) return '-';
  return `${v} / ${hex(v, 4)}`;
}

function time(v: string | null | undefined): string {
  if (!v) return '-';
  const d = new Date(v);
  return Number.isNaN(d.getTime()) ? v : d.toLocaleTimeString();
}

function age(v: number | null | undefined): string {
  if (v === null || v === undefined) return '-';
  if (v < 1000) return `${Math.max(0, Math.round(v))} ms`;
  return `${Math.round(v / 1000)} s`;
}

function db(v: number | null | undefined): string {
  if (v === null || v === undefined) return '-';
  return `${v.toFixed(1)} dB`;
}

function pct(v: number | null | undefined): string {
  if (v === null || v === undefined) return '-';
  return `${v.toFixed(1)}%`;
}

function FieldGrid({ fields }: { fields: Field[] }) {
  return (
    <div
      style={{
        display: 'grid',
        gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))',
        gap: 8,
      }}
    >
      {fields.map((f) => (
        <div
          key={f.label}
          style={{
            padding: '7px 8px',
            background: 'var(--bg-0)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
          }}
        >
          <div
            style={{
              fontSize: 9,
              fontWeight: 700,
              letterSpacing: '0.08em',
              textTransform: 'uppercase',
              color: 'var(--fg-3)',
            }}
          >
            {f.label}
          </div>
          <div
            className="mono"
            style={{
              marginTop: 3,
              fontSize: 12,
              color: 'var(--fg-0)',
              overflowWrap: 'anywhere',
            }}
          >
            {dash(f.value)}
          </div>
        </div>
      ))}
    </div>
  );
}

function detail(radio: RadioInfoDto, key: string): string | null {
  return radio.details?.[key] ?? null;
}

function RadioInventory({ radios }: { radios: RadioInfoDto[] | null }) {
  if (!radios) {
    return (
      <div style={{ fontSize: 12, lineHeight: 1.4, color: 'var(--fg-2)' }}>
        Waiting for discovery.
      </div>
    );
  }
  if (radios.length === 0) {
    return (
      <div style={{ fontSize: 12, lineHeight: 1.4, color: 'var(--fg-2)' }}>
        No OpenHPSDR radios answered the discovery sweep.
      </div>
    );
  }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      {radios.map((radio) => (
        <div
          key={`${radio.macAddress}:${radio.ipAddress}:${detail(radio, 'protocol') ?? ''}`}
          style={{
            padding: '8px 10px',
            background: 'var(--bg-0)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
          }}
        >
          <div
            style={{
              display: 'flex',
              gap: 8,
              alignItems: 'baseline',
              justifyContent: 'space-between',
              flexWrap: 'wrap',
            }}
          >
            <span style={{ fontSize: 12, fontWeight: 700, color: 'var(--fg-0)' }}>
              {radio.boardId}
            </span>
            <span className="mono" style={{ fontSize: 10, color: 'var(--fg-3)' }}>
              {detail(radio, 'protocol') ?? '-'} {radio.busy ? 'BUSY' : 'IDLE'}
            </span>
          </div>
          <FieldGrid
            fields={[
              { label: 'IP', value: radio.ipAddress },
              { label: 'MAC', value: radio.macAddress },
              { label: 'Firmware', value: radio.firmwareVersion },
              { label: 'Raw Board', value: detail(radio, 'rawBoardId') },
              { label: 'Receivers', value: detail(radio, 'numReceivers') },
              { label: 'Protocol Ver', value: detail(radio, 'protocolSupported') },
              { label: 'Gateware', value: detail(radio, 'gatewareBuild') },
              { label: 'HL2 Minor', value: detail(radio, 'hl2MinorVersion') },
              { label: 'Beta', value: detail(radio, 'betaVersion') },
              { label: 'Mercury 0', value: detail(radio, 'mercuryVersion0') },
              { label: 'Mercury 1', value: detail(radio, 'mercuryVersion1') },
              { label: 'Mercury 2', value: detail(radio, 'mercuryVersion2') },
              { label: 'Mercury 3', value: detail(radio, 'mercuryVersion3') },
              { label: 'Penny', value: detail(radio, 'pennyVersion') },
              { label: 'Metis', value: detail(radio, 'metisVersion') },
              { label: 'Raw Reply', value: detail(radio, 'rawReplyHex') },
            ]}
          />
        </div>
      ))}
    </div>
  );
}

function MappingRows({ items }: { items: HardwareDiagnosticItemDto[] }) {
  if (items.length === 0) return null;
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      {items.map((item) => (
        <div
          key={`${item.field}:${item.status}`}
          style={{
            padding: '8px 10px',
            background: 'var(--bg-0)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
          }}
        >
          <div
            style={{
              display: 'flex',
              alignItems: 'baseline',
              gap: 8,
              flexWrap: 'wrap',
            }}
          >
            <span style={{ fontSize: 12, fontWeight: 700, color: 'var(--fg-0)' }}>
              {item.field}
            </span>
            <span
              className="mono"
              style={{
                fontSize: 10,
                color: item.status === 'decoded' || item.status === 'mapped'
                  ? 'var(--accent)'
                  : 'var(--power)',
              }}
            >
              {item.status}
            </span>
          </div>
          {item.source && (
            <div className="mono" style={{ marginTop: 4, fontSize: 10, color: 'var(--fg-3)' }}>
              {item.source}
            </div>
          )}
          <div style={{ marginTop: 5, fontSize: 11, lineHeight: 1.35, color: 'var(--fg-2)' }}>
            {item.notes}
          </div>
        </div>
      ))}
    </div>
  );
}

type HardwareOpportunity = {
  item: HardwareFeatureSurfaceDto;
  plannedControls: string[];
  liveControls: string[];
  readiness: string;
  priority: number;
  nextStep: string;
};

function plannedControls(item: HardwareFeatureSurfaceDto): string[] {
  return item.candidateControls
    .filter((control) => control.startsWith('planned:'))
    .map((control) => control.slice('planned:'.length));
}

function liveControls(item: HardwareFeatureSurfaceDto): string[] {
  return item.candidateControls.filter((control) => !control.startsWith('planned:'));
}

function featureNextStep(id: string): string {
  switch (id) {
    case 'hardware.inventory.discovery':
      return 'Add a per-radio Hardware/Network profile for NIC selection, fixed IP, discovery speed, subnet behavior, protocol match, and P2 port notification.';
    case 'rx.auto-attenuation.adc-overload':
      return 'Use the live ADC protection endpoint for per-radio presets, overload history, and profile suggestions based on band noise and ADC headroom.';
    case 'pa.telemetry.power-supply':
      return 'Add calibrated PA/supply meters, per-radio conversion constants, warning thresholds, and supply sag alarms.';
    case 'hardware.user-io':
      return 'Expose user ADC/DIN labels, thresholds, debounce, and safe action bindings after each physical line is correlated.';
    case 'hardware.front-panel.leds':
      return 'Mirror the front-panel LED word as a read-only remote diagnostics strip and use it for UI state reconciliation.';
    case 'cw.hardware-keying':
      return 'Add guarded hardware key/PTT configuration with explicit arming, sidetone policy, and external PTT ownership display.';
    case 'hardware.mapping.correlation':
      return 'Turn the marker API into guided capture recipes so a single radio action can be mapped and promoted to a typed setting.';
    case 'rx.signal-intelligence.weak-signal':
      return 'Use the mirrored coherent scene metrics to annotate recordings, compare clients, and gate future server-side weak-signal policy safely.';
    case 'rx.smart-nr.adaptive':
      return 'Correlate Smart NR recommendations with RX-chain health over time so automation can distinguish noise-floor cleanup from ADC/headroom protection.';
    case 'tx.fidelity.spectral-density':
      return 'Add a TX fidelity policy endpoint for target headroom, occupied bandwidth, PureSignal feedback health, and spectral-density warnings tied to station profiles.';
    default:
      return 'Promote the advertised telemetry into a typed setting only after the hardware path is correlated and gated by board capability.';
  }
}

function rankFeature(item: HardwareFeatureSurfaceDto, plannedCount: number): number {
  const status = item.implementationStatus.toLowerCase();
  let score = 0;
  if (status.includes('telemetry-ready')) score += 4;
  if (status.includes('api-ready')) score += 3;
  if (item.userConfigurable) score += 2;
  if (plannedCount > 0) score += 2;
  if (item.safetyClass === 'rx-safe' || item.safetyClass === 'tx-monitoring-only') score += 1;
  if (item.safetyClass.includes('tx-capable')) score -= 1;
  if (item.id === 'rx.auto-attenuation.adc-overload') score += 2;
  if (item.id === 'rx.signal-intelligence.weak-signal') score += 3;
  if (item.id === 'rx.smart-nr.adaptive') score += 3;
  if (item.id === 'tx.fidelity.spectral-density') score += 3;
  if (item.id === 'pa.telemetry.power-supply') score += 1;
  return score;
}

function readinessLabel(item: HardwareFeatureSurfaceDto, plannedCount: number): string {
  const status = item.implementationStatus.toLowerCase();
  if (plannedCount > 0 && (status.includes('telemetry-ready') || status.includes('api-ready'))) {
    return 'ready to wire';
  }
  if (status.includes('telemetry-ready')) return 'telemetry-ready';
  if (status.includes('api-ready')) return 'api-ready';
  return item.implementationStatus || 'candidate';
}

function buildHardwareOpportunities(items: HardwareFeatureSurfaceDto[]): HardwareOpportunity[] {
  return items
    .map((item) => {
      const planned = plannedControls(item);
      return {
        item,
        plannedControls: planned,
        liveControls: liveControls(item),
        readiness: readinessLabel(item, planned.length),
        priority: rankFeature(item, planned.length),
        nextStep: featureNextStep(item.id),
      };
    })
    .filter((row) => row.item.userConfigurable || row.plannedControls.length > 0 || row.item.id === 'hardware.mapping.correlation')
    .sort((a, b) => b.priority - a.priority || a.item.id.localeCompare(b.item.id));
}

function HardwareOpportunityMatrix({ items }: { items: HardwareFeatureSurfaceDto[] }) {
  if (items.length === 0) {
    return (
      <div style={{ fontSize: 12, color: 'var(--fg-2)' }}>
        No hardware feature surfaces have been advertised yet.
      </div>
    );
  }

  const rows = buildHardwareOpportunities(items);
  const plannedApiCount = new Set(rows.flatMap((row) => row.plannedControls)).size;
  const txGuarded = items.filter((item) => item.safetyClass.includes('tx-capable')).length;
  const rxSafe = items.filter((item) => item.safetyClass === 'rx-safe').length;
  const top = rows[0];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      <FieldGrid
        fields={[
          { label: 'Advertised', value: items.length },
          { label: 'Configurable', value: items.filter((item) => item.userConfigurable).length },
          { label: 'Planned APIs', value: plannedApiCount },
          { label: 'RX-safe', value: rxSafe },
          { label: 'TX Guarded', value: txGuarded },
          { label: 'Top Target', value: top?.item.title },
        ]}
      />
      {rows.map((row, index) => (
        <div
          key={row.item.id}
          style={{
            padding: '9px 10px',
            background: 'var(--bg-0)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
          }}
        >
          <div
            style={{
              display: 'flex',
              alignItems: 'baseline',
              justifyContent: 'space-between',
              gap: 8,
              flexWrap: 'wrap',
            }}
          >
            <span style={{ fontSize: 12, fontWeight: 700, color: 'var(--fg-0)' }}>
              {index + 1}. {row.item.title}
            </span>
            <span
              className="mono"
              style={{
                fontSize: 10,
                color: row.item.safetyClass.includes('tx-capable') ? 'var(--tx)' : 'var(--accent)',
              }}
            >
              {row.readiness} / score {row.priority}
            </span>
          </div>
          <FieldGrid
            fields={[
              { label: 'Category', value: row.item.category },
              { label: 'Safety', value: row.item.safetyClass },
              { label: 'Configurable', value: boolLabel(row.item.userConfigurable) },
              { label: 'Planned', value: row.plannedControls.join(', ') || '-' },
            ]}
          />
          <div style={{ marginTop: 7, fontSize: 11, lineHeight: 1.35, color: 'var(--fg-2)' }}>
            {row.nextStep}
          </div>
          <div className="mono" style={{ marginTop: 6, fontSize: 10, color: 'var(--fg-3)' }}>
            live controls: {row.liveControls.join(', ') || '-'}
          </div>
          <div className="mono" style={{ marginTop: 3, fontSize: 10, color: 'var(--fg-3)' }}>
            telemetry: {row.item.telemetryPaths.slice(0, 5).join(', ')}
            {row.item.telemetryPaths.length > 5 ? ` +${row.item.telemetryPaths.length - 5} more` : ''}
          </div>
        </div>
      ))}
    </div>
  );
}

function FeatureSurfaceRows({ items }: { items: HardwareFeatureSurfaceDto[] }) {
  if (items.length === 0) {
    return (
      <div style={{ fontSize: 12, color: 'var(--fg-2)' }}>
        This backend has not advertised hardware feature surfaces yet.
      </div>
    );
  }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      {items.map((item) => (
        <div
          key={item.id}
          style={{
            padding: '8px 10px',
            background: 'var(--bg-0)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
          }}
        >
          <div
            style={{
              display: 'flex',
              alignItems: 'baseline',
              justifyContent: 'space-between',
              gap: 8,
              flexWrap: 'wrap',
            }}
          >
            <span style={{ fontSize: 12, fontWeight: 700, color: 'var(--fg-0)' }}>
              {item.title}
            </span>
            <span className="mono" style={{ fontSize: 10, color: 'var(--accent)' }}>
              {item.implementationStatus}
            </span>
          </div>
          <FieldGrid
            fields={[
              { label: 'ID', value: item.id },
              { label: 'Category', value: item.category },
              { label: 'Configurable', value: boolLabel(item.userConfigurable) },
              { label: 'Safety', value: item.safetyClass },
              { label: 'Source', value: item.source },
            ]}
          />
          <div style={{ marginTop: 7, fontSize: 11, lineHeight: 1.35, color: 'var(--fg-2)' }}>
            {item.notes}
          </div>
          <div className="mono" style={{ marginTop: 6, fontSize: 10, color: 'var(--fg-3)' }}>
            telemetry: {item.telemetryPaths.join(', ') || '-'}
          </div>
          <div className="mono" style={{ marginTop: 3, fontSize: 10, color: 'var(--fg-3)' }}>
            controls: {item.candidateControls.join(', ') || '-'}
          </div>
        </div>
      ))}
    </div>
  );
}

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

function changedBits(bitChangeCounts: number[]): string {
  const bits = bitChangeCounts
    .map((count, bit) => (count > 0 ? bit : null))
    .filter((bit): bit is number => bit !== null);
  return bits.length === 0 ? '-' : bits.join(',');
}

function P2ByteMap({ map }: { map: HardwareByteStreamMapDto }) {
  if (map.samples === 0) {
    return (
      <div style={{ fontSize: 12, color: 'var(--fg-2)' }}>
        No P2 hi-priority packets captured in this mapping window.
      </div>
    );
  }
  const wordRows = map.words.filter((w) => w.known || w.changeCount > 0);
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      <FieldGrid
        fields={[
          { label: 'Stream', value: map.stream },
          { label: 'Samples', value: map.samples },
          { label: 'Length', value: map.length },
          { label: 'Changed Bytes', value: map.changedByteCount },
          { label: 'Changed Words', value: map.changedWordCount },
          { label: 'Started', value: time(map.startedUtc) },
          { label: 'Updated', value: time(map.lastUpdatedUtc) },
          { label: 'Last Payload', value: map.lastHex },
        ]}
      />
      <div style={{ overflowX: 'auto' }}>
        <table style={tableStyle}>
          <thead>
            <tr>
              <th style={thStyle}>Byte</th>
              <th style={thStyle}>Known / Candidate</th>
              <th style={thStyle}>Last</th>
              <th style={thStyle}>Range</th>
              <th style={thStyle}>Delta</th>
              <th style={thStyle}>Bits</th>
            </tr>
          </thead>
          <tbody>
            {map.bytes.map((b) => (
              <tr key={b.offset}>
                <td style={tdStyle} className="mono">{b.hexOffset}</td>
                <td style={tdStyle}>{b.known ?? '-'}</td>
                <td style={tdStyle} className="mono">{hex(b.last, 2)}</td>
                <td style={tdStyle} className="mono">{hex(b.min, 2)}..{hex(b.max, 2)}</td>
                <td style={tdStyle} className="mono">
                  {b.changeCount} / {b.changedMaskHex}
                </td>
                <td style={tdStyle} className="mono">{changedBits(b.bitChangeCounts)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {wordRows.length > 0 && (
        <div style={{ overflowX: 'auto' }}>
          <table style={tableStyle}>
            <thead>
              <tr>
                <th style={thStyle}>Word</th>
                <th style={thStyle}>Known / Candidate</th>
                <th style={thStyle}>Last</th>
                <th style={thStyle}>Range</th>
                <th style={thStyle}>Delta</th>
              </tr>
            </thead>
            <tbody>
              {wordRows.map((w) => (
                <tr key={w.offset}>
                  <td style={tdStyle} className="mono">{w.hexOffset}</td>
                  <td style={tdStyle}>{w.known ?? 'changed overlapping u16'}</td>
                  <td style={tdStyle} className="mono">{adc(w.last)}</td>
                  <td style={tdStyle} className="mono">{adc(w.min)}..{adc(w.max)}</td>
                  <td style={tdStyle} className="mono">{w.changeCount}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function P1Map({ map }: { map: HardwareP1MapDto }) {
  if (map.samples === 0) {
    return (
      <div style={{ fontSize: 12, color: 'var(--fg-2)' }}>
        No Protocol 1 C&amp;C telemetry captured in this mapping window.
      </div>
    );
  }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      <FieldGrid
        fields={[
          { label: 'Stream', value: map.stream },
          { label: 'Samples', value: map.samples },
          { label: 'Addresses', value: map.addresses.length },
          { label: 'Started', value: time(map.startedUtc) },
          { label: 'Updated', value: time(map.lastUpdatedUtc) },
        ]}
      />
      <div style={{ overflowX: 'auto' }}>
        <table style={tableStyle}>
          <thead>
            <tr>
              <th style={thStyle}>C0</th>
              <th style={thStyle}>AIN0</th>
              <th style={thStyle}>AIN1</th>
              <th style={thStyle}>Last</th>
              <th style={thStyle}>Range</th>
              <th style={thStyle}>Delta</th>
            </tr>
          </thead>
          <tbody>
            {map.addresses.map((a) => (
              <tr key={a.hexAddress}>
                <td style={tdStyle} className="mono">{a.hexAddress}</td>
                <td style={tdStyle}>{a.knownAin0 ?? '-'}</td>
                <td style={tdStyle}>{a.knownAin1 ?? '-'}</td>
                <td style={tdStyle} className="mono">{adc(a.lastAin0)} / {adc(a.lastAin1)}</td>
                <td style={tdStyle} className="mono">
                  {adc(a.minAin0)}..{adc(a.maxAin0)} / {adc(a.minAin1)}..{adc(a.maxAin1)}
                </td>
                <td style={tdStyle} className="mono">
                  {a.ain0ChangeCount} / {a.ain1ChangeCount}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {map.rawGap && (
        <div style={{ fontSize: 11, lineHeight: 1.35, color: 'var(--fg-3)' }}>
          {map.rawGap}
        </div>
      )}
    </div>
  );
}

function signed(v: number | null | undefined): string {
  if (v === null || v === undefined) return '-';
  return v > 0 ? `+${v}` : String(v);
}

function bitList(bits: number[]): string {
  return bits.length === 0 ? '-' : bits.join(',');
}

function MarkerTimeline({ markers }: { markers: HardwareMappingMarkerDto[] }) {
  if (markers.length === 0) {
    return (
      <div style={{ fontSize: 12, color: 'var(--fg-2)' }}>
        No capture markers in this mapping window.
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      {markers.map((marker) => {
        const delta = marker.sincePrevious;
        const byteRows = delta.p2ChangedBytes.slice(0, 14);
        const wordRows = delta.p2ChangedWords.slice(0, 10);
        const p1Rows = delta.p1ChangedAddresses.slice(0, 8);
        return (
          <div
            key={marker.id}
            style={{
              padding: '8px 10px',
              background: 'var(--bg-0)',
              border: '1px solid var(--panel-border)',
              borderRadius: 'var(--r-sm)',
            }}
          >
            <div
              style={{
                display: 'flex',
                alignItems: 'baseline',
                gap: 8,
                justifyContent: 'space-between',
                flexWrap: 'wrap',
              }}
            >
              <span style={{ fontSize: 12, fontWeight: 700, color: 'var(--fg-0)' }}>
                {marker.label}
              </span>
              <span className="mono" style={{ fontSize: 10, color: 'var(--fg-3)' }}>
                #{marker.id} {time(marker.createdUtc)}
              </span>
            </div>
            {marker.notes && (
              <div style={{ marginTop: 4, fontSize: 11, color: 'var(--fg-2)' }}>
                {marker.notes}
              </div>
            )}
            <FieldGrid
              fields={[
                { label: 'Previous', value: delta.baseline ? 'baseline' : delta.previousLabel },
                { label: 'Protocol', value: marker.activeProtocol },
                { label: 'P2 Samples', value: delta.p2SampleDelta },
                { label: 'P1 Samples', value: delta.p1SampleDelta },
                { label: 'P2 Bytes', value: delta.p2ChangedBytes.length },
                { label: 'P2 Words', value: delta.p2ChangedWords.length },
                { label: 'P1 C0', value: delta.p1ChangedAddresses.length },
                { label: 'Endpoint', value: marker.endpoint },
              ]}
            />
            {byteRows.length > 0 && (
              <div style={{ overflowX: 'auto', marginTop: 8 }}>
                <table style={tableStyle}>
                  <thead>
                    <tr>
                      <th style={thStyle}>Byte</th>
                      <th style={thStyle}>Known / Candidate</th>
                      <th style={thStyle}>Prev</th>
                      <th style={thStyle}>Now</th>
                      <th style={thStyle}>XOR</th>
                      <th style={thStyle}>Interval</th>
                      <th style={thStyle}>Bits</th>
                    </tr>
                  </thead>
                  <tbody>
                    {byteRows.map((b) => (
                      <tr key={b.offset}>
                        <td style={tdStyle} className="mono">{b.hexOffset}</td>
                        <td style={tdStyle}>{b.known ?? '-'}</td>
                        <td style={tdStyle} className="mono">{b.previousHex}</td>
                        <td style={tdStyle} className="mono">{b.currentHex}</td>
                        <td style={tdStyle} className="mono">{b.xorMaskHex}</td>
                        <td style={tdStyle} className="mono">{b.intervalChangeCount}</td>
                        <td style={tdStyle} className="mono">{bitList(b.intervalChangedBits)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            {wordRows.length > 0 && (
              <div style={{ overflowX: 'auto', marginTop: 8 }}>
                <table style={tableStyle}>
                  <thead>
                    <tr>
                      <th style={thStyle}>Word</th>
                      <th style={thStyle}>Known / Candidate</th>
                      <th style={thStyle}>Prev</th>
                      <th style={thStyle}>Now</th>
                      <th style={thStyle}>Delta</th>
                      <th style={thStyle}>Interval</th>
                    </tr>
                  </thead>
                  <tbody>
                    {wordRows.map((w) => (
                      <tr key={w.offset}>
                        <td style={tdStyle} className="mono">{w.hexOffset}</td>
                        <td style={tdStyle}>{w.known ?? 'changed overlapping u16'}</td>
                        <td style={tdStyle} className="mono">{w.previousHex}</td>
                        <td style={tdStyle} className="mono">{w.currentHex}</td>
                        <td style={tdStyle} className="mono">{signed(w.valueDelta)}</td>
                        <td style={tdStyle} className="mono">{w.intervalChangeCount}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            {p1Rows.length > 0 && (
              <div style={{ overflowX: 'auto', marginTop: 8 }}>
                <table style={tableStyle}>
                  <thead>
                    <tr>
                      <th style={thStyle}>C0</th>
                      <th style={thStyle}>AIN0</th>
                      <th style={thStyle}>AIN1</th>
                      <th style={thStyle}>Delta</th>
                      <th style={thStyle}>Interval</th>
                    </tr>
                  </thead>
                  <tbody>
                    {p1Rows.map((p) => (
                      <tr key={p.c0Address}>
                        <td style={tdStyle} className="mono">{p.hexAddress}</td>
                        <td style={tdStyle}>{p.knownAin0 ?? '-'}</td>
                        <td style={tdStyle}>{p.knownAin1 ?? '-'}</td>
                        <td style={tdStyle} className="mono">
                          {signed(p.ain0Delta)} / {signed(p.ain1Delta)}
                        </td>
                        <td style={tdStyle} className="mono">
                          {p.ain0ChangeCountDelta} / {p.ain1ChangeCountDelta}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}

export function HardwareDiagnosticsPanel() {
  const [diag, setDiag] = useState<HardwareDiagnosticsDto | null>(null);
  const [radios, setRadios] = useState<RadioInfoDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [inventoryError, setInventoryError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [scanning, setScanning] = useState(false);
  const [resettingMap, setResettingMap] = useState(false);
  const [marking, setMarking] = useState(false);
  const [markerLabel, setMarkerLabel] = useState('');
  const [markerNotes, setMarkerNotes] = useState('');

  const load = useCallback(async (signal?: AbortSignal) => {
    setBusy(true);
    try {
      const next = await fetchHardwareDiagnostics(signal);
      setDiag(next);
      setError(null);
    } catch (err) {
      if ((err as DOMException).name !== 'AbortError') {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setBusy(false);
    }
  }, []);

  const scan = useCallback(async (signal?: AbortSignal) => {
    setScanning(true);
    try {
      const next = await fetchRadios(signal);
      setRadios(next);
      setInventoryError(null);
    } catch (err) {
      if ((err as DOMException).name !== 'AbortError') {
        setInventoryError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setScanning(false);
    }
  }, []);

  const resetMap = useCallback(async (signal?: AbortSignal) => {
    setResettingMap(true);
    try {
      const next = await resetHardwareDiagnosticsMap(signal);
      setDiag(next);
      setError(null);
    } catch (err) {
      if ((err as DOMException).name !== 'AbortError') {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setResettingMap(false);
    }
  }, []);

  const createMarker = useCallback(async (label?: string, signal?: AbortSignal) => {
    const effectiveLabel = (label ?? markerLabel).trim();
    setMarking(true);
    try {
      const next = await createHardwareDiagnosticsMarker(
        effectiveLabel || `Marker ${(diag?.mapping.markers.length ?? 0) + 1}`,
        markerNotes.trim() || undefined,
        signal,
      );
      setDiag(next);
      setMarkerLabel('');
      setMarkerNotes('');
      setError(null);
    } catch (err) {
      if ((err as DOMException).name !== 'AbortError') {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setMarking(false);
    }
  }, [diag?.mapping.markers.length, markerLabel, markerNotes]);

  useEffect(() => {
    const ac = new AbortController();
    void load(ac.signal);
    const id = window.setInterval(() => void load(), 1000);
    return () => {
      ac.abort();
      window.clearInterval(id);
    };
  }, [load]);

  useEffect(() => {
    const ac = new AbortController();
    void scan(ac.signal);
    const id = window.setInterval(() => void scan(), INVENTORY_INTERVAL_MS);
    return () => {
      ac.abort();
      window.clearInterval(id);
    };
  }, [scan]);

  const caps = diag?.capabilities;
  const connectionFields: Field[] = [
    { label: 'Status', value: diag?.connectionStatus },
    { label: 'Protocol', value: diag?.activeProtocol },
    { label: 'Endpoint', value: diag?.endpoint },
    { label: 'Detected', value: diag?.connectedBoard },
    { label: 'Effective', value: diag?.effectiveBoard },
    { label: '0x0A Variant', value: diag?.orionMkIIVariant },
    { label: 'Sample Rate', value: diag?.sampleRate },
    { label: 'Mode', value: diag?.mode },
  ];

  const capabilityFields: Field[] = [
    { label: 'RX ADCs', value: caps?.rxAdcCount },
    { label: 'MKII BPF', value: boolLabel(caps?.mkiiBpf) },
    { label: 'ADC Supply', value: caps ? `${caps.adcSupplyMv} mV` : null },
    { label: 'L/R Swap', value: boolLabel(caps?.lrAudioSwap) },
    { label: 'Volts', value: boolLabel(caps?.hasVolts) },
    { label: 'Amps', value: boolLabel(caps?.hasAmps) },
    { label: 'Audio Amp', value: boolLabel(caps?.hasAudioAmplifier) },
    { label: 'RX2 Step ATT', value: boolLabel(caps?.hasSteppedAttenuationRx2) },
    { label: 'Path Illustrator', value: boolLabel(caps?.supportsPathIllustrator) },
    { label: 'Max Watts', value: caps?.maxPowerWatts },
    { label: 'HL2 Options', value: boolLabel(caps?.hasHl2OptionalToggles) },
    { label: 'Anvelina DX OC', value: boolLabel(caps?.supportsAnvelinaDxOc) },
  ];

  const dsp = diag?.dsp;
  const dspFields: Field[] = [
    { label: 'Engine', value: dsp?.engineKind },
    { label: 'Runtime', value: dsp?.engine },
    { label: 'Readiness', value: dsp?.readiness },
    { label: 'WDSP Active', value: boolLabel(dsp?.wdspActive) },
    { label: 'Wisdom', value: dsp?.wdspWisdomPhase },
    { label: 'Channel', value: dsp?.channelId },
    { label: 'DSP Rate', value: dsp?.sampleRateHz },
    { label: 'Display Width', value: dsp?.displayWidth },
    { label: 'Tick Hz', value: dsp?.tickRateHz },
    { label: 'Audio Out', value: dsp?.audioOutputRateHz },
    { label: 'TX Block', value: dsp?.txBlockSamples },
    { label: 'TX IQ Out', value: dsp?.txOutputSamples },
    { label: 'TX Monitor', value: boolLabel(dsp?.txMonitorRequested) },
    { label: 'RX Sink', value: boolLabel(dsp?.rxSinkAttached) },
    { label: 'Audio Sinks', value: dsp?.audioSinkCount },
    { label: 'Monitor Backlog', value: dsp?.monitorBacklogSamples },
    { label: 'Wisdom Status', value: dsp?.wdspWisdomStatus },
  ];

  const scene = diag?.frontendDspScene;
  const sceneFields: Field[] = [
    { label: 'Available', value: boolLabel(scene?.available) },
    { label: 'Status', value: scene?.status },
    { label: 'Fresh', value: boolLabel(scene?.fresh) },
    { label: 'Stale', value: boolLabel(scene?.stale) },
    { label: 'Age', value: age(scene?.ageMs) },
    { label: 'Source Age', value: age(scene?.sourceAgeMs) },
    { label: 'Source Skew', value: age(scene?.sourceClockSkewMs) },
    { label: 'Diagnostic Action', value: scene?.diagnosticRecommendation },
    { label: 'Client', value: scene?.sourceClientId },
    { label: 'Mode', value: scene?.mode },
    { label: 'Signal Profile', value: scene?.signalProfile },
    { label: 'Signal Reason', value: scene?.signalReason },
    { label: 'Smart NR', value: scene?.smartNrProfile },
    { label: 'Smart NR Reason', value: scene?.smartNrReason },
    { label: 'Smart NR Action', value: scene?.smartNrRecommendation },
    { label: 'RX Hold', value: boolLabel(scene?.smartNrHeldByRxChain) },
    { label: 'RX Chain', value: scene?.smartNrRxChainLabel },
    { label: 'Max SNR', value: db(scene?.maxSnrDb) },
    { label: 'Coherent Max', value: db(scene?.coherentMaxSnrDb) },
    { label: 'Occupied', value: pct(scene?.occupiedPct) },
    { label: 'Coherent Occ', value: pct(scene?.coherentOccupiedPct) },
    { label: 'Impulsive', value: pct(scene?.impulsivePct) },
    { label: 'Peaks', value: scene?.peakCount },
    { label: 'Coherent Peaks', value: scene?.coherentPeakCount },
    { label: 'Subthreshold Ridge', value: boolLabel(scene?.coherentSubthresholdSignal) },
    { label: 'Source Updated', value: time(scene?.sourceAtUtc) },
    { label: 'Updated', value: time(scene?.atUtc) },
  ];

  const p1 = diag?.p1;
  const p1Fields: Field[] = [
    { label: 'Packets', value: p1?.packets },
    { label: 'Updated', value: time(p1?.lastUpdatedUtc) },
    { label: 'Last C0', value: hex(p1?.lastC0Address, 2) },
    { label: 'AIN0', value: adc(p1?.lastAin0) },
    { label: 'AIN1', value: adc(p1?.lastAin1) },
    { label: 'Exciter', value: adc(p1?.exciterAdc) },
    { label: 'Forward', value: adc(p1?.fwdAdc) },
    { label: 'Reverse', value: adc(p1?.revAdc) },
    { label: 'User ADC0', value: adc(p1?.userAdc0) },
    { label: 'User ADC1', value: adc(p1?.userAdc1) },
    { label: 'Supply', value: adc(p1?.supplyVoltsAdc) },
    { label: 'ADC Overload', value: hex(p1?.adcOverloadBits, 2) },
    { label: 'HW PTT', value: boolLabel(p1?.hardwarePtt) },
    { label: 'CW Key', value: boolLabel(p1?.cwKeyDown) },
  ];

  const p2 = diag?.p2;
  const p2Fields: Field[] = [
    { label: 'Packets', value: p2?.packets },
    { label: 'Updated', value: time(p2?.lastUpdatedUtc) },
    { label: 'PTT In', value: boolLabel(p2?.pttIn) },
    { label: 'Dot', value: boolLabel(p2?.dotIn) },
    { label: 'Dash', value: boolLabel(p2?.dashIn) },
    { label: 'PLL Lock', value: boolLabel(p2?.pllLocked) },
    { label: 'Sidetone', value: boolLabel(p2?.sidetoneActive) },
    { label: 'ADC Overload', value: hex(p2?.adcOverloadBits, 2) },
    { label: 'Exciter', value: adc(p2?.exciterAdc) },
    { label: 'Forward', value: adc(p2?.fwdAdc) },
    { label: 'Reverse', value: adc(p2?.revAdc) },
    { label: 'ADC0 Max', value: adc(p2?.adc0MaxMagnitude) },
    { label: 'ADC1 Max', value: adc(p2?.adc1MaxMagnitude) },
    { label: 'ADC0 Over Max', value: adc(p2?.adc0MaxMagnitudeAtOverload) },
    { label: 'ADC1 Over Max', value: adc(p2?.adc1MaxMagnitudeAtOverload) },
    { label: 'Supply', value: adc(p2?.supplyVoltsAdc) },
    { label: 'User ADC0', value: adc(p2?.userAdc0) },
    { label: 'User ADC1', value: adc(p2?.userAdc1) },
    { label: 'User ADC2', value: adc(p2?.userAdc2) },
    { label: 'User ADC3', value: adc(p2?.userAdc3) },
    { label: 'User DIN', value: hex(p2?.userDigitalIn, 2) },
    { label: 'HW LEDs', value: hex(p2?.hardwareLeds, 4) },
  ];

  return (
    <div className="ps-shell">
      <div className="ps-card">
        <h4>
          Hardware Diagnostics
          <span className="ps-card-hint">
            {busy ? 'refreshing' : diag ? `updated ${time(diag.generatedUtc)}` : 'loading'}
          </span>
        </h4>
        <div style={{ display: 'flex', gap: 8, marginBottom: 10 }}>
          <button type="button" className="btn sm" onClick={() => void load()} disabled={busy}>
            REFRESH
          </button>
          {error && (
            <span style={{ alignSelf: 'center', fontSize: 11, color: 'var(--tx)' }}>
              {error}
            </span>
          )}
        </div>
        <FieldGrid fields={connectionFields} />
      </div>

      <div className="ps-card">
        <h4>
          Network Inventory
          <span className="ps-card-hint">
            {scanning ? 'scanning' : `${radios?.length ?? 0} discovered`}
          </span>
        </h4>
        <div style={{ display: 'flex', gap: 8, marginBottom: 10 }}>
          <button type="button" className="btn sm" onClick={() => void scan()} disabled={scanning}>
            SCAN
          </button>
          {inventoryError && (
            <span style={{ alignSelf: 'center', fontSize: 11, color: 'var(--tx)' }}>
              {inventoryError}
            </span>
          )}
        </div>
        <RadioInventory radios={radios} />
      </div>

      <div className="ps-card">
        <h4>
          Board Capabilities
          <span className="ps-card-hint">Thetis-derived static map</span>
        </h4>
        <FieldGrid fields={capabilityFields} />
      </div>

      <div className="ps-card">
        <h4>
          DSP Runtime
          <span className="ps-card-hint">engine, timing, TX path, WDSP readiness</span>
        </h4>
        <FieldGrid fields={dspFields} />
      </div>

      <div className="ps-card">
        <h4>
          DSP Scene Intelligence
          <span className="ps-card-hint">frontend Signal Intelligence and Smart NR evidence</span>
        </h4>
        <FieldGrid fields={sceneFields} />
      </div>

      <div className="ps-card">
        <h4>
          Next-Level Opportunities
          <span className="ps-card-hint">ranked from live diagnostics</span>
        </h4>
        <HardwareOpportunityMatrix items={diag?.featureSurfaces ?? []} />
      </div>

      <div className="ps-card">
        <h4>
          Protocol 1 Live
          <span className="ps-card-hint">C&amp;C echo telemetry</span>
        </h4>
        <FieldGrid fields={p1Fields} />
      </div>

      <div className="ps-card">
        <h4>
          Protocol 2 Live
          <span className="ps-card-hint">hi-priority status</span>
        </h4>
        <FieldGrid fields={p2Fields} />
      </div>

      <div className="ps-card">
        <h4>
          Mapping Capture
          <span className="ps-card-hint">
            raw status byte map · {diag?.mapping.markers.length ?? 0} markers
          </span>
        </h4>
        <div style={{ display: 'flex', gap: 8, marginBottom: 10, flexWrap: 'wrap' }}>
          <button type="button" className="btn sm" onClick={() => void resetMap()} disabled={resettingMap}>
            RESET MAP
          </button>
          <button type="button" className="btn sm" onClick={() => void load()} disabled={busy}>
            REFRESH
          </button>
          <button type="button" className="btn sm" onClick={() => void createMarker('Baseline')} disabled={marking}>
            BASELINE
          </button>
          <button type="button" className="btn sm" onClick={() => void createMarker('After RX Action')} disabled={marking}>
            RX ACTION
          </button>
          <button type="button" className="btn sm" onClick={() => void createMarker()} disabled={marking}>
            MARK
          </button>
        </div>
        <div
          style={{
            display: 'grid',
            gridTemplateColumns: 'minmax(160px, 0.7fr) minmax(220px, 1.3fr)',
            gap: 8,
            marginBottom: 12,
          }}
        >
          <input
            value={markerLabel}
            onChange={(event) => setMarkerLabel(event.target.value)}
            placeholder="Marker label"
            maxLength={80}
            style={{
              minWidth: 0,
              padding: '6px 8px',
              background: 'var(--bg-0)',
              color: 'var(--fg-0)',
              border: '1px solid var(--panel-border)',
              borderRadius: 'var(--r-sm)',
              fontSize: 12,
            }}
          />
          <input
            value={markerNotes}
            onChange={(event) => setMarkerNotes(event.target.value)}
            placeholder="Notes"
            maxLength={220}
            style={{
              minWidth: 0,
              padding: '6px 8px',
              background: 'var(--bg-0)',
              color: 'var(--fg-0)',
              border: '1px solid var(--panel-border)',
              borderRadius: 'var(--r-sm)',
              fontSize: 12,
            }}
          />
        </div>
        {diag?.mapping && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
            <MarkerTimeline markers={diag.mapping.markers} />
            <P2ByteMap map={diag.mapping.p2HiPriority} />
            <P1Map map={diag.mapping.p1} />
          </div>
        )}
      </div>

      <div className="ps-card">
        <h4>
          Reference Map
          <span className="ps-card-hint">Thetis parity anchors</span>
        </h4>
        <MappingRows items={diag?.referenceMap ?? []} />
      </div>

      <div className="ps-card">
        <h4>
          Feature Surfaces
          <span className="ps-card-hint">API hooks for future controls</span>
        </h4>
        <FeatureSurfaceRows items={diag?.featureSurfaces ?? []} />
      </div>

      <div className="ps-card">
        <h4>
          Settings Candidates
          <span className="ps-card-hint">configurable follow-up surfaces</span>
        </h4>
        <MappingRows items={diag?.candidateSettings ?? []} />
      </div>
    </div>
  );
}
