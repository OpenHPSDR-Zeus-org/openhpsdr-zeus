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
// Server-side folder browser for the WAV Recorder. The recordings live on
// the machine running the backend, so a native OS folder dialog can't pick
// them — this walks the server filesystem via /api/wav/dirs and commits the
// choice via POST /api/wav/root. Token-only theming, keyboard-accessible.

import { useCallback, useEffect, useRef, useState } from 'react';
import { useDialogFocusTrap } from '../useDialogFocusTrap';

type DirEntry = { name: string; path: string };

type DirsResp = {
  path: string;
  parent: string | null;
  separator: string;
  dirs: DirEntry[];
};

type RootResp = { root: string; isDefault: boolean };

interface DirectoryPickerModalProps {
  /** Where to start browsing (typically the current recordings root). */
  initialPath: string;
  /** True when the current root is the built-in default (gates Reset). */
  rootIsDefault: boolean;
  onClose: () => void;
  /** Called after the root is successfully changed (or reset). */
  onChanged: (root: string, isDefault: boolean) => void;
}

export function DirectoryPickerModal({
  initialPath,
  rootIsDefault,
  onClose,
  onChanged,
}: DirectoryPickerModalProps) {
  const [path, setPath] = useState(initialPath);
  const [parent, setParent] = useState<string | null>(null);
  const [dirs, setDirs] = useState<DirEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const dialogRef = useRef<HTMLDivElement | null>(null);
  const listRef = useRef<HTMLDivElement | null>(null);
  const aliveRef = useRef(true);

  useDialogFocusTrap({ dialogRef, onClose });

  useEffect(() => {
    aliveRef.current = true;
    return () => {
      aliveRef.current = false;
    };
  }, []);

  // Navigate to a folder. On the initial open, fall back to the user's home
  // (empty path) if the requested folder can't be listed.
  const browse = useCallback(async (target: string, allowHomeFallback = false) => {
    setLoading(true);
    setError(null);
    try {
      const res = await fetch(`/api/wav/dirs?path=${encodeURIComponent(target)}`);
      if (!res.ok) throw new Error(`${res.status}`);
      const d = (await res.json()) as DirsResp;
      if (!aliveRef.current) return;
      setPath(d.path ?? target);
      setParent(d.parent ?? null);
      setDirs(Array.isArray(d.dirs) ? d.dirs : []);
      if (listRef.current) listRef.current.scrollTop = 0;
    } catch {
      if (!aliveRef.current) return;
      if (allowHomeFallback && target !== '') {
        void browse('', false);
        return;
      }
      setError('Could not open that folder.');
      setDirs([]);
    } finally {
      if (aliveRef.current) setLoading(false);
    }
  }, []);

  useEffect(() => {
    void browse(initialPath, true);
  }, [browse, initialPath]);

  const commit = useCallback(
    async (target: string | null) => {
      setBusy(true);
      setError(null);
      try {
        const res = await fetch('/api/wav/root', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ path: target }),
        });
        if (!res.ok) {
          let msg =
            res.status === 409
              ? 'Recorder is busy — stop recording or playback first.'
              : 'That folder can’t be used as the save location.';
          if (res.status !== 409) {
            try {
              const t = (await res.text()).trim();
              if (t) msg = t.slice(0, 160);
            } catch {
              /* keep default */
            }
          }
          throw new Error(msg);
        }
        const j = (await res.json()) as RootResp;
        if (!aliveRef.current) return;
        onChanged(j.root, j.isDefault);
        onClose();
      } catch (e) {
        if (!aliveRef.current) return;
        setError(e instanceof Error ? e.message : 'Failed to set folder.');
      } finally {
        if (aliveRef.current) setBusy(false);
      }
    },
    [onChanged, onClose],
  );

  return (
    <div className="zdr-dirpick-backdrop" onMouseDown={onClose}>
      <div
        ref={dialogRef}
        className="zdr-dirpick"
        role="dialog"
        aria-modal="true"
        aria-label="Choose recordings folder"
        tabIndex={-1}
        onMouseDown={(e) => e.stopPropagation()}
      >
        <header className="zdr-dirpick__head">
          <span className="zdr-dirpick__title">RECORDINGS FOLDER</span>
          <button
            type="button"
            className="zdr-dirpick__close"
            aria-label="Close folder picker"
            title="Close (Esc)"
            onClick={onClose}
          >
            ✕
          </button>
        </header>

        <div className="zdr-dirpick__pathrow">
          <button
            type="button"
            className="zdr-dirpick__up"
            onClick={() => parent !== null && void browse(parent)}
            disabled={parent === null || loading || busy}
            aria-label="Up one folder"
            title="Up one folder"
          >
            ↑
          </button>
          <span className="zdr-dirpick__path" title={path || 'Home'}>
            {path || 'Home'}
          </span>
        </div>

        <div ref={listRef} className="zdr-dirpick__list" aria-busy={loading}>
          {loading ? (
            <div className="zdr-dirpick__hint">Loading…</div>
          ) : dirs.length === 0 ? (
            <div className="zdr-dirpick__hint">No sub-folders here.</div>
          ) : (
            dirs.map((d) => (
              <button
                key={d.path}
                type="button"
                className="zdr-dirpick__dir"
                onClick={() => void browse(d.path)}
                disabled={busy}
                title={d.path}
              >
                <span className="zdr-dirpick__diricon" aria-hidden="true">
                  <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
                    <path
                      d="M3 6.5A1.5 1.5 0 0 1 4.5 5h4l2 2.2H19.5A1.5 1.5 0 0 1 21 8.7v9.8a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18.5Z"
                      fill="none"
                      stroke="currentColor"
                      strokeWidth="1.4"
                      strokeLinejoin="round"
                    />
                  </svg>
                </span>
                <span className="zdr-dirpick__dirname">{d.name}</span>
                <span className="zdr-dirpick__chev" aria-hidden="true">
                  ›
                </span>
              </button>
            ))
          )}
        </div>

        {error && (
          <div className="zdr-dirpick__error" role="alert">
            {error}
          </div>
        )}

        <footer className="zdr-dirpick__actions">
          <button
            type="button"
            className="zdr-dirpick__btn zdr-dirpick__btn--reset"
            onClick={() => void commit(null)}
            disabled={busy || rootIsDefault}
            title={rootIsDefault ? 'Already using the default folder' : 'Reset to the default recordings folder'}
          >
            Reset to default
          </button>
          <span className="zdr-dirpick__spacer" />
          <button type="button" className="zdr-dirpick__btn" onClick={onClose} disabled={busy}>
            Cancel
          </button>
          <button
            type="button"
            className="zdr-dirpick__btn zdr-dirpick__btn--use"
            onClick={() => void commit(path)}
            disabled={busy || loading || !path}
          >
            {busy ? 'Saving…' : 'Use this folder'}
          </button>
        </footer>
      </div>
    </div>
  );
}
