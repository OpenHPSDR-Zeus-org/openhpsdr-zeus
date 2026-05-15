// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Header gear → slide-down config flyout. Slimmed to a single operator
// toggle: Zeus mode. All other behaviour (scales shown, dBm readout, SWR
// alarm, attack / decay / averaging / peak hold) is locked to the
// ANALOG_METER_DEFAULTS — the previous "tweak everything" surface was
// adding noise without much value for the typical operator.

import { useAnalogMeterStore } from './analogMeterStore';

interface CheckRowProps {
  checked: boolean;
  onChange: (v: boolean) => void;
  children: React.ReactNode;
  sub?: string;
}

function CheckRow({ checked, onChange, children, sub }: CheckRowProps) {
  return (
    <label className="am-check">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      <span className="am-check-box" />
      <span className="am-check-body">
        <span className="am-check-lbl">{children}</span>
        {sub && <span className="am-check-sub">{sub}</span>}
      </span>
    </label>
  );
}

interface AnalogMeterConfigProps {
  open: boolean;
  onClose: () => void;
}

export function AnalogMeterConfig({ open, onClose }: AnalogMeterConfigProps) {
  const zeusMode = useAnalogMeterStore((s) => s.zeusMode);
  const setZeusMode = useAnalogMeterStore((s) => s.setZeusMode);
  // Mount/unmount instead of toggling .open keeps reconcile cost out of the
  // ballistic rAF loop when the gear is closed.
  if (!open) return null;

  return (
    <div className="am-config open">
      <div className="am-cf-grid">
        <section className="am-cf-sect">
          <header>
            <h4>S-Meter</h4>
          </header>
          <div className="am-cf-body">
            <CheckRow
              checked={zeusMode}
              onChange={setZeusMode}
              sub="Image fades in past S9, lightning crackles at S9+20"
            >
              Zeus mode
            </CheckRow>
          </div>
        </section>
      </div>

      <div className="am-cf-foot">
        <span className="am-cf-foot-hint">Click ⚙ in the header to close.</span>
        <button type="button" className="am-done" onClick={onClose}>
          Done
        </button>
      </div>
    </div>
  );
}
