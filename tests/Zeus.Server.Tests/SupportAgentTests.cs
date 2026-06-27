// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Zeus.Support.Contracts;
using Zeus.SupportAgent;

namespace Zeus.Server.Tests;

public class SidecarOptionsTests
{
    [Fact]
    public void Parse_Valid_PopulatesAllFields()
    {
        var ok = SidecarOptions.TryParse(
            ["--supervise-pid", "1234", "--session", "abc", "--app-log", "a.log",
             "--startup-log", "s.log", "--crash-dir", "c", "--app-version", "1.0"],
            out var opts, out var err);

        Assert.True(ok);
        Assert.Null(err);
        Assert.NotNull(opts);
        Assert.Equal(1234, opts!.SupervisePid);
        Assert.Equal("abc", opts.SessionToken);
        Assert.Equal("a.log", opts.AppLogPath);
        Assert.Equal("s.log", opts.StartupLogPath);
        Assert.Equal("c", opts.CrashDir);
        Assert.Equal("1.0", opts.AppVersion);
    }

    [Fact]
    public void Parse_MissingPid_Fails()
    {
        Assert.False(SidecarOptions.TryParse(["--crash-dir", "c"], out var opts, out var err));
        Assert.Null(opts);
        Assert.Contains("supervise-pid", err);
    }

    [Fact]
    public void Parse_MissingCrashDir_Fails()
    {
        Assert.False(SidecarOptions.TryParse(["--supervise-pid", "5"], out _, out var err));
        Assert.Contains("crash-dir", err);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("notanint")]
    public void Parse_BadPid_Fails(string pid)
    {
        Assert.False(SidecarOptions.TryParse(["--supervise-pid", pid, "--crash-dir", "c"], out _, out var err));
        Assert.Contains("supervise-pid", err);
    }

    [Fact]
    public void Parse_UnknownArg_Fails()
    {
        Assert.False(SidecarOptions.TryParse(["--bogus", "x"], out _, out var err));
        Assert.Contains("bogus", err);
    }
}

public class SupportAgentLogTailTests : IDisposable
{
    private readonly string _dir;
    public SupportAgentLogTailTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"zeus-tail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void ReadLastLines_ReturnsTrailingLines()
    {
        var p = Path.Combine(_dir, "f.log");
        File.WriteAllLines(p, ["a", "b", "c", "d", "e"]);
        Assert.Equal(["c", "d", "e"], LogTail.ReadLastLines(p, 3));
    }

    [Fact]
    public void ReadLastLines_FewerThanRequested_ReturnsAll()
    {
        var p = Path.Combine(_dir, "f.log");
        File.WriteAllLines(p, ["a", "b"]);
        Assert.Equal(["a", "b"], LogTail.ReadLastLines(p, 10));
    }

    [Fact]
    public void ReadLastLines_MissingFile_IsEmpty()
        => Assert.Empty(LogTail.ReadLastLines(Path.Combine(_dir, "nope.log"), 5));

    [Fact]
    public void ReadAppLogTail_SpansPreviousRollWhenActiveShort()
    {
        var active = Path.Combine(_dir, "zeus-app.log");
        var roll = Path.Combine(_dir, "zeus-app.1.log");
        File.WriteAllLines(roll, ["A1", "A2", "A3", "A4", "A5"]);
        File.WriteAllLines(active, ["B1", "B2"]);

        // Want 4 lines: active has 2, so the previous roll fills the other 2.
        Assert.Equal(["A4", "A5", "B1", "B2"], LogTail.ReadAppLogTail(active, 4));
    }

    [Fact]
    public void ReadAppLogTail_ActiveAloneSatisfiesRequest()
    {
        var active = Path.Combine(_dir, "zeus-app.log");
        File.WriteAllLines(active, ["B1", "B2", "B3", "B4"]);
        Assert.Equal(["B3", "B4"], LogTail.ReadAppLogTail(active, 2));
    }
}

public class CrashCaptureTests : IDisposable
{
    private readonly string _dir;
    public CrashCaptureTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"zeus-crash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SidecarOptions Opts(string? appLog = null, string? startupLog = null) =>
        new(SupervisePid: 4321, SessionToken: "s", AppLogPath: appLog,
            StartupLogPath: startupLog, CrashDir: _dir, AppVersion: "9.9");

