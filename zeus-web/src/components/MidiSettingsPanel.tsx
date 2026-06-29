// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useEffect, useMemo, useState } from 'react';
import { useMidiStore } from '../state/midi-store';
import type { MidiCommandInfo, MidiControlType } from '../api/midi';

const SECTION_TITLE: React.CSSProperties = {
  margin: '0 0 10px',
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: '0.12em',
  textTransform: 'uppercase',
  color: 'var(--fg-2)',
};

const CARD: React.CSSProperties = {
  padding: 12,
  marginBottom: 16,
  background: 'var(--bg-0)',
  border: '1px solid var(--panel-border)',
  borderRadius: 'var(--r-sm)',
};

const INPUT: React.CSSProperties = {
  padding: '6px 8px',
  fontSize: 12,
  background: 'var(--bg-0)',
  border: '1px solid var(--panel-border)',
  borderRadius: 'var(--r-sm)',
  color: 'var(--fg-0)',
};

function CommandSelect({
  commands,
  value,
  onChange,
  id,
}: {
  commands: MidiCommandInfo[];
  value: string;
  onChange: (cmd: string) => void;
  id?: string;
}) {
  return (
    <select
      id={id}
      value={value}
      onChange={(e) => onChange(e.target.value)}
      style={{ ...INPUT, minWidth: 220 }}
    >
      <option value="">— select command —</option>
      {commands.map((c) => (
        <option key={c.command} value={c.command}>
          {c.label} {c.supported ? '' : '(parity)'}
        </option>
      ))}
    </select>
  );
}

