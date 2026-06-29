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
// ChatRosterOverlay — paints connected ZeusChat operators onto the panadapter
// at the frequency they're tuned to. Each shows a transmit/status dot + their
// callsign; clicking the callsign opens their QRZ card (same as the chat
// roster), and hovering reveals a DM icon that opens a direct message. Only
// operators sharing their frequency (friends) carry a freqHz, so only they can
// be placed — everyone else is null and silently skipped.
//
// Positioned by percentage of the visible span, identical to SpotOverlay, so no
// DOM measurement is needed on resize.

import { useCallback, useMemo, useState } from 'react';
import { useChatStore, type ChatOperator } from '../state/chat-store';
import { useDisplayStore } from '../state/display-store';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import { useLayoutStore } from '../state/layout-store';
import { openProfileCard } from '../state/profile-overlay-store';

const STATUS_COLOR: Record<string, string> = {
  rx: 'var(--ok)',
  tx: 'var(--tx)',
  away: 'var(--fg-3)',
};

const STATUS_LABEL: Record<string, string> = {
  rx: 'Receiving',
  tx: 'Transmitting',
  away: 'Away',
};

// Small chat-bubble glyph — mirrors the DM button in the chat roster row.
function DmBubble() {
  return (
    <svg
      width="11"
      height="11"
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
  );
}

// A single operator marker: vertical tick + a pill carrying the status dot and
// callsign, with a DM icon that pops out on hover. `lane` stacks markers that
// land on (nearly) the same frequency so their labels don't overlap.
function RosterMarker({
  op,
  pct,
  lane,
  onDm,
}: {
  op: ChatOperator;
  pct: number;
  lane: number;
  onDm: (callsign: string) => void;
}) {
  const [hovered, setHovered] = useState(false);
  const status = op.status ?? '';
  const dotColor = STATUS_COLOR[status] ?? 'var(--fg-4)';
  const isTx = status === 'tx';
  const tip = [
    op.callsign,
    STATUS_LABEL[status],
    op.mode ?? null,
    `${(op.freqHz! / 1e6).toFixed(4)} MHz`,
  ]
    .filter(Boolean)
    .join(' · ');

  return (
    <div
      // z-[16] keeps the callsign pill ABOVE the frequency-scale chrome — the
      // ruler (z-10), band overlay (z-11), and especially the green VFO dial
      // marker / frequency line (z-15) — so the line never draws across a
      // callsign. Stays below the VFO readout box (z-25), which owns the corner.
      className="pointer-events-none absolute inset-y-0 z-[16] -translate-x-1/2"
      style={{ left: `${pct}%` }}
    >
      {/* vertical tick — non-interactive so it never blocks click-to-tune */}
      <div
        className="absolute inset-y-0 w-px"
        style={{ background: dotColor, opacity: hovered ? 0.85 : 0.45 }}
      />
      {/* label pill — the interactive part */}
      <div
        className="pointer-events-auto absolute flex -translate-x-1/2 items-center gap-1 whitespace-nowrap rounded-sm px-1 py-0.5 leading-none"
        style={{
          top: 30 + lane * 16,
          background: 'rgba(8, 10, 14, 0.82)',
          border: `1px solid ${hovered ? dotColor : 'rgba(255,255,255,0.14)'}`,
          boxShadow: isTx ? '0 0 6px var(--tx)' : 'none',
          transition: 'border-color var(--dur-fast) var(--ease-out)',
        }}
        onMouseEnter={() => setHovered(true)}
        onMouseLeave={() => setHovered(false)}
        title={tip}
      >
        {/* status / transmit dot */}
        <span
          aria-hidden
          style={{
            display: 'inline-block',
            width: 6,
            height: 6,
            borderRadius: '50%',
            background: dotColor,
            boxShadow: isTx ? '0 0 5px var(--tx)' : 'none',
            flexShrink: 0,
          }}
        />
        {/* callsign — click opens the QRZ card, same as in chat */}
        <button
          type="button"
          className="font-mono text-[9px]"
          onClick={(e) => {
            e.stopPropagation();
            openProfileCard(op.callsign);
          }}
          title={`Open ${op.callsign} on QRZ`}
          style={{
            background: 'none',
            border: 'none',
            padding: 0,
            cursor: 'pointer',
            color: '#e6e6ea',
            letterSpacing: '0.04em',
            fontWeight: 600,
            lineHeight: 1,
            textShadow: '0 0 3px rgba(0,0,0,0.8)',
          }}
        >
          {op.callsign}
        </button>
        {/* DM icon — pops out on hover */}
        {hovered && (
          <button
            type="button"
            onClick={(e) => {
              e.stopPropagation();
              onDm(op.callsign);
            }}
            title={`Message ${op.callsign}`}
            aria-label={`Start DM with ${op.callsign}`}
            style={{
              display: 'flex',
              alignItems: 'center',
              background: 'none',
              border: 'none',
              padding: 0,
              marginLeft: 1,
              cursor: 'pointer',
              lineHeight: 1,
              color: 'var(--accent-bright)',
              flexShrink: 0,
            }}
          >
            <DmBubble />
          </button>
        )}
      </div>
    </div>
  );
}

