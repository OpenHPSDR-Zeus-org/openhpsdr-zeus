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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import type { CSSProperties } from 'react';
import { Headphones, Lock, Send, Unlock, Volume2, VolumeX } from 'lucide-react';
import {
  setReceiver,
  setReceiverMuted,
  setRx2,
  setTxReceiver,
} from '../../api/client';
import { bandOf } from '../../components/design/data';
import { receiverColorByIndex } from '../../components/spectrumReceiverColor';
import { VfoDisplay } from '../../components/VfoDisplay';
import { useConnectionStore } from '../../state/connection-store';
import {
  getDesiredReceiverCount,
  getReceiverAfGainDb,
  getReceiverVfoHz,
  optimisticSetReceiverAfGain,
  optimisticSetReceiverVfo,
  setExposedReceiverCount,
} from '../../state/receiver-state';
import { useVfoLockStore } from '../../state/vfo-lock-store';

export function VfoPanel() {
  const applyState = useConnectionStore((s) => s.applyState);
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const vfoBHz = useConnectionStore((s) => getReceiverVfoHz(s, 1));
  const rx2Enabled = useConnectionStore((s) => s.rx2Enabled);
  const rx2AfGainDb = useConnectionStore((s) => getReceiverAfGainDb(s, 1));
  const receivers = useConnectionStore((s) => s.receivers);
  const txReceiverIndex = useConnectionStore((s) => s.txReceiverIndex);
  const focusedRxIndex = useConnectionStore((s) => s.focusedRxIndex);
  const setFocusedRxIndex = useConnectionStore((s) => s.setFocusedRxIndex);
  const selectedRxIndices = useConnectionStore((s) => s.selectedRxIndices);
  const toggleRxSelection = useConnectionStore((s) => s.toggleRxSelection);
  const vfoLocked = useVfoLockStore((s) => s.locked);
  const toggleVfoLock = useVfoLockStore((s) => s.toggle);

  const patchRx2 = (req: {
    enabled?: boolean;
    vfoBHz?: number;
    afGainDb?: number;
  }) => {
    if (req.enabled !== undefined) useConnectionStore.setState({ rx2Enabled: req.enabled });
    // RX2 VFO/AF are canonical on receivers[1]; the helpers dual-write the flat
    // mirror so any not-yet-migrated reader stays in sync.
    if (req.vfoBHz !== undefined) optimisticSetReceiverVfo(1, req.vfoBHz);
    if (req.afGainDb !== undefined) optimisticSetReceiverAfGain(1, req.afGainDb);
    setRx2(req).then(applyState).catch(() => {});
  };

  // Transmit on any receiver's VFO (RX1=0, RX2=1, RX3+=index). The server moves
  // the TX DUC/CTUN LO to the chosen receiver; clamps an unexposed index to RX1.
  const chooseTxReceiver = (index: number) => {
    useConnectionStore.setState({ txReceiverIndex: index, txVfo: index === 1 ? 'B' : 'A' });
    setTxReceiver(index).then(applyState).catch(() => {});
  };

  // Per-RX listen/mute (Thetis chkMUT/chkRX2Mute). "audible" is the UI sense;
  // the wire/state field is `muted`. Optimistic so the lane reacts immediately.
  const toggleAudible = (index: number, audible: boolean) => {
    const muted = !audible;
    useConnectionStore.setState((s) => ({
      receivers: s.receivers.map((r) => (r.index === index ? { ...r, muted } : r)),
    }));
    setReceiverMuted(index, muted).then(applyState).catch(() => {});
  };

  const audibleOf = (index: number) =>
    !(receivers.find((r) => r.index === index)?.muted ?? false);

  // MULTI RX master toggle: enable the whole multi-DDC set the operator
  // configured in Settings → Receivers (remembered count, default 2), or
  // collapse back to RX1 only. Replaces the old single "+ RX2" affordance.
  const multiRxOn = rx2Enabled;
  const toggleMultiRx = () => {
    if (multiRxOn) {
      setFocusedRxIndex(0);
      setExposedReceiverCount(1);
    } else {
      setFocusedRxIndex(1);
      setExposedReceiverCount(getDesiredReceiverCount());
    }
  };

  // AF gain for the active receiver. RX1's level is the main RX volume (lives in
  // the toolbar), so the lane AF targets RX2 (flat field) and RX3+ (per-receiver).
  const afGainOf = (index: number) =>
    index === 1
      ? rx2AfGainDb
      : index >= 2
      ? receivers.find((r) => r.index === index)?.afGainDb ?? 0
      : 0;

  const setAfGain = (index: number, db: number) => {
    if (index === 1) {
      patchRx2({ afGainDb: db });
      return;
    }
    useConnectionStore.setState((s) => ({
      receivers: s.receivers.map((r) => (r.index === index ? { ...r, afGainDb: db } : r)),
    }));
    setReceiver(index, { afGainDb: db }).then(applyState).catch(() => {});
  };

  // Exposed receivers, in DDC order: RX1 always, RX2 when enabled, then every
  // active extra DDC. The chip rail lists these; one is the active detail.
  const lanes: { index: number; vfoHz: number; abId?: 'A' | 'B' }[] = [
    { index: 0, vfoHz, abId: 'A' },
  ];
  if (rx2Enabled) lanes.push({ index: 1, vfoHz: vfoBHz, abId: 'B' });
  for (const r of receivers.filter((r) => r.index >= 2 && r.enabled))
    lanes.push({ index: r.index, vfoHz: r.vfoHz });

  // lanes always contains RX1, so this fallback is just to satisfy the compiler.
  const active =
    lanes.find((l) => l.index === focusedRxIndex) ??
    lanes[0] ?? { index: 0, vfoHz, abId: 'A' as const };
  const activeTitle = `RX${active.index + 1}`;
  const activeLabel = `RX${active.index + 1}`;
  const activeAudible = audibleOf(active.index);
  const activeTx = txReceiverIndex === active.index;

  return (
    <div className="freq-panel vfo-md">
      {/* Compact chip rail — one chip per exposed receiver, scrolls past 4. */}
      <div className="vfo-md__rail" role="tablist" aria-label="Receivers">
        {lanes.map((l) => {
          const muted = !audibleOf(l.index);
          const isTx = txReceiverIndex === l.index;
          const isActive = l.index === active.index;
          const isSelected = selectedRxIndices.includes(l.index);
          return (
            <button
              key={l.index}
              type="button"
              role="tab"
              aria-selected={isActive}
              className={`vfo-chip ${isActive ? 'is-active' : ''} ${
                isSelected && !isActive ? 'is-selected' : ''
              } ${isTx ? 'is-tx' : ''} ${muted ? 'is-muted' : ''}`}
              style={{ '--vfo-filter-color': receiverColorByIndex(l.index) } as CSSProperties}
              onClick={(e) =>
                e.ctrlKey || e.metaKey ? toggleRxSelection(l.index) : setFocusedRxIndex(l.index)
              }
              title={`RX${l.index + 1}: click to focus, Ctrl/⌘-click to multi-select${
                isTx ? ' · transmitting here' : ''
              }${muted ? ' · muted' : ''}`}
            >
              <span className="vfo-chip__id">
                {l.index + 1}
                {isTx && <span className="vfo-chip__tx">TX</span>}
                {muted && <VolumeX size={9} />}
              </span>
              <span className="vfo-chip__freq mono">{(l.vfoHz / 1_000_000).toFixed(3)}</span>
              <span className="vfo-chip__band mono">{bandOf(l.vfoHz)}</span>
            </button>
          );
        })}
        <button
          type="button"
          className={`vfo-chip vfo-chip--add ${multiRxOn ? 'is-active' : ''}`}
          onClick={toggleMultiRx}
          title={
            multiRxOn
              ? 'Disable extra receivers (back to RX1 only)'
              : 'Enable multiple receivers (set how many in Settings → Receivers)'
          }
          aria-label="Toggle multiple receivers"
          aria-pressed={multiRxOn}
        >
          <Headphones size={13} />
          <span className="vfo-chip__band">MULTI RX</span>
        </button>
      </div>

      {/* Active receiver detail — the full-size tuning surface. */}
      <div
        className={`vfo-md__detail ${activeTx ? 'is-tx' : ''}`}
        style={{ '--vfo-filter-color': receiverColorByIndex(active.index) } as CSSProperties}
      >
        <div className="vfo-md__head">
          <span className="vfo-md__title">{activeTitle}</span>
          <span className="vfo-md__band mono">{bandOf(active.vfoHz)}</span>
        </div>
        <VfoDisplay
          key={active.index}
          {...(active.abId ? { receiver: active.abId } : { rxIndex: active.index })}
          label={activeLabel}
        />
        <div className="vfo-md__actions">
          <button
            type="button"
            className={`vfo-listen-key ${activeAudible ? 'is-on' : ''}`}
            onClick={() => toggleAudible(active.index, !activeAudible)}
            title={activeAudible ? `Mute ${activeLabel} audio` : `Hear ${activeLabel} audio`}
            aria-pressed={activeAudible}
          >
            {activeAudible ? <Volume2 size={13} /> : <VolumeX size={13} />}
          </button>
          <button
            type="button"
            className={`vfo-lock-key ${vfoLocked ? 'is-on' : ''}`}
            onClick={toggleVfoLock}
            title={
              vfoLocked
                ? 'VFO LOCKED — click to unlock and tune'
                : 'Lock VFO — block all retune (dial, wheel, panadapter, keyboard)'
            }
            aria-label={vfoLocked ? 'VFO locked — click to unlock' : 'Lock VFO'}
            aria-pressed={vfoLocked}
          >
            {vfoLocked ? <Lock size={13} /> : <Unlock size={13} />}
          </button>
          <button
            type="button"
            className={`vfo-tx-key ${activeTx ? 'is-selected' : ''}`}
            onClick={() => chooseTxReceiver(active.index)}
            title={`Transmit on ${activeTitle}`}
            aria-pressed={activeTx}
          >
            <Send size={13} />
            <span>TX</span>
          </button>
          {active.index >= 1 && (
            <label className="vfo-md__af mono" title={`${activeLabel} audio gain`}>
              <span>AF</span>
              <input
                type="range"
                min={-30}
                max={12}
                step={1}
                value={afGainOf(active.index)}
                onChange={(e) => setAfGain(active.index, Number(e.currentTarget.value))}
                aria-label={`${activeLabel} audio gain`}
              />
              <span className="vfo-md__af-val">{afGainOf(active.index).toFixed(0)}</span>
            </label>
          )}
        </div>
      </div>
    </div>
  );
}
