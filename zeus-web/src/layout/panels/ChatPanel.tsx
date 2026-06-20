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
// ChatPanel — operator-to-operator chat tile. Premium tabbed UI: left roster
// sidebar (public operators, friends, friend requests), tab bar across the top
// (Public → Groups → DMs), message thread, and auto-growing composer. Non-public
// rooms get a warm golden glow as the signature premium cue. Hydrated on mount
// via chat-store REST calls and kept live by 0x35 push frames.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  useChatStore,
  dmOther,
  PUBLIC_ROOM,
  type ChatMessage,
  type ChatOperator,
} from '../../state/chat-store';
import { useQrzStore } from '../../state/qrz-store';
import { QrzCard } from '../../components/design/QrzCard';
import { qrzStationToContact } from '../../components/design/qrz-contact';
import type { Contact } from '../../components/design/data';
import type { QrzStation } from '../../api/qrz';

const MAX_MESSAGE_CHARS = 2000;

// ---------------------------------------------------------------------------
// Utility helpers
// ---------------------------------------------------------------------------

function fmtFreqMhz(hz: number | null | undefined): string {
  if (typeof hz !== 'number' || !Number.isFinite(hz) || hz <= 0) return '—';
  const mhz = hz / 1_000_000;
  const whole = Math.floor(mhz);
  const frac = Math.round((mhz - whole) * 100_000);
  const khz = String(Math.floor(frac / 100)).padStart(3, '0');
  const hhz = String(frac % 100).padStart(2, '0');
  return `${whole}.${khz}.${hhz}`;
}

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

function bandForHz(hz: number | null | undefined): string {
  if (typeof hz !== 'number' || !Number.isFinite(hz) || hz <= 0) return 'Other';
  for (const b of BANDS) if (hz >= b.lo && hz <= b.hi) return b.label;
  return 'Other';
}

function bandOrder(label: string): number {
  const i = BANDS.findIndex((b) => b.label === label);
  return i < 0 ? BANDS.length : i;
}

function fmtClock(ts: number): string {
  if (!Number.isFinite(ts) || ts <= 0) return '';
  return new Date(ts).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false });
}

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

// ---------------------------------------------------------------------------
// Sub-components
// ---------------------------------------------------------------------------

function StatusDot({ status }: { status: ChatOperator['status'] }) {
  const meta = (status && STATUS_META[status]) || { color: 'var(--fg-4)', label: 'Unknown' };
  return (
    <span
      title={meta.label}
      aria-label={meta.label}
      style={{
        display: 'inline-block',
        width: 7,
        height: 7,
        borderRadius: '50%',
        background: meta.color,
        boxShadow: status === 'tx' ? '0 0 5px var(--tx)' : 'none',
        flexShrink: 0,
      }}
    />
  );
}

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
        fontSize: prominent ? 12.5 : 12,
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

function GroupHeader({ label, count, accent }: { label: string; count: number; accent: string }) {
  return (
    <div
      style={{
        display: 'flex',
        alignItems: 'baseline',
        justifyContent: 'space-between',
        padding: '6px 8px 2px',
        fontSize: 9,
        fontWeight: 700,
        letterSpacing: '0.12em',
        textTransform: 'uppercase',
        color: accent,
      }}
    >
      <span>{label}</span>
      <span style={{ color: 'var(--fg-4)', fontWeight: 600 }}>{count}</span>
    </div>
  );
}

type FriendRelation = 'friend' | 'requested' | 'none';

const STAR_META: Record<FriendRelation, { glyph: string; color: string; title: string }> = {
  friend: { glyph: '★', color: 'var(--power)', title: 'Friends — click to remove' },
  requested: { glyph: '☆', color: 'var(--accent-bright)', title: 'Request pending — click to cancel' },
  none: { glyph: '☆', color: 'var(--fg-4)', title: 'Add friend (send request)' },
};

