// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

import { describe, it, expect } from 'vitest';
import {
  fmtBytes,
  fmtClock,
  fmtDb,
  parentOf,
  baseName,
  joinFolder,
  childFolders,
  breadcrumb,
  segmentZone,
  litSegments,
  abbreviatePath,
} from './wavRecorder.format';

describe('fmtBytes', () => {
  it('formats across magnitudes', () => {
    expect(fmtBytes(0)).toBe('0 B');
    expect(fmtBytes(512)).toBe('512 B');
    expect(fmtBytes(2048)).toBe('2 KB');
    expect(fmtBytes(5 * 1024 * 1024)).toBe('5.0 MB');
    expect(fmtBytes(3 * 1024 * 1024 * 1024)).toBe('3.00 GB');
  });
  it('guards bad input', () => {
    expect(fmtBytes(-1)).toBe('—');
    expect(fmtBytes(NaN)).toBe('—');
  });
});

describe('fmtClock', () => {
  it('formats mm:ss', () => {
    expect(fmtClock(0)).toBe('00:00');
    expect(fmtClock(5)).toBe('00:05');
    expect(fmtClock(65)).toBe('01:05');
    expect(fmtClock(600)).toBe('10:00');
  });
  it('clamps negatives and non-finite to 00:00', () => {
    expect(fmtClock(-3)).toBe('00:00');
    expect(fmtClock(NaN)).toBe('00:00');
  });
  it('rolls hours into minutes (mm:ss, no hour field)', () => {
    expect(fmtClock(3661)).toBe('61:01');
    expect(fmtClock(7200)).toBe('120:00');
  });
});

describe('fmtDb', () => {
  it('floors at -infinity', () => {
    expect(fmtDb(-100)).toBe('-∞');
    expect(fmtDb(-120)).toBe('-∞');
    expect(fmtDb(-26.3)).toBe('-26.3');
    expect(fmtDb(0)).toBe('0.0');
  });
});

describe('path helpers', () => {
  it('parentOf', () => {
    expect(parentOf('cq.wav')).toBe('');
    expect(parentOf('DX/cq.wav')).toBe('DX');
    expect(parentOf('DX/2024/cq.wav')).toBe('DX/2024');
  });
  it('baseName', () => {
    expect(baseName('cq.wav')).toBe('cq.wav');
    expect(baseName('DX/2024/cq.wav')).toBe('cq.wav');
  });
  it('joinFolder', () => {
    expect(joinFolder('', 'DX')).toBe('DX');
    expect(joinFolder('DX', 'sub')).toBe('DX/sub');
    expect(joinFolder('DX/', '/sub/')).toBe('DX/sub');
  });
});

describe('childFolders', () => {
  const folders = ['DX', 'DX/2024', 'DX/2025', 'Nets', 'Nets/Weekly'];
  it('returns immediate children of root', () => {
    expect(childFolders(folders, '')).toEqual(['DX', 'Nets']);
  });
  it('returns immediate children of a subfolder', () => {
    expect(childFolders(folders, 'DX')).toEqual(['DX/2024', 'DX/2025']);
  });
  it('returns empty for a leaf', () => {
    expect(childFolders(folders, 'Nets/Weekly')).toEqual([]);
  });
  it('does not treat a prefix-sibling as a child', () => {
    // "DXpedition" shares the "DX" prefix but is NOT a child of "DX".
    const f = ['DX', 'DXpedition', 'DX/2024'];
    expect(childFolders(f, 'DX')).toEqual(['DX/2024']);
    expect(childFolders(f, '')).toEqual(['DX', 'DXpedition']);
  });
});

describe('breadcrumb', () => {
  it('root', () => {
    expect(breadcrumb('')).toEqual([{ label: 'Recordings', path: '' }]);
  });
  it('nested', () => {
    expect(breadcrumb('DX/2024')).toEqual([
      { label: 'Recordings', path: '' },
      { label: 'DX', path: 'DX' },
      { label: '2024', path: 'DX/2024' },
    ]);
  });
});

describe('meter ladder', () => {
  it('classifies zones by position', () => {
    expect(segmentZone(0, 40)).toBe('green');
    expect(segmentZone(20, 40)).toBe('green');
    expect(segmentZone(30, 40)).toBe('amber');
    expect(segmentZone(39, 40)).toBe('red');
  });
  it('lit count clamps to 0..total', () => {
    expect(litSegments(0, 40)).toBe(0);
    expect(litSegments(0.5, 40)).toBe(20);
    expect(litSegments(1, 40)).toBe(40);
    expect(litSegments(2, 40)).toBe(40);
    expect(litSegments(-1, 40)).toBe(0);
    expect(litSegments(NaN, 40)).toBe(0);
  });
});

describe('abbreviatePath', () => {
  it('returns empty for empty input', () => {
    expect(abbreviatePath('')).toBe('');
  });
  it('leaves short paths intact', () => {
    expect(abbreviatePath('/home')).toBe('/home');
    expect(abbreviatePath('/home/doug')).toBe('/home/doug');
  });
  it('keeps the last two POSIX segments with an ellipsis prefix', () => {
    expect(abbreviatePath('/Users/doug/Music/Recordings')).toBe('…/Music/Recordings');
  });
  it('keeps the last two Windows segments with a backslash separator', () => {
    expect(abbreviatePath('C:\\Users\\Doug\\Recordings')).toBe('…\\Doug\\Recordings');
  });
  it('honors a custom tail length', () => {
    expect(abbreviatePath('/a/b/c/d', 1)).toBe('…/d');
    expect(abbreviatePath('/a/b/c/d', 3)).toBe('…/b/c/d');
  });
});
