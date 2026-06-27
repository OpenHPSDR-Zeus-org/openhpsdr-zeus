// SPDX-License-Identifier: GPL-2.0-or-later
//
// Radio-side speaker output store — parse robustness + optimistic toggle
// round-trip. Mirrors audio-store.test.ts.

import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  fetchRadioSpeakerOutput,
  updateRadioSpeakerOutput,
  useRadioSpeakerStore,
} from './radio-speaker-store';

afterEach(() => {
  vi.unstubAllGlobals();
  useRadioSpeakerStore.setState({
    settings: { enabled: false, available: false },
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

describe('radio-speaker-store parsing', () => {
  it('parses enabled + available', async () => {
    stubFetch({ enabled: true, available: true });
    const s = await fetchRadioSpeakerOutput();
    expect(s.enabled).toBe(true);
    expect(s.available).toBe(true);
  });

  it('defaults both false on garbage', async () => {
    stubFetch(42);
    const s = await fetchRadioSpeakerOutput();
    expect(s.enabled).toBe(false);
    expect(s.available).toBe(false);
  });

  it('throws on a non-OK PUT', async () => {
    stubFetch({ error: 'nope' }, 500);
    await expect(updateRadioSpeakerOutput(true)).rejects.toThrow();
  });
});

describe('radio-speaker-store toggle', () => {
  it('optimistically toggles then adopts the server response', async () => {
    stubFetch({ enabled: true, available: true });
    await useRadioSpeakerStore.getState().setEnabled(true);
    const s = useRadioSpeakerStore.getState().settings;
    expect(s.enabled).toBe(true);
    expect(s.available).toBe(true);
  });

  it('rolls back on PUT failure', async () => {
    useRadioSpeakerStore.setState({
      settings: { enabled: false, available: true },
    });
    stubFetch({ error: 'boom' }, 500);
    await useRadioSpeakerStore.getState().setEnabled(true);
    const s = useRadioSpeakerStore.getState().settings;
    expect(s.enabled).toBe(false); // rolled back
    expect(useRadioSpeakerStore.getState().error).toBeTruthy();
  });
});
