// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// CALIBRATION settings tab — per-radio frequency / clock-drift correction
// (issue #325). Hosts the one-button auto-cal card. Future expansion
// (separate cal slots for ext-10MHz reference, S-meter calibration,
// etc.) lands here.

import { FrequencyCalibrationCard } from './FrequencyCalibrationCard';

export function CalibrationPanel() {
  return (
    <div className="ps-shell">
      <FrequencyCalibrationCard />
    </div>
  );
}
