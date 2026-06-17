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
import { Copy, Headphones, Repeat2, Send } from 'lucide-react';
import { setRx2, setTxVfo, setVfo, swapVfos, type Rx2AudioMode, type TxVfo } from '../../api/client';
import { bandOf } from '../../components/design/data';
import { spectrumReceiverFilterColor } from '../../components/spectrumReceiverColor';
import { VfoDisplay } from '../../components/VfoDisplay';
import { useConnectionStore } from '../../state/connection-store';

const AUDIO_MODES: readonly { mode: Rx2AudioMode; label: string; title: string }[] = [
  { mode: 'both', label: 'Both', title: 'Hear RX1 and RX2 together' },
  { mode: 'rx1', label: 'RX1', title: 'Hear RX1 only' },
  { mode: 'rx2', label: 'RX2', title: 'Hear RX2 only' },
];

export function VfoPanel() {
  const applyState = useConnectionStore((s) => s.applyState);
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const vfoBHz = useConnectionStore((s) => s.vfoBHz);
  const rx2Enabled = useConnectionStore((s) => s.rx2Enabled);
  const rx2AudioMode = useConnectionStore((s) => s.rx2AudioMode);
  const rx2AfGainDb = useConnectionStore((s) => s.rx2AfGainDb);
  const txVfo = useConnectionStore((s) => s.txVfo);
  const rxFocus = useConnectionStore((s) => s.rxFocus);
  const setRxFocus = useConnectionStore((s) => s.setRxFocus);

  const patchRx2 = (req: {
    enabled?: boolean;
    vfoBHz?: number;
    audioMode?: Rx2AudioMode;
    afGainDb?: number;
  }) => {
    const optimistic: Partial<ReturnType<typeof useConnectionStore.getState>> = {};
    if (req.enabled !== undefined) optimistic.rx2Enabled = req.enabled;
    if (req.vfoBHz !== undefined) optimistic.vfoBHz = req.vfoBHz;
    if (req.audioMode !== undefined) optimistic.rx2AudioMode = req.audioMode;
    if (req.afGainDb !== undefined) optimistic.rx2AfGainDb = req.afGainDb;
    useConnectionStore.setState(optimistic);
    setRx2(req).then(applyState).catch(() => {});
  };

  const chooseTxVfo = (next: TxVfo) => {
    useConnectionStore.setState({ txVfo: next });
    setTxVfo(next).then(applyState).catch(() => {});
  };

  const copyBToA = () => {
    useConnectionStore.setState({ vfoHz: vfoBHz });
    setVfo(vfoBHz).then(applyState).catch(() => {});
  };

  const toggleRx2 = () => {
    const enabled = !rx2Enabled;
    if (enabled) setRxFocus('B');
    else setRxFocus('A');
    patchRx2({ enabled });
  };

  const receiverRow = (receiver: TxVfo, freqHz: number, enabled = true) => {
    const focused = rxFocus === receiver;
    const txSelected = txVfo === receiver;
    const title = receiver === 'A' ? 'RX1 / VFO A' : 'RX2 / VFO B';
    const label = receiver === 'A' ? 'VFO A' : 'VFO B';
    const receiverFilterColor = spectrumReceiverFilterColor(receiver);
    const mutedByListenMode = enabled && rx2Enabled && rx2AudioMode !== 'both'
      && ((rx2AudioMode === 'rx1' && receiver === 'B') || (rx2AudioMode === 'rx2' && receiver === 'A'));
    return (
      <div
        className={`vfo-lane ${focused ? 'is-focused' : ''} ${txSelected ? 'is-tx' : ''} ${
          mutedByListenMode ? 'is-listen-muted' : ''
        } ${enabled ? '' : 'is-disabled'}`}
        style={{ '--vfo-filter-color': receiverFilterColor } as CSSProperties}
        onClick={() => {
          if (enabled) setRxFocus(receiver);
        }}
      >
        <button
          type="button"
          className="vfo-focus-key"
          disabled={!enabled}
          onClick={(e) => {
            e.stopPropagation();
            setRxFocus(receiver);
          }}
          title={`Focus ${title}`}
          aria-label={`Focus ${title}`}
          aria-pressed={focused}
        >
          {receiver}
        </button>
        <div className="vfo-lane-body">
          <div className="vfo-lane-meta">
            <span className="vfo-lane-title">
              {title}
            </span>
            <span className="vfo-lane-band mono">{bandOf(freqHz)}</span>
          </div>
          <VfoDisplay receiver={receiver} label={label} compact />
        </div>
        <button
          type="button"
          className={`vfo-tx-key ${txSelected ? 'is-selected' : ''}`}
          disabled={!enabled}
          onClick={(e) => {
            e.stopPropagation();
            chooseTxVfo(receiver);
          }}
          title={`Transmit on VFO ${receiver}`}
          aria-label={`Transmit on VFO ${receiver}`}
          aria-pressed={txSelected}
        >
          <Send size={13} />
          <span>TX</span>
        </button>
      </div>
    );
  };

  return (
    <div className="freq-panel vfo-dual-panel">
      <div className="vfo-command-strip">
        <button
          type="button"
          className={`vfo-rx2-pill ${rx2Enabled ? 'is-on' : ''}`}
          onClick={toggleRx2}
          aria-pressed={rx2Enabled}
          title={rx2Enabled ? 'Disable RX2' : 'Enable RX2'}
        >
          <Headphones size={13} />
          <span>RX2</span>
          <span className="vfo-status-dot" aria-hidden="true" />
        </button>
        <div className="vfo-copy-bank" role="group" aria-label="VFO copy and swap">
          <button
            type="button"
            className="vfo-tool-key"
            onClick={() => patchRx2({ vfoBHz: vfoHz })}
            title="Copy VFO A to VFO B"
            aria-label="Copy VFO A to VFO B"
          >
            <Copy size={13} />
            <span>A&gt;B</span>
          </button>
          <button
            type="button"
            className="vfo-tool-key"
            onClick={copyBToA}
            disabled={!rx2Enabled}
            title="Copy VFO B to VFO A"
            aria-label="Copy VFO B to VFO A"
          >
            <Copy size={13} />
            <span>B&gt;A</span>
          </button>
          <button
            type="button"
            className="vfo-tool-key"
            onClick={() => swapVfos().then(applyState).catch(() => {})}
            title="Swap VFO A and VFO B"
            aria-label="Swap VFO A and VFO B"
          >
            <Repeat2 size={14} />
            <span>Swap</span>
          </button>
        </div>
      </div>

      <div className="vfo-stack">
        {receiverRow('A', vfoHz)}
        {receiverRow('B', vfoBHz, rx2Enabled)}
      </div>

      <div className="vfo-audio-strip">
        <div className="vfo-listen-bank" role="group" aria-label="RX2 listen mode">
          <span className="vfo-audio-mark" title="Listen mode" aria-hidden="true">
            <Headphones size={13} />
          </span>
          {AUDIO_MODES.map((m) => (
            <button
              key={m.mode}
              type="button"
              className={`vfo-segment ${rx2AudioMode === m.mode ? 'is-active' : ''}`}
              disabled={!rx2Enabled}
              onClick={() => {
                if (m.mode === 'rx1') setRxFocus('A');
                if (m.mode === 'rx2') setRxFocus('B');
                patchRx2({ audioMode: m.mode });
              }}
              title={m.title}
            >
              {m.label}
            </button>
          ))}
        </div>
        <label className="vfo-af-control mono">
          <span className="vfo-af-label">AF</span>
          <input
            type="range"
            min={-30}
            max={12}
            step={1}
            value={rx2AfGainDb}
            disabled={!rx2Enabled}
            onChange={(e) => patchRx2({ afGainDb: Number(e.currentTarget.value) })}
            aria-label="RX2 audio gain"
          />
          <span className="vfo-af-value">{rx2AfGainDb.toFixed(0)} dB</span>
        </label>
      </div>
    </div>
  );
}
