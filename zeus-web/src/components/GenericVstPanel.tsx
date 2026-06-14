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

import { useEffect, useRef } from 'react';
import { useVstEditor } from './useVstEditor';

interface GenericVstPanelProps {
  pluginId: string;
  name: string;
}

export function GenericVstPanel({ pluginId, name }: GenericVstPanelProps) {
  const { open, busy, error, toggle, openEditor } = useVstEditor(pluginId);

  // Selecting this VST's chip mounts the panel — auto-open its real
  // editor window so the chip click alone pops the GUI (no extra button
  // press). Fires once per mount; the server-side open is idempotent and
  // the operator can still Close it below. Re-selecting the chip remounts
  // and re-opens. A non-loadable .vst3 surfaces its error in the pane.
  const autoOpenedRef = useRef(false);
  useEffect(() => {
    if (autoOpenedRef.current) return;
    autoOpenedRef.current = true;
    openEditor();
  }, [openEditor]);

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
          Selecting this VST opens its real editor in a separate desktop
          window — a VST3 GUI is a native window, not browser HTML, so it
          can&rsquo;t render here. Use Close to dismiss it; the VST
          processes audio either way.
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
