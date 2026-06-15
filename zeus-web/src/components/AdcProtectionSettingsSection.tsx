// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ADC_PROTECTION_CONFIG_DEFAULT,
  fetchAdcProtection,
  setAdcProtection,
  type AdcProtectionConfigDto,
  type AdcProtectionStatusDto,
} from '../api/client';
import { analyzeRxChain } from '../dsp/rx-chain-health';
import { useRxMetersStore } from '../state/rx-meters-store';
import { useTxStore } from '../state/tx-store';
import { Slider } from './design/Slider';

const SAVE_DEBOUNCE_MS = 220;

function hex(v: number, width = 2): string {
  return `0x${Math.max(0, v).toString(16).toUpperCase().padStart(width, '0')}`;
}

function adc(v: number | null): string {
  return v === null ? '--' : `${v}`;
}

function db(v: number | null, digits = 1): string {
  return v === null ? '--' : `${v.toFixed(digits)} dB`;
}

function time(v: string | null): string {
  if (!v) return '--';
  const d = new Date(v);
  return Number.isNaN(d.getTime()) ? v : d.toLocaleTimeString();
}

function clampInt(v: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, Math.round(v)));
}

type Metric = {
  label: string;
  value: string;
  hot?: boolean;
};

function MetricGrid({ metrics }: { metrics: Metric[] }) {
  return (
    <div className="adc-protection-status">
      {metrics.map((m) => (
        <div key={m.label} className={m.hot ? 'hot' : ''}>
          <span>{m.label}</span>
          <strong className="mono">{m.value}</strong>
        </div>
      ))}
    </div>
  );
}

