// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class LogServiceDeleteTests
{
    private static async Task<LogEntry> Add(LogService svc, string callsign) =>
        await svc.CreateLogEntryAsync(new CreateLogEntryRequest(
            Callsign: callsign, Name: null, FrequencyMhz: 14.074, Band: "20M",
            Mode: "FT8", RstSent: "-12", RstRcvd: "-09"));

    [Fact]
    public async Task DeleteLogEntriesAsync_RemovesOnlySelected_AndReturnsCount()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"zeus-delete-test-{Guid.NewGuid():N}.db");
        try
        {
            using var svc = new LogService(NullLogger<LogService>.Instance, dbPath);
            var a = await Add(svc, "K2ABC");
            var b = await Add(svc, "N9WAR");
            var c = await Add(svc, "EI6LF");

            var deleted = await svc.DeleteLogEntriesAsync(new[] { a.Id, c.Id });

            Assert.Equal(2, deleted);
            var remaining = await svc.GetLogEntriesAsync();
            Assert.Equal(1, remaining.TotalCount);
            Assert.Equal(b.Id, Assert.Single(remaining.Entries).Id);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task DeleteLogEntriesAsync_SkipsUnknownIds_CountsOnlyReal()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"zeus-delete-test-{Guid.NewGuid():N}.db");
        try
        {
            using var svc = new LogService(NullLogger<LogService>.Instance, dbPath);
            var a = await Add(svc, "K2ABC");

            // One real id plus a stale/never-existed id: delete what we can and
            // report the true count, don't throw.
            var deleted = await svc.DeleteLogEntriesAsync(new[] { a.Id, "does-not-exist" });

            Assert.Equal(1, deleted);
            Assert.Equal(0, (await svc.GetLogEntriesAsync()).TotalCount);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task DeleteLogEntriesAsync_EmptyOrNullIds_IsNoOp()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"zeus-delete-test-{Guid.NewGuid():N}.db");
        try
        {
            using var svc = new LogService(NullLogger<LogService>.Instance, dbPath);
            await Add(svc, "K2ABC");

            Assert.Equal(0, await svc.DeleteLogEntriesAsync(Array.Empty<string>()));
            Assert.Equal(0, await svc.DeleteLogEntriesAsync(new[] { "  ", "" }));
            Assert.Equal(1, (await svc.GetLogEntriesAsync()).TotalCount);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* best effort */ }
        }
    }
}
