// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

public interface IBandPlanService
{
    BandRegion CurrentRegion { get; }
    IReadOnlyList<BandSegment> CurrentPlan { get; }
    BandSegment? GetSegment(long freqHz);
    bool InBand(long freqHz, RxMode mode);
    bool TxGuardIgnore { get; }

    event Action? PlanChanged;
}

/// <summary>
/// Resolves the effective band plan for the active region (parent chain merged),
/// provides InBand frequency/mode checks, and broadcasts plan-changed events.
///
/// Resolution: parent segments first, then child segments overlay (any overlap
/// is removed before inserting child segments). Output is sorted by LowHz with
/// no overlaps — safe for binary search in GetSegment.
/// </summary>
public sealed class BandPlanService : IBandPlanService
{
    private readonly BandPlanStore _store;
    private readonly BandPrefsStore _prefs;
    private readonly ILogger<BandPlanService> _log;

    private BandRegion _currentRegion;
    private IReadOnlyList<BandSegment> _currentPlan;
    private readonly object _lock = new();

    public event Action? PlanChanged;

    public BandPlanService(BandPlanStore store, BandPrefsStore prefs, ILogger<BandPlanService> log)
    {
        _store = store;
        _prefs = prefs;
        _log = log;

        var regionId = _prefs.GetRegionId();
        var region = _store.Regions.FirstOrDefault(r => r.Id == regionId)
                     ?? _store.Regions.FirstOrDefault()
                     ?? new BandRegion("IARU_R1", "IARU Region 1", "R1", null);

        _currentRegion = region;
        _currentPlan = ResolvePlan(region.Id);
        _log.LogInformation("band.plan.init regionId={R} segments={N}", region.Id, _currentPlan.Count);
    }

    public BandRegion CurrentRegion { get { lock (_lock) return _currentRegion; } }
    public IReadOnlyList<BandSegment> CurrentPlan { get { lock (_lock) return _currentPlan; } }
    public bool TxGuardIgnore => _prefs.GetTxGuardIgnore();

    public void SetRegion(string regionId)
    {
        var region = _store.Regions.FirstOrDefault(r => string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));
        if (region is null)
        {
            _log.LogWarning("band.set.region.unknown regionId={R}", regionId);
            return;
        }

        var plan = ResolvePlan(regionId);
        lock (_lock)
        {
            _currentRegion = region;
            _currentPlan = plan;
        }
        _prefs.SetRegionId(regionId);
        _log.LogInformation("band.region.changed regionId={R} segments={N}", regionId, plan.Count);
        PlanChanged?.Invoke();
    }

    public void SavePlan(string regionId, IReadOnlyList<BandSegment> segments)
    {
        ValidateSegments(segments);
        _store.SaveOverride(regionId, segments);

        // Refresh the current plan if this region or any ancestor is active
        var currentId = CurrentRegion.Id;
        if (IsAncestorOrSelf(regionId, currentId))
        {
            var plan = ResolvePlan(currentId);
            lock (_lock) { _currentPlan = plan; }
            _log.LogInformation("band.plan.updated regionId={R} segments={N}", regionId, plan.Count);
            PlanChanged?.Invoke();
        }
    }

    public void ResetPlan(string regionId)
    {
        _store.DeleteOverride(regionId);
        var currentId = CurrentRegion.Id;
        if (IsAncestorOrSelf(regionId, currentId))
        {
            var plan = ResolvePlan(currentId);
            lock (_lock) { _currentPlan = plan; }
            _log.LogInformation("band.plan.reset regionId={R}", regionId);
            PlanChanged?.Invoke();
        }
    }

    public void SetTxGuardIgnore(bool ignore)
    {
        _prefs.SetTxGuardIgnore(ignore);
        _log.LogInformation("band.guard.ignore={Ignore}", ignore);
    }

    /// <summary>
    /// Returns the effective plan for the given region, resolving the parent chain.
    /// The result is sorted by LowHz and has no overlaps (child segments supersede parent).
    /// </summary>
    public IReadOnlyList<BandSegment> ResolvePlan(string regionId)
    {
        // Walk the ancestry chain, oldest-first
        var chain = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = regionId;
        while (current is not null && visited.Add(current))
        {
            chain.Add(current);
            var reg = _store.Regions.FirstOrDefault(r => string.Equals(r.Id, current, StringComparison.OrdinalIgnoreCase));
            current = reg?.ParentId ?? null!;
        }
        chain.Reverse(); // oldest first

        // Merge: each layer's segments override overlapping segments from earlier layers
        var acc = new List<BandSegment>();
        foreach (var rid in chain)
        {
            var segs = _store.GetSegmentsForRegion(rid);
            foreach (var seg in segs)
            {
                // Remove any existing segments that overlap this one
                acc.RemoveAll(e => e.LowHz < seg.HighHz && e.HighHz > seg.LowHz);
                acc.Add(seg);
            }
        }

        acc.Sort((a, b) => a.LowHz.CompareTo(b.LowHz));
        return acc;
    }

    public BandSegment? GetSegment(long freqHz)
    {
        var plan = CurrentPlan;
        return BinarySearchSegment(plan, freqHz);
    }

    public bool InBand(long freqHz, RxMode mode)
    {
        var seg = GetSegment(freqHz);
        if (seg is null) return false;
        if (seg.Allocation != BandAllocation.Amateur) return false;
        return ModeMatchesRestriction(mode, seg.ModeRestriction);
    }

    private static BandSegment? BinarySearchSegment(IReadOnlyList<BandSegment> plan, long freqHz)
    {
        int lo = 0, hi = plan.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            var s = plan[mid];
            if (freqHz < s.LowHz) hi = mid - 1;
            else if (freqHz > s.HighHz) lo = mid + 1;
            else return s;
        }
        return null;
    }

    internal static bool ModeMatchesRestriction(RxMode mode, ModeRestriction restriction) => restriction switch
    {
        ModeRestriction.Any => true,
        ModeRestriction.CwOnly => mode is RxMode.CWU or RxMode.CWL,
        ModeRestriction.PhoneOnly => mode is RxMode.USB or RxMode.LSB or RxMode.AM or RxMode.SAM or RxMode.DSB or RxMode.FM,
        ModeRestriction.DigitalOnly => mode is RxMode.DIGL or RxMode.DIGU,
        ModeRestriction.CwAndDigital => mode is RxMode.CWU or RxMode.CWL or RxMode.DIGL or RxMode.DIGU,
        _ => false,
    };

    private bool IsAncestorOrSelf(string targetId, string currentId)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var id = currentId;
        while (id is not null && visited.Add(id))
        {
            if (string.Equals(id, targetId, StringComparison.OrdinalIgnoreCase)) return true;
            var reg = _store.Regions.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            id = reg?.ParentId ?? null!;
        }
        return false;
    }

    private static void ValidateSegments(IReadOnlyList<BandSegment> segments)
    {
        for (int i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (s.LowHz < 0 || s.HighHz < 0)
                throw new ArgumentException($"Segment [{i}] has negative frequency");
            if (s.LowHz >= s.HighHz)
                throw new ArgumentException($"Segment [{i}] LowHz >= HighHz");
            if (string.IsNullOrWhiteSpace(s.Label))
                throw new ArgumentException($"Segment [{i}] has empty label");
            if (i > 0 && segments[i].LowHz < segments[i - 1].HighHz)
                throw new ArgumentException($"Segments [{i - 1}] and [{i}] overlap — sort and remove overlaps before saving");
        }
    }
}