export function AdcProtectionSettingsSection() {
  const [status, setStatus] = useState<AdcProtectionStatusDto | null>(null);
  const [draft, setDraft] = useState<AdcProtectionConfigDto>({
    ...ADC_PROTECTION_CONFIG_DEFAULT,
  });
  const [error, setError] = useState<string | null>(null);
  const saveTimer = useRef<number | null>(null);
  const saveAbort = useRef<AbortController | null>(null);
  const dirty = useRef(false);
  const fallbackRxDbm = useTxStore((s) => s.rxDbm);
  const signalPk = useRxMetersStore((s) => s.signalPk);
  const signalAv = useRxMetersStore((s) => s.signalAv);
  const adcPk = useRxMetersStore((s) => s.adcPk);
  const adcAv = useRxMetersStore((s) => s.adcAv);
  const agcGain = useRxMetersStore((s) => s.agcGain);
  const agcEnvPk = useRxMetersStore((s) => s.agcEnvPk);
  const agcEnvAv = useRxMetersStore((s) => s.agcEnvAv);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const next = await fetchAdcProtection(signal);
      setStatus(next);
      if (!dirty.current) setDraft(next.config);
      setError(null);
    } catch (err) {
      if ((err as DOMException).name !== 'AbortError') {
        setError(err instanceof Error ? err.message : String(err));
      }
    }
  }, []);

  useEffect(() => {
    const ac = new AbortController();
    void load(ac.signal);
    const id = window.setInterval(() => void load(), 1_000);
    return () => {
      ac.abort();
      window.clearInterval(id);
      if (saveTimer.current !== null) window.clearTimeout(saveTimer.current);
      saveAbort.current?.abort();
    };
  }, [load]);

  const commit = useCallback((next: AdcProtectionConfigDto, immediate = false) => {
    dirty.current = true;
    if (saveTimer.current !== null) window.clearTimeout(saveTimer.current);

    const run = () => {
      saveAbort.current?.abort();
      const ac = new AbortController();
      saveAbort.current = ac;
      setAdcProtection(next, ac.signal)
        .then((saved) => {
          if (ac.signal.aborted) return;
          dirty.current = false;
          setStatus(saved);
          setDraft(saved.config);
          setError(null);
        })
        .catch((err) => {
          if ((err as DOMException).name !== 'AbortError') {
            setError(err instanceof Error ? err.message : String(err));
          }
        });
    };

    if (immediate) run();
    else saveTimer.current = window.setTimeout(run, SAVE_DEBOUNCE_MS);
  }, []);

  const update = useCallback((patch: Partial<AdcProtectionConfigDto>, immediate = false) => {
    setDraft((prev) => {
      const next = { ...prev, ...patch };
      commit(next, immediate);
      return next;
    });
  }, [commit]);

  const reset = useCallback(() => {
    setDraft({ ...ADC_PROTECTION_CONFIG_DEFAULT });
    commit({ ...ADC_PROTECTION_CONFIG_DEFAULT }, true);
  }, [commit]);

  const rx = analyzeRxChain({
    signalPk,
    signalAv,
    adcPk,
    adcAv,
    agcGain,
    agcEnvPk,
    agcEnvAv,
    fallbackDbm: fallbackRxDbm,
  });
  const rxWarn = rx.actionTone === 'protect';
  const rxOptimize = rx.actionTone === 'optimize';
  const metrics: Metric[] = [
    {
      label: 'RX Fit',
      value: `${rx.score > 0 ? rx.score : '--'} ${rx.label}`,
      hot: rxWarn || rxOptimize,
    },
    { label: 'RX Action', value: rx.recommendation, hot: rxWarn },
    { label: 'WDSP ADC Pk', value: db(rx.adcPk), hot: rxWarn },
    { label: 'ADC Headroom', value: db(rx.adcHeadroomDb, 0), hot: rxWarn },
    {
      label: 'WDSP AGC',
      value: `${rx.agcGain >= 0 ? '+' : ''}${rx.agcGain.toFixed(0)} dB`,
      hot: rx.state === 'agc-stressed',
    },
    { label: 'Effective', value: `${status?.effectiveDb ?? 0} dB`, hot: status?.warning },
    { label: 'Baseline', value: `${status?.attenDb ?? 0} dB` },
    { label: 'Auto Offset', value: `${status?.offsetDb ?? 0} dB`, hot: (status?.offsetDb ?? 0) > 0 },
    { label: 'Overload', value: hex(status?.lastOverloadBits ?? 0), hot: (status?.lastOverloadBits ?? 0) !== 0 },
    { label: 'ADC0 Max', value: adc(status?.adc0MaxMagnitude ?? null) },
    { label: 'ADC1 Max', value: adc(status?.adc1MaxMagnitude ?? null) },
    { label: 'ADC0 Trip', value: `${status?.adc0MaxMagnitudeAtOverload ?? 0}` },
    { label: 'ADC1 Trip', value: `${status?.adc1MaxMagnitudeAtOverload ?? 0}` },
    { label: 'Updated', value: time(status?.lastTelemetryUtc ?? null) },
  ];

  return (
    <div className="smart-nr-settings">
      <div className="sig-toggle-row">
        <label className="sig-switch">
          <input
            type="checkbox"
            checked={draft.enabled}
            onChange={(e) => update({ enabled: e.currentTarget.checked }, true)}
          />
          <span className="sig-switch-track" aria-hidden="true" />
          <span>{draft.enabled ? 'Enabled' : 'Disabled'}</span>
        </label>
        <button type="button" className="sig-reset-btn" onClick={reset}>
          Reset
        </button>
        <span className="sig-status mono">
          {status?.warning ? 'PROTECT' : draft.enabled ? 'ARMED' : 'OFF'}
        </span>
        {error && <span className="sig-status mono">{error}</span>}
      </div>

      <MetricGrid metrics={metrics} />

      <div className="sig-grid">
        <Slider
          label="Attack"
          value={draft.attackMs}
          min={25}
          max={1000}
          disabled={!draft.enabled}
          onChange={(v) => update({ attackMs: clampInt(v, 25, 1000) })}
          formatValue={(v) => `${Math.round(v)} ms`}
        />
        <Slider
          label="Release"
          value={draft.releaseMs}
          min={50}
          max={5000}
          disabled={!draft.enabled}
          onChange={(v) => update({ releaseMs: clampInt(v, 50, 5000) })}
          formatValue={(v) => `${Math.round(v)} ms`}
        />
        <Slider
          label="Attack Step"
          value={draft.attackStepDb}
          min={1}
          max={6}
          disabled={!draft.enabled}
          onChange={(v) => update({ attackStepDb: clampInt(v, 1, 6) })}
          formatValue={(v) => `${Math.round(v)} dB`}
        />
        <Slider
          label="Release Step"
          value={draft.releaseStepDb}
          min={1}
          max={6}
          disabled={!draft.enabled}
          onChange={(v) => update({ releaseStepDb: clampInt(v, 1, 6) })}
          formatValue={(v) => `${Math.round(v)} dB`}
        />
        <Slider
          label="Max Offset"
          value={draft.maxOffsetDb}
          min={0}
          max={31}
          disabled={!draft.enabled}
          onChange={(v) => update({ maxOffsetDb: clampInt(v, 0, 31) })}
          formatValue={(v) => `${Math.round(v)} dB`}
        />
        <Slider
          label="Warn Level"
          value={draft.warningThreshold}
          min={0}
          max={5}
          disabled={!draft.enabled}
          onChange={(v) => update({ warningThreshold: clampInt(v, 0, 5) })}
          formatValue={(v) => `${Math.round(v)}`}
        />
      </div>

      <label className="sig-number-field">
        <span className="label-xs">P2 Magnitude Limit</span>
        <input
          type="number"
          min={0}
          max={65535}
          step={256}
          disabled={!draft.enabled}
          value={draft.magnitudeSoftLimit}
          onChange={(e) =>
            update({
              magnitudeSoftLimit: clampInt(Number(e.currentTarget.value), 0, 65535),
            })
          }
        />
      </label>
    </div>
  );
}