function RosterRow({
  op,
  onOpen,
  relation,
  onStar,
  onDm,
  isAdmin,
  onBan,
}: {
  op: ChatOperator;
  onOpen: (callsign: string) => void;
  relation: FriendRelation;
  onStar: (callsign: string) => void;
  onDm: (callsign: string) => void;
  isAdmin: boolean;
  onBan: (callsign: string) => void;
}) {
  const [hovered, setHovered] = useState(false);
  const freq = fmtFreqMhz(op.freqHz);
  const tip = [
    STATUS_META[op.status ?? '']?.label,
    freq !== '—' ? `${freq} MHz` : null,
    op.mode ?? null,
    op.grid ? `Grid ${op.grid}` : null,
  ]
    .filter(Boolean)
    .join(' · ');
  const star = STAR_META[relation];
  const starVisible = relation !== 'none' || hovered;

  return (
    <div
      title={tip || undefined}
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 6,
        padding: '4px 8px',
        borderRadius: 'var(--r-sm)',
        background: hovered ? 'var(--bg-3)' : 'transparent',
        transition: 'background var(--dur-fast) var(--ease-out)',
      }}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
    >
      <StatusDot status={op.status} />
      <div style={{ minWidth: 0, flex: 1 }}>
        <CallsignButton callsign={op.callsign} onOpen={onOpen} prominent />
      </div>
      {/* DM button — appears on hover */}
      {hovered && (
        <button
          type="button"
          onClick={() => onDm(op.callsign)}
          title={`Message ${op.callsign}`}
          aria-label={`Start DM with ${op.callsign}`}
          style={{
            flexShrink: 0,
            background: 'none',
            border: 'none',
            padding: '0 2px',
            cursor: 'pointer',
            fontSize: 11,
            lineHeight: 1,
            color: 'var(--accent-bright)',
            opacity: 0.8,
          }}
        >
          {/* Inline chat bubble SVG */}
          <svg
            width="13"
            height="13"
            viewBox="0 0 16 16"
            fill="none"
            xmlns="http://www.w3.org/2000/svg"
            aria-hidden="true"
          >
            <path
              d="M2 2h12a1 1 0 0 1 1 1v8a1 1 0 0 1-1 1H5l-3 3V3a1 1 0 0 1 1-1z"
              stroke="currentColor"
              strokeWidth="1.5"
              strokeLinejoin="round"
            />
          </svg>
        </button>
      )}
      {/* Admin ban button — subtle, appears on hover */}
      {isAdmin && hovered && (
        <button
          type="button"
          onClick={() => onBan(op.callsign)}
          title={`Ban ${op.callsign}`}
          aria-label={`Ban ${op.callsign}`}
          style={{
            flexShrink: 0,
            background: 'none',
            border: 'none',
            padding: '0 2px',
            cursor: 'pointer',
            fontSize: 11,
            lineHeight: 1,
            color: 'var(--tx)',
            opacity: 0.6,
          }}
        >
          <svg
            width="12"
            height="12"
            viewBox="0 0 16 16"
            fill="none"
            xmlns="http://www.w3.org/2000/svg"
            aria-hidden="true"
          >
            <circle cx="8" cy="8" r="6.5" stroke="currentColor" strokeWidth="1.5" />
            <line x1="3" y1="13" x2="13" y2="3" stroke="currentColor" strokeWidth="1.5" />
          </svg>
        </button>
      )}
      {/* Star control */}
      <button
        type="button"
        onClick={() => onStar(op.callsign)}
        title={star.title}
        aria-label={`${star.title} — ${op.callsign}`}
        aria-pressed={relation === 'friend'}
        style={{
          flexShrink: 0,
          background: 'none',
          border: 'none',
          padding: '0 2px',
          cursor: 'pointer',
          fontSize: 12,
          lineHeight: 1,
          color: star.color,
          opacity: starVisible ? 1 : 0,
          transition: 'opacity var(--dur-fast) var(--ease-out)',
        }}
      >
        {star.glyph}
      </button>
    </div>
  );
}

