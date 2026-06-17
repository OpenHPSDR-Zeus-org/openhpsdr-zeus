import { describe, expect, it } from 'vitest';
import { frameBinRangeForHz, miniPanSignalLevel, sampleSpectrumAtHz } from './FilterMiniPan';

describe('FilterMiniPan frequency sampling', () => {
  it('keeps partial frame overlap at its true frequency position', () => {
    const frameStartHz = 14_260_000;
    const binsPerHz = 1 / 100;
    const binsLength = 100;

    expect(frameBinRangeForHz(14_258_000, 14_259_000, frameStartHz, binsPerHz, binsLength)).toBeNull();
    expect(frameBinRangeForHz(14_259_500, 14_260_500, frameStartHz, binsPerHz, binsLength)).toEqual([0, 5]);
    expect(frameBinRangeForHz(14_269_500, 14_271_000, frameStartHz, binsPerHz, binsLength)).toEqual([95, 100]);
    expect(frameBinRangeForHz(14_271_000, 14_272_000, frameStartHz, binsPerHz, binsLength)).toBeNull();
  });

  it('interpolates in-frame samples and returns null off-frame', () => {
    const frameStartHz = 14_260_000;
    const binsPerHz = 1 / 100;
    const bins = new Float32Array([-100, -80, -60]);

    expect(sampleSpectrumAtHz(bins, 14_259_999, frameStartHz, binsPerHz)).toBeNull();
    expect(sampleSpectrumAtHz(bins, 14_260_000, frameStartHz, binsPerHz)).toBeCloseTo(-100);
    expect(sampleSpectrumAtHz(bins, 14_260_050, frameStartHz, binsPerHz)).toBeCloseTo(-90);
    expect(sampleSpectrumAtHz(bins, 14_260_200, frameStartHz, binsPerHz)).toBeCloseTo(-60);
    expect(sampleSpectrumAtHz(bins, 14_260_201, frameStartHz, binsPerHz)).toBeNull();
  });

  it('keeps no-signal noise at the floor while lifting signals above it', () => {
    const floorDb = -110;

    expect(miniPanSignalLevel(-109, floorDb, 3.5, 20)).toBeLessThan(0.01);
    expect(miniPanSignalLevel(-106, floorDb, 3.5, 20)).toBeLessThan(0.08);
    expect(miniPanSignalLevel(-100, floorDb, 3.5, 20)).toBeGreaterThan(0.45);
    expect(miniPanSignalLevel(-88, floorDb, 3.5, 20)).toBeGreaterThan(0.9);
  });
});
