// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { useEffect, useRef } from 'react';
import type { RefObject } from 'react';
import type { AudioChainStageId } from '../state/audio-chain-health-store';

/**
 * Custom-event name the AudioChainMonitor widget dispatches on its
 * "Show me" button. Lives at module scope so both ends of the
 * deeplink (emitter and listener) stay in lock-step — renaming
 * requires touching both files.
 */
export const CHAIN_FOCUS_EVENT = 'zeus:chain-focus';

export type ChainFocusEventDetail = {
  stageId: AudioChainStageId;
};

/**
 * Subscribe a control element to the Audio Chain Monitor's
 * "Show me" deeplink for a specific stage. When the widget fires
 * `zeus:chain-focus` with a matching stageId, the control:
 *
 *   1. Scrolls itself into view (smooth, centered).
 *   2. Receives the `acm-focus-pulse` CSS class for 1.5 seconds,
 *      which animates an --accent outline around it.
 *
 * Multiple controls can claim the same stage — they'll all pulse
 * together (e.g. mic gain has a slider in the TX panel and a knob
 * in the Audio Suite; both should highlight). Worst-case is two
 * outlines lighting up at once, which is preferable to the operator
 * having to guess which copy of the control to look at.
 *
 * The hook is no-op safe before the ref attaches and during SSR —
 * subscribes on mount, unsubscribes on unmount.
 *
 * @example
 *   const ref = useRef<HTMLLabelElement>(null);
 *   useChainFocus(AudioChainStageId.Mic, ref);
 *   return <label ref={ref}>...</label>;
 */
export function useChainFocus<T extends HTMLElement>(
  stageId: AudioChainStageId,
  ref: RefObject<T | null>,
): void {
  // Track the active timeout so a back-to-back focus event doesn't
  // remove the pulse class prematurely. Stays per-hook-instance.
  const pulseTimerRef = useRef<number | null>(null);

  useEffect(() => {
    const onFocus = (ev: Event) => {
      const detail = (ev as CustomEvent<ChainFocusEventDetail>).detail;
      if (!detail || detail.stageId !== stageId) return;
      const el = ref.current;
      if (!el) return;
      // Smooth-scroll the control into view. `center` is friendlier
      // than the default `start` for tall settings panels where the
      // control would otherwise pin to the top edge of the viewport.
      try {
        el.scrollIntoView({ behavior: 'smooth', block: 'center' });
      } catch {
        // Older browsers — fall back to instant scroll.
        el.scrollIntoView();
      }
      // Replace any in-flight pulse so a rapid second click restarts
      // the 1.5 s window from now rather than ending mid-animation.
      if (pulseTimerRef.current !== null) {
        window.clearTimeout(pulseTimerRef.current);
      }
      el.classList.remove('acm-focus-pulse');
      // Force a reflow so removing-then-adding the same class
      // restarts the CSS animation instead of being deduped.
      void el.offsetWidth;
      el.classList.add('acm-focus-pulse');
      pulseTimerRef.current = window.setTimeout(() => {
        el.classList.remove('acm-focus-pulse');
        pulseTimerRef.current = null;
      }, 1500);
    };
    document.addEventListener(CHAIN_FOCUS_EVENT, onFocus);
    return () => {
      document.removeEventListener(CHAIN_FOCUS_EVENT, onFocus);
      if (pulseTimerRef.current !== null) {
        window.clearTimeout(pulseTimerRef.current);
        pulseTimerRef.current = null;
      }
    };
  }, [stageId, ref]);
}