export function MidiSettingsPanel() {
  const config = useMidiStore((s) => s.config);
  const status = useMidiStore((s) => s.status);
  const commands = useMidiStore((s) => s.commands);
  const lastLearn = useMidiStore((s) => s.lastLearn);
  const refreshStatus = useMidiStore((s) => s.refreshStatus);
  const refreshCommands = useMidiStore((s) => s.refreshCommands);
  const refreshConfig = useMidiStore((s) => s.refreshConfig);
  const setEnabled = useMidiStore((s) => s.setEnabled);
  const upsertMapping = useMidiStore((s) => s.upsertMapping);
  const removeMapping = useMidiStore((s) => s.removeMapping);
  const upsertStreamDeckMapping = useMidiStore((s) => s.upsertStreamDeckMapping);
  const removeStreamDeckMapping = useMidiStore((s) => s.removeStreamDeckMapping);
  const startLearn = useMidiStore((s) => s.startLearn);
  const stopLearn = useMidiStore((s) => s.stopLearn);

  const [search, setSearch] = useState('');
  const [learnCommand, setLearnCommand] = useState('');
  const [sdCommand, setSdCommand] = useState('');
  const [selectedKey, setSelectedKey] = useState<{ serial: string; index: number } | null>(null);

  useEffect(() => {
    void refreshStatus();
    void refreshCommands();
    void refreshConfig();
    const id = window.setInterval(() => void refreshStatus(), 2000);
    return () => window.clearInterval(id);
  }, [refreshStatus, refreshCommands, refreshConfig]);

  const learning = status?.learning ?? false;
  const midiAvailable = status?.midiEngineAvailable ?? false;
  const sdAvailable = status?.streamDeckEngineAvailable ?? false;
  const midiDevices = status?.midiDevices ?? [];
  const sdDevices = status?.streamDeckDevices ?? [];

  const commandLabel = useMemo(() => {
    const m = new Map<string, MidiCommandInfo>();
    for (const c of commands) m.set(c.command, c);
    return m;
  }, [commands]);

  const filteredCommands = useMemo(() => {
    const q = search.trim().toLowerCase();
    if (!q) return commands;
    return commands.filter(
      (c) => c.command.toLowerCase().includes(q) || c.label.toLowerCase().includes(q),
    );
  }, [commands, search]);

  function inferControlType(): MidiControlType {
    if (lastLearn) return lastLearn.controlType;
    const info = commandLabel.get(learnCommand);
    return info?.controlType ?? 'Button';
  }

  async function bindLearned() {
    if (!lastLearn || !learnCommand) return;
    const info = commandLabel.get(learnCommand);
    await upsertMapping({
      deviceName: lastLearn.deviceName,
      controlId: lastLearn.controlId,
      controlType: inferControlType(),
      command: learnCommand,
      min: 0,
      max: 127,
      toggle: info?.isToggle ?? false,
    });
    setLearnCommand('');
  }

  async function bindStreamDeckKey() {
    if (!selectedKey || !sdCommand) return;
    await upsertStreamDeckMapping({
      serial: selectedKey.serial,
      buttonIndex: selectedKey.index,
      command: sdCommand,
    });
    setSelectedKey(null);
    setSdCommand('');
  }

  const sdMappingFor = (serial: string, index: number) =>
    config.bindings.streamDeckMappings.find((m) => m.serial === serial && m.buttonIndex === index);

  return (
    <div style={{ maxWidth: 760 }}>
      <h3 style={SECTION_TITLE}>MIDI Controller &amp; Stream Deck</h3>

      <div style={{ ...CARD, fontSize: 11, lineHeight: 1.5, color: 'var(--fg-2)' }}>
        Map a hardware MIDI controller (DJ deck / control surface) or an Elgato Stream Deck to Zeus
        controls. Turn on, click <strong>Learn</strong>, then move a knob or press a key — Zeus
        highlights it and you pick the command to bind. PureSignal arming is intentionally not in
        the command list. This feature has not been bench-verified yet (no MIDI/Stream Deck device on
        the test radio).
      </div>

      <label style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 14 }}>
        <input
          type="checkbox"
          checked={config.enabled}
          onChange={(e) => void setEnabled(e.target.checked)}
          style={{ accentColor: 'var(--accent)' }}
        />
        <span style={{ fontSize: 12, fontWeight: 600, color: 'var(--fg-1)' }}>
          Enabled — route mapped controls to the radio
        </span>
      </label>

      {/* Device status */}
      <div style={CARD}>
        <div style={{ fontSize: 11, fontWeight: 700, color: 'var(--fg-2)', marginBottom: 8 }}>
          MIDI DEVICES
        </div>
        {!midiAvailable && (
          <div style={{ fontSize: 11, color: 'var(--fg-3)' }}>
            No MIDI backend available on this platform, or no device attached. MIDI controller input
            is supported on Windows and macOS only — there is no Linux/Raspberry&nbsp;Pi MIDI backend.
            (Stream Deck control below works on Linux too.)
          </div>
        )}
        {midiAvailable && midiDevices.length === 0 && (
          <div style={{ fontSize: 11, color: 'var(--fg-3)' }}>No MIDI input devices detected.</div>
        )}
        {midiDevices.map((d) => (
          <div key={d.name} style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 12 }}>
            <span
              style={{
                width: 8,
                height: 8,
                borderRadius: '50%',
                background: d.connected ? 'var(--accent)' : 'var(--fg-3)',
              }}
            />
            <span style={{ color: 'var(--fg-1)' }}>{d.name}</span>
          </div>
        ))}
      </div>

      {/* Learn + bind */}
      <div style={CARD}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10 }}>
          <button
            type="button"
            className={`btn sm${learning ? ' active' : ''}`}
            onClick={() => (learning ? void stopLearn() : void startLearn())}
          >
            {learning ? 'STOP LEARN' : 'LEARN'}
          </button>
          <span style={{ fontSize: 11, color: 'var(--fg-3)' }}>
            {learning ? 'Move a control or press a key…' : 'Click Learn, then operate a control.'}
          </span>
        </div>

        {learning && lastLearn && (
          <div
            style={{
              display: 'flex',
              flexWrap: 'wrap',
              alignItems: 'center',
              gap: 8,
              padding: 10,
              marginBottom: 8,
              background: 'var(--bg-1)',
              border: '1px solid var(--accent)',
              borderRadius: 'var(--r-sm)',
            }}
          >
            <span style={{ fontSize: 11, color: 'var(--fg-2)' }}>Detected</span>
            <code style={{ fontSize: 11, color: 'var(--accent)' }}>{lastLearn.controlId}</code>
            <span style={{ fontSize: 11, color: 'var(--fg-3)' }}>
              on {lastLearn.deviceName} ({lastLearn.controlType}, val {lastLearn.value}
              {lastLearn.delta !== 0 ? `, Δ${lastLearn.delta}` : ''})
            </span>
            <CommandSelect commands={filteredCommands} value={learnCommand} onChange={setLearnCommand} />
            <button type="button" className="btn sm active" disabled={!learnCommand} onClick={() => void bindLearned()}>
              BIND
            </button>
          </div>
        )}
      </div>

      {/* Current MIDI mappings */}
      <div style={CARD}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}>
          <div style={{ fontSize: 11, fontWeight: 700, color: 'var(--fg-2)' }}>MAPPINGS</div>
          <span style={{ flex: 1 }} />
          <input
            type="text"
            value={search}
            placeholder="filter commands…"
            onChange={(e) => setSearch(e.target.value)}
            style={{ ...INPUT, width: 180 }}
          />
        </div>
        {config.bindings.mappings.length === 0 ? (
          <div style={{ fontSize: 11, color: 'var(--fg-3)' }}>No mappings yet — use Learn to add one.</div>
        ) : (
          <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 11 }}>
            <thead>
              <tr style={{ color: 'var(--fg-3)', textAlign: 'left' }}>
                <th style={{ padding: 4 }}>Device</th>
                <th style={{ padding: 4 }}>Control</th>
                <th style={{ padding: 4 }}>Type</th>
                <th style={{ padding: 4 }}>Command</th>
                <th style={{ padding: 4 }} />
              </tr>
            </thead>
            <tbody>
              {config.bindings.mappings.map((m) => {
                const info = commandLabel.get(m.command);
                return (
                  <tr key={`${m.deviceName}|${m.controlId}`} style={{ borderTop: '1px solid var(--panel-border)' }}>
                    <td style={{ padding: 4, color: 'var(--fg-1)' }}>{m.deviceName}</td>
                    <td style={{ padding: 4, fontFamily: 'monospace', color: 'var(--fg-1)' }}>{m.controlId}</td>
                    <td style={{ padding: 4, color: 'var(--fg-2)' }}>{m.controlType}</td>
                    <td style={{ padding: 4, color: info?.supported === false ? 'var(--fg-3)' : 'var(--fg-1)' }}>
                      {info?.label ?? m.command}
                      {info?.supported === false ? ' (parity)' : ''}
                    </td>
                    <td style={{ padding: 4 }}>
                      <button
                        type="button"
                        className="btn sm"
                        onClick={() => void removeMapping(m.deviceName, m.controlId)}
                      >
                        ✕
                      </button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
        {search.trim().length > 0 && (
          <div style={{ marginTop: 8, fontSize: 10, color: 'var(--fg-3)' }}>
            {filteredCommands.length} command(s) match “{search.trim()}”. Learn a control to bind one.
          </div>
        )}
      </div>

      {/* Stream Deck */}
      <div style={CARD}>
        <div style={{ fontSize: 11, fontWeight: 700, color: 'var(--fg-2)', marginBottom: 8 }}>
          STREAM DECK
        </div>
        {!sdAvailable && (
          <div style={{ fontSize: 11, color: 'var(--fg-3)' }}>
            No Stream Deck detected, or HID access is unavailable on this platform (on Linux/Pi a udev
            rule may be required).
          </div>
        )}
        {sdDevices.map((d) => (
          <div key={d.serial} style={{ marginBottom: 10 }}>
            <div style={{ fontSize: 11, color: 'var(--fg-1)', marginBottom: 6 }}>
              {d.name} <span style={{ color: 'var(--fg-3)' }}>({d.serial})</span>
            </div>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
              {Array.from({ length: d.buttonCount }, (_, i) => i).map((i) => {
                const bound = sdMappingFor(d.serial, i);
                const active = selectedKey?.serial === d.serial && selectedKey?.index === i;
                return (
                  <button
                    key={i}
                    type="button"
                    onClick={() => setSelectedKey({ serial: d.serial, index: i })}
                    title={bound ? commandLabel.get(bound.command)?.label ?? bound.command : `Key ${i + 1}`}
                    style={{
                      width: 56,
                      height: 44,
                      fontSize: 9,
                      lineHeight: 1.1,
                      overflow: 'hidden',
                      color: bound ? 'var(--fg-0)' : 'var(--fg-3)',
                      background: bound ? 'var(--bg-1)' : 'var(--bg-0)',
                      border: `1px solid ${active ? 'var(--accent)' : 'var(--panel-border)'}`,
                      borderRadius: 'var(--r-sm)',
                      cursor: 'pointer',
                    }}
                  >
                    {bound ? commandLabel.get(bound.command)?.label ?? bound.command : i + 1}
                  </button>
                );
              })}
            </div>
          </div>
        ))}
        {selectedKey && (
          <div style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 8, marginTop: 8 }}>
            <span style={{ fontSize: 11, color: 'var(--fg-2)' }}>
              Key {selectedKey.index + 1} →
            </span>
            <CommandSelect commands={filteredCommands} value={sdCommand} onChange={setSdCommand} />
            <button type="button" className="btn sm active" disabled={!sdCommand} onClick={() => void bindStreamDeckKey()}>
              BIND
            </button>
            {sdMappingFor(selectedKey.serial, selectedKey.index) && (
              <button
                type="button"
                className="btn sm"
                onClick={() => void removeStreamDeckMapping(selectedKey.serial, selectedKey.index)}
              >
                CLEAR
              </button>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
