// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Simone Fabris (IU3QEZ), and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

internal static class RitXitMath
{
    public const int MaxOffsetHz = 3000;

    public static int ClampOffset(int hz) =>
        Math.Clamp(hz, -MaxOffsetHz, MaxOffsetHz);

    public static int FilterAwareStepHz(int filterBandwidthHz) =>
        filterBandwidthHz <= 250 ? 5 : 10;

    public static (long RxHz, long TxHz) WireFreqs(RxMode mode, long dialHz,
        IncrementalTuningMode itMode, int ritOffsetHz, int xitOffsetHz)
    {
        int ritDelta = itMode == IncrementalTuningMode.Rit ? ritOffsetHz : 0;
        int xitDelta = itMode == IncrementalTuningMode.Xit ? xitOffsetHz : 0;
        return (
            CwOffset.EffectiveLoHz(mode, dialHz + ritDelta),
            CwOffset.EffectiveLoHz(mode, dialHz + xitDelta));
    }
}
