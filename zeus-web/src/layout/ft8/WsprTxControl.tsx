// SPDX-License-Identifier: GPL-2.0-or-later
//
// WsprTxControl — the beacon TX cluster for the WSPR workspace. WSPR has no QSO
// state machine: it is a fire-and-forget beacon keyed by the backend WsprTxService
// on even-minute 120 s slots, gated by a tx-percent probability. This cluster
// only sets the beacon content/cadence, arms (explicit), and offers TUNE/HALT.
//
// Arm + transmit lamps come from the shared 0x3A status (mode === "WSPR"); the
// backend is authoritative. No power model is duplicated here.

import { useEffect } from 'react';
import { useState } from 'react';
import { setTun } from '../../api/client';
import { useFt8TxStore } from '../../state/ft8-tx-store';
import { useWsprStore } from '../../state/wspr-store';

export interface WsprTxControlProps {
  myCall: string;
  myGrid: string;
}

async function postJson(url: string, body: unknown): Promise<void> {
  try {
    await fetch(url, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body),
    });
  } catch {
    // Backend arm-state + watchdog are authoritative; a dropped POST is benign.
  }
}

/** Best-effort disarm of the WSPR beacon that survives a tab close / page unload
 *  (where an in-flight fetch would be killed). */
function beaconDisarm(): void {
  try {
    const body = new Blob([JSON.stringify({ enabled: false })], { type: 'application/json' });
    navigator.sendBeacon?.('/api/wspr/tx/arm', body);
  } catch {
    // sendBeacon unavailable / blocked — the backend watchdog is the backstop.
  }
}

export function WsprTxControl({ myCall, myGrid }: WsprTxControlProps) {
  const status = useFt8TxStore((s) => s.status);
  const band = useWsprStore((s) => s.band);
  const [dBm, setDBm] = useState(30);
  const [audioHz, setAudioHz] = useState(1500);
  const [txPercent, setTxPercent] = useState(20);
  const [tunOn, setTunOn] = useState(false);

  // Safety: disarm the beacon when the WSPR control unmounts (workspace close) or
  // the tab closes. WSPR is fully backend-autonomous once armed (no per-slot
  // frontend interaction), so without this it would beacon every 120 s slot on a
  // real amp until the 30-min watchdog — the watchdog must not be the SOLE backstop.
  useEffect(() => {
    window.addEventListener('pagehide', beaconDisarm);
    return () => {
      window.removeEventListener('pagehide', beaconDisarm);
      beaconDisarm();
    };
  }, []);

  // Safety: a band change force-disarms the beacon (never auto-beacon onto a band
  // the operator just switched to).
  useEffect(() => {
    beaconDisarm();
  }, [band]);

  const wsprStatus = status?.mode === 'WSPR' ? status : null;
  const armed = wsprStatus?.armed ?? false;
  const transmitting = wsprStatus?.transmitting ?? false;
  const canBeacon = myCall.trim().length > 0 && myGrid.trim().length >= 4;

  const pushSettings = () =>
    void postJson('/api/wspr/tx/settings', {
      call: myCall,
      grid4: myGrid.slice(0, 4),
      dBm,
      audioHz,
      txPercent: txPercent / 100,
    });

  const toggleArm = () => {
    if (!armed) pushSettings(); // sync content before arming
    void postJson('/api/wspr/tx/arm', { enabled: !armed });
  };

  const toggleTune = () => {
    const next = !tunOn;
    setTunOn(next);
    void setTun(next).catch(() => setTunOn(!next));
  };

  return (
    <div className="ft8-tx" aria-label="WSPR TX control">
      <div className="ft8-tx__lamps">
        <span className={`ft8-tx__lamp${armed ? ' is-armed' : ''}`} title="Backend armed">
          ARMED
        </span>
        <span className={`ft8-tx__lamp${transmitting ? ' is-tx' : ''}`} title="On air">
          TX
        </span>
        {armed && wsprStatus && wsprStatus.watchdogSecsRemaining > 0 && (
          <span className="ft8-tx__wdt" title="Watchdog auto-disarm">
            WDT {Math.ceil(wsprStatus.watchdogSecsRemaining / 60)}m
          </span>
        )}
      </div>

      <label className="ft8-tx__field">
        <span>POWER (dBm)</span>
        <input
          type="number"
          min={0}
          max={43}
          value={dBm}
          onChange={(e) => setDBm(Number(e.target.value))}
          onBlur={() => armed && pushSettings()}
        />
      </label>
      <label className="ft8-tx__field">
        <span>OFFSET (Hz)</span>
        <input
          type="number"
          min={1400}
          max={1600}
          value={audioHz}
          onChange={(e) => setAudioHz(Number(e.target.value))}
          onBlur={() => armed && pushSettings()}
        />
      </label>
      <label className="ft8-tx__field">
        <span>TX % (slots beaconed)</span>
        <input
          type="number"
          min={0}
          max={100}
          step={5}
          value={txPercent}
          onChange={(e) => setTxPercent(Number(e.target.value))}
          onBlur={() => armed && pushSettings()}
        />
      </label>

      <div className="ft8-tx__row">
        <button
          type="button"
          className={`ft8-tx__enable${armed ? ' is-on' : ''}`}
          disabled={!canBeacon}
          title={canBeacon ? 'Arm the WSPR beacon (explicit)' : 'Set Call + Grid to beacon'}
          onClick={toggleArm}
        >
          {armed ? 'BEACON ON · DISABLE' : 'ENABLE BEACON'}
        </button>
      </div>

      <div className="ft8-tx__row">
        <button
          type="button"
          className={`ft8-tx__tune${tunOn ? ' is-on' : ''}`}
          onClick={toggleTune}
          title="Key a single-tone carrier (antenna tune)"
        >
          {tunOn ? 'TUNE ON' : 'TUNE'}
        </button>
        <button
          type="button"
          className="ft8-tx__halt"
          onClick={() => void postJson('/api/wspr/tx/halt', {})}
          title="Abort: disarm and drop the beacon immediately"
        >
          HALT
        </button>
      </div>
    </div>
  );
}
