// SPDX-License-Identifier: GPL-2.0-or-later
//
// GenericVstPanel — the rack body for a plugin that ships no UI module
// of its own, i.e. a VST3 registered via "Add VST directory". The host
// can't render a VST3's native editor inside the browser (it's a native
// OS window, not HTML), but the in-process bridge CAN open that real
// editor as a separate window on the host machine's desktop — exactly
// like a standalone VST host. This panel surfaces that "Open Editor"
// action plus the plugin's identity. Reorder / park / remove still work
// from the rack slot chrome, and the VST processes audio regardless.

import { useCallback, useEffect, useState } from 'react';

interface GenericVstPanelProps {
  pluginId: string;
  name: string;
}

export function GenericVstPanel({ pluginId, name }: GenericVstPanelProps) {
  const base = `/api/audio-suite/plugins/${encodeURIComponent(pluginId)}/editor`;
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Reflect the actual editor state on mount (the native window may
  // already be open from a previous interaction).
  useEffect(() => {
    let alive = true;
    fetch(base)
      .then((r) => (r.ok ? r.json() : null))
      .then((j) => {
        if (alive && j && typeof j.open === 'boolean') setOpen(j.open);
      })
      .catch(() => {
        /* transient — leave state as-is */
      });
    return () => {
      alive = false;
    };
  }, [base]);

  const toggle = useCallback(async () => {
    setBusy(true);
    setError(null);
    try {
      const res = await fetch(base, { method: open ? 'DELETE' : 'POST' });
      const body = await res.json().catch(() => null);
      if (res.ok) {
        setOpen(body && typeof body.open === 'boolean' ? body.open : !open);
      } else {
        setError(body?.error ?? `Request failed (${res.status})`);
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Request failed');
    } finally {
      setBusy(false);
    }
  }, [base, open]);

  return (
    <div
      style={{
        display: 'flex',
        flexDirection: 'column',
        gap: 10,
        fontSize: 11,
        color: 'var(--fg-2)',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <span
          style={{
            fontSize: 9,
            fontWeight: 700,
            letterSpacing: 1,
            color: 'var(--fg-0)',
            background: 'var(--bg-2)',
            border: '1px solid var(--line)',
            borderRadius: 3,
            padding: '1px 5px',
          }}
        >
          VST3
        </span>
        <span style={{ color: 'var(--fg-1)', fontWeight: 600 }}>{name}</span>
      </div>

      <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
        <button
          type="button"
          onClick={() => void toggle()}
          disabled={busy}
          title={
            open
              ? 'Close the plugin’s native editor window'
              : 'Open the plugin’s real GUI as a separate desktop window'
          }
          style={{
            padding: '6px 14px',
            borderRadius: 4,
            border: '1px solid ' + (open ? 'var(--tx)' : 'var(--accent)'),
            background: open ? 'var(--tx)' : 'var(--accent)',
            color: '#fff',
            cursor: busy ? 'progress' : 'pointer',
            opacity: busy ? 0.6 : 1,
            fontSize: 11,
            fontWeight: 600,
            letterSpacing: 0.6,
            textTransform: 'uppercase',
            fontFamily: 'inherit',
          }}
        >
          {busy ? '…' : open ? 'Close Editor' : 'Open Editor'}
        </button>
        <span style={{ color: 'var(--fg-3)', fontSize: 10, lineHeight: 1.3, flex: 1, minWidth: 160 }}>
          Opens the plugin&rsquo;s real editor as a separate window on the
          desktop (not inside the browser). The VST processes audio in the
          chain whether or not the editor is open.
        </span>
      </div>

      {error && (
        <div
          role="alert"
          style={{
            fontSize: 10,
            color: 'var(--tx)',
            background: 'var(--tx-soft)',
            border: '1px solid var(--tx)',
            borderRadius: 3,
            padding: '5px 8px',
            lineHeight: 1.4,
          }}
        >
          {error}
        </div>
      )}

      <code
        style={{
          fontSize: 9.5,
          color: 'var(--fg-3)',
          fontFamily: 'var(--font-mono, JetBrains Mono, monospace)',
          wordBreak: 'break-all',
        }}
      >
        {pluginId}
      </code>
    </div>
  );
}
