// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// ChatPanel — operator-to-operator chat tile. A roster of online operators
// (live frequency + mode + rx/tx/away status), a scrollable message thread,
// and a send box. Hydrated on mount via chat-store REST calls and kept live
// by the 0x35 push frames decoded in realtime/ws-client.ts. Clicking any
// callsign (roster or message) opens that operator's QRZ profile in an
// overlay, reusing the QrzCard component and the qrz-store lookup cache so we
// don't re-implement the profile card or burn QRZ quota.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useChatStore, type ChatMessage, type ChatOperator } from '../../state/chat-store';
import { useQrzStore } from '../../state/qrz-store';
import { QrzCard } from '../../components/design/QrzCard';
import { qrzStationToContact } from '../../components/design/qrz-contact';
import type { Contact } from '../../components/design/data';
import type { QrzStation } from '../../api/qrz';

const MAX_MESSAGE_CHARS = 2000;
const CHAR_WARN_THRESHOLD = 1800;

/** Hz → MHz with the operator-friendly "14.074.00" grouping. */
function fmtFreqMhz(hz: number | null | undefined): string {
  if (typeof hz !== 'number' || !Number.isFinite(hz) || hz <= 0) return '—';
  const mhz = hz / 1_000_000;
  // e.g. 14074000 → "14.074.00": MHz . kHz(3) . hHz(2)
  const whole = Math.floor(mhz);
  const frac = Math.round((mhz - whole) * 100_000); // 5 fractional digits
  const khz = String(Math.floor(frac / 100)).padStart(3, '0');
  const hhz = String(frac % 100).padStart(2, '0');
  return `${whole}.${khz}.${hhz}`;
}

/** Ham-band edges (Hz) for grouping the roster. Order = display order. */
const BANDS: ReadonlyArray<{ label: string; lo: number; hi: number }> = [
  { label: '2200m', lo: 135_700, hi: 137_800 },
  { label: '630m', lo: 472_000, hi: 479_000 },
  { label: '160m', lo: 1_800_000, hi: 2_000_000 },
  { label: '80m', lo: 3_500_000, hi: 4_000_000 },
  { label: '60m', lo: 5_250_000, hi: 5_450_000 },
  { label: '40m', lo: 7_000_000, hi: 7_300_000 },
  { label: '30m', lo: 10_100_000, hi: 10_150_000 },
  { label: '20m', lo: 14_000_000, hi: 14_350_000 },
  { label: '17m', lo: 18_068_000, hi: 18_168_000 },
  { label: '15m', lo: 21_000_000, hi: 21_450_000 },
  { label: '12m', lo: 24_890_000, hi: 24_990_000 },
  { label: '10m', lo: 28_000_000, hi: 29_700_000 },
  { label: '6m', lo: 50_000_000, hi: 54_000_000 },
  { label: '4m', lo: 70_000_000, hi: 70_500_000 },
  { label: '2m', lo: 144_000_000, hi: 148_000_000 },
  { label: '1.25m', lo: 222_000_000, hi: 225_000_000 },
  { label: '70cm', lo: 420_000_000, hi: 450_000_000 },
  { label: '33cm', lo: 902_000_000, hi: 928_000_000 },
  { label: '23cm', lo: 1_240_000_000, hi: 1_300_000_000 },
];

/** Band label for a frequency, or "Other" when off-band/unknown. */
function bandForHz(hz: number | null | undefined): string {
  if (typeof hz !== 'number' || !Number.isFinite(hz) || hz <= 0) return 'Other';
  for (const b of BANDS) if (hz >= b.lo && hz <= b.hi) return b.label;
  return 'Other';
}

/** Sort key for band labels (display order; "Other" last). */
function bandOrder(label: string): number {
  const i = BANDS.findIndex((b) => b.label === label);
  return i < 0 ? BANDS.length : i;
}

/** Clock time (HH:MM) from epoch ms, in the operator's local zone. */
function fmtClock(ts: number): string {
  if (!Number.isFinite(ts) || ts <= 0) return '';
  return new Date(ts).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false });
}

/** Short relative age, e.g. "now", "3m", "2h", or a date for older stamps. */
function fmtRelative(ts: number): string {
  if (!Number.isFinite(ts) || ts <= 0) return '';
  const deltaMs = Date.now() - ts;
  const sec = Math.floor(deltaMs / 1000);
  if (sec < 45) return 'now';
  const min = Math.floor(sec / 60);
  if (min < 60) return `${min}m`;
  const hr = Math.floor(min / 60);
  if (hr < 24) return `${hr}h`;
  const day = Math.floor(hr / 24);
  if (day < 7) return `${day}d`;
  return new Date(ts).toLocaleDateString([], { month: 'short', day: 'numeric' });
}

