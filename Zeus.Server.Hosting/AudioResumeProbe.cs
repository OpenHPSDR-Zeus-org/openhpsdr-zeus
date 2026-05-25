// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Brian Keating (EI6LF) and contributors.
//
// One-shot TX→RX audio-resume timing probe. Armed on the un-key (MOX/TUN
// falling) edge, it records monotonic timestamps for five milestones along
// the RX-resume path and logs a single "rx.resume.probe" line once audible
// output is reached. This is the diagnostic that localized the ~1.7 s
// resume-delay symptom in #468 (#403 lineage): it tells you which segment
// of the path eats the latency — radio packet arrival (t0→t1), WDSP audio
// production (t1→t2), publish to the sink (t2→t3), or the OS playback buffer
// draining to the speaker (t3→t4). A deep WASAPI shared-mode buffer shows up
// as a large t3→t4 gap.

using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Zeus.Server;

/// <summary>
/// Process-wide one-shot probe for TX→RX audio-resume latency. Writers live
/// on two threads — the DSP thread fills t0–t3, the miniaudio playback worker
/// fills t4 — so every stage uses an Interlocked compare-exchange to capture
/// only the FIRST occurrence after an arm. State is a tiny static surface
/// because the producers (DspPipelineService, NativeAudioSink) are wired in
/// different parts of the host and don't share an instance; making the probe
/// an injected singleton would add DI churn for a diagnostic that's off on
/// the happy path. The cost when disarmed is one relaxed bool read per stage.
/// </summary>
internal static class AudioResumeProbe
{
    // Armed on un-key; cleared after the line is logged (or on a fresh arm).
    // Volatile so the playback worker observes the arm/disarm flips promptly.
    private static volatile bool _armed;

    // Monotonic Stopwatch ticks for each milestone. 0 = not yet captured.
    // Each is written exactly once per arm via Interlocked.CompareExchange.
    private static long _t0Unkey;
    private static long _t1FirstIq;
    private static long _t2FirstReadAudio;
    private static long _t3FirstPublish;
    private static long _t4FirstAudible;

    // Logger captured at arm time. The DSP thread owns the arm, so this is
    // set before any stage races to write — a plain field read is fine.
    private static ILogger? _log;

    /// <summary>Arm the probe on the un-key edge (t0). Resets all later
    /// milestones so a fresh resume is measured cleanly. Idempotent within a
    /// single resume — re-arming before t4 just resets the window, which is
    /// the right behaviour if the operator double-taps the key.</summary>
    public static void ArmUnkey(ILogger log)
    {
        _log = log;
        Volatile.Write(ref _t1FirstIq, 0);
        Volatile.Write(ref _t2FirstReadAudio, 0);
        Volatile.Write(ref _t3FirstPublish, 0);
        Volatile.Write(ref _t4FirstAudible, 0);
        Volatile.Write(ref _t0Unkey, Stopwatch.GetTimestamp());
        _armed = true;
    }

    /// <summary>t1 — first RX IQ frame fed to the engine after un-key.</summary>
    public static void MarkFirstIq()
    {
        if (!_armed) return;
        Interlocked.CompareExchange(ref _t1FirstIq, Stopwatch.GetTimestamp(), 0);
    }

    /// <summary>t2 — first WDSP audio block (ReadAudio &gt; 0) after un-key.</summary>
    public static void MarkFirstReadAudio()
    {
        if (!_armed) return;
        Interlocked.CompareExchange(ref _t2FirstReadAudio, Stopwatch.GetTimestamp(), 0);
    }

    /// <summary>t3 — first AudioFrame published to the sinks after un-key.</summary>
    public static void MarkFirstPublish()
    {
        if (!_armed) return;
        Interlocked.CompareExchange(ref _t3FirstPublish, Stopwatch.GetTimestamp(), 0);
    }

    /// <summary>t4 — first non-silent samples written to the OS playback
    /// device after un-key. Logs the probe line and disarms. Called on the
    /// miniaudio playback worker thread; keeps work to the single log call
    /// only when this thread wins the t4 CAS.</summary>
    public static void MarkFirstAudibleOutput()
    {
        if (!_armed) return;
        if (Interlocked.CompareExchange(ref _t4FirstAudible, Stopwatch.GetTimestamp(), 0) != 0)
            return; // already captured this resume

        _armed = false;
        LogProbe();
    }

    private static void LogProbe()
    {
        var log = _log;
        if (log is null) return;

        long t0 = Volatile.Read(ref _t0Unkey);
        long t1 = Volatile.Read(ref _t1FirstIq);
        long t2 = Volatile.Read(ref _t2FirstReadAudio);
        long t3 = Volatile.Read(ref _t3FirstPublish);
        long t4 = Volatile.Read(ref _t4FirstAudible);

        // Milestones relative to t0, in milliseconds. A stage that never
        // fired reads -1 so the line shows which segment stalled.
        log.LogInformation(
            "rx.resume.probe t0=0.0ms t1={T1}ms t2={T2}ms t3={T3}ms t4={T4}ms " +
            "(t3->t4 buffer-drain={Drain}ms)",
            MsSince(t0, t1), MsSince(t0, t2), MsSince(t0, t3), MsSince(t0, t4),
            MsSince(t3, t4));
    }

    private static string MsSince(long from, long to)
    {
        if (from == 0 || to == 0) return "n/a";
        double ms = (to - from) * 1000.0 / Stopwatch.Frequency;
        return ms.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Test-only: force the probe back to its disarmed initial state.
    /// The probe is process-wide static, so a test that asserts the disarmed
    /// no-op path must not inherit an armed state from an earlier test.</summary>
    internal static void ResetForTest()
    {
        _armed = false;
        _log = null;
        Volatile.Write(ref _t0Unkey, 0);
        Volatile.Write(ref _t1FirstIq, 0);
        Volatile.Write(ref _t2FirstReadAudio, 0);
        Volatile.Write(ref _t3FirstPublish, 0);
        Volatile.Write(ref _t4FirstAudible, 0);
    }
}
