// SPDX-License-Identifier: GPL-2.0-or-later
//
// HamClockSettingsPanel — Settings → HamClock tab. Installs / starts / stops
// the OpenHamClock sidecar (MIT, github.com/accius/openhamclock) and opens it
// as a Zeus panel. Mirrors AboutPanel's layout + Zeus tokens.
//
// Install downloads HamClock's source from GitHub and builds it with the
// operator's local Node/npm (a few hundred MB of npm deps land in app-data,
// not the Zeus installer). Node 18+ must be present; the panel surfaces a
// clear prereq warning when it isn't.

import { useEffect, useRef, useState, type CSSProperties } from 'react';
import {
  HAMCLOCK_LAYOUT_NAME,
  HAMCLOCK_PUSH_DEFAULT_PORT,
  useHamClockStore,
  type HamClockPushConfig,
} from '../state/hamclock-store';
import { useLayoutStore } from '../state/layout-store';

export function HamClockSettingsPanel() {
  const status = useHamClockStore((s) => s.status);
  const loadStatus = useHamClockStore((s) => s.loadStatus);
  const install = useHamClockStore((s) => s.install);
  const start = useHamClockStore((s) => s.start);
  const stop = useHamClockStore((s) => s.stop);
  const openWorkspace = useHamClockStore((s) => s.openWorkspace);
  const disableWorkspace = useHamClockStore((s) => s.disableWorkspace);

  // Whether the HamClock workspace tab currently exists (reactive — the toggle
  // reflects adds/removes from anywhere). Per-radio, like every Zeus layout.
  const workspaceEnabled = useLayoutStore((s) =>
    s.layouts.some((l) => l.name === HAMCLOCK_LAYOUT_NAME),
  );

  const logRef = useRef<HTMLDivElement>(null);

  // Poll status while this tab is mounted — faster while an install/start is
  // in flight so the log streams.
  useEffect(() => {
    void loadStatus();
    const busy = status.busy || status.phase === 'Installing' || status.phase === 'Starting';
    const id = window.setInterval(() => void loadStatus(), busy ? 1500 : 5000);
    return () => window.clearInterval(id);
  }, [loadStatus, status.busy, status.phase]);

  // Keep the log scrolled to the newest line.
  useEffect(() => {
    if (logRef.current) logRef.current.scrollTop = logRef.current.scrollHeight;
  }, [status.log]);

  const installing = status.phase === 'Installing';
  const starting = status.phase === 'Starting';
  const canInstall = !status.busy && !installing && !starting;

  return (
    <div style={{ maxWidth: 640 }}>
      <h3 style={{
        margin: '0 0 16px 0', fontSize: 13, fontWeight: 700,
        letterSpacing: '0.12em', textTransform: 'uppercase', color: 'var(--fg-0)',
      }}>
        HamClock
      </h3>

      <p style={{ margin: '0 0 16px 0', lineHeight: 1.6, color: 'var(--fg-2)', fontSize: 12 }}>
        Embed{' '}
        <a
          href="https://github.com/accius/openhamclock"
          target="_blank"
          rel="noopener noreferrer"
          style={{ color: 'var(--accent)', textDecoration: 'underline' }}
        >
          OpenHamClock
        </a>{' '}
        — a ham-radio dashboard (propagation, DX cluster, satellites, POTA/SOTA,
        space weather) — as a panel inside Zeus. Installing downloads it from
        GitHub and builds it locally; nothing is bundled into the Zeus installer.
      </p>

      {/* Status line */}
      <div style={{ marginBottom: 14, display: 'flex', alignItems: 'center', gap: 10 }}>
        <span style={{ color: 'var(--fg-2)', fontSize: 12 }}>Status:</span>
        <StatusPill phase={status.phase} />
        {status.version && (
          <span style={{ color: 'var(--fg-3)', fontSize: 11, fontFamily: 'var(--font-mono, ui-monospace, monospace)' }}>
            v{status.version}
          </span>
        )}
        {status.running && status.port > 0 && (
          <span style={{ color: 'var(--fg-3)', fontSize: 11, fontFamily: 'var(--font-mono, ui-monospace, monospace)' }}>
            port {status.port}
          </span>
        )}
      </div>

      {/* Node prereq — informational. Install fetches a private Node when the
          system has none, so this never blocks installation. */}
      {!status.nodeAvailable && !installing && (
        <div style={{
          padding: 10, marginBottom: 14, borderRadius: 'var(--r-sm, 4px)',
          background: 'rgba(74, 158, 255, 0.08)', border: '1px solid rgba(74, 158, 255, 0.25)',
          color: 'var(--fg-1)', fontSize: 12, lineHeight: 1.5,
        }}>
          No system Node.js detected — Install will download a private copy
          (~30 MB) automatically. No separate install needed.
        </div>
      )}
      {status.nodeAvailable && status.nodeVersion && (
        <div style={{ marginBottom: 14, fontSize: 11, color: 'var(--fg-3)' }}>
          Node {status.nodeVersion} ready.
        </div>
      )}

      {/* Error */}
      {status.error && (
        <div style={{
          padding: 10, marginBottom: 14, borderRadius: 'var(--r-sm, 4px)',
          background: 'rgba(230, 58, 43, 0.1)', border: '1px solid rgba(230, 58, 43, 0.3)',
          color: 'var(--tx)', fontSize: 12, lineHeight: 1.5,
        }}>
          {status.error}
        </div>
      )}

      {/* Actions — install, then enable/disable the workspace tab. */}
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', marginBottom: 6 }}>
        <button
          type="button"
          className="btn sm"
          disabled={!canInstall}
          onClick={() => void install()}
          title={status.installed
            ? 'Re-download and rebuild HamClock'
            : 'Download and build HamClock (downloads Node automatically if needed)'}
        >
          {installing ? 'INSTALLING…' : status.installed ? 'REINSTALL' : 'INSTALL'}
        </button>

        {/* Enable workspace — creates the HamClock layout tab and switches to
            it (the panel auto-starts the sidecar on mount). */}
        {status.installed && !workspaceEnabled && (
          <button
            type="button"
            className="btn sm active"
            disabled={status.busy || starting}
            onClick={() => {
              if (!status.running) void start();
              openWorkspace();
            }}
            title="Add the HamClock workspace tab and open it"
          >
            {starting ? 'STARTING…' : 'ENABLE WORKSPACE'}
          </button>
        )}

        {/* Workspace already enabled — jump to it or remove it. */}
        {status.installed && workspaceEnabled && (
          <>
            <button
              type="button"
              className="btn sm active"
              onClick={() => {
                if (!status.running) void start();
                openWorkspace();
              }}
              title="Switch to the HamClock workspace"
            >
              OPEN
            </button>
            <button
              type="button"
              className="btn sm"
              onClick={() => disableWorkspace()}
              title="Remove the HamClock workspace tab (does not uninstall)"
            >
              DISABLE WORKSPACE
            </button>
          </>
        )}

        {/* Stop the sidecar process. Independent of the workspace tab. */}
        {status.running && (
          <button
            type="button"
            className="btn sm"
            onClick={() => void stop()}
            title="Stop the HamClock server process"
          >
            STOP SERVER
          </button>
        )}
      </div>

      <div style={{ marginBottom: 16, fontSize: 11, color: 'var(--fg-3)', lineHeight: 1.5 }}>
        {!status.installed
          ? 'Install once, then enable the workspace to add a HamClock tab to the left layout bar.'
          : workspaceEnabled
            ? 'HamClock has its own tab in the left layout bar. Disable to remove it.'
            : 'Enable the workspace to add a HamClock tab to the left layout bar.'}
      </div>

      {/* Push DX to HamClock — outbound, SEND-ONLY, OFF by default. */}
      <PushDxSection />

      {/* Install / run log */}
      {status.log.length > 0 && (
        <div>
          <div style={{
            fontSize: 10, letterSpacing: 0.8, textTransform: 'uppercase',
            color: 'var(--fg-3)', marginBottom: 6,
          }}>
            Log
          </div>
          <div
            ref={logRef}
            style={{
              maxHeight: 220, overflowY: 'auto',
              padding: '8px 10px',
              background: 'var(--bg-1, #0e1014)', border: '1px solid var(--line-1, var(--line))',
              borderRadius: 6,
              fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
              fontSize: 10.5, lineHeight: 1.5, color: 'var(--fg-1)',
              whiteSpace: 'pre-wrap', wordBreak: 'break-word',
            }}
          >
            {status.log.map((line, i) => (
              <div key={i} style={{ color: line.startsWith('ERROR') ? 'var(--tx)' : undefined }}>
                {line}
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

// Outbound "push the worked station onto the HamClock map" config. SEND-ONLY:
// the backend issues a single HTTP GET to the HamClock REST API (set_newdx?grid=)
// when a QSO partner is clicked or goes active in the FT8 pop-out. Egress is OFF
// by default; the server-side forward avoids browser CORS / HTTPS mixed-content.
// The bundled OpenHamClock sidecar has no set-DX endpoint yet, so that target is
// surfaced as unsupported (an upstream PR is needed).
function PushDxSection() {
  const pushConfig = useHamClockStore((s) => s.pushConfig);
  const loadPushConfig = useHamClockStore((s) => s.loadPushConfig);
  const savePushConfig = useHamClockStore((s) => s.savePushConfig);

  const [form, setForm] = useState<HamClockPushConfig>(pushConfig);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    void loadPushConfig();
  }, [loadPushConfig]);
  useEffect(() => {
    setForm(pushConfig);
  }, [pushConfig]);

  const patch = (p: Partial<HamClockPushConfig>) => setForm((f) => ({ ...f, ...p }));

  async function onSave() {
    setSaving(true);
    try {
      const port =
        Number.isFinite(form.externalPort) && form.externalPort > 0 && form.externalPort < 65536
          ? form.externalPort
          : HAMCLOCK_PUSH_DEFAULT_PORT;
      await savePushConfig({ ...form, externalPort: port, externalHost: form.externalHost.trim() });
    } finally {
      setSaving(false);
    }
  }

  const labelStyle: CSSProperties = { color: 'var(--fg-2)', fontSize: 12, minWidth: 92 };
  const inputStyle: CSSProperties = {
    flex: 1,
    background: 'var(--bg-1, #0e1014)',
    border: '1px solid var(--line-1, var(--line))',
    borderRadius: 'var(--r-sm, 4px)',
    color: 'var(--fg-0)',
    fontSize: 12,
    padding: '5px 8px',
  };
  const rowStyle: CSSProperties = { display: 'flex', alignItems: 'center', gap: 10, marginBottom: 10 };

  return (
    <div style={{ marginTop: 4, marginBottom: 20, paddingTop: 16, borderTop: '1px solid var(--line-1, var(--line))' }}>
      <div style={{
        fontSize: 11, letterSpacing: 0.8, textTransform: 'uppercase',
        color: 'var(--fg-2)', marginBottom: 10, fontWeight: 700,
      }}>
        Push DX to HamClock
      </div>
      <p style={{ margin: '0 0 12px 0', lineHeight: 1.6, color: 'var(--fg-3)', fontSize: 12 }}>
        When you work or click a station in the digital pop-out, place it on the HamClock map by its
        grid. Send-only — nothing controls your radio. Off by default.
      </p>

      <label style={{ ...rowStyle, cursor: 'pointer' }}>
        <span style={labelStyle}>Enable</span>
        <button
          type="button"
          role="switch"
          aria-checked={form.enabled}
          aria-label="Push DX to HamClock"
          className={`btn sm${form.enabled ? ' active' : ''}`}
          onClick={() => patch({ enabled: !form.enabled })}
        >
          {form.enabled ? 'ON' : 'OFF'}
        </button>
      </label>

      {form.enabled && (
        <>
          <div style={rowStyle}>
            <span style={labelStyle}>Trigger</span>
            <select
              aria-label="Push trigger"
              style={inputStyle}
              value={form.trigger}
              onChange={(e) => patch({ trigger: e.target.value as HamClockPushConfig['trigger'] })}
            >
              <option value="on-click">On click (when I click a station)</option>
              <option value="on-active-QSO">On active QSO (the station I'm working)</option>
            </select>
          </div>

          <div style={rowStyle}>
            <span style={labelStyle}>Target</span>
            <select
              aria-label="Push target"
              style={inputStyle}
              value={form.target}
              onChange={(e) => patch({ target: e.target.value as HamClockPushConfig['target'] })}
            >
              <option value="external">External HamClock (host : port)</option>
              <option value="bundled" disabled>
                Bundled OpenHamClock (set-DX not supported yet)
              </option>
            </select>
          </div>

          {form.target === 'external' && (
            <>
              <div style={rowStyle}>
                <span style={labelStyle}>Host</span>
                <input
                  aria-label="HamClock host"
                  style={inputStyle}
                  value={form.externalHost}
                  placeholder="192.168.1.50"
                  spellCheck={false}
                  onChange={(e) => patch({ externalHost: e.target.value })}
                />
              </div>
              <div style={rowStyle}>
                <span style={labelStyle}>Port</span>
                <input
                  aria-label="HamClock port"
                  type="number"
                  min={1}
                  max={65535}
                  style={inputStyle}
                  value={form.externalPort}
                  onChange={(e) => patch({ externalPort: Number(e.target.value) })}
                />
              </div>
            </>
          )}
        </>
      )}

      <button type="button" className="btn sm" disabled={saving} onClick={() => void onSave()}>
        {saving ? 'SAVING…' : 'SAVE'}
      </button>
    </div>
  );
}

function StatusPill({ phase }: { phase: string }) {
  const map: Record<string, { color: string; bg: string }> = {
    Running:      { color: 'var(--accent)', bg: 'rgba(74,158,255,0.12)' },
    Installed:    { color: 'var(--fg-1)',   bg: 'var(--bg-2)' },
    Installing:   { color: 'var(--power)',  bg: 'rgba(255,201,58,0.12)' },
    Starting:     { color: 'var(--power)',  bg: 'rgba(255,201,58,0.12)' },
    Error:        { color: 'var(--tx)',     bg: 'rgba(230,58,43,0.12)' },
    NotInstalled: { color: 'var(--fg-3)',   bg: 'var(--bg-2)' },
  };
  const s = map[phase] ?? map.NotInstalled!;
  return (
    <span style={{
      padding: '2px 8px', borderRadius: 10, fontSize: 11, fontWeight: 600,
      color: s.color, background: s.bg, border: `1px solid ${s.color}`,
    }}>
      {phase}
    </span>
  );
}
