// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Test stub for IBandPlanService: always reports InBand=true and TxGuardIgnore=true
/// so TX tests can key MOX without needing a real band plan loaded.
/// </summary>
internal sealed class NullBandPlanService : IBandPlanService
{
    public static readonly NullBandPlanService Instance = new();

    public BandRegion CurrentRegion { get; } =
        new BandRegion("IARU_R1", "IARU Region 1", "R1", null);

    public IReadOnlyList<BandSegment> CurrentPlan { get; } = Array.Empty<BandSegment>();

    public bool TxGuardIgnore => true;

#pragma warning disable CS0067
    public event Action? PlanChanged;
#pragma warning restore CS0067

    public BandSegment? GetSegment(long freqHz) => null;

    public bool InBand(long freqHz, RxMode mode) => true;
}
