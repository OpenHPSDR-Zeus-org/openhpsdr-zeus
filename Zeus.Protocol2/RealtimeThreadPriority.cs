// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Zeus.Protocol2;

/// <summary>
/// Promotes the calling thread to the platform's pro-audio / low-latency
/// thread class. Used at the top of the protocol RxLoop so PureSignal
/// feedback samples and RX IQ frames are pumped at a priority that doesn't
/// have to fight Photino render / SignalR fan-out / general .NET ThreadPool
/// work for cycles. Mirrors the discipline Thetis gets by running the same
/// path off the ASIO driver's real-time-class thread — see
/// <c>docs/rca/2026-05-28-ps-load-sensitivity.md</c>.
/// </summary>
/// <remarks>
/// All P/Invoke failures are non-fatal: the thread keeps running at
/// whatever priority the OS will grant. We log at warning level so an
/// operator chasing a perf issue can confirm the promotion landed (or didn't).
///
/// Per-platform mapping:
/// <list type="bullet">
/// <item><b>macOS</b> — <c>pthread_set_qos_class_self_np(QOS_CLASS_USER_INTERACTIVE, 0)</c>.
/// The high QoS tier for user-driven low-latency work. Doesn't need
/// entitlements or the mach real-time ceremony.</item>
/// <item><b>Windows</b> — <c>AvSetMmThreadCharacteristicsW("Pro Audio")</c>.
/// MMCSS Pro Audio task class — the same one DAWs and Thetis (via ASIO)
/// request. The returned handle is intentionally not reverted; the RX
/// thread runs for the lifetime of the protocol session.</item>
/// <item><b>Linux / other</b> — <c>Thread.CurrentThread.Priority = ThreadPriority.Highest</c>.
/// <c>SCHED_FIFO</c> needs <c>CAP_SYS_NICE</c> which an operator process
/// typically lacks; the .NET fallback maps to an elevated nice value, which
/// is the best we can do without privileged setup.</item>
/// </list>
///
/// This file is intentionally duplicated in <c>Zeus.Protocol1</c>; the two
/// protocol projects don't share a utility assembly. Keep the two copies in
/// sync — if you fix one, fix the other.
/// </remarks>
// Public so Zeus.Server.Hosting (TxTuneDriver pump) can promote its dedicated
// thread too — same RT need as the protocol RX/TX threads (#559).
public static partial class RealtimeThreadPriority
{
    /// <summary>
    /// Promote the calling thread to the platform's pro-audio class. Safe
    /// to call once per long-running thread (e.g. at the top of an RxLoop).
    /// </summary>
    public static void PromoteCallingThreadToProAudio(ILogger log)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                PromoteMacOs(log);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                PromoteWindows(log);
            else
                PromoteFallback(log);
        }
        catch (DllNotFoundException ex)
        {
            log.LogWarning(ex, "thread.priority native library not found — leaving thread at default priority");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "thread.priority promotion threw — leaving thread at default priority");
        }
    }

    // ---- macOS -------------------------------------------------------------
    private const int QOS_CLASS_USER_INTERACTIVE = 0x21;
    private const int QOS_CLASS_USER_INITIATED = 0x19;
    private const int THREAD_TIME_CONSTRAINT_POLICY = 2;
    private const uint THREAD_TIME_CONSTRAINT_POLICY_COUNT = 4;

    [LibraryImport("libSystem.dylib", EntryPoint = "pthread_set_qos_class_self_np")]
    private static partial int pthread_set_qos_class_self_np(int qos_class, int relative_priority);

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadTimeConstraintPolicy
    {
        public uint period;       // mach abs ticks: nominal cycle
        public uint computation;  // mach abs ticks: CPU needed per cycle
        public uint constraint;   // mach abs ticks: deadline after period start
        public int preemptible;   // 1 = soft RT (preemptible after computation)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MachTimebaseInfo { public uint numer; public uint denom; }

    [LibraryImport("libSystem.dylib", EntryPoint = "mach_thread_self")]
    private static partial uint mach_thread_self();

    [LibraryImport("libSystem.dylib", EntryPoint = "mach_timebase_info")]
    private static partial int mach_timebase_info(ref MachTimebaseInfo info);

    [LibraryImport("libSystem.dylib", EntryPoint = "thread_policy_set")]
    private static partial int thread_policy_set(
        uint thread, int flavor, ref ThreadTimeConstraintPolicy policy, uint count);

    private static void PromoteMacOs(ILogger log)
    {
        // 1) Real-time scheduling (THREAD_TIME_CONSTRAINT_POLICY) — CoreAudio's
        //    mechanism. This genuinely preempts the Photino WebView's render
        //    threads, which the QoS path could not: macOS denies the
        //    USER_INTERACTIVE QoS tier to non-UI threads (we saw rc=1/EPERM),
        //    so the previous promotion was a silent no-op and desktop two-tone
        //    stayed dirty. RT is granted to ordinary app threads.
        try
        {
            var tb = new MachTimebaseInfo();
            mach_timebase_info(ref tb);
            if (tb.numer != 0 && tb.denom != 0)
            {
                // ns → mach abs ticks = ns * denom / numer.
                double nsToAbs = (double)tb.denom / tb.numer;
                uint Abs(double ms) => (uint)(ms * 1_000_000.0 * nsToAbs);
                // Conservative budget: ~1 ms compute per 5 ms cycle, 3 ms
                // deadline, preemptible. Ample for the lightweight TX
                // sender / RX pump while leaving the system schedulable.
                var policy = new ThreadTimeConstraintPolicy
                {
                    period = Abs(5.0),
                    computation = Abs(1.0),
                    constraint = Abs(3.0),
                    preemptible = 1,
                };
                int rcRt = thread_policy_set(
                    mach_thread_self(), THREAD_TIME_CONSTRAINT_POLICY,
                    ref policy, THREAD_TIME_CONSTRAINT_POLICY_COUNT);
                if (rcRt == 0)
                {
                    log.LogInformation("thread.priority promoted to mach RT (TIME_CONSTRAINT 1ms/5ms)");
                    return;
                }
                log.LogWarning("thread.priority thread_policy_set rc={Rc} — trying QoS", rcRt);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "thread.priority mach RT threw — trying QoS");
        }

        // 2) QoS fallback — USER_INTERACTIVE (often denied) then USER_INITIATED.
        int rc = pthread_set_qos_class_self_np(QOS_CLASS_USER_INTERACTIVE, 0);
        if (rc != 0) rc = pthread_set_qos_class_self_np(QOS_CLASS_USER_INITIATED, 0);
        if (rc == 0)
            log.LogInformation("thread.priority promoted via QoS (macOS)");
        else
            log.LogWarning("thread.priority QoS rc={Rc} — using ThreadPriority.Highest floor", rc);

        // 3) Always set a .NET priority floor — harmless, no-op-safe.
        TrySetThreadPriorityHighest(log);
    }

    // ---- Windows -----------------------------------------------------------
    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW",
                   StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr AvSetMmThreadCharacteristicsW(string TaskName, out uint TaskIndex);

    private static void PromoteWindows(ILogger log)
    {
        uint taskIdx = 0;
        IntPtr handle;
        try
        {
            handle = AvSetMmThreadCharacteristicsW("Pro Audio", out taskIdx);
        }
        catch (DllNotFoundException)
        {
            log.LogWarning("thread.priority avrt.dll not available — falling back to ThreadPriority.Highest");
            TrySetThreadPriorityHighest(log);
            return;
        }

        if (handle != IntPtr.Zero)
        {
            log.LogInformation("thread.priority promoted to MMCSS Pro Audio (Windows, taskIdx={Idx})", taskIdx);
        }
        else
        {
            int err = Marshal.GetLastPInvokeError();
            log.LogWarning("thread.priority AvSetMmThreadCharacteristicsW err={Err} — falling back to ThreadPriority.Highest", err);
            TrySetThreadPriorityHighest(log);
        }
    }

    // ---- Linux / other -----------------------------------------------------
    private static void PromoteFallback(ILogger log)
    {
        if (TrySetThreadPriorityHighest(log))
            log.LogInformation("thread.priority set to ThreadPriority.Highest (Linux/other)");
    }

    private static bool TrySetThreadPriorityHighest(ILogger log)
    {
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "thread.priority Thread.Priority.Highest threw");
            return false;
        }
    }
}
