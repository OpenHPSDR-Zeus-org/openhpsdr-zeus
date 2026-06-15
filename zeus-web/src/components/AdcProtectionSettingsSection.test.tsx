// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';

import { render, act } from './meters/__tests__/harness';
import { useConnectionStore } from '../state/connection-store';
import { useRxMetersStore } from '../state/rx-meters-store';
import { useTxStore } from '../state/tx-store';
import { AdcProtectionSettingsSection } from './AdcProtectionSettingsSection';

const fetchAdcProtectionMock = vi.hoisted(() => vi.fn());
const setAdcProtectionMock = vi.hoisted(() => vi.fn());

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return {
    ...actual,
    fetchAdcProtection: fetchAdcProtectionMock,
    setAdcProtection: setAdcProtectionMock,
  };
});

const STATUS = {
  config: {
    enabled: true,
    attackMs: 100,
    releaseMs: 100,
    attackStepDb: 1,
    releaseStepDb: 1,
    maxOffsetDb: 31,
    warningThreshold: 3,
    magnitudeSoftLimit: 0,
  },
  attenDb: 0,
  offsetDb: 0,
  effectiveDb: 0,
  warning: false,
  overloadLevel: 0,
  lastOverloadBits: 0,
  adc0MaxMagnitude: 1200,
  adc1MaxMagnitude: null,
  adc0MaxMagnitudeAtOverload: 0,
  adc1MaxMagnitudeAtOverload: 0,
  lastTelemetryUtc: null,
};

function resetStores() {
  useConnectionStore.setState({
    autoAgcEnabled: false,
    autoAttEnabled: true,
  });
  useTxStore.setState({ rxDbm: -130 });
  useRxMetersStore.setState({
    signalPk: -Infinity,
    signalAv: -Infinity,
    adcPk: -Infinity,
    adcAv: -Infinity,
    agcGain: 0,
    agcEnvPk: -Infinity,
    agcEnvAv: -Infinity,
  });
}

describe('AdcProtectionSettingsSection', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    fetchAdcProtectionMock.mockResolvedValue(STATUS);
    setAdcProtectionMock.mockResolvedValue(STATUS);
    resetStores();
  });

  it('surfaces RX-chain health from live RxMetersV2 telemetry', async () => {
    useRxMetersStore.setState({
      signalPk: -86,
      signalAv: -90,
      adcPk: -82,
      adcAv: -88,
      agcGain: 4,
      agcEnvPk: -92,
      agcEnvAv: -96,
    });

    const { container, unmount } = render(
      createElement(AdcProtectionSettingsSection),
    );

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(container.textContent).toContain('Front end under-filled');
    expect(container.textContent).toContain('Recover dynamic range');
    expect(container.textContent).toContain('WDSP ADC Pk-82.0 dB');
    expect(container.textContent).toContain('ADC Headroom82 dB');
    expect(container.textContent).toContain('WDSP AGC+4 dB');

    unmount();
  });

  it('surfaces auto-optimizing AGC health when auto correction is active', async () => {
    useConnectionStore.setState({
      autoAgcEnabled: true,
      autoAttEnabled: true,
    });
    useRxMetersStore.setState({
      signalPk: -58,
      signalAv: -63,
      adcPk: -5,
      adcAv: -18,
      agcGain: -32,
      agcEnvPk: -60,
      agcEnvAv: -66,
    });

    const { container, unmount } = render(
      createElement(AdcProtectionSettingsSection),
    );

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(container.textContent).toContain('AGC auto-optimizing');
    expect(container.textContent).toContain('Auto AGC/ATT restoring headroom');

    unmount();
  });
});