const STATUS_META: Record<string, { color: string; label: string }> = {
  rx: { color: 'var(--ok)', label: 'Receiving' },
  tx: { color: 'var(--tx)', label: 'Transmitting' },
  away: { color: 'var(--fg-3)', label: 'Away' },
};

function StatusDot({ status }: { status: ChatOperator['status'] }) {
  const meta = (status && STATUS_META[status]) || { color: 'var(--fg-4)', label: 'Unknown' };
  return (
    <span
      title={meta.label}
      aria-label={meta.label}
      style={{
        display: 'inline-block',
        width: 8,
        height: 8,
        borderRadius: '50%',
        background: meta.color,
        boxShadow: status === 'tx' ? '0 0 6px var(--tx)' : 'none',
        flexShrink: 0,
      }}
    />
  );
}

/** A callsign that opens the QRZ profile overlay when clicked. */
function CallsignButton({
  callsign,
  onOpen,
  prominent,
  own,
}: {
  callsign: string;
  onOpen: (callsign: string) => void;
  prominent?: boolean;
  own?: boolean;
}) {
  return (
    <button
      type="button"
      className="mono"
      onClick={() => onOpen(callsign)}
      title={`Open ${callsign} on QRZ`}
      style={{
        background: 'none',
        border: 'none',
        padding: 0,
        cursor: 'pointer',
        font: 'inherit',
        fontWeight: prominent ? 700 : 600,
        fontSize: prominent ? 13 : 12,
        letterSpacing: '0.04em',
        color: own ? 'var(--accent-bright)' : 'var(--fg-0)',
        textAlign: 'left',
      }}
      onMouseEnter={(e) => (e.currentTarget.style.textDecoration = 'underline')}
      onMouseLeave={(e) => (e.currentTarget.style.textDecoration = 'none')}
    >
      {callsign}
    </button>
  );
}

function RosterRow({
  op,
  onOpen,
}: {
  op: ChatOperator;
  onOpen: (callsign: string) => void;
}) {
  const tip = [
    STATUS_META[op.status ?? '']?.label,
    op.grid ? `Grid ${op.grid}` : null,
    op.mode ?? null,
  ]
    .filter(Boolean)
    .join(' · ');
  return (
    <div
      title={tip || undefined}
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 8,
        padding: '5px 8px',
        borderRadius: 'var(--r-sm)',
        transition: 'background var(--dur-fast) var(--ease-out)',
      }}
      onMouseEnter={(e) => (e.currentTarget.style.background = 'var(--bg-3)')}
      onMouseLeave={(e) => (e.currentTarget.style.background = 'transparent')}
    >
      <StatusDot status={op.status} />
      <div style={{ display: 'flex', flexDirection: 'column', minWidth: 0, flex: 1 }}>
        <CallsignButton callsign={op.callsign} onOpen={onOpen} prominent />
        <div
          className="mono"
          style={{
            display: 'flex',
            gap: 6,
            alignItems: 'baseline',
            fontSize: 10.5,
            color: 'var(--fg-2)',
            whiteSpace: 'nowrap',
            overflow: 'hidden',
            textOverflow: 'ellipsis',
          }}
        >
          <span style={{ color: 'var(--power)' }}>{fmtFreqMhz(op.freqHz)}</span>
          {op.mode ? <span style={{ color: 'var(--fg-2)' }}>{op.mode}</span> : null}
        </div>
      </div>
    </div>
  );
}

function MessageRow({
  msg,
  own,
  onOpen,
}: {
  msg: ChatMessage;
  own: boolean;
  onOpen: (callsign: string) => void;
}) {
  return (
    <div
      style={{
        display: 'flex',
        flexDirection: 'column',
        alignItems: own ? 'flex-end' : 'flex-start',
        gap: 2,
        padding: '0 10px',
      }}
    >
      <div style={{ display: 'flex', gap: 6, alignItems: 'baseline' }}>
        <CallsignButton callsign={msg.from} onOpen={onOpen} own={own} />
        <span className="mono" title={fmtClock(msg.ts)} style={{ fontSize: 10, color: 'var(--fg-3)' }}>
          {fmtRelative(msg.ts)}
        </span>
      </div>
      <div
        style={{
          maxWidth: '85%',
          padding: '6px 10px',
          borderRadius: 'var(--r-lg)',
          background: own ? 'var(--accent-soft)' : 'var(--bg-2)',
          border: own ? '1px solid var(--accent-line)' : '1px solid var(--line)',
          color: 'var(--fg-1)',
          fontSize: 12.5,
          lineHeight: 1.45,
          wordBreak: 'break-word',
          whiteSpace: 'pre-wrap',
        }}
      >
        {msg.text}
      </div>
    </div>
  );
}

