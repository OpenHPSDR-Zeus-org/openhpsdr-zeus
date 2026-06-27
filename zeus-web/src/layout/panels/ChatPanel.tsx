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
// ChatPanel — operator-to-operator chat tile. Premium tabbed UI: left roster
// sidebar (public operators, friends, friend requests), tab bar across the top
// (Public → Groups → DMs), message thread, and auto-growing composer. Non-public
// rooms get a warm golden glow as the signature premium cue. Hydrated on mount
// via chat-store REST calls and kept live by 0x35 push frames.

import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import {
  useChatStore,
  dmOther,
  PUBLIC_ROOM,
  type ChatMessage,
  type ChatOperator,
  type ChatAttachment,
} from '../../state/chat-store';
import {
  compressImageToAttachment,
  ChatImageError,
  CHAT_IMAGE_ACCEPT,
} from '../../util/chat-image';
import { useQrzStore } from '../../state/qrz-store';
import { ConfirmDialog } from '../ConfirmDialog';
import { QrzCard } from '../../components/design/QrzCard';
import { qrzStationToContact } from '../../components/design/qrz-contact';
import type { Contact } from '../../components/design/data';
import type { QrzStation } from '../../api/qrz';

const MAX_MESSAGE_CHARS = 2000;

/**
 * A single-line text prompt rendered as a proper in-app dialog (wraps
 * ConfirmDialog with an input) — replaces window.prompt so it matches Zeus
 * chrome. Enter submits; empty input is a no-op. The input grabs focus after
 * ConfirmDialog's focus trap settles on Cancel.
 */
