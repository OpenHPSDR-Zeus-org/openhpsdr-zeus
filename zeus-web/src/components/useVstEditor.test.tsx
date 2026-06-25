// SPDX-License-Identifier: GPL-2.0-or-later
import { describe, expect, it, vi } from 'vitest';
import { act, renderHook } from './meters/__tests__/harness';
import { useVstEditor } from './useVstEditor';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

async function flushAsyncWork() {
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
  });
}

describe('useVstEditor', () => {
  it('uses the TX editor route by default', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => jsonResponse({ open: false }));
    vi.stubGlobal('fetch', fetchMock);

    const hook = renderHook(() => useVstEditor('com.openhpsdr.zeus.vst.clear'));
    await flushAsyncWork();

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/tx-audio-suite/plugins/com.openhpsdr.zeus.vst.clear/editor',
    );

    await act(async () => {
      hook.result.current.openEditor();
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/tx-audio-suite/plugins/com.openhpsdr.zeus.vst.clear/editor',
      { method: 'POST' },
    );

    hook.unmount();
    vi.unstubAllGlobals();
  });

  it('waits for the engine to start, then retries the open after a 409', async () => {
    // Fresh-install cold-start race: the first POST 409s because the engine
    // isn't routing yet. The hook should poll processing-mode, see the engine
    // come up, and retry the open so the editor ends up open (not failed).
    const editorBase =
      '/api/tx-audio-suite/plugins/com.openhpsdr.zeus.vst.blendeq/editor';
    let postCount = 0;
    const fetchMock = vi.fn<typeof fetch>(async (input, init) => {
      const url = String(input);
      if (url.endsWith('/processing-mode')) {
        return jsonResponse({ mode: 'vst', engineActive: true });
      }
      if (url === editorBase && init?.method === 'POST') {
        postCount += 1;
        // First open races the still-starting engine → 409; retry succeeds.
        return postCount === 1
          ? jsonResponse({ error: "installed but isn't routing yet" }, 409)
          : jsonResponse({ open: true });
      }
      return jsonResponse({ open: false });
    });
    vi.stubGlobal('fetch', fetchMock);

    const hook = renderHook(() => useVstEditor('com.openhpsdr.zeus.vst.blendeq'));
    await flushAsyncWork();

    await act(async () => {
      hook.result.current.openEditor();
      // mount-GET + POST(409) + processing-mode poll + POST(retry) settle.
      for (let i = 0; i < 6; i += 1) await Promise.resolve();
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/tx-audio-suite/processing-mode',
    );
    expect(postCount).toBe(2);
    expect(hook.result.current.open).toBe(true);
    expect(hook.result.current.error).toBeNull();

    hook.unmount();
    vi.unstubAllGlobals();
  });

  it('flags a crash-loop instead of retrying when the engine keeps crashing', async () => {
    // Installed engine that keeps crashing (Faulted): waiting won't help, so the
    // hook should stop, set crashed=true, and not retry the open.
    const editorBase =
      '/api/tx-audio-suite/plugins/com.openhpsdr.zeus.vst.blendeq/editor';
    let postCount = 0;
    const fetchMock = vi.fn<typeof fetch>(async (input, init) => {
      const url = String(input);
      if (url.endsWith('/processing-mode')) {
        return jsonResponse({ mode: 'vst', engineActive: false, engineCrashLooping: true });
      }
      if (url === editorBase && init?.method === 'POST') {
        postCount += 1;
        return jsonResponse({ error: "installed but isn't routing yet" }, 409);
      }
      return jsonResponse({ open: false });
    });
    vi.stubGlobal('fetch', fetchMock);

    const hook = renderHook(() => useVstEditor('com.openhpsdr.zeus.vst.blendeq'));
    await flushAsyncWork();

    await act(async () => {
      hook.result.current.openEditor();
      for (let i = 0; i < 6; i += 1) await Promise.resolve();
    });

    expect(postCount).toBe(1); // no retry — waiting can't fix a crash-loop
    expect(hook.result.current.crashed).toBe(true);
    expect(hook.result.current.open).toBe(false);
    expect(hook.result.current.error).toContain('keeps crashing');

    hook.unmount();
    vi.unstubAllGlobals();
  });

  it('does not wait when a 409 comes back in Native mode', async () => {
    // A Native-mode 409 means "switch to VST mode" — there is no engine to wait
    // for, so the original error should surface immediately without retrying.
    const editorBase =
      '/api/tx-audio-suite/plugins/com.openhpsdr.zeus.vst.blendeq/editor';
    let postCount = 0;
    const fetchMock = vi.fn<typeof fetch>(async (input, init) => {
      const url = String(input);
      if (url.endsWith('/processing-mode')) {
        return jsonResponse({ mode: 'native', engineActive: false });
      }
      if (url === editorBase && init?.method === 'POST') {
        postCount += 1;
        return jsonResponse({ error: 'TX VSTs run in the dedicated VST engine.' }, 409);
      }
      return jsonResponse({ open: false });
    });
    vi.stubGlobal('fetch', fetchMock);

    const hook = renderHook(() => useVstEditor('com.openhpsdr.zeus.vst.blendeq'));
    await flushAsyncWork();

    await act(async () => {
      hook.result.current.openEditor();
      for (let i = 0; i < 6; i += 1) await Promise.resolve();
    });

    expect(postCount).toBe(1); // no retry
    expect(hook.result.current.open).toBe(false);
    expect(hook.result.current.error).toContain('dedicated VST engine');

    hook.unmount();
    vi.unstubAllGlobals();
  });

  it('uses the RX editor route for receive-side VST instances', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => jsonResponse({ open: false }));
    vi.stubGlobal('fetch', fetchMock);

    const hook = renderHook(() =>
      useVstEditor('com.openhpsdr.zeus.rxvst.clear', true, 'rx'),
    );
    await flushAsyncWork();

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/rx-audio-suite/plugins/com.openhpsdr.zeus.rxvst.clear/editor',
    );

    await act(async () => {
      hook.result.current.openEditor();
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/rx-audio-suite/plugins/com.openhpsdr.zeus.rxvst.clear/editor',
      { method: 'POST' },
    );

    hook.unmount();
    vi.unstubAllGlobals();
  });
});
