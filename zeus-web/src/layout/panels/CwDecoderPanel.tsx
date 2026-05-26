// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { CwDecoder } from '../../components/design/CwDecoder';
import { useCwDecoderStore } from '../../state/cw-decoder-store';
import { TileChrome } from '../TileChrome';
import type { PanelComponentProps } from '../panels';

export function CwDecoderPanel({ onRemove }: PanelComponentProps) {
  const { state, setEnabled } = useCwDecoderStore();

  const isEnabled = state !== 'idle';

  const handleRemove = onRemove ?? (() => {});

  return (
    <div className="workspace-tile">
      <TileChrome
        title="CW Decoder"
        onRemove={handleRemove}
        rightSlot={
          <button
            type="button"
            className={`btn ${isEnabled ? 'accent' : ''}`}
            onClick={() => setEnabled(!isEnabled)}
            aria-label={isEnabled ? 'Disable decoder' : 'Enable decoder'}
          >
            {isEnabled ? 'ON' : 'OFF'}
          </button>
        }
      />
      <div className="workspace-tile-body">
        <CwDecoder />
      </div>
    </div>
  );
}