function RequestRow({
  callsign,
  onOpen,
  onAccept,
  onDeny,
}: {
  callsign: string;
  onOpen: (callsign: string) => void;
  onAccept: (callsign: string) => void;
  onDeny: (callsign: string) => void;
}) {
  const btn = (label: string, color: string, title: string, fn: () => void) => (
    <button
      type="button"
      onClick={fn}
      title={title}
      aria-label={`${title} — ${callsign}`}
      style={{
        flexShrink: 0,
        width: 20,
        height: 20,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        background: 'none',
        border: `1px solid ${color}`,
        borderRadius: 'var(--r-sm)',
        color,
        cursor: 'pointer',
        fontSize: 11,
        lineHeight: 1,
      }}
    >
      {label}
    </button>
  );
  return (
    <div
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 6,
        padding: '4px 8px',
        borderRadius: 'var(--r-sm)',
      }}
    >
      <div style={{ minWidth: 0, flex: 1 }}>
        <CallsignButton callsign={callsign} onOpen={onOpen} prominent />
        <div style={{ fontSize: 9, color: 'var(--fg-3)', letterSpacing: '0.04em', marginTop: 1 }}>
          wants to be friends
        </div>
      </div>
      {btn('✓', 'var(--ok)', 'Accept request', () => onAccept(callsign))}
      {btn('✗', 'var(--tx)', 'Deny request', () => onDeny(callsign))}
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
        gap: 3,
        padding: '0 10px',
      }}
    >
      <div style={{ display: 'flex', gap: 6, alignItems: 'baseline' }}>
        <CallsignButton callsign={msg.from} onOpen={onOpen} own={own} />
        <span
          className="mono"
          title={fmtClock(msg.ts)}
          style={{ fontSize: 9.5, color: 'var(--fg-3)' }}
        >
          {fmtRelative(msg.ts)}
        </span>
      </div>
      <div
        style={{
          maxWidth: '85%',
          padding: '5px 10px',
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

// ---------------------------------------------------------------------------
// Eye icon — inline SVG for freq visibility toggle
// ---------------------------------------------------------------------------

function EyeIcon({ open }: { open: boolean }) {
  if (open) {
    return (
      <svg
        width="15"
        height="15"
        viewBox="0 0 16 16"
        fill="none"
        xmlns="http://www.w3.org/2000/svg"
        aria-hidden="true"
      >
        <ellipse cx="8" cy="8" rx="6" ry="3.5" stroke="currentColor" strokeWidth="1.4" />
        <circle cx="8" cy="8" r="1.8" fill="currentColor" />
      </svg>
    );
  }
  return (
    <svg
      width="15"
      height="15"
      viewBox="0 0 16 16"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
      aria-hidden="true"
    >
      <ellipse cx="8" cy="8" rx="6" ry="3.5" stroke="currentColor" strokeWidth="1.4" />
      <circle cx="8" cy="8" r="1.8" fill="currentColor" />
      <line x1="2" y1="14" x2="14" y2="2" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
    </svg>
  );
}

// ---------------------------------------------------------------------------
// Tab bar sub-components
// ---------------------------------------------------------------------------

/**
 * Gold glow style for non-public private room tabs/threads.
 * Tuned to be soft — a premium cue, not a neon sign.
 */
const GOLD_RING_IDLE =
  'inset 0 0 0 1px rgba(255,177,60,0.22), 0 0 8px rgba(255,177,60,0.10)';
const GOLD_RING_HOVER =
  'inset 0 0 0 1px rgba(255,177,60,0.35), 0 0 12px rgba(255,177,60,0.16)';
const GOLD_RING_ACTIVE =
  'inset 0 0 0 1px rgba(255,177,60,0.40), 0 0 14px rgba(255,177,60,0.20)';

interface TabItemProps {
  id: string;
  label: string;
  isPrivate: boolean;
  isActive: boolean;
  unread: number;
  closable?: boolean;
  onClick: () => void;
  onClose?: () => void;
}

function TabItem({ id: _id, label, isPrivate, isActive, unread, closable, onClick, onClose }: TabItemProps) {
  const [hovered, setHovered] = useState(false);

  const boxShadow = isPrivate
    ? isActive
      ? GOLD_RING_ACTIVE
      : hovered
      ? GOLD_RING_HOVER
      : GOLD_RING_IDLE
    : 'none';

  const borderBottom = isActive
    ? isPrivate
      ? '2px solid var(--power)'
      : '2px solid var(--accent-bright)'
    : '2px solid transparent';

  return (
    <div
      role="tab"
      aria-selected={isActive}
      onClick={onClick}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      style={{
        position: 'relative',
        display: 'flex',
        alignItems: 'center',
        gap: 5,
        padding: '0 10px',
        height: '100%',
        cursor: 'pointer',
        boxShadow,
        borderBottom,
        background: isActive ? 'var(--bg-2)' : hovered ? 'var(--bg-2)' : 'transparent',
        transition: `background var(--dur-fast) var(--ease-out), box-shadow var(--dur-fast) var(--ease-out)`,
        userSelect: 'none',
        flexShrink: 0,
      }}
    >
      <span
        style={{
          fontSize: 11,
          fontWeight: isActive ? 700 : 500,
          letterSpacing: '0.04em',
          color: isActive
            ? isPrivate
              ? 'var(--power)'
              : 'var(--fg-0)'
            : 'var(--fg-2)',
          transition: `color var(--dur-fast) var(--ease-out)`,
          maxWidth: 88,
          overflow: 'hidden',
          textOverflow: 'ellipsis',
          whiteSpace: 'nowrap',
        }}
      >
        {label}
      </span>
      {unread > 0 && !isActive && (
        <span
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            justifyContent: 'center',
            minWidth: 15,
            height: 15,
            padding: '0 4px',
            borderRadius: 8,
            background: isPrivate ? 'var(--power)' : 'var(--accent)',
            color: '#fff',
            fontSize: 9,
            fontWeight: 700,
            lineHeight: 1,
          }}
        >
          {unread > 99 ? '99+' : unread}
        </span>
      )}
      {closable && onClose && (
        <button
          type="button"
          onClick={(e) => {
            e.stopPropagation();
            onClose();
          }}
          aria-label={`Close ${label} tab`}
          style={{
            flexShrink: 0,
            background: 'none',
            border: 'none',
            padding: 0,
            cursor: 'pointer',
            fontSize: 10,
            lineHeight: 1,
            color: 'var(--fg-3)',
            marginLeft: 2,
            opacity: hovered ? 1 : 0,
            transition: `opacity var(--dur-fast) var(--ease-out)`,
          }}
        >
          ✕
        </button>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Group room management strip (admin only)
// ---------------------------------------------------------------------------

function GroupManagementStrip({
  roomId,
  members,
}: {
  roomId: string;
  members: string[];
}) {
  const [open, setOpen] = useState(false);
  const addMember = useChatStore((s) => s.addMember);
  const removeMember = useChatStore((s) => s.removeMember);
  const deleteRoom = useChatStore((s) => s.deleteRoom);
  const setActiveRoom = useChatStore((s) => s.setActiveRoom);

  const handleAdd = () => {
    const call = window.prompt('Add member — enter callsign:');
    if (call && call.trim()) void addMember(roomId, call.trim().toUpperCase());
  };

  const handleRemove = (call: string) => {
    if (window.confirm(`Remove ${call} from this group?`)) void removeMember(roomId, call);
  };

  const handleDelete = () => {
    if (window.confirm('Delete this group room? This cannot be undone.')) {
      void deleteRoom(roomId);
      setActiveRoom(PUBLIC_ROOM);
    }
  };

  return (
    <div
      style={{
        borderBottom: '1px solid var(--line)',
        background: 'var(--bg-1)',
      }}
    >
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 6,
          width: '100%',
          padding: '4px 10px',
          background: 'none',
          border: 'none',
          cursor: 'pointer',
          fontSize: 9.5,
          fontWeight: 700,
          letterSpacing: '0.10em',
          textTransform: 'uppercase',
          color: 'var(--power)',
        }}
      >
        <span style={{ fontSize: 9, opacity: 0.7 }}>{open ? '▼' : '▶'}</span>
        Group Management
      </button>
      {open && (
        <div
          style={{
            padding: '4px 10px 8px',
            display: 'flex',
            flexDirection: 'column',
            gap: 4,
          }}
        >
          {/* Member list */}
          {members.length > 0 && (
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4, marginBottom: 4 }}>
              {members.map((m) => (
                <span
                  key={m}
                  className="mono"
                  style={{
                    display: 'inline-flex',
                    alignItems: 'center',
                    gap: 4,
                    padding: '2px 6px',
                    borderRadius: 'var(--r-sm)',
                    background: 'var(--bg-2)',
                    border: '1px solid var(--line)',
                    fontSize: 10.5,
                    color: 'var(--fg-1)',
                  }}
                >
                  {m}
                  <button
                    type="button"
                    onClick={() => handleRemove(m)}
                    aria-label={`Remove ${m} from group`}
                    style={{
                      background: 'none',
                      border: 'none',
                      padding: 0,
                      cursor: 'pointer',
                      fontSize: 9,
                      color: 'var(--fg-3)',
                      lineHeight: 1,
                    }}
                  >
                    ✕
                  </button>
                </span>
              ))}
            </div>
          )}
          <div style={{ display: 'flex', gap: 6 }}>
            <button
              type="button"
              className="btn sm"
              onClick={handleAdd}
            >
              + Add member
            </button>
            <button
              type="button"
              className="btn sm"
              onClick={handleDelete}
              style={{ color: 'var(--tx)', borderColor: 'var(--tx)', marginLeft: 'auto' }}
            >
              Delete room
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Main panel
// ---------------------------------------------------------------------------

export function ChatPanel() {
  const enabled = useChatStore((s) => s.enabled);
  const connected = useChatStore((s) => s.connected);
  const ownCall = useChatStore((s) => s.callsign);
  const relayError = useChatStore((s) => s.relayError);
  const isAdmin = useChatStore((s) => s.isAdmin);
  const freqPublic = useChatStore((s) => s.freqPublic);
  const roster = useChatStore((s) => s.roster);
  const rooms = useChatStore((s) => s.rooms);
  const activeRoom = useChatStore((s) => s.activeRoom);
  const messagesByRoom = useChatStore((s) => s.messagesByRoom);
  const unreadByRoom = useChatStore((s) => s.unreadByRoom);
  const acceptedFriends = useChatStore((s) => s.acceptedFriends);
  const incomingRequests = useChatStore((s) => s.incomingRequests);
  const outgoingRequests = useChatStore((s) => s.outgoingRequests);

  const refreshStatus = useChatStore((s) => s.refreshStatus);
  const setEnabled = useChatStore((s) => s.setEnabled);
  const send = useChatStore((s) => s.send);
  const loadHistory = useChatStore((s) => s.loadHistory);
  const loadRoster = useChatStore((s) => s.loadRoster);
  const loadRooms = useChatStore((s) => s.loadRooms);
  const loadFriends = useChatStore((s) => s.loadFriends);
  const setActiveRoom = useChatStore((s) => s.setActiveRoom);
  const openDm = useChatStore((s) => s.openDm);
  const setFreqVisibility = useChatStore((s) => s.setFreqVisibility);
  const requestFriend = useChatStore((s) => s.requestFriend);
  const acceptFriend = useChatStore((s) => s.acceptFriend);
  const denyFriend = useChatStore((s) => s.denyFriend);
  const removeFriend = useChatStore((s) => s.removeFriend);
  const createRoom = useChatStore((s) => s.createRoom);
  const ban = useChatStore((s) => s.ban);

  const qrzConnected = useQrzStore((s) => s.connected);

  const [draft, setDraft] = useState('');
  const [profileCall, setProfileCall] = useState<string | null>(null);
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const inputRef = useRef<HTMLTextAreaElement | null>(null);

  // Auto-grow textarea
  useEffect(() => {
    const el = inputRef.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${Math.min(el.scrollHeight, 90)}px`;
  }, [draft]);

  // Hydrate on mount
  useEffect(() => {
    void refreshStatus();
    void loadHistory();
    void loadRoster();
    void loadRooms();
    void loadFriends();
  }, [refreshStatus, loadHistory, loadRoster, loadRooms, loadFriends]);

  // Auto-scroll to newest message in the active room
  const activeMessages = messagesByRoom[activeRoom] ?? [];
  useEffect(() => {
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [activeMessages.length, activeRoom]);

  const openProfile = useCallback((callsign: string) => {
    setProfileCall(callsign.trim().toUpperCase());
  }, []);

  // Sorted roster
  const sortedRoster = useMemo(() => {
    const rank: Record<string, number> = { tx: 0, rx: 1, away: 2 };
    return [...roster].sort((a, b) => {
      const ra = rank[a.status ?? ''] ?? 3;
      const rb = rank[b.status ?? ''] ?? 3;
      if (ra !== rb) return ra - rb;
      return a.callsign.localeCompare(b.callsign);
    });
  }, [roster]);

  const friendSet = useMemo(() => new Set(acceptedFriends.map((c) => c.toUpperCase())), [acceptedFriends]);
  const outgoingSet = useMemo(() => new Set(outgoingRequests.map((c) => c.toUpperCase())), [outgoingRequests]);
  const incomingSet = useMemo(() => new Set(incomingRequests.map((c) => c.toUpperCase())), [incomingRequests]);

  const friendsOnline = useMemo(
    () => sortedRoster.filter((op) => friendSet.has(op.callsign.toUpperCase())),
    [sortedRoster, friendSet],
  );

  const rosterByBand = useMemo(() => {
    const groups = new Map<string, ChatOperator[]>();
    for (const op of sortedRoster) {
      const call = op.callsign.toUpperCase();
      if (friendSet.has(call) || incomingSet.has(call)) continue;
      const band = bandForHz(op.freqHz);
      const arr = groups.get(band);
      if (arr) arr.push(op);
      else groups.set(band, [op]);
    }
    return [...groups.entries()].sort((a, b) => bandOrder(a[0]) - bandOrder(b[0]));
  }, [sortedRoster, friendSet, incomingSet]);

  const relationFor = useCallback(
    (callsign: string): FriendRelation => {
      const c = callsign.toUpperCase();
      if (friendSet.has(c)) return 'friend';
      if (outgoingSet.has(c)) return 'requested';
      return 'none';
    },
    [friendSet, outgoingSet],
  );

  const onStar = useCallback(
    (callsign: string) => {
      const rel = relationFor(callsign);
      if (rel === 'friend' || rel === 'requested') void removeFriend(callsign);
      else void requestFriend(callsign);
    },
    [relationFor, removeFriend, requestFriend],
  );

  const onBan = useCallback(
    (callsign: string) => {
      if (window.confirm(`Ban ${callsign} from ZeusChat?`)) void ban(callsign);
    },
    [ban],
  );

  // Tab ordering: public first, then groups, then DMs
  const orderedRooms = useMemo(() => {
    const pub = rooms.filter((r) => r.kind === 'public');
    const grp = rooms.filter((r) => r.kind === 'group');
    const dms = rooms.filter((r) => r.kind === 'dm');
    return [...pub, ...grp, ...dms];
  }, [rooms]);

  const activeRoomObj = useMemo(
    () => rooms.find((r) => r.id === activeRoom) ?? null,
    [rooms, activeRoom],
  );

  const isPrivateRoom = activeRoomObj ? activeRoomObj.kind !== 'public' : false;

  // Placeholder label for the composer
  const composerPlaceholder = (() => {
    if (!connected) return 'Not connected';
    if (!activeRoomObj) return 'Message… (Enter to send)';
    if (activeRoomObj.kind === 'dm') {
      const other = dmOther(activeRoomObj.id, ownCall);
      return `Message @${other ?? activeRoomObj.name} (Enter to send, Shift+Enter for newline)`;
    }
    if (activeRoomObj.kind === 'group') {
      return `Message #${activeRoomObj.name} (Enter to send, Shift+Enter for newline)`;
    }
    return 'Message everyone (Enter to send, Shift+Enter for newline)';
  })();

  const canSend = enabled && connected && draft.trim().length > 0 && draft.length <= MAX_MESSAGE_CHARS;

  const doSend = useCallback(async () => {
    const text = draft.trim();
    if (!text || !connected || text.length > MAX_MESSAGE_CHARS) return;
    setDraft('');
    const ok = await send(text);
    if (!ok) setDraft(text);
  }, [draft, connected, send]);

  // Status pill
  const statusPill = (() => {
    if (!enabled) return { color: 'var(--fg-3)', bg: 'var(--bg-2)', label: 'Disabled' };
    if (connected) return { color: 'var(--ok)', bg: 'var(--ok-soft)', label: 'Connected' };
    if (relayError) {
      const label = /qrz/i.test(relayError) ? 'Login to QRZ' : 'Disconnected';
      return { color: 'var(--tx)', bg: 'var(--tx-soft)', label };
    }
    return { color: 'var(--power)', bg: 'var(--power-soft)', label: 'Connecting…' };
  })();

  // Handle DM tab close: just navigate back to public
  const handleTabClose = useCallback(
    (id: string) => {
      if (activeRoom === id) setActiveRoom(PUBLIC_ROOM);
    },
    [activeRoom, setActiveRoom],
  );

  // Admin: create group room
  const handleCreateRoom = () => {
    const name = window.prompt('New group name:');
    if (name && name.trim()) void createRoom(name.trim());
  };

  // Golden thread border for private rooms
  const threadTopBorder = isPrivateRoom
    ? '2px solid rgba(255,177,60,0.30)'
    : '1px solid transparent';

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
      {/* ── Header ── */}
      <div
        style={{
          padding: '5px 10px',
          borderBottom: '1px solid var(--panel-border)',
          display: 'flex',
          alignItems: 'center',
          gap: 7,
          flexShrink: 0,
        }}
      >
        <span
          style={{
            fontSize: 12,
            fontWeight: 700,
            letterSpacing: '0.14em',
            textTransform: 'uppercase',
            color: 'var(--fg-1)',
          }}
        >
          Chat
        </span>

        {/* Status pill */}
        <span
          title={relayError ?? statusPill.label}
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: 4,
            padding: '2px 7px',
            borderRadius: 'var(--r-lg)',
            background: statusPill.bg,
            color: statusPill.color,
            fontSize: 10,
            fontWeight: 600,
            letterSpacing: '0.04em',
            flexShrink: 0,
          }}
        >
          <span
            style={{
              width: 5,
              height: 5,
              borderRadius: '50%',
              background: statusPill.color,
              flexShrink: 0,
            }}
          />
          {statusPill.label}
        </span>

        {/* Own callsign */}
        {ownCall ? (
          <span className="mono" style={{ fontSize: 10.5, color: 'var(--fg-2)', flexShrink: 0 }}>
            {ownCall}
          </span>
        ) : null}

        <div style={{ flex: 1 }} />

        {/* Freq visibility eye toggle */}
        {enabled && connected && (
          <button
            type="button"
            onClick={() => void setFreqVisibility(!freqPublic)}
            aria-label={
              freqPublic
                ? 'Your frequency is visible to friends — click to hide'
                : 'Your frequency is hidden from everyone — click to share'
            }
            aria-pressed={freqPublic}
            title={
              freqPublic
                ? 'Your frequency is visible to friends'
                : 'Your frequency is hidden from everyone'
            }
            style={{
              background: 'none',
              border: 'none',
              padding: '2px 4px',
              cursor: 'pointer',
              color: freqPublic ? 'var(--accent-bright)' : 'var(--fg-3)',
              display: 'flex',
              alignItems: 'center',
              borderRadius: 'var(--r-sm)',
              transition: 'color var(--dur-fast) var(--ease-out)',
            }}
          >
            <EyeIcon open={freqPublic} />
          </button>
        )}

        {/* Enable/disable toggle */}
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

      {/* ── Call-to-action banners ── */}
      {!enabled && (
        <div
          style={{
            padding: '5px 10px',
            borderBottom: '1px solid var(--panel-border)',
            background: 'var(--bg-2)',
            fontSize: 11,
            color: 'var(--fg-2)',
            lineHeight: 1.4,
            flexShrink: 0,
          }}
        >
          Enabling chat connects you to the public operator relay and{' '}
          <strong style={{ color: 'var(--fg-1)' }}>
            broadcasts your callsign and live VFO frequency
          </strong>{' '}
          to other logged-in operators.
        </div>
      )}
      {enabled && !qrzConnected && (
        <div
          style={{
            padding: '5px 10px',
            borderBottom: '1px solid var(--panel-border)',
            background: 'var(--bg-2)',
            fontSize: 11,
            color: 'var(--fg-2)',
            display: 'flex',
            alignItems: 'center',
            gap: 6,
            flexShrink: 0,
          }}
        >
          <span>Log into QRZ to chat and view operator profiles.</span>
          <span style={{ color: 'var(--fg-3)' }}>(Settings → QRZ)</span>
        </div>
      )}
      {relayError && (
        <div
          style={{
            padding: '5px 10px',
            borderBottom: '1px solid var(--panel-border)',
            background: 'var(--tx-soft)',
            fontSize: 11,
            color: 'var(--tx)',
            flexShrink: 0,
          }}
        >
          {relayError}
        </div>
      )}

      {/* ── Body: sidebar + right column ── */}
      <div style={{ flex: 1, display: 'flex', minHeight: 0, overflow: 'hidden' }}>

        {/* ── Left sidebar: public roster ── */}
        <div
          style={{
            width: 148,
            flexShrink: 0,
            borderRight: '1px solid var(--panel-border)',
            display: 'flex',
            flexDirection: 'column',
            minHeight: 0,
            background: 'var(--bg-1)',
          }}
        >
          {/* Online count header */}
          <div
            style={{
              padding: '5px 8px 3px',
              fontSize: 9,
              fontWeight: 700,
              letterSpacing: '0.12em',
              textTransform: 'uppercase',
              color: 'var(--fg-3)',
              borderBottom: '1px solid var(--line)',
              flexShrink: 0,
            }}
          >
            Online · {sortedRoster.length}
          </div>

          {/* Scrollable roster list */}
          <div
            style={{
              flex: 1,
              overflowY: 'auto',
              overflowX: 'hidden',
              minHeight: 0,
              padding: '2px 4px 6px',
            }}
          >
            {sortedRoster.length === 0 && incomingRequests.length === 0 ? (
              <div style={{ padding: '10px 8px', fontSize: 10.5, color: 'var(--fg-3)' }}>
                No one here yet
              </div>
            ) : (
              <>
                {/* Friends section */}
                {(incomingRequests.length > 0 || friendsOnline.length > 0) && (
                  <div style={{ marginBottom: 3 }}>
                    <GroupHeader
                      label="Friends"
                      count={friendsOnline.length + incomingRequests.length}
                      accent="var(--power)"
                    />
                    {incomingRequests.map((call) => (
                      <RequestRow
                        key={`req-${call}`}
                        callsign={call}
                        onOpen={openProfile}
                        onAccept={acceptFriend}
                        onDeny={denyFriend}
                      />
                    ))}
                    {friendsOnline.map((op) => (
                      <RosterRow
                        key={op.callsign}
                        op={op}
                        onOpen={openProfile}
                        relation="friend"
                        onStar={onStar}
                        onDm={openDm}
                        isAdmin={isAdmin}
                        onBan={onBan}
                      />
                    ))}
                  </div>
                )}

                {/* Band groups */}
                {rosterByBand.map(([band, ops]) => (
                  <div key={band} style={{ marginBottom: 2 }}>
                    <GroupHeader label={band} count={ops.length} accent="var(--accent-bright)" />
                    {ops.map((op) => (
                      <RosterRow
                        key={op.callsign}
                        op={op}
                        onOpen={openProfile}
                        relation={relationFor(op.callsign)}
                        onStar={onStar}
                        onDm={openDm}
                        isAdmin={isAdmin}
                        onBan={onBan}
                      />
                    ))}
                  </div>
                ))}
              </>
            )}
          </div>
        </div>

        {/* ── Right column: tab bar + thread + composer ── */}
        <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>

          {/* ── Tab bar ── */}
          <div
            role="tablist"
            aria-label="Chat rooms"
            style={{
              display: 'flex',
              alignItems: 'stretch',
              height: 30,
              borderBottom: '1px solid var(--panel-border)',
              background: 'var(--bg-1)',
              overflowX: 'auto',
              overflowY: 'hidden',
              flexShrink: 0,
            }}
          >
            {orderedRooms.map((room) => {
              const isDm = room.kind === 'dm';
              const isPrivate = room.kind !== 'public';
              const label = isDm
                ? (dmOther(room.id, ownCall) ?? room.name)
                : room.name;
              return (
                <TabItem
                  key={room.id}
                  id={room.id}
                  label={label}
                  isPrivate={isPrivate}
                  isActive={activeRoom === room.id}
                  unread={unreadByRoom[room.id] ?? 0}
                  closable={isDm}
                  onClick={() => setActiveRoom(room.id)}
                  onClose={isDm ? () => handleTabClose(room.id) : undefined}
                />
              );
            })}

            {/* Admin: create group room */}
            {isAdmin && (
              <button
                type="button"
                onClick={handleCreateRoom}
                aria-label="Create group room"
                title="Create group room"
                style={{
                  flexShrink: 0,
                  background: 'none',
                  border: 'none',
                  padding: '0 10px',
                  cursor: 'pointer',
                  fontSize: 16,
                  lineHeight: 1,
                  color: 'var(--fg-3)',
                  alignSelf: 'center',
                  display: 'flex',
                  alignItems: 'center',
                }}
              >
                +
              </button>
            )}
          </div>

          {/* Admin group management strip */}
          {isAdmin && activeRoomObj?.kind === 'group' && (
            <GroupManagementStrip
              roomId={activeRoom}
              members={activeRoomObj.members}
            />
          )}

          {/* ── Message thread ── */}
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
              borderTop: threadTopBorder,
              transition: `border-color var(--dur-fast) var(--ease-out)`,
            }}
          >
            {activeMessages.length === 0 ? (
              <div
                style={{
                  margin: 'auto',
                  fontSize: 12,
                  color: 'var(--fg-3)',
                  textAlign: 'center',
                  padding: 16,
                }}
              >
                {enabled
                  ? 'No messages yet'
                  : 'Chat is disabled — enable it to join the conversation.'}
              </div>
            ) : (
              activeMessages.map((m) => {
                const own = !!ownCall && m.from.toUpperCase() === ownCall.toUpperCase();
                return (
                  <MessageRow
                    key={m.id || `${m.from}-${m.ts}`}
                    msg={m}
                    own={own}
                    onOpen={openProfile}
                  />
                );
              })
            )}
          </div>

          {/* ── Composer ── */}
          <div
            style={{
              borderTop: '1px solid var(--panel-border)',
              padding: '5px 8px',
              display: 'flex',
              flexDirection: 'column',
              gap: 4,
              flexShrink: 0,
            }}
          >
            <div style={{ display: 'flex', gap: 6, alignItems: 'flex-end' }}>
              <textarea
                ref={inputRef}
                className="mono"
                value={draft}
                onChange={(e) => setDraft(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' && !e.shiftKey) {
                    e.preventDefault();
                    void doSend();
                  }
                }}
                placeholder={composerPlaceholder}
                disabled={!connected}
                rows={1}
                maxLength={MAX_MESSAGE_CHARS + 64}
                style={{
                  flex: 1,
                  resize: 'none',
                  overflowY: 'auto',
                  maxHeight: 90,
                  minHeight: 28,
                  padding: '5px 8px',
                  boxSizing: 'border-box',
                  borderRadius: 'var(--r-sm)',
                  border: isPrivateRoom
                    ? '1px solid rgba(255,177,60,0.28)'
                    : '1px solid var(--line-strong)',
                  background: connected ? '#0c0c10' : 'var(--bg-1)',
                  color: '#d8d8dc',
                  fontSize: 12,
                  lineHeight: 1.4,
                  outline: 'none',
                  transition: `border-color var(--dur-fast) var(--ease-out)`,
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
            {/* Character counter when near limit */}
            {draft.length > MAX_MESSAGE_CHARS * 0.8 && (
              <div
                style={{
                  fontSize: 9.5,
                  textAlign: 'right',
                  color: draft.length > MAX_MESSAGE_CHARS ? 'var(--tx)' : 'var(--fg-3)',
                  paddingRight: 2,
                }}
              >
                {draft.length}/{MAX_MESSAGE_CHARS}
              </div>
            )}
          </div>
        </div>
      </div>

      {/* ── QRZ profile overlay ── */}
      {profileCall ? (
        <ProfileOverlay callsign={profileCall} onClose={() => setProfileCall(null)} />
      ) : null}
    </div>
  );
}
