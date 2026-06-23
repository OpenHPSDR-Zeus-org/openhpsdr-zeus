// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.
//
// NR3 (RNNoise) model install panel. Lives in the DSP menu. Zeus ships no
// model — NR3 stays hidden from the NR cycle until the operator installs an
// RNNoise weights file here (local upload or fetch-from-URL). Once a model is
// installed and libwdsp exports the RNNR symbols, NR3 appears in the NR button
// cycle. See NrControls / DspPanel nrCycleFor().

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  downloadNr3Model,
  removeNr3Model,
  uploadNr3Model,
  type RadioStateDto,
} from '../../api/client';
import { useConnectionStore } from '../../state/connection-store';

export function Nr3ModelPanel() {
  const available = useConnectionStore((s) => s.wdspNr3RnnrAvailable);
  const modelName = useConnectionStore((s) => s.nr3ModelName);
  const applyState = useConnectionStore((s) => s.applyState);

  const [url, setUrl] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const fileInput = useRef<HTMLInputElement | null>(null);
  const inflight = useRef<AbortController | null>(null);

  useEffect(() => () => inflight.current?.abort(), []);

  // Runs an install/remove action, funnelling the returned StateDto through
  // applyState so the NR cycle reveals/hides NR3 immediately.
  const run = useCallback(
    async (action: (signal: AbortSignal) => Promise<RadioStateDto>) => {
      inflight.current?.abort();
      const ac = new AbortController();
      inflight.current = ac;
      setBusy(true);
      setError(null);
      try {
        const state = await action(ac.signal);
        if (!ac.signal.aborted) applyState(state);
      } catch (e) {
        if (!ac.signal.aborted) setError(e instanceof Error ? e.message : 'NR3 model operation failed');
      } finally {
        if (!ac.signal.aborted) setBusy(false);
      }
    },
    [applyState],
  );

  const onFilePicked = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const file = e.target.files?.[0];
      // Reset the input so picking the same file again re-fires onChange.
      e.target.value = '';
      if (file) void run((signal) => uploadNr3Model(file, signal));
    },
    [run],
  );

  const onDownload = useCallback(() => {
    const trimmed = url.trim();
    if (!trimmed) return;
    void run((signal) => downloadNr3Model(trimmed, signal)).then(() => setUrl(''));
  }, [url, run]);

  const onRemove = useCallback(() => {
    void run((signal) => removeNr3Model(signal));
  }, [run]);

  if (!available) {
    return (
      <div className="nr-settings" role="region" aria-label="NR3 model">
        <h4 className="nr-settings__subhdr">NR3 — RNNoise model</h4>
        <p className="nr-settings__hint">
          NR3 (RNNoise) is not available in this build — the loaded WDSP library
          was compiled without RNNoise support.
        </p>
      </div>
    );
  }

  return (
    <div className="nr-settings" role="region" aria-label="NR3 model">
      <h4 className="nr-settings__subhdr">NR3 — RNNoise model</h4>

      <p className="nr-settings__hint">
        {modelName
          ? `Installed model: ${modelName}. NR3 is now in the NR cycle.`
          : 'No model installed — NR3 is hidden until you install an RNNoise weights file. Zeus ships no model; bring your own.'}
      </p>

      <div className="nr-settings__row">
        <span className="nr-settings__label">File</span>
        <input
          ref={fileInput}
          type="file"
          accept=".rnnn,.bin,.txt,application/octet-stream"
          style={{ display: 'none' }}
          onChange={onFilePicked}
        />
        <button
          type="button"
          className="btn sm"
          disabled={busy}
          onClick={() => fileInput.current?.click()}
          title="Upload an RNNoise model file from this device"
        >
          {busy ? '…' : 'Upload…'}
        </button>
      </div>

      <div className="nr-settings__row">
        <span className="nr-settings__label">URL</span>
        <input
          type="url"
          className="nr-settings__url-input"
          placeholder="https://…/model.rnnn"
          value={url}
          disabled={busy}
          onChange={(e) => setUrl(e.target.value)}
        />
        <button
          type="button"
          className="btn sm"
          disabled={busy || !url.trim()}
          onClick={onDownload}
          title="Download and install an RNNoise model from a URL"
        >
          Get
        </button>
      </div>

      {modelName && (
        <div className="nr-settings__buttons">
          <button
            type="button"
            className="nr-settings__button"
            disabled={busy}
            onClick={onRemove}
            title="Remove the installed NR3 model (NR3 reverts to off + hidden)"
          >
            Remove model
          </button>
        </div>
      )}

      {error && (
        <p className="nr-settings__hint" role="alert" style={{ color: 'var(--tx)' }}>
          {error}
        </p>
      )}
    </div>
  );
}