    [Fact]
    public void BuildRecord_CapturesLogTailsAndMetadata()
    {
        var appLog = Path.Combine(_dir, "zeus-app.log");
        File.WriteAllLines(appLog, ["one", "two", "three"]);

        var rec = CrashCapture.BuildRecord(Opts(appLog), exitCode: -1073741819, nowUnixMs: 1_700_000_000_000);

        Assert.Equal(SupportCrashRecord.CurrentSchemaVersion, rec.SchemaVersion);
        Assert.Equal(4321, rec.Pid);
        Assert.Equal(-1073741819, rec.ExitCode);
        Assert.True(rec.Crashed);
        Assert.Equal("9.9", rec.AppVersion);
        Assert.Contains("three", rec.AppLogTail);
        Assert.False(string.IsNullOrEmpty(rec.Platform));
    }

    [Fact]
    public void WriteCrashRecord_WritesDeserialisableJson()
    {
        var path = CrashCapture.WriteCrashRecord(Opts(), exitCode: 5, nowUnixMs: 1_700_000_000_000);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));

        var back = JsonSerializer.Deserialize(File.ReadAllText(path!), SupportIpcJsonContext.Default.SupportCrashRecord);
        Assert.NotNull(back);
        Assert.Equal(4321, back!.Pid);
        Assert.Equal(5, back.ExitCode);
        Assert.True(back.Crashed);
    }

    [Fact]
    public void WriteCrashRecord_PrunesToBoundedHistory()
    {
        // Seed more than the retention cap with time-sortable names.
        for (int i = 0; i < 30; i++)
            File.WriteAllText(Path.Combine(_dir, SupportPaths.CrashRecordFileName(1_000_000_000_000 + i, 1)), "{}");

        CrashCapture.WriteCrashRecord(Opts(), exitCode: 0, nowUnixMs: 1_700_000_000_000);

        var remaining = Directory.GetFiles(_dir, "crash-*.json").Length;
        Assert.True(remaining <= 25, $"expected <=25 retained crash records, found {remaining}");
    }
}

public class SupportPathsTests
{
    [Fact]
    public void CleanExitMarkerName_IsPerPid()
        => Assert.Equal("clean-exit-777.marker", SupportPaths.CleanExitMarkerName(777));

    [Fact]
    public void CrashRecordFileName_IsTimeSortable()
    {
        var early = SupportPaths.CrashRecordFileName(1_000_000_000_000, 1);
        var late = SupportPaths.CrashRecordFileName(1_700_000_000_000, 1);
        Assert.True(string.CompareOrdinal(early, late) < 0);
        Assert.StartsWith("crash-", early);
        Assert.EndsWith(".json", early);
    }
}

public class ProcessSupervisorTests
{
    // A short-lived child that sleeps briefly then exits with a known code, so the
    // supervisor reliably attaches before it dies. Cross-platform via the OS shell.
    private static Process StartDummy(int exitCode, bool linger)
    {
        ProcessStartInfo psi;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var cmd = linger
                ? $"ping -n 2 127.0.0.1 >nul & exit {exitCode}"
                : $"exit {exitCode}";
            psi = new ProcessStartInfo("cmd.exe", $"/c \"{cmd}\"");
        }
        else
        {
            var cmd = linger ? $"sleep 0.6; exit {exitCode}" : $"exit {exitCode}";
            psi = new ProcessStartInfo("/bin/sh", $"-c \"{cmd}\"");
        }
        psi.CreateNoWindow = true;
        psi.UseShellExecute = false;
        return Process.Start(psi)!;
    }

    [Fact]
    public async Task WaitForExit_ReturnsExitCodeOfSupervisedProcess()
    {
        using var child = StartDummy(exitCode: 7, linger: true);
        var result = await ProcessSupervisor.WaitForExitAsync(child.Id, CancellationToken.None);

        Assert.False(result.Cancelled);
        Assert.True(result.ProcessFound);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task WaitForExit_ProcessAlreadyGone_ReportsNotFound()
    {
        using var child = StartDummy(exitCode: 3, linger: false);
        child.WaitForExit(); // ensure it is fully gone before we attach
        var goneId = child.Id;

        var result = await ProcessSupervisor.WaitForExitAsync(goneId, CancellationToken.None);
        Assert.False(result.ProcessFound);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task WaitForExit_Cancellation_ReportsCancelled()
    {
        using var child = StartDummy(exitCode: 0, linger: true);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        var result = await ProcessSupervisor.WaitForExitAsync(child.Id, cts.Token);
        Assert.True(result.Cancelled);

        try { child.Kill(); } catch { }
    }
}
