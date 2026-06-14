// SPDX-License-Identifier: GPL-2.0-or-later
//
// Audio front-end store — parse robustness + optimistic update round-trip
// (external-ports plan, Phase 4).

import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  fetchAudioFrontEnd,
  updateAudioFrontEnd,
  useAudioStore,
} from './audio-store';

afterEach(() => {
  vi.unstubAllGlobals();
  useAudioStore.setState({
    settings: {
      hasOnboardCodec: false,
      hermesLite2MicFrontEnd: false,
      lineIn: false,
      micBoost: false,
      micBias: false,
      balancedInput: false,
      lineInGain: 0,
    },
    loaded: false,
    inflight: false,
    error: null,
  });
});

function stubFetch(body: unknown, status = 200) {
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify(body), {
        status,
        headers: { 'content-type': 'application/json' },
      }),
    ),
  );
}

describe('audio-store parsing', () => {
  it('clamps lineInGain into 0..31', async () => {
    stubFetch({ hasOnboardCodec: true, lineInGain: 99 });
    const s = await fetchAudioFrontEnd();
    expect(s.lineInGain).toBe(31);
  });

  it('defaults gates false on garbage', async () => {
    stubFetch(42);
    const s = await fetchAudioFrontEnd();
    expect(s.hasOnboardCodec).toBe(false);
    expect(s.hermesLite2MicFrontEnd).toBe(false);
    expect(s.lineInGain).toBe(0);
  });

  it('throws on a 409 from the PUT', async () => {
    stubFetch({ error: 'no audio' }, 409);
    await expect(
      updateAudioFrontEnd({
        lineIn: true,
        micBoost: false,
        micBias: false,
        balancedInput: false,
        lineInGain: 0,
      }),
    ).rejects.toThrow();
  });
});

describe('audio-store update', () => {
  it('optimistically patches then adopts the server response', async () => {
    stubFetch({
      hasOnboardCodec: true,
      hermesLite2MicFrontEnd: false,
      lineIn: true,
      micBoost: true,
      micBias: false,
      balancedInput: false,
      lineInGain: 12,
    });
    await useAudioStore.getState().update({ lineIn: true, micBoost: true });
    const s = useAudioStore.getState().settings;
    expect(s.lineIn).toBe(true);
    expect(s.micBoost).toBe(true);
    expect(s.lineInGain).toBe(12);
  });

  it('rolls back on PUT failure', async () => {
    useAudioStore.setState({
      settings: {
        hasOnboardCodec: true,
        hermesLite2MicFrontEnd: false,
        lineIn: false,
        micBoost: false,
        micBias: false,
        balancedInput: false,
        lineInGain: 3,
      },
    });
    stubFetch({ error: 'boom' }, 409);
    await useAudioStore.getState().update({ micBias: true });
    const s = useAudioStore.getState().settings;
    expect(s.micBias).toBe(false); // rolled back
    expect(useAudioStore.getState().error).toBeTruthy();
  });
});
