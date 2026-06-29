// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

// Regression coverage for the issue #844 mic-unavailable surfacing: when the
// browser blocks getUserMedia because the page is a non-secure context (a
// plain http:// LAN IP), the MIC chip must read "needs HTTPS" and point the
// operator at the https:// URL — NOT the generic "mic unavailable". The
// distinction is the whole fix, and it must never misfire when the origin IS
// secure (https:// or http://localhost, e.g. the Photino desktop shell), where
// a mic error means something else entirely.

import { afterEach, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { createElement } from 'react';

import { render } from './meters/__tests__/harness';
import { useTxStore } from '../state/tx-store';
import { MicMeter } from './MicMeter';

beforeAll(() => {
  const g = globalThis as unknown as {
    ResizeObserver?: typeof ResizeObserver;
    requestAnimationFrame?: typeof requestAnimationFrame;
    cancelAnimationFrame?: typeof cancelAnimationFrame;
  };
  if (!g.ResizeObserver) {
    g.ResizeObserver = class ResizeObserver {
      observe() {}
      unobserve() {}
      disconnect() {}
    };
  }
  if (!g.requestAnimationFrame) {
    g.requestAnimationFrame = (cb: FrameRequestCallback) =>
      window.setTimeout(() => cb(performance.now()), 16);
    g.cancelAnimationFrame = (id: number) => window.clearTimeout(id);
  }
});

function setSecureContext(secure: boolean) {
  Object.defineProperty(window, 'isSecureContext', {
    configurable: true,
    value: secure,
  });
}

beforeEach(() => {
  useTxStore.setState({ micError: null });
});

afterEach(() => {
  useTxStore.setState({ micError: null });
});

describe('MicMeter mic-unavailable surfacing (#844)', () => {
  it('shows "needs HTTPS" — not "mic unavailable" — on an insecure origin with a mic error', () => {
    setSecureContext(false);
    useTxStore.setState({ micError: 'NotAllowedError: getUserMedia unavailable' });

    const { container, unmount } = render(createElement(MicMeter));
    try {
      expect(container.textContent).toContain('needs HTTPS');
      expect(container.textContent).not.toContain('mic unavailable');
    } finally {
      unmount();
    }
  });

  it('shows the generic "mic unavailable" on a secure origin (https / localhost) with a mic error', () => {
    // A secure context with a mic error is a real device/permission fault, not
    // an HTTPS problem — pointing the operator at HTTPS would be misleading.
    setSecureContext(true);
    useTxStore.setState({ micError: 'NotReadableError: device in use' });

    const { container, unmount } = render(createElement(MicMeter));
    try {
      expect(container.textContent).toContain('mic unavailable');
      expect(container.textContent).not.toContain('needs HTTPS');
    } finally {
      unmount();
    }
  });

  it('shows neither error chip when the mic is working (no error), even on an insecure origin', () => {
    // No micError → the meter renders normally. The "needs HTTPS" branch must
    // stay gated behind an actual error so it can never nag a working mic.
    setSecureContext(false);
    useTxStore.setState({ micError: null });

    const { container, unmount } = render(createElement(MicMeter));
    try {
      expect(container.textContent).not.toContain('needs HTTPS');
      expect(container.textContent).not.toContain('mic unavailable');
    } finally {
      unmount();
    }
  });
});