function PromptDialog({
  title,
  label,
  placeholder,
  confirmLabel,
  onSubmit,
  onCancel,
}: {
  title: string;
  label: string;
  placeholder?: string;
  confirmLabel: string;
  onSubmit: (value: string) => void;
  onCancel: () => void;
}) {
  const [value, setValue] = useState('');
  const inputRef = useRef<HTMLInputElement | null>(null);
  useEffect(() => {
    const id = window.setTimeout(() => {
      inputRef.current?.focus();
      inputRef.current?.select();
    }, 0);
    return () => window.clearTimeout(id);
  }, []);
  const submit = () => {
    const v = value.trim();
    if (v) onSubmit(v);
  };
  return (
    <ConfirmDialog
      title={title}
      confirmLabel={confirmLabel}
      intent="primary"
      onConfirm={submit}
      onCancel={onCancel}
    >
      <label style={{ display: 'flex', flexDirection: 'column', gap: 6, fontSize: 12.5, color: 'var(--fg-1)' }}>
        <span>{label}</span>
        <input
          ref={inputRef}
          className="mono"
          value={value}
          placeholder={placeholder}
          onChange={(e) => setValue(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              submit();
            }
          }}
          style={{
            padding: '6px 8px',
            borderRadius: 'var(--r-sm)',
            border: '1px solid var(--line-strong)',
            background: '#0c0c10',
            color: '#d8d8dc',
            fontSize: 13,
            outline: 'none',
          }}
        />
      </label>
    </ConfirmDialog>
  );
}

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
        {/* Frequency / mode — only present for friends sharing their freq
            (the relay strips freqHz for everyone else). */}
        {freq !== '—' && (
          <div
            className="mono"
            style={{
              fontSize: 9.5,
              color: 'var(--accent-bright)',
              letterSpacing: '0.02em',
              marginTop: 1,
              whiteSpace: 'nowrap',
              overflow: 'hidden',
              textOverflow: 'ellipsis',
            }}
          >
            {freq}
            {op.mode ? <span style={{ color: 'var(--fg-3)' }}> · {op.mode}</span> : null}
          </div>
        )}
      </div>
      {/* Action icons — grouped + spaced away from the callsign */}
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 6,
          flexShrink: 0,
          marginLeft: 8,
        }}
      >
        {/* DM button — appears on hover */}
        {hovered && (
          <button
            type="button"
            onClick={() => onDm(op.callsign)}
            title={`Message ${op.callsign}`}
            aria-label={`Start DM with ${op.callsign}`}
            style={{
              flexShrink: 0,
              display: 'flex',
              alignItems: 'center',
              background: 'none',
              border: 'none',
              padding: 0,
              cursor: 'pointer',
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
              display: 'flex',
              alignItems: 'center',
              background: 'none',
              border: 'none',
              padding: 0,
              cursor: 'pointer',
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
            display: 'flex',
            alignItems: 'center',
            background: 'none',
            border: 'none',
            padding: 0,
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

/** Small paperclip glyph for the attach button (inherits text color). */
function PaperclipIcon() {
  return (
    <svg
      width="14"
      height="14"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      style={{ display: 'block' }}
    >
      <path d="M21.44 11.05l-9.19 9.19a6 6 0 0 1-8.49-8.49l9.19-9.19a4 4 0 0 1 5.66 5.66l-9.2 9.19a2 2 0 0 1-2.83-2.83l8.49-8.48" />
    </svg>
  );
}

function MessageRow({
  msg,
  own,
  onOpen,
  onExpandImage,
}: {
  msg: ChatMessage;
  own: boolean;
  onOpen: (callsign: string) => void;
  onExpandImage: (att: ChatAttachment) => void;
}) {
  const att = msg.attachment;
  const hasText = msg.text.trim().length > 0;
  // Constrain the thumbnail to the message's native aspect when known so the
  // bubble doesn't jump as the image decodes.
  const ratio = att && att.width && att.height ? att.width / att.height : undefined;
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
          display: 'flex',
          flexDirection: 'column',
          alignItems: own ? 'flex-end' : 'flex-start',
          gap: 4,
          maxWidth: '85%',
        }}
      >
        {att ? (
          <button
            type="button"
            onClick={() => onExpandImage(att)}
            title={att.name ?? 'Open image'}
            style={{
              display: 'block',
              padding: 0,
              margin: 0,
              border: own ? '1px solid var(--accent-line)' : '1px solid var(--line)',
              borderRadius: 'var(--r-lg)',
              background: 'var(--bg-2)',
              cursor: 'zoom-in',
              overflow: 'hidden',
              lineHeight: 0,
              maxWidth: '100%',
            }}
          >
            <img
              src={att.dataUrl}
              alt={att.name ?? 'Shared photo'}
              loading="lazy"
              style={{
                display: 'block',
                maxWidth: 280,
                maxHeight: 280,
                width: 'auto',
                height: 'auto',
                aspectRatio: ratio ? String(ratio) : undefined,
                objectFit: 'cover',
              }}
            />
          </button>
        ) : null}
        {hasText ? (
          <div
            style={{
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
        ) : null}
      </div>
    </div>
  );
}

/**
 * Full-size image viewer. A dim backdrop with the photo centered; click anywhere
 * (or Esc) to close. Kept deliberately simple — no zoom/pan, just "see it big".
 */
function ImageLightbox({ att, onClose }: { att: ChatAttachment; onClose: () => void }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);
  return (
    <div
      role="dialog"
      aria-modal="true"
      onClick={onClose}
      style={{
        position: 'absolute',
        inset: 0,
        zIndex: 30,
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        justifyContent: 'center',
        gap: 8,
        padding: 16,
        background: 'rgba(0,0,0,0.82)',
        cursor: 'zoom-out',
      }}
    >
      <img
        src={att.dataUrl}
        alt={att.name ?? 'Shared photo'}
        onClick={(e) => e.stopPropagation()}
        style={{
          maxWidth: '100%',
          maxHeight: 'calc(100% - 56px)',
          objectFit: 'contain',
          borderRadius: 'var(--r-sm)',
          cursor: 'default',
        }}
      />
      <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
        {att.name ? (
          <span className="mono" style={{ fontSize: 10.5, color: 'var(--fg-2)' }}>
            {att.name}
          </span>
        ) : null}
        <a
          href={att.dataUrl}
          download={att.name ?? 'photo.jpg'}
          onClick={(e) => e.stopPropagation()}
          className="btn sm"
        >
          Download
        </a>
        <button type="button" className="btn sm" onClick={onClose}>
          Close
        </button>
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

// Violet glow for group rooms — the same soft-ring treatment as the gold DM
// glow, in the dedicated group hue (--chat-group, #b07cff) so a group tab reads
// as visually distinct from a DM at a glance.
const VIOLET_RING_IDLE =
  'inset 0 0 0 1px rgba(176,124,255,0.22), 0 0 8px rgba(176,124,255,0.10)';
const VIOLET_RING_HOVER =
  'inset 0 0 0 1px rgba(176,124,255,0.35), 0 0 12px rgba(176,124,255,0.16)';
const VIOLET_RING_ACTIVE =
  'inset 0 0 0 1px rgba(176,124,255,0.40), 0 0 14px rgba(176,124,255,0.20)';

/** Per-tab tone: which channel kind drives the accent color/glow. */
type TabTone = 'public' | 'group' | 'dm';

/**
 * Whether `me` is a member of `room` (case-insensitive). Returns true when our
 * callsign is unknown (pre-connect) so a group never shows as locked before we
 * know who we are.
 */
function isRoomMember(room: { members: string[] }, me: string | null): boolean {
  const meUp = (me ?? '').toUpperCase();
  if (!meUp) return true;
  return room.members.some((m) => m.toUpperCase() === meUp);
}

/** A small padlock, shown on a group tab the viewer isn't a member of. */
function LockGlyph() {
  return (
    <svg width="9" height="9" viewBox="0 0 12 12" fill="none" aria-hidden="true" style={{ flexShrink: 0 }}>
      <rect x="2.5" y="5.5" width="7" height="5" rx="1" stroke="currentColor" strokeWidth="1.1" />
      <path d="M4 5.5V4a2 2 0 0 1 4 0v1.5" stroke="currentColor" strokeWidth="1.1" />
    </svg>
  );
}

interface TabItemProps {
  id: string;
  label: string;
  tone: TabTone;
  /** Group the viewer isn't a member of: discoverable but locked until added. */
  locked?: boolean;
  isActive: boolean;
  unread: number;
  closable?: boolean;
  onClick: () => void;
  onClose?: () => void;
}

function TabItem({ id: _id, label, tone, locked, isActive, unread, closable, onClick, onClose }: TabItemProps) {
  const [hovered, setHovered] = useState(false);

  const ring = (idle: string, hover: string, active: string) =>
    isActive ? active : hovered ? hover : idle;
  const boxShadow =
    tone === 'dm'
      ? ring(GOLD_RING_IDLE, GOLD_RING_HOVER, GOLD_RING_ACTIVE)
      : tone === 'group'
      ? ring(VIOLET_RING_IDLE, VIOLET_RING_HOVER, VIOLET_RING_ACTIVE)
      : 'none';

  // Accent per channel kind: public=blue, group=violet, dm=gold.
  const accent =
    tone === 'dm' ? 'var(--power)' : tone === 'group' ? 'var(--chat-group)' : 'var(--accent-bright)';

  const borderBottom = isActive ? `2px solid ${accent}` : '2px solid transparent';

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
      {locked && (
        <span style={{ color: accent, display: 'inline-flex', opacity: 0.85 }} title="Invite-only — an admin must add you">
          <LockGlyph />
        </span>
      )}
      <span
        style={{
          fontSize: 11,
          fontWeight: isActive ? 700 : 500,
          letterSpacing: '0.04em',
          color: isActive
            ? tone === 'public'
              ? 'var(--fg-0)'
              : accent
            : 'var(--fg-2)',
          opacity: locked && !isActive ? 0.7 : 1,
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
            background:
              tone === 'dm' ? 'var(--power)' : tone === 'group' ? 'var(--chat-group)' : 'var(--accent)',
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

/**
 * Chevron button that scrolls the tab bar when the tabs overflow horizontally —
 * mirrors the topbar-controls scroll affordance. Both arrows appear together
 * whenever overflow exists (`show`); the direction with nothing left to scroll
 * keeps its column reserved (visibility) so the tab strip doesn't reflow as you
 * page through it.
 */
function TabScrollButton({
  direction,
  show,
  enabled,
  onClick,
}: {
  direction: -1 | 1;
  show: boolean;
  enabled: boolean;
  onClick: () => void;
}) {
  const left = direction < 0;
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={!enabled}
      aria-label={left ? 'Scroll tabs left' : 'Scroll tabs right'}
      title={left ? 'Scroll tabs left' : 'Scroll tabs right'}
      style={{
        display: show ? 'inline-flex' : 'none',
        alignItems: 'center',
        justifyContent: 'center',
        flex: '0 0 22px',
        width: 22,
        height: '100%',
        padding: 0,
        background: 'var(--bg-1)',
        border: 'none',
        borderRight: left ? '1px solid var(--line)' : 'none',
        borderLeft: left ? 'none' : '1px solid var(--line)',
        color: enabled ? 'var(--fg-0)' : 'var(--fg-4)',
        cursor: enabled ? 'pointer' : 'default',
        visibility: enabled ? 'visible' : 'hidden',
      }}
    >
      {left ? (
        <ChevronLeft size={14} strokeWidth={2.25} aria-hidden />
      ) : (
        <ChevronRight size={14} strokeWidth={2.25} aria-hidden />
      )}
    </button>
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
  const [dialog, setDialog] = useState<
    null | { k: 'add' } | { k: 'remove'; call: string } | { k: 'delete' }
  >(null);
  const addMember = useChatStore((s) => s.addMember);
  const removeMember = useChatStore((s) => s.removeMember);
  const deleteRoom = useChatStore((s) => s.deleteRoom);
  const setActiveRoom = useChatStore((s) => s.setActiveRoom);

  const handleAdd = () => setDialog({ k: 'add' });
  const handleRemove = (call: string) => setDialog({ k: 'remove', call });
  const handleDelete = () => setDialog({ k: 'delete' });

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

      {dialog?.k === 'add' && (
        <PromptDialog
          title="Add member"
          label="Callsign"
          placeholder="e.g. N9WAR"
          confirmLabel="Add"
          onSubmit={(v) => {
            void addMember(roomId, v.toUpperCase());
            setDialog(null);
          }}
          onCancel={() => setDialog(null)}
        />
      )}
      {dialog?.k === 'remove' && (
        <ConfirmDialog
          title="Remove member"
          confirmLabel="Remove"
          intent="danger"
          onConfirm={() => {
            void removeMember(roomId, dialog.call);
            setDialog(null);
          }}
          onCancel={() => setDialog(null)}
        >
          Remove <strong>{dialog.call}</strong> from this group?
        </ConfirmDialog>
      )}
      {dialog?.k === 'delete' && (
        <ConfirmDialog
          title="Delete group"
          confirmLabel="Delete"
          intent="danger"
          onConfirm={() => {
            void deleteRoom(roomId);
            setActiveRoom(PUBLIC_ROOM);
            setDialog(null);
          }}
          onCancel={() => setDialog(null)}
        >
          Delete this group room? This cannot be undone.
        </ConfirmDialog>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Admin console (relay moderators only — N9WAR / KB2UKA)
// ---------------------------------------------------------------------------

// Inline icons — kept local to the admin console so it reads as a self-contained
// premium control surface rather than emoji-decorated buttons.
function MegaphoneIcon() {
  return (
    <svg width="15" height="15" viewBox="0 0 16 16" fill="none" aria-hidden="true">
      <path
        d="M2.5 6.2 11 2.8v10.4L2.5 9.8H2a1 1 0 0 1-1-1V7.2a1 1 0 0 1 1-1h.5Z"
        stroke="currentColor"
        strokeWidth="1.3"
        strokeLinejoin="round"
      />
      <path d="M4 10v2.2a1 1 0 0 0 1 1h.6a1 1 0 0 0 1-1V10.6" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" />
      <path d="M13 6.2a2.4 2.4 0 0 1 0 3.6" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
    </svg>
  );
}

function ClearIcon() {
  return (
    <svg width="15" height="15" viewBox="0 0 16 16" fill="none" aria-hidden="true">
      <path d="M2.5 4h11" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
      <path d="M6 4V2.6a.8.8 0 0 1 .8-.8h2.4a.8.8 0 0 1 .8.8V4" stroke="currentColor" strokeWidth="1.3" />
      <path
        d="M4 4.6 4.6 13a1 1 0 0 0 1 .9h4.8a1 1 0 0 0 1-.9L12 4.6"
        stroke="currentColor"
        strokeWidth="1.3"
        strokeLinejoin="round"
      />
      <path d="M6.6 6.8v4.6M9.4 6.8v4.6" stroke="currentColor" strokeWidth="1.1" strokeLinecap="round" />
    </svg>
  );
}

function UnbanIcon() {
  return (
    <svg width="15" height="15" viewBox="0 0 16 16" fill="none" aria-hidden="true">
      <circle cx="8" cy="8" r="6" stroke="currentColor" strokeWidth="1.3" />
      <path d="M5.3 8.2 7 9.9 10.8 6" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

function ShieldIcon() {
  return (
    <svg width="13" height="13" viewBox="0 0 16 16" fill="none" aria-hidden="true">
      <path
        d="M8 1.6 13 3.4v4.2c0 3.2-2.1 5.6-5 6.8-2.9-1.2-5-3.6-5-6.8V3.4L8 1.6Z"
        stroke="currentColor"
        strokeWidth="1.3"
        strokeLinejoin="round"
      />
      <path d="M5.8 8 7.4 9.6 10.4 6.2" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

const ADMIN_GOLD_RING = 'inset 0 0 0 1px rgba(255,177,60,0.22), 0 0 10px rgba(255,177,60,0.07)';

/**
 * A single action in the admin console: an icon, a title, a one-line description
 * of exactly what it does and who it touches, and the trigger button. The verbose
 * description is deliberate — these are network-wide, irreversible actions and the
 * operator should never have to guess at the blast radius.
 */
function AdminAction({
  icon,
  title,
  description,
  actionLabel,
  danger,
  onClick,
}: {
  icon: ReactNode;
  title: string;
  description: string;
  actionLabel: string;
  danger?: boolean;
  onClick: () => void;
}) {
  const [hovered, setHovered] = useState(false);
  const accent = danger ? 'var(--tx)' : 'var(--power)';
  return (
    <div
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 10,
        padding: '7px 9px',
        borderRadius: 'var(--r-sm)',
        background: hovered ? 'var(--bg-2)' : 'var(--bg-2)',
        border: '1px solid var(--line)',
        boxShadow: hovered ? ADMIN_GOLD_RING : 'none',
        transition: 'box-shadow var(--dur-fast) var(--ease-out)',
      }}
    >
      <span
        aria-hidden
        style={{
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          width: 26,
          height: 26,
          flexShrink: 0,
          borderRadius: 'var(--r-sm)',
          background: danger ? 'var(--tx-soft)' : 'var(--power-soft)',
          color: accent,
        }}
      >
        {icon}
      </span>
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ fontSize: 11.5, fontWeight: 700, color: 'var(--fg-0)', letterSpacing: '0.02em' }}>
          {title}
        </div>
        <div style={{ fontSize: 10, color: 'var(--fg-3)', lineHeight: 1.35, marginTop: 1 }}>
          {description}
        </div>
      </div>
      <button
        type="button"
        className="btn sm"
        onClick={onClick}
        style={
          danger
            ? { flexShrink: 0, color: 'var(--tx)', borderColor: 'var(--tx)' }
            : { flexShrink: 0 }
        }
      >
        {actionLabel}
      </button>
    </div>
  );
}

/**
 * The unban surface: a live list of every currently-banned callsign (relay-
 * authoritative, persisted across relay restarts). Each row lifts that ban; the
 * relay echoes the updated list back so the dialog updates in place and stays
 * open for unbanning several at once.
 */
function BanListDialog({
  bans,
  onUnban,
  onClose,
}: {
  bans: string[];
  onUnban: (callsign: string) => void;
  onClose: () => void;
}) {
  return (
    <ConfirmDialog
      title="Banned operators"
      confirmLabel="Done"
      cancelLabel="Close"
      intent="primary"
      onConfirm={onClose}
      onCancel={onClose}
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8, minWidth: 248 }}>
        <div style={{ fontSize: 11, color: 'var(--fg-3)', lineHeight: 1.4 }}>
          Bans are stored on the relay and persist across restarts. Lift one to let
          that callsign reconnect to ZeusChat.
        </div>
        {bans.length === 0 ? (
          <div
            style={{
              padding: '12px 8px',
              textAlign: 'center',
              fontSize: 12,
              color: 'var(--fg-3)',
              border: '1px dashed var(--line)',
              borderRadius: 'var(--r-sm)',
            }}
          >
            No operators are currently banned.
          </div>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 4, maxHeight: 260, overflowY: 'auto' }}>
            {bans.map((call) => (
              <div
                key={call}
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: 8,
                  padding: '5px 8px',
                  borderRadius: 'var(--r-sm)',
                  background: 'var(--bg-2)',
                  border: '1px solid var(--line)',
                }}
              >
                <span
                  className="mono"
                  style={{
                    flex: 1,
                    fontWeight: 700,
                    fontSize: 12.5,
                    color: 'var(--fg-0)',
                    letterSpacing: '0.04em',
                    overflow: 'hidden',
                    textOverflow: 'ellipsis',
                  }}
                >
                  {call}
                </span>
                <button
                  type="button"
                  className="btn sm"
                  onClick={() => onUnban(call)}
                  title={`Unban ${call}`}
                  style={{ flexShrink: 0, color: 'var(--power)', borderColor: 'var(--power)' }}
                >
                  Unban
                </button>
              </div>
            ))}
          </div>
        )}
      </div>
    </ConfirmDialog>
  );
}

/**
 * Collapsible strip of network-wide moderator tools, shown only to operators the
 * relay flagged as admins (N9WAR / KB2UKA). Gathers the actions that aren't tied
 * to a single roster row or room: global announcement, clear the public lobby,
 * and unban. Per-row ban and per-group management live with their row/tab.
 */
function AdminConsole() {
  const [open, setOpen] = useState(false);
  const [dialog, setDialog] = useState<null | 'broadcast' | 'clear' | 'unban'>(null);
  const broadcast = useChatStore((s) => s.broadcast);
  const clearRoom = useChatStore((s) => s.clearRoom);
  const unban = useChatStore((s) => s.unban);
  const listBans = useChatStore((s) => s.listBans);
  const bannedUsers = useChatStore((s) => s.bannedUsers);
  const ownCall = useChatStore((s) => s.callsign);

  // Refresh the ban list whenever the console is opened, so the count + list are
  // current without waiting for the next ban/unban push.
  useEffect(() => {
    if (open) void listBans();
  }, [open, listBans]);

  const banCount = bannedUsers.length;

  return (
    <div
      style={{
        borderBottom: '1px solid var(--panel-border)',
        background: 'var(--bg-1)',
        boxShadow: open ? 'inset 0 0 0 1px rgba(255,177,60,0.10)' : 'none',
        flexShrink: 0,
      }}
    >
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 7,
          width: '100%',
          padding: '5px 10px',
          background: 'none',
          border: 'none',
          cursor: 'pointer',
          fontSize: 9.5,
          fontWeight: 700,
          letterSpacing: '0.12em',
          textTransform: 'uppercase',
          color: 'var(--power)',
        }}
      >
        <span style={{ fontSize: 9, opacity: 0.7 }}>{open ? '▼' : '▶'}</span>
        <span style={{ display: 'flex', alignItems: 'center', color: 'var(--power)' }}>
          <ShieldIcon />
        </span>
        Admin Console
        <span
          style={{
            marginLeft: 'auto',
            padding: '1px 6px',
            borderRadius: 'var(--r-lg)',
            background: 'var(--power-soft)',
            color: 'var(--power)',
            fontSize: 8.5,
            fontWeight: 700,
            letterSpacing: '0.10em',
          }}
        >
          MODERATOR
        </span>
      </button>
      {open && (
        <div style={{ padding: '0 10px 9px', display: 'flex', flexDirection: 'column', gap: 6 }}>
          <div
            style={{
              fontSize: 10,
              color: 'var(--fg-3)',
              lineHeight: 1.4,
              padding: '0 1px 3px',
            }}
          >
            Network-wide moderation for the public relay. You are signed in as{' '}
            <span className="mono" style={{ color: 'var(--accent-bright)', fontWeight: 700 }}>
              {ownCall ?? '—'}
            </span>
            . These actions affect <strong style={{ color: 'var(--fg-1)' }}>every connected operator</strong>.
          </div>

          <AdminAction
            icon={<MegaphoneIcon />}
            title="Global message"
            description="Broadcast a one-off announcement banner to every operator, in any room."
            actionLabel="Compose"
            onClick={() => setDialog('broadcast')}
          />
          <AdminAction
            icon={<UnbanIcon />}
            title="Unban operator"
            description={
              banCount === 0
                ? 'No operators are currently banned.'
                : `${banCount} operator${banCount === 1 ? '' : 's'} banned — open the list to lift a ban.`
            }
            actionLabel="Manage"
            onClick={() => {
              void listBans();
              setDialog('unban');
            }}
          />
          <AdminAction
            icon={<ClearIcon />}
            title="Clear public chat"
            description="Permanently wipe the public lobby history for everyone. Cannot be undone."
            actionLabel="Clear"
            danger
            onClick={() => setDialog('clear')}
          />
        </div>
      )}

      {dialog === 'broadcast' && (
        <PromptDialog
          title="Global message"
          label="Announcement broadcast to every operator"
          placeholder="e.g. Net starting now on 7.200"
          confirmLabel="Send"
          onSubmit={(v) => {
            void broadcast(v);
            setDialog(null);
          }}
          onCancel={() => setDialog(null)}
        />
      )}
      {dialog === 'unban' && (
        <BanListDialog
          bans={bannedUsers}
          onUnban={(call) => void unban(call)}
          onClose={() => setDialog(null)}
        />
      )}
      {dialog === 'clear' && (
        <ConfirmDialog
          title="Clear public chat"
          confirmLabel="Clear"
          intent="danger"
          onConfirm={() => {
            void clearRoom(PUBLIC_ROOM);
            setDialog(null);
          }}
          onCancel={() => setDialog(null)}
        >
          Permanently delete <strong>all messages</strong> in the public lobby for everyone? This
          cannot be undone.
        </ConfirmDialog>
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
  const announcement = useChatStore((s) => s.announcement);
  const dismissAnnouncement = useChatStore((s) => s.dismissAnnouncement);

  const refreshStatus = useChatStore((s) => s.refreshStatus);
  const setEnabled = useChatStore((s) => s.setEnabled);
  const setPanelVisible = useChatStore((s) => s.setPanelVisible);
  const send = useChatStore((s) => s.send);
  const loadHistory = useChatStore((s) => s.loadHistory);
  const loadRoster = useChatStore((s) => s.loadRoster);
  const loadRooms = useChatStore((s) => s.loadRooms);
  const loadFriends = useChatStore((s) => s.loadFriends);
  const setActiveRoom = useChatStore((s) => s.setActiveRoom);
  const openDm = useChatStore((s) => s.openDm);
  const closeDm = useChatStore((s) => s.closeDm);
  const hiddenDms = useChatStore((s) => s.hiddenDms);
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
  // Pending inline photo: compressed and ready to send, shown as a preview chip
  // above the composer until the operator sends or removes it.
  const [pendingAttachment, setPendingAttachment] = useState<ChatAttachment | null>(null);
  const [attaching, setAttaching] = useState(false);
  const [attachError, setAttachError] = useState<string | null>(null);
  // The image currently open full-size in the lightbox, if any.
  const [lightbox, setLightbox] = useState<ChatAttachment | null>(null);
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const inputRef = useRef<HTMLTextAreaElement | null>(null);
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  // Horizontal scroll affordance for the room tab bar — when the tabs overflow,
  // page them with chevron buttons just like the topbar-controls strip.
  const tabBarRef = useRef<HTMLDivElement | null>(null);
  const [tabScroll, setTabScroll] = useState({ canLeft: false, canRight: false });

  const syncTabScroll = useCallback(() => {
    const el = tabBarRef.current;
    if (!el) return;
    const maxScroll = Math.max(0, el.scrollWidth - el.clientWidth);
    const next = {
      canLeft: el.scrollLeft > 1,
      canRight: maxScroll > 1 && el.scrollLeft < maxScroll - 1,
    };
    setTabScroll((prev) =>
      prev.canLeft === next.canLeft && prev.canRight === next.canRight ? prev : next,
    );
  }, []);

  const scrollTabs = useCallback(
    (direction: -1 | 1) => {
      const el = tabBarRef.current;
      if (!el) return;
      const amount = Math.max(120, Math.floor(el.clientWidth * 0.75));
      el.scrollBy({ left: direction * amount, behavior: 'smooth' });
      window.setTimeout(syncTabScroll, 180);
    },
    [syncTabScroll],
  );

  // Re-evaluate the arrows every render (tabs are added/removed as DMs and
  // groups open/close) and on container resize.
  useEffect(() => {
    syncTabScroll();
  });
  useEffect(() => {
    const el = tabBarRef.current;
    if (!el) return;
    window.addEventListener('resize', syncTabScroll);
    const ro =
      typeof ResizeObserver !== 'undefined' ? new ResizeObserver(syncTabScroll) : null;
    ro?.observe(el);
    return () => {
      window.removeEventListener('resize', syncTabScroll);
      ro?.disconnect();
    };
  }, [syncTabScroll]);

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

  // Presence is gated on the operator actually showing this panel: heartbeat
  // "visible" while mounted (re-pinged so a closed browser lapses on the
  // backend timeout) and "hidden" on unmount, which drops us off the roster.
  useEffect(() => {
    void setPanelVisible(true);
    const id = window.setInterval(() => void setPanelVisible(true), 15_000);
    return () => {
      window.clearInterval(id);
      void setPanelVisible(false);
    };
  }, [setPanelVisible]);

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

  const [banTarget, setBanTarget] = useState<string | null>(null);
  const [creatingRoom, setCreatingRoom] = useState(false);

  const onBan = useCallback((callsign: string) => setBanTarget(callsign), []);

  // Tab ordering: public first, then groups, then DMs
  const orderedRooms = useMemo(() => {
    const hidden = new Set(hiddenDms);
    const pub = rooms.filter((r) => r.kind === 'public');
    const grp = rooms.filter((r) => r.kind === 'group');
    const dms = rooms.filter((r) => r.kind === 'dm' && !hidden.has(r.id));
    return [...pub, ...grp, ...dms];
  }, [rooms, hiddenDms]);

  const activeRoomObj = useMemo(
    () => rooms.find((r) => r.id === activeRoom) ?? null,
    [rooms, activeRoom],
  );

  // Tone of the active room drives the thread/composer accent (group=violet, dm=gold).
  const activeTone: TabTone =
    activeRoomObj?.kind === 'group' ? 'group' : activeRoomObj?.kind === 'dm' ? 'dm' : 'public';
  // The active room is a group the operator hasn't been added to: discoverable
  // but invite-only — show a locked body and block the composer until an admin
  // adds them. (Admins still get the management strip below to add members.)
  const lockedActive =
    !!activeRoomObj && activeRoomObj.kind === 'group' && !isRoomMember(activeRoomObj, ownCall);

  // Placeholder label for the composer
  const composerPlaceholder = (() => {
    if (!connected) return 'Not connected';
    if (!activeRoomObj) return 'Message… (Enter to send)';
    if (lockedActive) return 'Invite-only group — an admin must add you to post';
    if (activeRoomObj.kind === 'dm') {
      const other = dmOther(activeRoomObj.id, ownCall);
      return `Message @${other ?? activeRoomObj.name} (Enter to send, Shift+Enter for newline)`;
    }
    if (activeRoomObj.kind === 'group') {
      return `Message #${activeRoomObj.name} (Enter to send, Shift+Enter for newline)`;
    }
    return 'Message everyone (Enter to send, Shift+Enter for newline)';
  })();

  // Sendable when connected and there's either text within the limit or a
  // pending photo (image-only messages are allowed).
  const canSend =
    enabled &&
    connected &&
    !lockedActive &&
    draft.length <= MAX_MESSAGE_CHARS &&
    (draft.trim().length > 0 || pendingAttachment !== null);

  const doSend = useCallback(async () => {
    const text = draft.trim();
    const att = pendingAttachment;
    if (!connected || text.length > MAX_MESSAGE_CHARS) return;
    if (!text && !att) return;
    // Clear optimistically; restore on failure so nothing is silently lost.
    setDraft('');
    setPendingAttachment(null);
    const ok = await send(text, att);
    if (!ok) {
      setDraft(text);
      setPendingAttachment(att);
    }
  }, [draft, pendingAttachment, connected, send]);

  // Compress a chosen/pasted/dropped image and stage it for sending.
  const attachFile = useCallback(async (file: File | null | undefined) => {
    if (!file) return;
    setAttachError(null);
    setAttaching(true);
    try {
      const att = await compressImageToAttachment(file);
      setPendingAttachment(att);
      // Return focus to the composer so a caption can be typed immediately.
      inputRef.current?.focus();
    } catch (err) {
      setAttachError(
        err instanceof ChatImageError ? err.message : "Couldn't attach that image.",
      );
    } finally {
      setAttaching(false);
    }
  }, []);

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

  // Close (hide) a DM tab; a new message or reopening brings it back.
  const handleTabClose = useCallback((id: string) => closeDm(id), [closeDm]);

  // Admin: create group room
  const handleCreateRoom = () => setCreatingRoom(true);

  // Accent thread/composer border for private rooms — violet for groups, gold
  // for DMs, none for the public lobby.
  const threadTopBorder =
    activeTone === 'group'
      ? '2px solid rgba(176,124,255,0.30)'
      : activeTone === 'dm'
      ? '2px solid rgba(255,177,60,0.30)'
      : '1px solid transparent';
  const composerBorder =
    activeTone === 'group'
      ? '1px solid rgba(176,124,255,0.28)'
      : activeTone === 'dm'
      ? '1px solid rgba(255,177,60,0.28)'
      : '1px solid var(--line-strong)';

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

      {/* ── Global announcement banner (admin broadcast) ── */}
      {announcement && (
        <div
          style={{
            display: 'flex',
            alignItems: 'flex-start',
            gap: 8,
            padding: '7px 10px',
            borderBottom: '1px solid var(--panel-border)',
            background: 'var(--accent-soft)',
            fontSize: 11.5,
            color: 'var(--fg-1)',
            lineHeight: 1.4,
            flexShrink: 0,
          }}
        >
          <span
            aria-hidden
            style={{ display: 'flex', alignItems: 'center', color: 'var(--accent-bright)', marginTop: 1 }}
          >
            <MegaphoneIcon />
          </span>
          <div style={{ flex: 1, minWidth: 0, wordBreak: 'break-word' }}>
            {announcement.from && (
              <span className="mono" style={{ fontWeight: 700, color: 'var(--accent-bright)', marginRight: 6 }}>
                {announcement.from}
              </span>
            )}
            <span>{announcement.text}</span>
          </div>
          <button
            type="button"
            onClick={dismissAnnouncement}
            aria-label="Dismiss announcement"
            style={{
              background: 'none',
              border: 'none',
              padding: 2,
              cursor: 'pointer',
              color: 'var(--fg-3)',
              fontSize: 11,
              lineHeight: 1,
              flexShrink: 0,
            }}
          >
            ✕
          </button>
        </div>
      )}

      {/* ── Admin console (relay moderators only) ── */}
      {isAdmin && <AdminConsole />}

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
        {/* minWidth:0 is load-bearing — without it this flex child keeps its
            content's intrinsic min-content width (the tab strip's TabItems are
            flexShrink:0), so the whole column overflows the body and gets clipped
            instead of the inner tab bar scrolling. That left scrollWidth ===
            clientWidth, so the overflow arrows never lit up. */}
        <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0, minWidth: 0 }}>

          {/* ── Tab bar ── */}
          <div
            style={{
              display: 'flex',
              alignItems: 'stretch',
              height: 30,
              borderBottom: '1px solid var(--panel-border)',
              background: 'var(--bg-1)',
              flexShrink: 0,
            }}
          >
            <TabScrollButton
              direction={-1}
              show={tabScroll.canLeft || tabScroll.canRight}
              enabled={tabScroll.canLeft}
              onClick={() => scrollTabs(-1)}
            />
            <div
              ref={tabBarRef}
              role="tablist"
              aria-label="Chat rooms"
              onScroll={syncTabScroll}
              style={{
                display: 'flex',
                alignItems: 'stretch',
                flex: 1,
                minWidth: 0,
                overflowX: 'auto',
                overflowY: 'hidden',
                scrollbarWidth: 'none',
              }}
            >
              {orderedRooms.map((room) => {
                const isDm = room.kind === 'dm';
                const tone: TabTone =
                  room.kind === 'public' ? 'public' : room.kind === 'group' ? 'group' : 'dm';
                // A group the viewer isn't a member of: visible but locked. Only
                // flagged once we know our own callsign (avoids a false lock pre-connect).
                const locked = room.kind === 'group' && !isRoomMember(room, ownCall);
                const label = isDm
                  ? (dmOther(room.id, ownCall) ?? room.name)
                  : room.name;
                return (
                  <TabItem
                    key={room.id}
                    id={room.id}
                    label={label}
                    tone={tone}
                    locked={locked}
                    isActive={activeRoom === room.id}
                    unread={unreadByRoom[room.id] ?? 0}
                    closable={isDm}
                    onClick={() => setActiveRoom(room.id)}
                    onClose={isDm ? () => handleTabClose(room.id) : undefined}
                  />
                );
              })}
            </div>

            {/* Admin: create group room — pinned so it stays reachable
                regardless of how far the tab strip is scrolled. */}
            {isAdmin && (
              <button
                type="button"
                onClick={handleCreateRoom}
                aria-label="Create group room"
                title="Create group room"
                style={{
                  flexShrink: 0,
                  background: 'var(--bg-1)',
                  border: 'none',
                  borderLeft: '1px solid var(--line)',
                  padding: '0 10px',
                  cursor: 'pointer',
                  fontSize: 16,
                  lineHeight: 1,
                  color: 'var(--fg-3)',
                  display: 'flex',
                  alignItems: 'center',
                }}
              >
                +
              </button>
            )}
            <TabScrollButton
              direction={1}
              show={tabScroll.canLeft || tabScroll.canRight}
              enabled={tabScroll.canRight}
              onClick={() => scrollTabs(1)}
            />
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
            {lockedActive ? (
              <div
                style={{
                  margin: 'auto',
                  display: 'flex',
                  flexDirection: 'column',
                  alignItems: 'center',
                  gap: 8,
                  maxWidth: 260,
                  textAlign: 'center',
                  padding: 16,
                  color: 'var(--fg-3)',
                }}
              >
                <span style={{ color: 'var(--chat-group)', display: 'inline-flex' }}>
                  <LockGlyph />
                </span>
                <div style={{ fontSize: 12.5, fontWeight: 700, color: 'var(--chat-group)' }}>
                  {activeRoomObj?.name}
                </div>
                <div style={{ fontSize: 11.5, lineHeight: 1.5 }}>
                  This is an invite-only group. An admin must add you before you can
                  read or post here.
                </div>
              </div>
            ) : activeMessages.length === 0 ? (
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
                    onExpandImage={setLightbox}
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
            {/* Hidden file picker driven by the paperclip button. */}
            <input
              ref={fileInputRef}
              type="file"
              accept={CHAT_IMAGE_ACCEPT}
              style={{ display: 'none' }}
              onChange={(e) => {
                void attachFile(e.target.files?.[0]);
                e.target.value = ''; // allow re-picking the same file
              }}
            />

            {/* Pending photo preview + attach error. */}
            {pendingAttachment ? (
              <div
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: 8,
                  padding: 4,
                  borderRadius: 'var(--r-sm)',
                  background: 'var(--bg-2)',
                  border: '1px solid var(--line)',
                }}
              >
                <img
                  src={pendingAttachment.dataUrl}
                  alt={pendingAttachment.name ?? 'Attached photo'}
                  style={{
                    width: 40,
                    height: 40,
                    objectFit: 'cover',
                    borderRadius: 'var(--r-sm)',
                    flexShrink: 0,
                  }}
                />
                <span
                  className="mono"
                  style={{
                    flex: 1,
                    minWidth: 0,
                    fontSize: 10.5,
                    color: 'var(--fg-2)',
                    overflow: 'hidden',
                    textOverflow: 'ellipsis',
                    whiteSpace: 'nowrap',
                  }}
                >
                  {pendingAttachment.name ?? 'photo.jpg'}
                  {pendingAttachment.size
                    ? ` · ${Math.max(1, Math.round(pendingAttachment.size / 1024))} KB`
                    : ''}
                </span>
                <button
                  type="button"
                  className="btn sm"
                  onClick={() => setPendingAttachment(null)}
                  title="Remove photo"
                >
                  ✕
                </button>
              </div>
            ) : null}
            {attachError ? (
              <div className="mono" style={{ fontSize: 10, color: 'var(--tx)', paddingLeft: 2 }}>
                {attachError}
              </div>
            ) : null}

            <div style={{ display: 'flex', gap: 6, alignItems: 'flex-end' }}>
              {/* Paperclip — attach a photo. */}
              <button
                type="button"
                className="btn sm"
                disabled={!connected || attaching || lockedActive}
                onClick={() => fileInputRef.current?.click()}
                title={connected ? (lockedActive ? 'Not a member of this group' : 'Attach a photo') : 'Not connected'}
                aria-label="Attach a photo"
                style={{ flexShrink: 0, padding: '5px 8px' }}
              >
                {attaching ? '…' : <PaperclipIcon />}
              </button>
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
                onPaste={(e) => {
                  // Paste an image straight from the clipboard, like texting.
                  const item = Array.from(e.clipboardData.items).find((it) =>
                    it.type.startsWith('image/'),
                  );
                  if (item) {
                    e.preventDefault();
                    void attachFile(item.getAsFile());
                  }
                }}
                placeholder={composerPlaceholder}
                disabled={!connected || lockedActive}
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
                  border: composerBorder,
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

      {/* ── Full-size photo viewer ── */}
      {lightbox ? <ImageLightbox att={lightbox} onClose={() => setLightbox(null)} /> : null}

      {/* ── Moderation / room dialogs (proper in-app, not window.*) ── */}
      {banTarget ? (
        <ConfirmDialog
          title="Ban operator"
          confirmLabel="Ban"
          intent="danger"
          onConfirm={() => {
            void ban(banTarget);
            setBanTarget(null);
          }}
          onCancel={() => setBanTarget(null)}
        >
          Ban <strong>{banTarget}</strong> from ZeusChat? They’ll be disconnected and blocked from
          reconnecting.
        </ConfirmDialog>
      ) : null}
      {creatingRoom ? (
        <PromptDialog
          title="New group"
          label="Group name"
          placeholder="e.g. Net Control"
          confirmLabel="Create"
          onSubmit={(v) => {
            void createRoom(v);
            setCreatingRoom(false);
          }}
          onCancel={() => setCreatingRoom(false)}
        />
      ) : null}
    </div>
  );
}