function ProfileOverlay({
  callsign,
  onClose,
}: {
  callsign: string;
  onClose: () => void;
}) {
  const lookupCached = useQrzStore((s) => s.lookupCached);
  const qrzConnected = useQrzStore((s) => s.connected);
  const qrzHome = useQrzStore((s) => s.home);
  const [station, setStation] = useState<QrzStation | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    setLoading(true);
    setError(null);
    void lookupCached(callsign)
      .then((s) => {
        if (!live) return;
        if (s) setStation(s);
        else setError(qrzConnected ? 'No QRZ record' : 'Log into QRZ to view profiles');
      })
      .finally(() => {
        if (live) setLoading(false);
      });
    return () => {
      live = false;
    };
  }, [callsign, lookupCached, qrzConnected]);

  const contact: Contact | null = useMemo(
    () => qrzStationToContact(station, qrzHome),
    [station, qrzHome],
  );

  return (
    <div
      onClick={onClose}
      style={{
        position: 'absolute',
        inset: 0,
        background: 'rgba(0,0,0,0.55)',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        zIndex: 30,
        padding: 16,
      }}
    >
      <div
        onClick={(e) => e.stopPropagation()}
        style={{
          background: 'var(--panel-top)',
          border: '1px solid var(--panel-border)',
          borderRadius: 'var(--r-lg)',
          boxShadow: '0 12px 40px rgba(0,0,0,0.6)',
          width: 340,
          maxWidth: '100%',
          maxHeight: '90%',
          display: 'flex',
          flexDirection: 'column',
          overflow: 'hidden',
        }}
      >
        <div
          style={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            padding: '6px 10px',
            borderBottom: '1px solid var(--panel-border)',
          }}
        >
          <span
            className="mono"
            style={{ fontWeight: 700, letterSpacing: '0.06em', color: 'var(--fg-0)' }}
          >
            {callsign}
          </span>
          <button type="button" className="btn sm" onClick={onClose} title="Close">
            ✕
          </button>
        </div>
        <div style={{ flex: 1, overflow: 'auto', minHeight: 0 }}>
          <QrzCard
            contact={contact}
            enriching={loading}
            lookupError={!loading && !contact ? (error ?? 'No QRZ record') : null}
          />
        </div>
      </div>
    </div>
  );
}

