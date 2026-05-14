// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

import { useState } from 'react';
import { LeafletWorldMap } from '../../components/design/LeafletWorldMap';
import { LeafletMapErrorBoundary } from '../../components/design/LeafletMapErrorBoundary';
import { bearingDeg, distanceKm } from '../../components/design/geo';
import { useQrzStore } from '../../state/qrz-store';
import { useRotatorStore } from '../../state/rotator-store';

function normalizeAz(deg: number | null | undefined): number | null {
  if (deg == null || !Number.isFinite(deg)) return null;
  return ((deg % 360) + 360) % 360;
}

function fmtAz(deg: number | null): string {
  if (deg == null) return '---°';
  return `${deg.toFixed(0).padStart(3, '0')}°`;
}

export function RotatorCompassPanel() {
  const home = useQrzStore((s) => s.home);
  const target = useQrzStore((s) => s.lastLookup);
  const rotConnected = useRotatorStore((s) => !!s.status?.connected);
  const rotEnabled = useRotatorStore((s) => s.config.enabled);
  const rotMoving = useRotatorStore((s) => !!s.status?.moving);
  const rotCurrentAz = useRotatorStore((s) => normalizeAz(s.status?.currentAz));
  const setAzimuth = useRotatorStore((s) => s.setAzimuth);
  const stopRotator = useRotatorStore((s) => s.stop);

  const hasHome = !!(home && home.lat != null && home.lon != null);
  const hasTarget = hasHome && !!(target && target.lat != null && target.lon != null);

  const spBearing = hasTarget
    ? bearingDeg(home!.lat as number, home!.lon as number, target!.lat as number, target!.lon as number)
    : null;
  const lpBearing = spBearing == null ? null : (spBearing + 180) % 360;
  const dist = hasTarget
    ? distanceKm(home!.lat as number, home!.lon as number, target!.lat as number, target!.lon as number)
    : null;

  const [path, setPath] = useState<'sp' | 'lp'>('sp');
  const [manualInput, setManualInput] = useState('');

  const rotReady = rotEnabled && rotConnected;
  const manualHeadingDeg = (() => {
    const n = Number.parseFloat(manualInput);
    if (!Number.isFinite(n)) return null;
    return ((n % 360) + 360) % 360;
  })();
  const manualHeadingValid = manualHeadingDeg != null && manualInput.trim() !== '';
  const submitManualHeading = () => {
    if (!rotReady || manualHeadingDeg == null) return;
    void setAzimuth(Math.round(manualHeadingDeg));
  };

  const targetVisibleAz = path === 'lp' ? lpBearing : spBearing;
  const beamForMap = rotConnected && rotCurrentAz != null
    ? rotCurrentAz
    : targetVisibleAz ?? undefined;

  return (
    <div className="rotator-compass">
      <div className="rc-map">
        <LeafletMapErrorBoundary onError={() => { /* silent */ }} fallback={null}>
          {hasHome && (
            <LeafletWorldMap
              home={{
                call: home!.callsign,
                lat: home!.lat as number,
                lon: home!.lon as number,
                grid: home!.grid,
                imageUrl: home!.imageUrl,
              }}
              target={
                hasTarget
                  ? {
                      call: target!.callsign,
                      lat: target!.lat as number,
                      lon: target!.lon as number,
                      grid: target!.grid,
                      imageUrl: target!.imageUrl,
                    }
                  : null
              }
              beamBearing={beamForMap}
              beamRangeKm={6000}
              beamHalfWidthDeg={10}
              active={true}
              interactive={false}
              onRotateToBearing={(deg) => { void setAzimuth(Math.round(deg)); }}
            />
          )}
        </LeafletMapErrorBoundary>
        {!hasHome && (
          <div className="rc-empty">
            <span className="label-xs">No QRZ home location</span>
            <span className="rc-empty-hint">Log in via the QRZ panel to enable the map.</span>
          </div>
        )}
      </div>

      <div className="rc-overlay">
        <div className="rc-now-badge">
          <span className="label-xs">NOW</span>
          <span className="mono">{fmtAz(rotCurrentAz)}</span>
          {rotMoving && targetVisibleAz != null && (
            <span className="rc-arrow">→ {fmtAz(targetVisibleAz)}</span>
          )}
        </div>

        <div className="rc-controls">
          {hasTarget && dist != null && (
            <div className="rc-badge rc-badge--right">
              <span className="label-xs">DIST</span>
              <span className="mono">{Math.round(dist).toLocaleString()} km</span>
            </div>
          )}
          <div className="rc-sp-lp">
            <button
              type="button"
              className={`rc-btn rc-btn--path${path === 'sp' ? ' selected' : ''}`}
              onClick={() => {
                setPath('sp');
                if (spBearing != null) void setAzimuth(Math.round(spBearing));
              }}
              disabled={!hasTarget || !rotReady}
              title={hasTarget ? 'Rotate short-path' : 'Look up a QRZ callsign for SP / LP'}
            >
              SP {fmtAz(spBearing)}
            </button>
            <button
              type="button"
              className={`rc-btn rc-btn--path${path === 'lp' ? ' selected' : ''}`}
              onClick={() => {
                setPath('lp');
                if (lpBearing != null) void setAzimuth(Math.round(lpBearing));
              }}
              disabled={!hasTarget || !rotReady}
              title={hasTarget ? 'Rotate long-path' : 'Look up a QRZ callsign for SP / LP'}
            >
              LP {fmtAz(lpBearing)}
            </button>
          </div>
          <form
            className="rc-manual"
            onSubmit={(e) => {
              e.preventDefault();
              submitManualHeading();
            }}
          >
            <input
              type="number"
              inputMode="numeric"
              min={0}
              max={359}
              step={1}
              className="rc-input"
              placeholder="HDG"
              value={manualInput}
              onChange={(e) => setManualInput(e.currentTarget.value)}
              aria-label="Heading in degrees"
            />
            <button
              type="submit"
              className="rc-btn rc-btn--go"
              disabled={!rotReady || !manualHeadingValid}
              title={rotReady ? 'Rotate to entered heading' : 'Rotator not connected'}
            >
              GO
            </button>
          </form>
          <button
            type="button"
            className="rc-btn rc-btn--stop"
            onClick={() => { void stopRotator(); }}
            disabled={!rotReady || !rotMoving}
            title={rotMoving ? 'Stop rotator' : 'Rotator is idle'}
          >
            STOP
          </button>
          {!rotEnabled && <span className="rc-hint">Rotator disabled</span>}
          {rotEnabled && !rotConnected && <span className="rc-hint">Connecting…</span>}
        </div>
      </div>
    </div>
  );
}
