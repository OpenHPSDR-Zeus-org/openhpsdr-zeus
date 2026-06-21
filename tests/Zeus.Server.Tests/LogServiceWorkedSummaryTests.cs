// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class LogServiceWorkedSummaryTests
{
    [Fact]
    public void BuildWorkedSummary_ReturnsLatestQsoAndRecentHistory()
    {
        var older = new DateTime(2026, 5, 1, 12, 10, 0, DateTimeKind.Utc);
        var middle = new DateTime(2026, 5, 4, 21, 35, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 5, 7, 18, 45, 0, DateTimeKind.Utc);

        var summary = LogService.BuildWorkedSummary("n9war", new[]
        {
            Entry("N9WAR", older, "20m", "SSB", 14.250, "59", "58", grid: "EN52", country: "United States"),
            Entry("K1ABC", middle, "15m", "CW", 21.040, "599", "599"),
            Entry("N9WAR", newer, "17m", "CW", 18.073, "599", "579", comment: "Quick exchange"),
            Entry("N9WAR", middle, "40m", "FT8", 7.074, "-10", "-12", state: "IL"),
        }, recentTake: 2);

        Assert.True(summary.WorkedBefore);
        Assert.Equal("N9WAR", summary.Callsign);
        Assert.Equal(3, summary.TotalCount);
        Assert.Equal(newer, summary.LastWorkedUtc);
        Assert.Equal("17m", summary.LastBand);
        Assert.Equal("CW", summary.LastMode);
        Assert.Equal(18.073, summary.LastFrequencyMhz);
        Assert.Equal("599", summary.LastRstSent);
        Assert.Equal("579", summary.LastRstRcvd);
        Assert.Equal("Quick exchange", summary.LastComment);
        Assert.Equal(new[] { "17m", "40m", "20m" }, summary.Bands);
        Assert.Equal(new[] { "CW", "FT8", "SSB" }, summary.Modes);
        Assert.Equal(2, summary.RecentQsos.Count);
        Assert.Equal(newer, summary.RecentQsos[0].QsoDateTimeUtc);
        Assert.Equal(middle, summary.RecentQsos[1].QsoDateTimeUtc);
    }

    [Fact]
    public void BuildWorkedSummary_ReturnsEmptySummaryWhenCallsignHasNoMatches()
    {
        var summary = LogService.BuildWorkedSummary("N0CALL", new[]
        {
            Entry("K1ABC", new DateTime(2026, 5, 4, 21, 35, 0, DateTimeKind.Utc), "15m", "CW", 21.040, "599", "599"),
        });

        Assert.False(summary.WorkedBefore);
        Assert.Equal("N0CALL", summary.Callsign);
        Assert.Equal(0, summary.TotalCount);
        Assert.Null(summary.LastWorkedUtc);
        Assert.Empty(summary.Bands);
        Assert.Empty(summary.Modes);
        Assert.Empty(summary.RecentQsos);
    }

    private static LogEntryDocument Entry(
        string callsign,
        DateTime qsoUtc,
        string band,
        string mode,
        double frequencyMhz,
        string rstSent,
        string rstRcvd,
        string? grid = null,
        string? country = null,
        string? state = null,
        string? comment = null) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            QsoDateTimeUtc = qsoUtc,
            Callsign = callsign,
            Band = band,
            Mode = mode,
            FrequencyMhz = frequencyMhz,
            RstSent = rstSent,
            RstRcvd = rstRcvd,
            Grid = grid,
            Country = country,
            State = state,
            Comment = comment,
            CreatedUtc = qsoUtc,
        };
}