export function ChatPanel() {
  const enabled = useChatStore((s) => s.enabled);
  const connected = useChatStore((s) => s.connected);
  const ownCall = useChatStore((s) => s.callsign);
  const relayError = useChatStore((s) => s.relayError);
  const roster = useChatStore((s) => s.roster);
  const messages = useChatStore((s) => s.messages);
  const refreshStatus = useChatStore((s) => s.refreshStatus);
  const setEnabled = useChatStore((s) => s.setEnabled);
  const send = useChatStore((s) => s.send);
  const loadHistory = useChatStore((s) => s.loadHistory);
  const loadRoster = useChatStore((s) => s.loadRoster);

  const qrzConnected = useQrzStore((s) => s.connected);

  const [draft, setDraft] = useState('');
  const [profileCall, setProfileCall] = useState<string | null>(null);
  const scrollRef = useRef<HTMLDivElement | null>(null);

  // Hydrate on mount; live 0x35 frames keep us current afterwards.
  useEffect(() => {
    void refreshStatus();
    void loadHistory();
    void loadRoster();
  }, [refreshStatus, loadHistory, loadRoster]);

  // Auto-scroll to newest message when the thread grows.
  useEffect(() => {
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [messages.length]);

  const openProfile = useCallback((callsign: string) => {
    setProfileCall(callsign.trim().toUpperCase());
  }, []);

  const sortedRoster = useMemo(() => {
    const rank: Record<string, number> = { tx: 0, rx: 1, away: 2 };
    return [...roster].sort((a, b) => {
      const ra = rank[a.status ?? ''] ?? 3;
      const rb = rank[b.status ?? ''] ?? 3;
      if (ra !== rb) return ra - rb;
      return a.callsign.localeCompare(b.callsign);
    });
  }, [roster]);

  // Group the (already status/callsign-sorted) roster by band for display.
  const rosterByBand = useMemo(() => {
    const groups = new Map<string, ChatOperator[]>();
    for (const op of sortedRoster) {
      const band = bandForHz(op.freqHz);
      const arr = groups.get(band);
      if (arr) arr.push(op);
      else groups.set(band, [op]);
    }
    return [...groups.entries()].sort((a, b) => bandOrder(a[0]) - bandOrder(b[0]));
  }, [sortedRoster]);

  const canSend = enabled && connected && draft.trim().length > 0 && draft.length <= MAX_MESSAGE_CHARS;

  const doSend = useCallback(async () => {
    const text = draft.trim();
    if (!text || !connected || text.length > MAX_MESSAGE_CHARS) return;
    setDraft('');
    const ok = await send(text);
    if (!ok) setDraft(text); // restore on failure so the operator can retry
  }, [draft, connected, send]);

  const statusPill = (() => {
    if (!enabled) return { color: 'var(--fg-3)', bg: 'var(--bg-2)', label: 'Disabled' };
    if (connected) return { color: 'var(--ok)', bg: 'var(--ok-soft)', label: 'Connected' };
    return { color: 'var(--power)', bg: 'var(--power-soft)', label: 'Connecting…' };
  })();

  const remaining = MAX_MESSAGE_CHARS - draft.length;
  const counterColor =
    draft.length > MAX_MESSAGE_CHARS
      ? 'var(--tx)'
      : draft.length >= CHAR_WARN_THRESHOLD
        ? 'var(--power)'
        : 'var(--fg-3)';

  return (
    <div
      style={{
        flex: 1,
        display: 'flex',
        flexDirection: 'column',
        overflow: 'hidden',
        position: 'relative',
      }}
    >
      {/* Header */}
      <div
        style={{
          padding: '6px 10px',
          borderBottom: '1px solid var(--panel-border)',
          display: 'flex',
          alignItems: 'center',
          gap: 8,
          flexWrap: 'wrap',
        }}
      >
        <span
          style={{
            fontSize: 13,
            fontWeight: 700,
            letterSpacing: '0.14em',
            textTransform: 'uppercase',
            color: 'var(--fg-1)',
          }}
        >
          Chat
        </span>
        <span
          title={relayError ?? statusPill.label}
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: 5,
            padding: '2px 8px',
            borderRadius: 'var(--r-lg)',
            background: statusPill.bg,
            color: statusPill.color,
            fontSize: 10.5,
            fontWeight: 600,
            letterSpacing: '0.04em',
          }}
        >
          <span
            style={{
              width: 6,
              height: 6,
              borderRadius: '50%',
              background: statusPill.color,
            }}
          />
          {statusPill.label}
        </span>
        {ownCall ? (
          <span className="mono" style={{ fontSize: 11, color: 'var(--fg-2)' }}>
            {ownCall}
          </span>
        ) : null}
        <div style={{ flex: 1 }} />
        {!enabled ? (
          <button
            type="button"
            className="btn sm active"
            onClick={() => void setEnabled(true)}
            title="Connect to the operator chat relay"
          >
            Enable
          </button>
        ) : (
          <button
            type="button"
            className="btn sm"
            onClick={() => void setEnabled(false)}
            title="Disconnect from the operator chat relay"
          >
            Disable
          </button>
        )}
      </div>

      {/* Call-to-action banners */}
      {!enabled ? (
        <div
          style={{
            padding: '6px 10px',
            borderBottom: '1px solid var(--panel-border)',
            background: 'var(--bg-2)',
            fontSize: 11.5,
            color: 'var(--fg-2)',
            lineHeight: 1.4,
          }}
        >
          Enabling chat connects you to the public operator relay and{' '}
          <strong style={{ color: 'var(--fg-1)' }}>
            broadcasts your callsign and live VFO frequency
          </strong>{' '}
          to other logged-in operators.
        </div>
      ) : null}
      {enabled && !qrzConnected ? (
        <div
          style={{
            padding: '6px 10px',
            borderBottom: '1px solid var(--panel-border)',
            background: 'var(--bg-2)',
            fontSize: 11.5,
            color: 'var(--fg-2)',
            display: 'flex',
            alignItems: 'center',
            gap: 6,
          }}
        >
          <span>Log into QRZ to chat and view operator profiles.</span>
          <span style={{ color: 'var(--fg-3)' }}>(Settings → QRZ)</span>
        </div>
      ) : null}
      {relayError ? (
        <div
          style={{
            padding: '6px 10px',
            borderBottom: '1px solid var(--panel-border)',
            background: 'var(--tx-soft)',
            fontSize: 11.5,
            color: 'var(--tx)',
          }}
        >
          {relayError}
        </div>
      ) : null}

      {/* Body: roster column + message thread */}
      <div style={{ flex: 1, display: 'flex', minHeight: 0, overflow: 'hidden' }}>
        {/* Roster */}
        <div
          style={{
            width: 150,
            flexShrink: 0,
            borderRight: '1px solid var(--panel-border)',
            display: 'flex',
            flexDirection: 'column',
            minHeight: 0,
          }}
        >
          <div
            style={{
              padding: '5px 8px 3px',
              fontSize: 10,
              fontWeight: 700,
              letterSpacing: '0.12em',
              textTransform: 'uppercase',
              color: 'var(--fg-3)',
            }}
          >
            Online · {sortedRoster.length}
          </div>
          <div
            style={{
              flex: 1,
              overflowY: 'auto',
              overflowX: 'hidden',
              minHeight: 0,
              padding: '0 4px 6px',
            }}
          >
            {sortedRoster.length === 0 ? (
              <div style={{ padding: '10px 8px', fontSize: 11, color: 'var(--fg-3)' }}>
                No one here yet
              </div>
            ) : (
              rosterByBand.map(([band, ops]) => (
                <div key={band} style={{ marginBottom: 2 }}>
                  <div
                    style={{
                      display: 'flex',
                      alignItems: 'baseline',
                      justifyContent: 'space-between',
                      padding: '5px 6px 2px',
                      fontSize: 9.5,
                      fontWeight: 700,
                      letterSpacing: '0.1em',
                      textTransform: 'uppercase',
                      color: 'var(--accent-bright)',
                    }}
                  >
                    <span>{band}</span>
                    <span style={{ color: 'var(--fg-4)', fontWeight: 600 }}>{ops.length}</span>
                  </div>
                  {ops.map((op) => (
                    <RosterRow key={op.callsign} op={op} onOpen={openProfile} />
                  ))}
                </div>
              ))
            )}
          </div>
        </div>

        {/* Thread */}
        <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
          <div
            ref={scrollRef}
            style={{
              flex: 1,
              overflow: 'auto',
              minHeight: 0,
              display: 'flex',
              flexDirection: 'column',
              gap: 8,
              padding: '10px 0',
            }}
          >
            {messages.length === 0 ? (
              <div
                style={{
                  margin: 'auto',
                  fontSize: 12,
                  color: 'var(--fg-3)',
                  textAlign: 'center',
                  padding: 16,
                }}
              >
                {enabled ? 'No messages' : 'Chat is disabled — enable it to join the conversation.'}
              </div>
            ) : (
              messages.map((m) => {
                const own = !!ownCall && m.from.toUpperCase() === ownCall.toUpperCase();
                return <MessageRow key={m.id || `${m.from}-${m.ts}`} msg={m} own={own} onOpen={openProfile} />;
              })
            )}
          </div>

          {/* Input row */}
          <div
            style={{
              borderTop: '1px solid var(--panel-border)',
              padding: '6px 8px',
              display: 'flex',
              flexDirection: 'column',
              gap: 4,
            }}
          >
            <div style={{ display: 'flex', gap: 6, alignItems: 'flex-end' }}>
              <textarea
                className="mono"
                value={draft}
                onChange={(e) => setDraft(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' && !e.shiftKey) {
                    e.preventDefault();
                    void doSend();
                  }
                }}
                placeholder={
                  connected ? 'Message… (Enter to send, Shift+Enter for newline)' : 'Not connected'
                }
                disabled={!connected}
                rows={1}
                maxLength={MAX_MESSAGE_CHARS + 64}
                style={{
                  flex: 1,
                  resize: 'none',
                  maxHeight: 90,
                  minHeight: 28,
                  padding: '5px 8px',
                  borderRadius: 'var(--r-sm)',
                  border: '1px solid var(--line-strong)',
                  background: connected ? '#0c0c10' : 'var(--bg-1)',
                  color: '#d8d8dc',
                  fontSize: 12.5,
                  lineHeight: 1.4,
                  outline: 'none',
                }}
              />
              <button
                type="button"
                className={`btn sm${canSend ? ' active' : ''}`}
                disabled={!canSend}
                onClick={() => void doSend()}
                title={connected ? 'Send (Enter)' : 'Not connected'}
              >
                Send
              </button>
            </div>
            <div
              style={{
                display: 'flex',
                justifyContent: 'flex-end',
                fontSize: 10,
                color: counterColor,
                opacity: draft.length >= CHAR_WARN_THRESHOLD || draft.length === 0 ? 1 : 0.6,
              }}
            >
              {draft.length >= CHAR_WARN_THRESHOLD ? `${remaining} left` : `${draft.length}/${MAX_MESSAGE_CHARS}`}
            </div>
          </div>
        </div>
      </div>

      {profileCall ? (
        <ProfileOverlay callsign={profileCall} onClose={() => setProfileCall(null)} />
      ) : null}
    </div>
  );
}
