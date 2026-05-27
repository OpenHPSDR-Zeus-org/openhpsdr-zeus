// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, useRef } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { useChainFocus, CHAIN_FOCUS_EVENT } from './use-chain-focus';
import { AudioChainStageId } from '../state/audio-chain-health-store';

function Subject({ stage }: { stage: AudioChainStageId }) {
  const ref = useRef<HTMLDivElement>(null);
  useChainFocus(stage, ref);
  return <div data-testid="target" ref={ref} />;
}

describe('useChainFocus', () => {
  let container: HTMLDivElement;
  let root: Root;
  let scrollSpy: ReturnType<typeof vi.fn>;
  let originalScrollIntoView: typeof HTMLElement.prototype.scrollIntoView;

  beforeEach(() => {
    vi.useFakeTimers();
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
    scrollSpy = vi.fn();
    originalScrollIntoView = HTMLElement.prototype.scrollIntoView;
    // jsdom doesn't implement scrollIntoView — stub so the hook
    // doesn't blow up.
    HTMLElement.prototype.scrollIntoView = scrollSpy;
  });

  afterEach(() => {
    act(() => {
      root.unmount();
    });
    container.remove();
    HTMLElement.prototype.scrollIntoView = originalScrollIntoView;
    vi.useRealTimers();
  });

  function target(): HTMLElement {
    return container.querySelector('[data-testid="target"]') as HTMLElement;
  }

  it('applies the pulse class on a matching event', () => {
    act(() => {
      root.render(<Subject stage={AudioChainStageId.Mic} />);
    });
    expect(target().classList.contains('acm-focus-pulse')).toBe(false);

    act(() => {
      document.dispatchEvent(
        new CustomEvent(CHAIN_FOCUS_EVENT, {
          detail: { stageId: AudioChainStageId.Mic },
        }),
      );
    });

    expect(target().classList.contains('acm-focus-pulse')).toBe(true);
  });

  it('ignores events for a different stage', () => {
    act(() => {
      root.render(<Subject stage={AudioChainStageId.Mic} />);
    });

    act(() => {
      document.dispatchEvent(
        new CustomEvent(CHAIN_FOCUS_EVENT, {
          detail: { stageId: AudioChainStageId.Pa },
        }),
      );
    });

    expect(target().classList.contains('acm-focus-pulse')).toBe(false);
  });

  it('removes the pulse class after 1.5 s', () => {
    act(() => {
      root.render(<Subject stage={AudioChainStageId.Mic} />);
    });

    act(() => {
      document.dispatchEvent(
        new CustomEvent(CHAIN_FOCUS_EVENT, {
          detail: { stageId: AudioChainStageId.Mic },
        }),
      );
    });
    expect(target().classList.contains('acm-focus-pulse')).toBe(true);

    act(() => {
      vi.advanceTimersByTime(1500);
    });
    expect(target().classList.contains('acm-focus-pulse')).toBe(false);
  });

  it('calls scrollIntoView on the target', () => {
    act(() => {
      root.render(<Subject stage={AudioChainStageId.Mic} />);
    });

    act(() => {
      document.dispatchEvent(
        new CustomEvent(CHAIN_FOCUS_EVENT, {
          detail: { stageId: AudioChainStageId.Mic },
        }),
      );
    });

    expect(scrollSpy).toHaveBeenCalled();
  });

  it('unsubscribes on unmount', () => {
    act(() => {
      root.render(<Subject stage={AudioChainStageId.Mic} />);
    });
    const el = target();
    act(() => {
      root.unmount();
    });

    // After unmount, the listener is gone and dispatching shouldn't
    // touch the (detached) element. Make a fresh root to avoid the
    // afterEach unmount blowing up on a double-unmount.
    root = createRoot(container);

    act(() => {
      document.dispatchEvent(
        new CustomEvent(CHAIN_FOCUS_EVENT, {
          detail: { stageId: AudioChainStageId.Mic },
        }),
      );
    });
    expect(el.classList.contains('acm-focus-pulse')).toBe(false);
  });
});
