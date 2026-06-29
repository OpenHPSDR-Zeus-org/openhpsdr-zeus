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

import { useEffect, useMemo, useState } from 'react';
import { useRotatorStore, ROTATOR_BANDS } from '../state/rotator-store';
import type { RotctldSlot } from '../api/rotator';

const MAX_SLOTS = 4;

type EditableSlot = {
  id: number;
  label: string;
  enabled: boolean;
  host: string;
  port: string;
  bands: string[];
  pollingIntervalMs: number;
};

function slotToEditable(s: RotctldSlot): EditableSlot {
  return {
    id: s.id,
    label: s.label,
    enabled: s.enabled,
    host: s.host,
    port: String(s.port),
    bands: [...s.bands],
    pollingIntervalMs: s.pollingIntervalMs,
  };
}

export function RotatorSettingsPanel() {
  const multi = useRotatorStore((s) => s.multi);
  const status = useRotatorStore((s) => s.status);
  const testInFlight = useRotatorStore((s) => s.testInFlight);
  const lastTestResult = useRotatorStore((s) => s.lastTestResult);
  const saveMultiConfig = useRotatorStore((s) => s.saveMultiConfig);
  const setActiveSlot = useRotatorStore((s) => s.setActiveSlot);
  const stop = useRotatorStore((s) => s.stop);
  const test = useRotatorStore((s) => s.test);

  const [slots, setSlots] = useState<EditableSlot[]>(() => multi.slots.map(slotToEditable));
  const [activeSlotId, setActiveSlotIdLocal] = useState<number>(multi.activeSlotId);
  const [autoRoute, setAutoRoute] = useState<boolean>(multi.autoRoute);
  const [saving, setSaving] = useState(false);
  const [testingSlotId, setTestingSlotId] = useState<number | null>(null);
  // Result display is keyed to the slot we last *finished* testing, not the
  // in-flight one (which is cleared in onTest's finally before the result
  // lands) — otherwise the ✓/✗ feedback never renders.
  const [lastTestedSlotId, setLastTestedSlotId] = useState<number | null>(null);
  // True once the operator edits any field. While dirty we suppress the
  // [multi] rehydrate so an active-slot switch (this panel's ACTIVE radio or
  // the Compass/Dial selector, both of which refresh `multi`) can't silently
  // wipe unsaved edits. Cleared on save.
  const [dirty, setDirty] = useState(false);

  // Rehydrate the form when the backend snapshot changes (other tab, restart).
  // Skip while the form has unsaved edits so a live active-slot switch doesn't
  // clobber them.
  useEffect(() => {
    if (dirty) return;
    setSlots(multi.slots.map(slotToEditable));
    setActiveSlotIdLocal(multi.activeSlotId);
    setAutoRoute(multi.autoRoute);
  }, [multi, dirty]);

  const activeRuntimeAz = status?.currentAz;
  const activeRuntimeTarget = status?.targetAz;
  const activeMoving = !!status?.moving;
  const activeConnected = !!status?.connected;
  const activeError = status?.error ?? null;

  const canAdd = slots.length < MAX_SLOTS;

  function updateSlot(id: number, patch: Partial<EditableSlot>) {
    setDirty(true);
    setSlots((prev) => prev.map((s) => (s.id === id ? { ...s, ...patch } : s)));
  }

  function toggleBand(id: number, band: string) {
    setDirty(true);
    setSlots((prev) =>
      prev.map((s) => {
        if (s.id !== id) return s;
        const has = s.bands.includes(band);
        return { ...s, bands: has ? s.bands.filter((b) => b !== band) : [...s.bands, band] };
      }),
    );
  }

  function addSlot() {
    if (slots.length >= MAX_SLOTS) return;
    setDirty(true);
    const used = new Set(slots.map((s) => s.id));
    let nextId = 1;
    while (used.has(nextId)) nextId++;
    setSlots((prev) => [
      ...prev,
      {
        id: nextId,
        label: `Rotator ${nextId}`,
        enabled: false,
        host: '127.0.0.1',
        port: '4533',
        bands: [],
        pollingIntervalMs: 500,
      },
    ]);
  }

  function removeSlot(id: number) {
    setDirty(true);
    setSlots((prev) => {
      const next = prev.filter((s) => s.id !== id);
      if (next.length === 0) return prev; // keep at least one
      return next;
    });
    if (activeSlotId === id) {
      const remaining = slots.filter((s) => s.id !== id);
      const first = remaining[0];
      if (first) setActiveSlotIdLocal(first.id);
    }
  }

  async function onSave() {
    setSaving(true);
    try {
      const sanitizedSlots = slots.map((s) => {
        const portNum = Number(s.port);
        return {
          id: s.id,
          label: s.label.trim() || `Rotator ${s.id}`,
          enabled: s.enabled,
          host: s.host.trim() || '127.0.0.1',
          port: Number.isFinite(portNum) && portNum > 0 && portNum < 65536 ? portNum : 4533,
          bands: s.bands,
          pollingIntervalMs: s.pollingIntervalMs,
        };
      });
      const saved = await saveMultiConfig({
        activeSlotId,
        autoRoute,
        slots: sanitizedSlots,
      });
      // Adopt the server's sanitized snapshot as the new clean baseline, then
      // clear dirty so external snapshot changes rehydrate normally again.
      setSlots(saved.slots.map(slotToEditable));
      setActiveSlotIdLocal(saved.activeSlotId);
      setAutoRoute(saved.autoRoute);
      setDirty(false);
    } finally {
      setSaving(false);
    }
  }

  async function onTest(slot: EditableSlot) {
    const portNum = Number(slot.port);
    if (!Number.isFinite(portNum) || portNum <= 0 || portNum >= 65536) return;
    setTestingSlotId(slot.id);
    try {
      await test(slot.host.trim() || '127.0.0.1', portNum);
      setLastTestedSlotId(slot.id);
    } finally {
      setTestingSlotId(null);
    }
  }

  async function onMakeActive(slotId: number) {
    setActiveSlotIdLocal(slotId);
    await setActiveSlot(slotId);
  }

  return (
    <div style={{ maxWidth: 720 }}>
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
        ROTATORS (HAMLIB / PSTROTATOR)
      </h3>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        <label style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <input
            type="checkbox"
            checked={autoRoute}
            onChange={(e) => {
              setDirty(true);
              setAutoRoute(e.target.checked);
            }}
            style={{ accentColor: 'var(--accent)' }}
          />
          <span style={{ fontSize: 12, fontWeight: 600, color: 'var(--fg-1)' }}>
            Auto-route active rotator by TX band
          </span>
          <span style={{ fontSize: 10, color: 'var(--fg-3)' }}>
            (when on, QSY into a band assigned below switches the live rotator automatically)
          </span>
        </label>

        {slots.map((slot) => {
          const isActive = slot.id === activeSlotId;
          const showRuntime = isActive && activeConnected;
          return (
            <div
              key={slot.id}
              style={{
                padding: 12,
                background: 'var(--bg-1)',
                border: `1px solid ${isActive ? 'var(--accent)' : 'var(--panel-border)'}`,
                borderRadius: 'var(--r-md)',
                display: 'flex',
                flexDirection: 'column',
                gap: 10,
              }}
            >
              <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                <label
                  style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 11, color: 'var(--fg-2)' }}
                  title="Make this rotator the live one"
                >
                  <input
                    type="radio"
                    name="rotator-active-slot"
                    checked={isActive}
                    onChange={() => void onMakeActive(slot.id)}
                    style={{ accentColor: 'var(--accent)' }}
                  />
                  ACTIVE
                </label>
                <input
                  type="text"
                  value={slot.label}
                  onChange={(e) => updateSlot(slot.id, { label: e.target.value })}
                  placeholder={`Rotator ${slot.id}`}
                  spellCheck={false}
                  style={{
                    flex: 1,
                    padding: '6px 8px',
                    fontSize: 13,
                    fontWeight: 600,
                    background: 'var(--bg-0)',
                    border: '1px solid var(--panel-border)',
                    borderRadius: 'var(--r-sm)',
                    color: 'var(--fg-0)',
                  }}
                />
                <label style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 11, color: 'var(--fg-2)' }}>
                  <input
                    type="checkbox"
                    checked={slot.enabled}
                    onChange={(e) => updateSlot(slot.id, { enabled: e.target.checked })}
                    style={{ accentColor: 'var(--accent)' }}
                  />
                  ENABLED
                </label>
                {slots.length > 1 && (
                  <button
                    type="button"
                    onClick={() => removeSlot(slot.id)}
                    className="btn sm"
                    title="Remove this rotator slot"
                    style={{ borderColor: 'var(--tx)', color: 'var(--tx)' }}
                  >
                    REMOVE
                  </button>
                )}
              </div>

              <div style={{ display: 'flex', gap: 10 }}>
                <label style={{ display: 'flex', flexDirection: 'column', gap: 4, flex: 2 }}>
                  <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>Host</span>
                  <input
                    type="text"
                    value={slot.host}
                    onChange={(e) => updateSlot(slot.id, { host: e.target.value })}
                    spellCheck={false}
                    style={{
                      padding: '6px 8px',
                      fontSize: 12,
                      fontFamily: 'monospace',
                      background: 'var(--bg-0)',
                      border: '1px solid var(--panel-border)',
                      borderRadius: 'var(--r-sm)',
                      color: 'var(--fg-0)',
                    }}
                  />
                </label>
                <label style={{ display: 'flex', flexDirection: 'column', gap: 4, flex: 1 }}>
                  <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>Port</span>
                  <input
                    type="number"
                    value={slot.port}
                    onChange={(e) => updateSlot(slot.id, { port: e.target.value })}
                    min={1}
                    max={65535}
                    style={{
                      padding: '6px 8px',
                      fontSize: 12,
                      fontFamily: 'monospace',
                      background: 'var(--bg-0)',
                      border: '1px solid var(--panel-border)',
                      borderRadius: 'var(--r-sm)',
                      color: 'var(--fg-0)',
                    }}
                  />
                </label>
              </div>

              <div>
                <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)', marginBottom: 6 }}>
                  Bands assigned to this rotator
                </div>
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
                  {ROTATOR_BANDS.map((band) => {
                    const on = slot.bands.includes(band);
                    return (
                      <button
                        type="button"
                        key={band}
                        onClick={() => toggleBand(slot.id, band)}
                        className={`btn sm${on ? ' active' : ''}`}
                        style={{ minWidth: 44 }}
                        title={`${on ? 'Remove' : 'Add'} ${band}`}
                      >
                        {band}
                      </button>
                    );
                  })}
                </div>
              </div>

              <BandConflictWarning slot={slot} allSlots={slots} />

              {isActive && activeError && (
                <div
                  style={{
                    padding: 8,
                    fontSize: 12,
                    color: 'var(--tx)',
                    background: 'rgba(230, 58, 43, 0.1)',
                    border: '1px solid var(--tx)',
                    borderRadius: 'var(--r-sm)',
                  }}
                >
                  {activeError}
                </div>
              )}

              {showRuntime && (
                <div
                  style={{
                    padding: 8,
                    background: 'var(--bg-0)',
                    border: '1px solid var(--panel-border)',
                    borderRadius: 'var(--r-sm)',
                    display: 'flex',
                    alignItems: 'center',
                    gap: 10,
                    fontSize: 12,
                    color: 'var(--fg-1)',
                  }}
                >
                  <span style={{ color: 'var(--fg-2)' }}>Current:</span>
                  <span style={{ fontFamily: 'monospace', fontWeight: 600, color: 'var(--accent)' }}>
                    {formatAz(activeRuntimeAz)}
                  </span>
                  {activeRuntimeTarget != null && (
                    <>
                      <span style={{ color: 'var(--fg-2)' }}>· Target:</span>
                      <span style={{ fontFamily: 'monospace', fontWeight: 600, color: 'var(--power)' }}>
                        {formatAz(activeRuntimeTarget)}
                      </span>
                    </>
                  )}
                  {activeMoving && (
                    <span style={{ color: 'var(--power)', fontWeight: 600 }}>moving</span>
                  )}
                </div>
              )}

              <div style={{ display: 'flex', gap: 6 }}>
                <button
                  type="button"
                  onClick={() => void onTest(slot)}
                  disabled={testingSlotId === slot.id || testInFlight}
                  className="btn sm"
                >
                  {testingSlotId === slot.id && testInFlight ? 'TESTING…' : 'TEST CONNECTION'}
                </button>
                {isActive && activeConnected && (
                  <button
                    type="button"
                    onClick={() => stop()}
                    className="btn sm"
                    style={{
                      borderColor: 'var(--tx)',
                      color: 'var(--tx)',
                    }}
                  >
                    STOP
                  </button>
                )}
                {lastTestedSlotId === slot.id && testingSlotId === null && lastTestResult && (
                  <span
                    style={{
                      alignSelf: 'center',
                      fontSize: 12,
                      color: lastTestResult.ok ? 'var(--accent)' : 'var(--tx)',
                    }}
                  >
                    {lastTestResult.ok
                      ? `✓ OK — reachable`
                      : `✗ ${lastTestResult.error ?? 'unknown error'}`}
                  </span>
                )}
              </div>
            </div>
          );
        })}

        <div style={{ display: 'flex', gap: 6 }}>
          {canAdd && (
            <button type="button" onClick={addSlot} className="btn sm">
              + ADD ROTATOR
            </button>
          )}
          <span style={{ flex: 1 }} />
          <button type="button" onClick={() => void onSave()} disabled={saving} className="btn sm active">
            {saving ? 'SAVING…' : 'SAVE'}
          </button>
        </div>

        <div
          style={{
            fontSize: 10,
            lineHeight: 1.4,
            color: 'var(--fg-3)',
          }}
        >
          Each rotator slot opens its own TCP connection to a Hamlib-compatible rotator server —
          that's either <span style={{ fontFamily: 'monospace' }}>rotctld</span> (hamlib's daemon)
          or PSTRotator's built-in Hamlib-Rotor port. Auto-route follows the TX VFO band: when you
          QSY into a band assigned to a different rotator, Zeus switches the live connection. Only
          the active slot is connected at any one time. Up to {MAX_SLOTS} rotators. Settings are
          stored server-side in zeus-prefs.db and shared across browsers and sessions.
        </div>
      </div>
    </div>
  );
}

