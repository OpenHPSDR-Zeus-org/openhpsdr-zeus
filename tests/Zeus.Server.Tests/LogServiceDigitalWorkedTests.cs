// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Pins LogService.GetDigitalWorkedCallsignsAsync: the FT8 worked-before
// highlight must count ONLY prior FT8/FT4 QSOs (KB2UKA standing order), never
// SSB/CW/AM/etc. Runs against a throw-away LiteDB file through the normal shared
// lease (no `new LiteDatabase`, no sockets).

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class LogServiceDigitalWorkedTests : IDisposable
{
    private readonly string _logDbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-log-digworked-{Guid.NewGuid():N}.db");

    private LogService NewService() => new(NullLogger<LogService>.Instance, _logDbPath);

    private static CreateLogEntryRequest Qso(string call, string mode) => new(
        Callsign: call,
        Name: null,
        FrequencyMhz: 14.074,
        Band: "20m",
        Mode: mode,
        RstSent: "-10",
        RstRcvd: "-12",
        Grid: null,
        Country: null,
        Dxcc: null,
        CqZone: null,
        ItuZone: null,
        State: null,
        Comment: null,
        QsoDateTimeUtc: new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task GetDigitalWorked_IncludesFt8AndFt4_ExcludesPhoneAndCw()
    {
        using var svc = NewService();
        await svc.CreateLogEntryAsync(Qso("K1FT8", "FT8"));
        await svc.CreateLogEntryAsync(Qso("K4FT4", "FT4"));
        await svc.CreateLogEntryAsync(Qso("W2SSB", "SSB"));
        await svc.CreateLogEntryAsync(Qso("N3CW", "CW"));
        await svc.CreateLogEntryAsync(Qso("K5AM", "AM"));

        var worked = await svc.GetDigitalWorkedCallsignsAsync();

        Assert.Contains("K1FT8", worked);
        Assert.Contains("K4FT4", worked);
        Assert.DoesNotContain("W2SSB", worked);
        Assert.DoesNotContain("N3CW", worked);
        Assert.DoesNotContain("K5AM", worked);
    }

    [Fact]
    public async Task GetDigitalWorked_IncludesCallWorkedOnDigital_EvenIfAlsoWorkedOnPhone()
    {
        using var svc = NewService();
        // Same operator, two QSOs: one SSB (must not, alone, count) and one FT8.
        await svc.CreateLogEntryAsync(Qso("DL1ABC", "SSB"));
        await svc.CreateLogEntryAsync(Qso("DL1ABC", "FT8"));

        var worked = await svc.GetDigitalWorkedCallsignsAsync();

        Assert.Contains("DL1ABC", worked);
    }

    [Fact]
    public async Task GetDigitalWorked_PhoneOrCwOnlyCall_IsNotWorkedBefore()
    {
        using var svc = NewService();
        await svc.CreateLogEntryAsync(Qso("VK2PHONE", "SSB"));
        await svc.CreateLogEntryAsync(Qso("VK2PHONE", "CW"));

        var worked = await svc.GetDigitalWorkedCallsignsAsync();

        Assert.DoesNotContain("VK2PHONE", worked);
    }

    [Fact]
    public async Task GetDigitalWorked_IsCaseInsensitiveForLookups()
    {
        using var svc = NewService();
        await svc.CreateLogEntryAsync(Qso("rk9ax", "FT8")); // stored upper-cased

        var worked = await svc.GetDigitalWorkedCallsignsAsync();

        // The set normalises to upper and compares case-insensitively, so a raw
        // decoded sender (any case) probes cleanly.
        Assert.Contains("RK9AX", worked);
        Assert.Contains("rk9ax", worked);
    }

    [Fact]
    public async Task GetDigitalWorked_EmptyLogbook_ReturnsEmptySet()
    {
        using var svc = NewService();
        var worked = await svc.GetDigitalWorkedCallsignsAsync();
        Assert.Empty(worked);
    }

    public void Dispose()
    {
        try { File.Delete(_logDbPath); } catch { /* best-effort temp cleanup */ }
    }
}
