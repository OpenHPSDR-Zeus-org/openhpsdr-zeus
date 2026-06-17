// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.

import { describe, expect, it } from 'vitest';
import { spectrumReceiverFilterColor } from './spectrumReceiverColor';

describe('spectrumReceiverFilterColor', () => {
  it('matches the filter mini-pan receiver palette', () => {
    expect(spectrumReceiverFilterColor('A')).toBe('var(--accent, #4a9eff)');
    expect(spectrumReceiverFilterColor('B')).toBe('var(--signal, #25d366)');
  });
});
