// SPDX-License-Identifier: GPL-2.0-or-later
//
// Ft8ReceivePanel — the RECEIVE region of the FT8/FT4 workspace. It REUSES the
// shipping spectrum surfaces: a short Panadapter header strip + the full
// WaterfallSurface (WebGPU heightfield, WebGL2 fallback), both on receiver A,
// exactly as HeroPanel mounts them. A transparent Ft8WaterfallOverlay sits on
// top for FT8 click-to-offset, the RX/TX cursors, and decode ticks; the WF
// SPEED / ZOOM / OFFSET strip sits beneath. No new renderer.
//
// The display is framed onto the FT8 passband by ft8-framing (called from
// digital-mode entry), so the panel just composes — it reads the live geometry,
// it does not configure the radio.

import { Panadapter } from '../../components/Panadapter';
import { WaterfallSurface } from '../../components/WaterfallSurface';
import { useConnectionStore } from '../../state/connection-store';
import type { Ft8TxRunnerView } from '../../dsp/ft8-tx-runner';
import { Ft8WaterfallOverlay } from './Ft8WaterfallOverlay';
import { Ft8WaterfallControls } from './Ft8WaterfallControls';

export interface Ft8ReceivePanelProps {
  runner: Ft8TxRunnerView;
  rxFocusHz: number;
  setRxFocusHz: (hz: number) => void;
  myCall?: string;
}

export function Ft8ReceivePanel({ runner, rxFocusHz, setRxFocusHz, myCall }: Ft8ReceivePanelProps) {
  const connected = useConnectionStore((s) => s.status === 'Connected');

  return (
    <section className="ft8-region ft8-region--grow ft8-receive">
      <div className="ft8-region__head">
        Receive · waterfall
        {/* HOLD TX FREQ defaults on (ft8-sequencer), so a click moves only the
            RX focus until the operator releases HOLD to also move TX. */}
        <small> · click to set RX (release HOLD to move TX)</small>
      </div>
      <div className="ft8-receive__stack">
        {connected ? (
          <>
            <div className="ft8-receive__pan">
              <Panadapter receiver="A" />
            </div>
            <div className="ft8-receive__wf">
              <WaterfallSurface receiver="A" />
            </div>
            <Ft8WaterfallOverlay
              runner={runner}
              rxFocusHz={rxFocusHz}
              setRxFocusHz={setRxFocusHz}
              myCall={myCall}
            />
          </>
        ) : (
          <div className="ft8-placeholder">connect a radio to see the waterfall</div>
        )}
      </div>
      <Ft8WaterfallControls />
    </section>
  );
}
