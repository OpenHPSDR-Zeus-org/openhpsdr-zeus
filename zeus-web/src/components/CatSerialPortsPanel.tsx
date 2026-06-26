// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useEffect, useState } from 'react';
import { useCatSerialStore } from '../state/cat-serial-store';
import {
  CAT_SERIAL_BAUD_RATES,
  CAT_SERIAL_PARITIES,
  CAT_SERIAL_DATA_BITS,
  CAT_SERIAL_STOP_BITS,
  type CatSerialPortConfig,
  type CatSerialPortStatus,
} from '../api/catSerial';

const fieldStyle: React.CSSProperties = {
  padding: '5px 7px',
  fontSize: 12,
  fontFamily: 'monospace',
  background: 'var(--bg-0)',
  border: '1px solid var(--panel-border)',
  borderRadius: 'var(--r-sm)',
  color: 'var(--fg-0)',
};

const labelStyle: React.CSSProperties = {
  fontSize: 10,
  fontWeight: 600,
  letterSpacing: '0.04em',
  color: 'var(--fg-2)',
};

function StatusDot({ live }: { live: CatSerialPortStatus | undefined }) {
  const color = live?.error ? 'var(--tx)' : live?.open ? 'var(--accent)' : 'var(--fg-3)';
  const text = live?.error ? 'Error' : live?.open ? 'Open' : 'Closed';
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
      <span style={{ width: 8, height: 8, borderRadius: '50%', background: color, flexShrink: 0 }} />
      <span style={{ fontSize: 11, color: 'var(--fg-2)' }}>{text}</span>
    </span>
  );
}

