// SPDX-License-Identifier: GPL-2.0-or-later
//
// Ft8WaterfallControls — the control strip under the FT8 receive waterfall.
// WF SPEED and WF ZOOM reuse the shipping main-app controls verbatim (they drive
// the same display-settings / zoom state the heightfield honours). WF OFFSET is
// a tiny stepper that pans the visible window WITHOUT moving the dial, by nudging
// the hardware LO via the same setRadioLo lever use-pan-tune-gesture uses for
// RX1 pan. Under the CTUN passband framing this slides the window while the dial
// (the decoder's reference) stays put — the natural "WF OFFSET" semantics.

import { setRadioLo } from '../../api/client';
import { useConnectionStore } from '../../state/connection-store';
import { viewCenterFor } from '../../state/view-center';
import { ZoomControl } from '../../components/ZoomControl';
import { WaterfallSpeedControl } from '../../components/WaterfallSpeedControl';

const OFFSET_STEPS_HZ = [-500, -100, 100, 500] as const;

export function Ft8WaterfallControls() {
  const connected = useConnectionStore((s) => s.status === 'Connected');

  const nudgeOffset = (deltaHz: number) => {
    const cur = useConnectionStore.getState().radioLoHz;
    const next = Math.max(0, cur + deltaHz);
    const applied = next - cur;
    // The renderers anchor on the ANIMATED view-center tween, not radioLoHz, so
    // the window only slides when the tween moves. Drive it the same way the pan
    // gesture does: stamp the optimistic-tune clock (suppresses poll rubber-band)
    // and glide by the delta — that is what makes the slide immediate. Then POST
    // setRadioLo; its echo reconciles the store to the server-clamped value.
    const vc = viewCenterFor('A');
    vc.markOptimisticTune();
    if (applied !== 0) vc.nudgeTargetHz(applied);
    useConnectionStore.setState({ radioLoHz: next });
    setRadioLo(next)
      .then((s) => useConnectionStore.getState().applyState(s))
      .catch(() => {
        /* next state poll reconciles */
      });
  };

  return (
    <div className="ft8-wf-controls" role="group" aria-label="Waterfall controls">
      <WaterfallSpeedControl />
      <ZoomControl />
      <div className="ft8-wf-offset" role="group" aria-label="Waterfall offset">
        <span className="ft8-wf-offset__label">WF OFFSET</span>
        {OFFSET_STEPS_HZ.map((d) => (
          <button
            key={d}
            type="button"
            className="ft8-wf-offset__btn"
            disabled={!connected}
            onClick={() => nudgeOffset(d)}
            title={`Pan the waterfall window ${d > 0 ? '+' : ''}${d} Hz (dial unchanged)`}
          >
            {d > 0 ? `+${d}` : d}
          </button>
        ))}
      </div>
    </div>
  );
}
