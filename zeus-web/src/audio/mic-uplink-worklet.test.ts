// SPDX-License-Identifier: GPL-2.0-or-later

import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import vm from 'node:vm';
import { describe, expect, it } from 'vitest';

type WorkletMessage = { samples?: Float32Array; peak?: number };

type Processor = {
  port: { postMessage: (message: WorkletMessage, transfer?: ArrayBuffer[]) => void };
  process: (inputs: Float32Array[][]) => boolean;
};

function loadProcessor(actualSampleRate: number): { processor: Processor; messages: WorkletMessage[] } {
  const code = readFileSync(resolve(process.cwd(), 'public/mic-uplink-worklet.js'), 'utf8');
  let ProcessorCtor: new () => Processor;

  const sandbox = {
    Float32Array,
    Math,
    Number,
    sampleRate: actualSampleRate,
    AudioWorkletProcessor: class {
      port = { postMessage: () => { /* replaced below */ } };
    },
    registerProcessor: (_name: string, ctor: new () => Processor) => {
      ProcessorCtor = ctor;
    },
  };

  vm.runInNewContext(code, sandbox);
  const processor = new ProcessorCtor!();
  const messages: WorkletMessage[] = [];
  processor.port = {
    postMessage: (message: WorkletMessage) => messages.push(message),
  };
  return { processor, messages };
}

function processBlock(processor: Processor, samples: Float32Array): void {
  expect(processor.process([[samples]])).toBe(true);
}

function sine(rate: number, hz: number, count: number): Float32Array {
  const samples = new Float32Array(count);
  for (let i = 0; i < count; i++) {
    samples[i] = Math.sin((2 * Math.PI * hz * i) / rate);
  }
  return samples;
}

function rmsError(samples: Float32Array, rate: number, hz: number, skip: number): number {
  let sum = 0;
  let n = 0;
  for (let i = skip; i < samples.length; i++) {
    const expected = Math.sin((2 * Math.PI * hz * i) / rate);
    const e = samples[i]! - expected;
    sum += e * e;
    n++;
  }
  return Math.sqrt(sum / Math.max(1, n));
}

describe('mic-uplink-worklet', () => {
  it('passes through 48 kHz input into one 960-sample block', () => {
    const { processor, messages } = loadProcessor(48_000);
    const samples = new Float32Array(960);
    for (let i = 0; i < samples.length; i++) samples[i] = i / 960;

    processBlock(processor, samples);

    expect(messages).toHaveLength(1);
    const out = messages[0]!.samples;
    expect(out).toBeInstanceOf(Float32Array);
    expect(out).toHaveLength(960);
    expect(messages[0]!.peak).toBeCloseTo(959 / 960, 6);
    for (let i = 0; i < samples.length; i++) {
      expect(out![i]).toBeCloseTo(samples[i]!, 6);
    }
  });

  it('resamples 44.1 kHz capture to 48 kHz mic blocks', () => {
    const { processor, messages } = loadProcessor(44_100);
    processBlock(processor, sine(44_100, 1_000, 1_200));

    expect(messages.length).toBeGreaterThanOrEqual(1);
    const out = messages[0]!.samples!;
    expect(out).toHaveLength(960);
    expect(messages[0]!.peak).toBeGreaterThan(0.9);
    expect(rmsError(out, 48_000, 1_000, 48)).toBeLessThan(0.03);
  });

  it('downsamples 96 kHz capture to 48 kHz mic blocks', () => {
    const { processor, messages } = loadProcessor(96_000);
    processBlock(processor, sine(96_000, 1_000, 2_200));

    expect(messages.length).toBeGreaterThanOrEqual(1);
    const out = messages[0]!.samples!;
    expect(out).toHaveLength(960);
    expect(messages[0]!.peak).toBeGreaterThan(0.9);
    expect(rmsError(out, 48_000, 1_000, 48)).toBeLessThan(0.03);
  });
});
