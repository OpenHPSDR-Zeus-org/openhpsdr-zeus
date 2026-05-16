// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

namespace Zeus.Contracts;

/// <summary>
/// A named band plan region (e.g. "IARU_R1", "EI", "US_FCC_EXTRA").
/// Country regions declare a ParentId so their segments overlay the parent plan
/// rather than replacing it wholesale — only the changed segments need to be in
/// the country file.
/// </summary>
public sealed record BandRegion(
    string Id,
    string DisplayName,
    string ShortCode,
    string? ParentId);

/// <summary>
/// A single frequency segment within a region plan.
/// Frequencies are in Hz (wire consistency with the rest of Zeus).
/// </summary>
public sealed record BandSegment(
    string RegionId,
    long LowHz,
    long HighHz,
    string Label,
    BandAllocation Allocation,
    ModeRestriction ModeRestriction,
    int? MaxPowerW,
    string? Notes);

public enum BandAllocation : byte
{
    Amateur = 0,
    SWL = 1,
    Broadcast = 2,
    Reserved = 3,
    Unknown = 4,
}

public enum ModeRestriction : byte
{
    Any = 0,
    CwOnly = 1,
    PhoneOnly = 2,
    DigitalOnly = 3,
    // CW + narrowband digital permitted, phone prohibited.
    // Required by 30m WARC allocation in IARU R1/R2 where CW and digital
    // share the band but phone is not authorised.
    CwAndDigital = 4,
}

/// <summary>
/// Wire DTO returned by GET /api/bands/plan — the resolved (parent-merged) plan
/// for a region. Segments are sorted by LowHz, non-overlapping.
/// </summary>
public sealed record BandPlanDto(
    string RegionId,
    IReadOnlyList<BandSegment> Segments);

/// <summary>Request body for POST /api/bands/current.</summary>
public sealed record BandPlanCurrentSetRequest(string RegionId);

/// <summary>Request body for PUT /api/bands/plan (user override).</summary>
public sealed record BandPlanSaveRequest(
    string RegionId,
    IReadOnlyList<BandSegment> Segments);

/// <summary>
/// Broadcast on MsgType.BandPlanChanged (0x18) when the active region changes
/// or the plan is edited. Payload: [type:1][regionIdUtf8:variable].
/// </summary>
public sealed record BandPlanChangedPayload(string RegionId);

/// <summary>Request body for POST /api/bands/guard.</summary>
public sealed record BandGuardSetRequest(bool Ignore);