export function CatSerialPortsPanel() {
  const config = useCatSerialStore((s) => s.config);
  const status = useCatSerialStore((s) => s.status);
  const testingIndex = useCatSerialStore((s) => s.testingIndex);
  const lastTest = useCatSerialStore((s) => s.lastTest);
  const saveConfig = useCatSerialStore((s) => s.saveConfig);
  const test = useCatSerialStore((s) => s.test);

  const [ports, setPorts] = useState<CatSerialPortConfig[]>(config);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  useEffect(() => {
    setPorts(config);
  }, [config]);

  const availablePorts = status?.availablePorts ?? [];

  function updatePort(i: number, patch: Partial<CatSerialPortConfig>) {
    setPorts((prev) => prev.map((p, idx) => (idx === i ? { ...p, ...patch } : p)));
  }

  async function onSave(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true);
    setSaveError(null);
    try {
      await saveConfig(ports);
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : 'Save failed');
    } finally {
      setSaving(false);
    }
  }

  return (
    <div style={{ maxWidth: 700 }}>
      <h3
        style={{
          margin: '0 0 14px',
          fontSize: 11,
          fontWeight: 700,
          letterSpacing: '0.12em',
          textTransform: 'uppercase',
          color: 'var(--fg-2)',
        }}
      >
        CAT SERIAL PORTS
      </h3>

      <div
        style={{
          padding: 12,
          marginBottom: 16,
          fontSize: 11,
          lineHeight: 1.5,
          color: 'var(--fg-2)',
          background: 'var(--bg-0)',
          border: '1px solid var(--panel-border)',
          borderRadius: 'var(--r-sm)',
        }}
      >
        Four serial CAT ports (Kenwood TS-2000), the same command set as CAT-over-TCP above. Point
        a logger or digimode app at a virtual serial pair — <strong>com0com</strong> on Windows,{' '}
        <strong>socat</strong> on macOS/Linux — and enter Zeus's end of the pair below. The port
        name is free-form (<span style={{ fontFamily: 'monospace' }}>COM5</span>,{' '}
        <span style={{ fontFamily: 'monospace' }}>/dev/cu.usbserial-1</span>); virtual pairs aren't
        auto-listed. Changes apply immediately — no restart.
      </div>

      <form onSubmit={onSave} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        {ports.map((port, i) => {
          const live = status?.ports[i];
          const isTesting = testingIndex === i;
          const testResult = lastTest && lastTest.index === i ? lastTest.result : null;
          return (
            <fieldset
              key={i}
              style={{
                margin: 0,
                padding: 12,
                border: '1px solid var(--panel-border)',
                borderRadius: 'var(--r-sm)',
                background: 'var(--bg-1)',
                display: 'flex',
                flexDirection: 'column',
                gap: 10,
              }}
            >
              <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                <label style={{ display: 'flex', alignItems: 'center', gap: 8, flex: 1 }}>
                  <input
                    type="checkbox"
                    checked={port.enabled}
                    onChange={(e) => updatePort(i, { enabled: e.target.checked })}
                    style={{ accentColor: 'var(--accent)' }}
                  />
                  <span style={{ fontSize: 12, fontWeight: 700, color: 'var(--fg-1)' }}>
                    CAT {i + 1}
                  </span>
                </label>
                {port.enabled && <StatusDot live={live} />}
              </div>

              <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                <span style={labelStyle}>PORT / DEVICE</span>
                <input
                  type="text"
                  list={`cat-serial-ports-${i}`}
                  value={port.portName}
                  onChange={(e) => updatePort(i, { portName: e.target.value })}
                  spellCheck={false}
                  placeholder="COM5  ·  /dev/cu.usbserial-1  ·  /dev/ttys013"
                  style={fieldStyle}
                />
                {availablePorts.length > 0 && (
                  <datalist id={`cat-serial-ports-${i}`}>
                    {availablePorts.map((p) => (
                      <option key={p} value={p} />
                    ))}
                  </datalist>
                )}
              </label>

              <div style={{ display: 'flex', flexWrap: 'wrap', gap: 10 }}>
                <label style={{ display: 'flex', flexDirection: 'column', gap: 4, flex: '1 1 120px' }}>
                  <span style={labelStyle}>BAUD</span>
                  <select
                    value={port.baudRate}
                    onChange={(e) => updatePort(i, { baudRate: Number(e.target.value) })}
                    style={fieldStyle}
                  >
                    {CAT_SERIAL_BAUD_RATES.map((b) => (
                      <option key={b} value={b}>
                        {b}
                      </option>
                    ))}
                  </select>
                </label>

                <label style={{ display: 'flex', flexDirection: 'column', gap: 4, flex: '1 1 90px' }}>
                  <span style={labelStyle}>PARITY</span>
                  <select
                    value={port.parity}
                    onChange={(e) => updatePort(i, { parity: e.target.value })}
                    style={fieldStyle}
                  >
                    {CAT_SERIAL_PARITIES.map((p) => (
                      <option key={p} value={p}>
                        {p}
                      </option>
                    ))}
                  </select>
                </label>

                <label style={{ display: 'flex', flexDirection: 'column', gap: 4, flex: '1 1 70px' }}>
                  <span style={labelStyle}>DATA</span>
                  <select
                    value={port.dataBits}
                    onChange={(e) => updatePort(i, { dataBits: Number(e.target.value) })}
                    style={fieldStyle}
                  >
                    {CAT_SERIAL_DATA_BITS.map((d) => (
                      <option key={d} value={d}>
                        {d}
                      </option>
                    ))}
                  </select>
                </label>

                <label style={{ display: 'flex', flexDirection: 'column', gap: 4, flex: '1 1 70px' }}>
                  <span style={labelStyle}>STOP</span>
                  <select
                    value={port.stopBits}
                    onChange={(e) => updatePort(i, { stopBits: e.target.value })}
                    style={fieldStyle}
                  >
                    {CAT_SERIAL_STOP_BITS.map((s) => (
                      <option key={s.value} value={s.value}>
                        {s.label}
                      </option>
                    ))}
                  </select>
                </label>
              </div>

              {live?.error && port.enabled && (
                <div style={{ fontSize: 11, color: 'var(--tx)' }}>{live.error}</div>
              )}

              {testResult && (
                <div style={{ fontSize: 11, color: testResult.ok ? 'var(--accent)' : 'var(--tx)' }}>
                  {testResult.ok
                    ? `✓ ${port.portName || 'port'} opened OK`
                    : `✗ ${testResult.error ?? 'test failed'}`}
                </div>
              )}

              <div>
                <button
                  type="button"
                  className="btn sm"
                  disabled={isTesting || !port.portName.trim()}
                  onClick={() => test(i, port)}
                >
                  {isTesting ? 'TESTING…' : 'TEST PORT'}
                </button>
              </div>
            </fieldset>
          );
        })}

        {saveError && (
          <div
            style={{
              padding: 10,
              fontSize: 12,
              color: 'var(--tx)',
              background: 'rgba(230, 58, 43, 0.1)',
              border: '1px solid var(--tx)',
              borderRadius: 'var(--r-sm)',
            }}
          >
            ✗ Save failed: {saveError}
          </div>
        )}

        <div style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
          <span style={{ flex: 1, fontSize: 10, color: 'var(--fg-3)' }}>
            CAT grants full TX control with no authentication — only expose serial ports to software
            you trust.
          </span>
          <button type="submit" disabled={saving} className="btn sm active">
            {saving ? 'SAVING…' : 'SAVE'}
          </button>
        </div>
      </form>
    </div>
  );
}
