// SPDX-License-Identifier: GPL-2.0-or-later
//
// Ft8Workspace — the fixed full-screen FT8 command-center (KB2UKA design,
// Option A dark-HUD). NOT the moveable panel grid: a purpose-built locked
// layout that comes up when FT8 mode is engaged. Three columns under a header,
// status bar across the bottom, matching docs/designs/ft8-ui.md.
//
// This is the SHELL: the decode table is live (real ft8-store data); the
// waterfall, band map, activity log, and TX controls are labelled placeholders
// to be filled in (each reuses an existing Zeus seam — panadapter WebGL,
// LogService, VFO/CAT). Header tabs and the own-VFO readout are scaffolded.

import { useEffect, useState } from 'react';
import { useFt8Store } from '../../state/ft8-store';
import { Ft8DecodeTable } from './Ft8DecodeTable';
import '../../styles/ft8-theme.css';

export interface Ft8WorkspaceProps {
  /** Called when the operator leaves FT8 (e.g. closes the workspace). */
  onClose?: () => void;
  /** Operator callsign for the directed-at-me decode highlight. */
  myCall?: string;
  /** Dial frequency to show in the workspace VFO (Hz). */
  dialHz?: number;
}

function useUtcClock(): string {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const id = setInterval(() => setNow(new Date()), 1000);
    return () => clearInterval(id);
  }, []);
  const p = (n: number) => n.toString().padStart(2, '0');
  return `${p(now.getUTCHours())}:${p(now.getUTCMinutes())}:${p(now.getUTCSeconds())} UTC`;
}

function fmtMHz(hz?: number): string {
  if (!hz || hz <= 0) return '—.———.———';
  return (hz / 1e6).toFixed(6);
}

export function Ft8Workspace({ onClose, myCall, dialHz }: Ft8WorkspaceProps) {
  const clock = useUtcClock();
  const nativeAvailable = useFt8Store((s) => s.nativeAvailable);
  const protocol = useFt8Store((s) => s.protocol);
  const decodeCount = useFt8Store((s) => s.rows.length);

  return (
    <div className="ft8-workspace" role="region" aria-label="FT8 workspace">
      <header className="ft8-ws-header">
        <span className="ft8-ws-title">{protocol} DIGITAL MODE</span>
        <span className="ft8-ws-clock">{clock}</span>
        {onClose && (
          <button type="button" className="ft8-ws-close" onClick={onClose}>
            Exit
          </button>
        )}
      </header>

      <div className="ft8-ws-body">
        {/* Left — radio / VFO / band activity / waterfall */}
        <div className="ft8-ws-col">
          <section className="ft8-region">
            <div className="ft8-region__head">Radio</div>
            <div className="ft8-vfo">
              {fmtMHz(dialHz)} <small>MHz · USB</small>
            </div>
          </section>
          <section className="ft8-region">
            <div className="ft8-region__head">Band activity</div>
            <div className="ft8-placeholder">spectrum — reuses panadapter WebGL</div>
          </section>
          <section className="ft8-region ft8-region--grow">
            <div className="ft8-region__head">Receive · waterfall</div>
            <div className="ft8-placeholder">waterfall + decode markers</div>
          </section>
        </div>

        {/* Center — decode table (live) + activity log */}
        <div className="ft8-ws-col ft8-ws-col--center">
          <section className="ft8-region ft8-region--decodes ft8-region--grow">
            <div className="ft8-region__head">Decoded messages</div>
            <div className="ft8-region__body">
              <Ft8DecodeTable myCall={myCall} />
            </div>
          </section>
          <section className="ft8-region">
            <div className="ft8-region__head">Activity log</div>
            <div className="ft8-placeholder">QSO log — feeds existing LogService (ADIF)</div>
          </section>
        </div>

        {/* Right — band map / TX control / stats */}
        <div className="ft8-ws-col">
          <section className="ft8-region ft8-region--grow">
            <div className="ft8-region__head">Band map</div>
            <div className="ft8-placeholder">great-circle map from decoded grids</div>
          </section>
          <section className="ft8-region">
            <div className="ft8-region__head">TX control</div>
            <div className="ft8-placeholder">arm · CQ · even/odd · power (bench-gated)</div>
          </section>
          <section className="ft8-region">
            <div className="ft8-region__head">Stats</div>
            <div className="ft8-placeholder">QSOs · DXCC · grids · best DX</div>
          </section>
        </div>
      </div>

      <footer className="ft8-ws-status">
        <span className={nativeAvailable ? 'ok' : 'warn'}>
          {nativeAvailable ? 'DECODER READY' : 'DECODER UNAVAILABLE'}
        </span>
        <span>{decodeCount} decodes</span>
        <span style={{ marginLeft: 'auto' }}>FT8 / FT4 native — no audio routing</span>
      </footer>
    </div>
  );
}

/**
 * Self-contained mount point: subscribes to ft8-store `open` and renders the
 * workspace overlay or nothing. Drop a single &lt;Ft8WorkspaceMount/&gt; into
 * the app shell — it owns its own visibility, so the host needs no extra hooks
 * or conditional render logic. The overlay is position:fixed and covers the app
 * when open.
 */
export function Ft8WorkspaceMount() {
  const open = useFt8Store((s) => s.open);
  const close = useFt8Store((s) => s.closeWorkspace);
  if (!open) return null;
  return <Ft8Workspace onClose={close} />;
}