export function ChatRosterOverlay() {
  const enabled = useChatStore((s) => s.enabled);
  const connected = useChatStore((s) => s.connected);
  const roster = useChatStore((s) => s.roster);
  const openDm = useChatStore((s) => s.openDm);
  const freqPublic = useChatStore((s) => s.freqPublic);
  const myCallsign = useChatStore((s) => s.callsign);
  const show = useDisplaySettingsStore((s) => s.showChatRosterOverlay);

  const centerHz = useDisplayStore((s) => s.centerHz);
  const hzPerPixel = useDisplayStore((s) => s.hzPerPixel);
  const width = useDisplayStore((s) => s.panDb?.length ?? 0);

  // Starting a DM from the panadapter surfaces the Chat tile if it isn't
  // already on the active layout — otherwise openDm would set the active room
  // on a panel the operator can't see. Chat is a singleton panel, so addTile is
  // a no-op when it already exists.
  const handleDm = useCallback(
    (callsign: string) => {
      const layout = useLayoutStore.getState();
      const hasChat = layout.workspace.tiles.some((t) => t.panelId === 'chat');
      if (!hasChat) layout.addTile('chat');
      openDm(callsign);
    },
    [openDm],
  );

  // Place only operators who share a frequency and fall inside the view. Lanes
  // stack labels that crowd the same x so callsigns stay readable. Memoised on
  // the geometry + roster so we don't re-sort every animation frame.
  const placed = useMemo(() => {
    if (!show || !enabled || !connected || !width || hzPerPixel <= 0) {
      return [] as Array<{ op: ChatOperator; pct: number; lane: number }>;
    }
    const spanHz = width * hzPerPixel;
    const startHz = Number(centerHz) - spanHz / 2;
    // Respect the eye toggle for your OWN marker. The relay strips your freq
    // from every other operator's roster the moment you hide it, so they stop
    // seeing your callsign here — but it always sends you your own freq (so the
    // rest of the UI can use it), which would otherwise leave your callsign
    // pinned to your own panadapter after you've hidden. Drop it so "hidden"
    // means hidden everywhere, including your own view.
    const mine = (myCallsign ?? '').toUpperCase();
    const inView = roster
      .filter((op) => typeof op.freqHz === 'number' && Number.isFinite(op.freqHz))
      .filter((op) => freqPublic || !mine || op.callsign.toUpperCase() !== mine)
      .map((op) => ({ op, pct: ((op.freqHz! - startHz) / spanHz) * 100 }))
      .filter(({ pct }) => pct >= -2 && pct <= 102)
      .sort((a, b) => a.pct - b.pct);

    // Greedy lane assignment: a marker shares lane 0 unless the previous marker
    // in a lane is closer than ~2.5% of the span, in which case it drops down.
    const laneLastPct: number[] = [];
    const MIN_GAP = 2.5;
    return inView.map(({ op, pct }) => {
      let lane = 0;
      let last = laneLastPct[lane];
      while (last !== undefined && pct - last < MIN_GAP) {
        lane += 1;
        last = laneLastPct[lane];
      }
      laneLastPct[lane] = pct;
      return { op, pct, lane };
    });
  }, [show, enabled, connected, roster, centerHz, hzPerPixel, width, freqPublic, myCallsign]);

  if (placed.length === 0) return null;

  return (
    <>
      {placed.map(({ op, pct, lane }) => (
        <RosterMarker key={op.callsign} op={op} pct={pct} lane={lane} onDm={handleDm} />
      ))}
    </>
  );
}
