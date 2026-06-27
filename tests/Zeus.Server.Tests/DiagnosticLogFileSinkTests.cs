// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Server.Diagnostics;

namespace Zeus.Server.Tests;

// Rolling on-disk log sink that mirrors the in-memory diagnostic ring so the
// recent log survives a backend crash for the support sidecar to tail.
public class DiagnosticLogFileSinkTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public DiagnosticLogFileSinkTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"zeus-logsink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "zeus-app.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static string Rolled(string path, int n)
    {
        var ext = Path.GetExtension(path);
        var stem = path[..(path.Length - ext.Length)];
        return $"{stem}.{n}{ext}";
    }

    [Fact]
    public void Append_WritesLinesToDisk()
    {
        using (var sink = new DiagnosticLogFileSink(_path))
        {
            sink.Append("12:00:00.000 INFO RadioService hello");
            sink.Append("12:00:00.100 WARN RadioService world");
        }

        var lines = File.ReadAllLines(_path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("hello", lines[0]);
        Assert.Contains("world", lines[1]);
    }

    [Fact]
    public void Append_CreatesParentDirectoryOnDemand()
    {
        // The host points the sink at DataDir/logs which may not exist yet.
        var nested = Path.Combine(_dir, "logs", "zeus-app.log");
        using var sink = new DiagnosticLogFileSink(nested);
        sink.Append("line");

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Append_RollsAndBoundsHistory()
    {
        // Tiny cap so a handful of lines forces several rolls; keep 2 rolls.
        using (var sink = new DiagnosticLogFileSink(_path, maxBytes: 200, maxRolls: 2))
        {
            for (int i = 0; i < 200; i++)
                sink.Append($"{i:D4} {new string('x', 60)}");
        }

        Assert.True(File.Exists(_path));            // active
        Assert.True(File.Exists(Rolled(_path, 1))); // newest roll
        Assert.True(File.Exists(Rolled(_path, 2))); // oldest retained roll
        // History is bounded to maxRolls — nothing older is kept.
        Assert.False(File.Exists(Rolled(_path, 3)));
    }

    [Fact]
    public void Append_BadPath_DoesNotThrow()
    {
        // Point the sink at a path whose "directory" is actually an existing file,
        // so the directory create fails. A logging sink must degrade silently, not
        // throw into the logging pipeline.
        var asFile = Path.Combine(_dir, "not-a-dir");
        File.WriteAllText(asFile, "x");
        var doomed = Path.Combine(asFile, "sub", "zeus-app.log");

        using var sink = new DiagnosticLogFileSink(doomed);
        var ex = Record.Exception(() => sink.Append("should be swallowed"));
        Assert.Null(ex);
    }

    [Fact]
    public void Append_AfterDispose_IsNoOp()
    {
        var sink = new DiagnosticLogFileSink(_path);
        sink.Append("before");
        sink.Dispose();

        var ex = Record.Exception(() => sink.Append("after"));
        Assert.Null(ex);

        var lines = File.ReadAllLines(_path);
        Assert.Single(lines);
        Assert.Contains("before", lines[0]);
    }
}
