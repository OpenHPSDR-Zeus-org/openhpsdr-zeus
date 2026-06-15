// SPDX-License-Identifier: GPL-2.0-or-later
//
// TX-audio source store — parse robustness + optimistic update round-trip
// (external-ports plan, §2/§11).

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
      hasRadioLineIn: false,
      hasBalancedXlr: false,
      hasMicBias: false,
      source: 'Host',
      micBoost: false,
      micBias: false,
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
    stubFetch({ hasOnboardCodec: true, source: 'RadioLineIn', lineInGain: 99 });
    const s = await fetchAudioFrontEnd();
    expect(s.lineInGain).toBe(31);
  });

  it('defaults gates false + source Host on garbage', async () => {
    stubFetch(42);
    const s = await fetchAudioFrontEnd();
    expect(s.hasOnboardCodec).toBe(false);
    expect(s.hermesLite2MicFrontEnd).toBe(false);
    expect(s.source).toBe('Host');
    expect(s.lineInGain).toBe(0);
  });

  it('parses the numeric enum form of source', async () => {
    stubFetch({ source: 1 }); // RadioMic
    const s = await fetchAudioFrontEnd();
    expect(s.source).toBe('RadioMic');
  });

  it('falls back to Host on an unknown source string', async () => {
    stubFetch({ source: 'Nonsense' });
    const s = await fetchAudioFrontEnd();
    expect(s.source).toBe('Host');
  });

  it('throws on a 409 from the PUT', async () => {
    stubFetch({ error: 'no audio' }, 409);
    await expect(
      updateAudioFrontEnd({
        source: 'RadioMic',
        micBoost: false,
        micBias: false,
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
      source: 'RadioMic',
      micBoost: true,
      micBias: false,
      lineInGain: 12,
    });
    await useAudioStore.getState().update({ source: 'RadioMic', micBoost: true });
    const s = useAudioStore.getState().settings;
    expect(s.source).toBe('RadioMic');
    expect(s.micBoost).toBe(true);
    expect(s.lineInGain).toBe(12);
  });

  it('rolls back on PUT failure', async () => {
    useAudioStore.setState({
      settings: {
        hasOnboardCodec: true,
        hermesLite2MicFrontEnd: false,
        hasRadioLineIn: false,
        hasBalancedXlr: false,
        hasMicBias: true,
        source: 'RadioMic',
        micBoost: false,
        micBias: false,
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
