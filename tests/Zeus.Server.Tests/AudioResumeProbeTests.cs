// SPDX-License-Identifier: GPL-2.0-or-later
//
// Tests for AudioResumeProbe — the one-shot TX→RX audio-resume timing probe
// re-added under issue #468. The probe is armed on un-key (t0), then captures
// the FIRST occurrence of each later milestone (t1 firstIq, t2 firstReadAudio,
// t3 firstPublish, t4 firstAudibleOutput) and logs a single rx.resume.probe
// line when audible output is reached. These tests assert the one-shot CAS
// semantics and the disarmed-no-op behaviour without touching real audio.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Xunit;
using Zeus.Server;

namespace Zeus.Server.Tests;

// The probe is process-wide static. Collection-serialise this class so two
// tests in different classes can't race the shared state, and reset in the
// ctor so each test starts from a known disarmed baseline.
[Collection("AudioResumeProbe")]
public class AudioResumeProbeTests
{
    public AudioResumeProbeTests() => AudioResumeProbe.ResetForTest();

    // Minimal logger that records formatted messages so a test can assert the
    // probe emitted exactly one rx.resume.probe line.
    private sealed class CapturingLogger : ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Enqueue(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    [Fact]
    public void FullResumeSequence_LogsExactlyOneProbeLine()
    {
        var log = new CapturingLogger();

        AudioResumeProbe.ArmUnkey(log);
        AudioResumeProbe.MarkFirstIq();
        AudioResumeProbe.MarkFirstReadAudio();
        AudioResumeProbe.MarkFirstPublish();
        AudioResumeProbe.MarkFirstAudibleOutput();

        var lines = log.Messages.ToArray();
        Assert.Single(lines);
        Assert.StartsWith("rx.resume.probe", lines[0]);
        // All four downstream stages fired, so none should read n/a.
        Assert.DoesNotContain("n/a", lines[0]);
        Assert.Contains("t4=", lines[0]);
    }

    [Fact]
    public void AudibleOutput_IsOneShot_PerArm()
    {
        var log = new CapturingLogger();

        AudioResumeProbe.ArmUnkey(log);
        AudioResumeProbe.MarkFirstIq();
        AudioResumeProbe.MarkFirstReadAudio();
        AudioResumeProbe.MarkFirstPublish();

        // The playback worker fires the callback continuously; only the first
        // one after an arm should log.
        AudioResumeProbe.MarkFirstAudibleOutput();
        AudioResumeProbe.MarkFirstAudibleOutput();
        AudioResumeProbe.MarkFirstAudibleOutput();

        Assert.Single(log.Messages);
    }

    [Fact]
    public void Disarmed_MarksAreNoOps()
    {
        var log = new CapturingLogger();

        // No ArmUnkey — every mark must be a cheap no-op and emit nothing.
        AudioResumeProbe.MarkFirstIq();
        AudioResumeProbe.MarkFirstReadAudio();
        AudioResumeProbe.MarkFirstPublish();
        AudioResumeProbe.MarkFirstAudibleOutput();

        Assert.Empty(log.Messages);
    }

    [Fact]
    public void MissingMiddleStage_ShowsNa_StillLogsOnAudible()
    {
        var log = new CapturingLogger();

        // Simulate a resume where t2 (firstReadAudio) never fired before
        // audible output was reached — the line should still log, with the
        // missing stage rendered as n/a so the operator sees which segment
        // stalled.
        AudioResumeProbe.ArmUnkey(log);
        AudioResumeProbe.MarkFirstIq();
        // (skip MarkFirstReadAudio)
        AudioResumeProbe.MarkFirstPublish();
        AudioResumeProbe.MarkFirstAudibleOutput();

        var lines = log.Messages.ToArray();
        Assert.Single(lines);
        Assert.Contains("t2=n/a", lines[0]);
    }

    [Fact]
    public void ReArmingBeforeAudible_ResetsLaterMilestones()
    {
        var log = new CapturingLogger();

        // Operator double-taps the key: arm, partial progress, then arm again
        // before audible output. The second arm clears t1–t4 so the measured
        // window reflects the second resume only. After the re-arm only t3 is
        // re-marked (the provenance gate requires a fresh publish before t4),
        // so t1/t2 read n/a but t3/t4 reflect the second resume.
        AudioResumeProbe.ArmUnkey(log);
        AudioResumeProbe.MarkFirstIq();
        AudioResumeProbe.MarkFirstReadAudio();

        AudioResumeProbe.ArmUnkey(log);   // re-arm
        AudioResumeProbe.MarkFirstPublish();          // fresh t3 for the 2nd resume
        AudioResumeProbe.MarkFirstAudibleOutput();

        var lines = log.Messages.ToArray();
        Assert.Single(lines);
        // After re-arm, t1/t2 were never re-marked, so they read n/a.
        Assert.Contains("t1=n/a", lines[0]);
        Assert.Contains("t2=n/a", lines[0]);
        // t3 fired after the re-arm, so it is NOT n/a.
        Assert.DoesNotContain("t3=n/a", lines[0]);
    }

    [Fact]
    public void AudibleOutput_BeforePublish_DoesNotLog()
    {
        var log = new CapturingLogger();

        // The provenance gate (issue #468): the keep-warm silence feed keeps
        // the output ring fed during TX, and the falling-edge drain can leave a
        // stale residual tail. If audible output is reported before t3 (fresh
        // publish) has fired, that output is NOT the post-un-key resume — t4
        // must not stamp and the line must not log yet.
        AudioResumeProbe.ArmUnkey(log);
        AudioResumeProbe.MarkFirstIq();
        AudioResumeProbe.MarkFirstReadAudio();
        // (no MarkFirstPublish — t3 has not fired)
        AudioResumeProbe.MarkFirstAudibleOutput();   // stale/residual output

        Assert.Empty(log.Messages);

        // Once fresh audio is actually published, the NEXT audible output is
        // the real resume and the line logs with a meaningful t4.
        AudioResumeProbe.MarkFirstPublish();
        AudioResumeProbe.MarkFirstAudibleOutput();

        var lines = log.Messages.ToArray();
        Assert.Single(lines);
        Assert.DoesNotContain("t3=n/a", lines[0]);
        Assert.Contains("t4=", lines[0]);
        Assert.DoesNotContain("t4=n/a", lines[0]);
    }
}