function BandConflictWarning(props: { slot: EditableSlot; allSlots: EditableSlot[] }) {
  const conflicts = useMemo(() => {
    const out: string[] = [];
    for (const band of props.slot.bands) {
      const claimedBy = props.allSlots.filter((s) => s.id !== props.slot.id && s.bands.includes(band));
      if (claimedBy.length > 0) out.push(band);
    }
    return out;
  }, [props.slot, props.allSlots]);
  if (conflicts.length === 0) return null;
  return (
    <div
      style={{
        padding: 6,
        fontSize: 11,
        color: 'var(--power)',
        background: 'rgba(255, 160, 40, 0.08)',
        border: '1px solid var(--power)',
        borderRadius: 'var(--r-sm)',
      }}
    >
      Also assigned elsewhere: {conflicts.join(', ')}. On auto-route, the first matching slot wins.
    </div>
  );
}

function formatAz(az: number | null | undefined): string {
  if (az == null || !Number.isFinite(az)) return '—';
  // hamlib can report signed azimuths when the rotator crosses its zero
  // point (e.g. -79° on a rotor that can swing past 0°). For display we
  // want the equivalent 0..359 heading so the compass-style reading is
  // unambiguous (−79° → 281°).
  const normalized = ((az % 360) + 360) % 360;
  return `${normalized.toFixed(0).padStart(3, '0')}°`;
}
