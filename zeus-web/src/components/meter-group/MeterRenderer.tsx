// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Renders a single meter inside the MeterGroup panel. Picks the right SVG
// primitive based on the widget's effective kind, plumbing live data from
// the catalog reading. Returns *just* the gauge — no card chrome, no
// header bar — because the surrounding MeterGroup tile owns the chrome
// and the immersive primitives carry their own internal labels / readouts.

import { type ReactNode } from 'react';
import {
  METER_CATALOG,
  MeterReadingId,
  zoneTransitionTicks,
} from '../meters/meterCatalog';
import { useMeterReading } from '../meters/useMeterReading';
import { useEmaSmoothed } from '../../util/useEmaSmoothed';
import { BigArc } from '../immersive-meters/BigArc';
import { VuColumn } from '../immersive-meters/VuColumn';
import { PullDownArc } from '../immersive-meters/PullDownArc';
import { HBarMeter } from '../meters/widgets/HBarMeter';
import { useRadioStore } from '../../state/radio-store';
import { usePaStore } from '../../state/pa-store';
import {
  effectiveKind,
  type MeterGroupWidget,
} from './meterGroupConfig';

interface MeterRendererProps {
  widget: MeterGroupWidget;
}

export function MeterRenderer({ widget }: MeterRendererProps) {
  const def = METER_CATALOG[widget.reading];
  // 90 ms EMA smoothing on the raw 10 Hz feed kills inter-frame steppiness
  // without lagging visibly behind voice dynamics on SSB. See
  // useEmaSmoothed.ts for the alpha = 1 - exp(-dt/tau) ballistics.
  const rawValue = useMeterReading(widget.reading);
  const value = useEmaSmoothed(rawValue, 90);

  const settings = widget.settings ?? {};
  const min = settings.min ?? def.defaultMin;
  const baseMax = settings.max ?? def.defaultMax;
  const label = settings.label ?? def.label;
  const kind = effectiveKind(widget);

  // For TX forward power, the axis top should follow the connected radio's
  // rated wattage (HL2 = 10 W, ANAN-100 = 120 W, 8000DLE = 250 W) so the
  // bar isn't blank on a small rig or pegged on a big one. Resolution:
  // explicit settings.max → operator paMaxPowerWatts override → board
  // default → catalog default. Mirrors the recipe TxStageMeters uses.
  const boardMaxWatts = useRadioStore((s) => s.capabilities.maxPowerWatts);
  const paMaxWatts = usePaStore((s) => s.settings.global.paMaxPowerWatts);
  const max =
    widget.reading === MeterReadingId.TxFwdWatts && settings.max === undefined
      ? paMaxWatts > 0
        ? paMaxWatts
        : boardMaxWatts > 0
          ? boardMaxWatts
          : baseMax
      : baseMax;

  const zoneTicks = zoneTransitionTicks(def, min, max);

  let body: ReactNode = null;
  switch (kind) {
    case 'bigarc': {
      const defsId = `mg-bigarc-${widget.uid}`;
      if (def.unit === 'W') {
        body = (
          <BigArc
            mode="watts"
            watts={value}
            maxWatts={max}
            label={label}
            defsId={defsId}
            zoneTicks={zoneTicks}
          />
        );
      } else if (def.unit === 'ratio') {
        body = (
          <BigArc
            mode="swr"
            ratio={value}
            label={label}
            defsId={defsId}
            zoneTicks={zoneTicks}
          />
        );
      } else {
        body = (
          <BigArc
            mode="dbfs"
            valueDb={value}
            label={label}
            defsId={defsId}
            zoneTicks={zoneTicks}
          />
        );
      }
      break;
    }
    case 'vucolumn': {
      // VuColumn label is split into name + sub from the catalog short
      // (e.g. "MIC Pk" → "MIC" + "PK"). When there's no sub-marker, fall
      // back to using the full short as the name and an empty sub.
      const short = def.short.trim();
      const split = short.match(/^(.+?)\s+(Pk|Av|Avg|GR)$/i);
      const name = split?.[1] ?? short;
      const sub = split?.[2]?.toUpperCase() ?? '';
      body = (
        <VuColumn
          valueDb={value}
          name={name}
          sub={sub}
          defsId={`mg-vu-${widget.uid}`}
          zoneTicks={zoneTicks}
        />
      );
      break;
    }
    case 'pulldown': {
      // PullDownArc anchors at the right end of its arc (0 dB). Zone-tick
      // fractions from `zoneTransitionTicks` run left → right across the
      // raw axis; remap via `1 - frac` so they land at the visually
      // matching position on the right-anchored arc.
      const remapped = zoneTicks.map((t) => ({ ...t, frac: 1 - t.frac }));
      const grValue = isFinite(value) && value > -200 ? Math.max(0, value) : 0;
      body = (
        <PullDownArc
          gainReductionDb={grValue}
          label={label}
          defsId={`mg-pulldown-${widget.uid}`}
          maxGrDb={max > 0 ? max : 20}
          zoneTicks={remapped}
        />
      );
      break;
    }
    case 'hbar':
      body = (
        <HBarMeter
          value={value}
          peak={value}
          def={def}
          label={label}
          settings={{
            min: settings.min,
            max: settings.max,
            label: settings.label,
            peakHold: settings.peakHold,
          }}
          zoneTicks={zoneTicks}
        />
      );
      break;
  }

  return <div style={{ flex: 1, minWidth: 0, display: 'flex' }}>{body}</div>;
}
