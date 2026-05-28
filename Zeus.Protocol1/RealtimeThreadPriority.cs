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

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Zeus.Protocol1;

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
/// </remarks>
internal static partial class RealtimeThreadPriority
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
            // Library missing — e.g. WSL stub w/o avrt.dll, or a stripped
            // libSystem build. Fall back without crashing.
            log.LogWarning(ex, "thread.priority native library not found — leaving thread at default priority");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "thread.priority promotion threw — leaving thread at default priority");
        }
    }

    // ---- macOS -------------------------------------------------------------
    // QOS_CLASS_USER_INTERACTIVE = 0x21 per <pthread/qos.h>.
    private const int QOS_CLASS_USER_INTERACTIVE = 0x21;

    [LibraryImport("libSystem.dylib", EntryPoint = "pthread_set_qos_class_self_np")]
    private static partial int pthread_set_qos_class_self_np(int qos_class, int relative_priority);

    private static void PromoteMacOs(ILogger log)
    {
        int rc = pthread_set_qos_class_self_np(QOS_CLASS_USER_INTERACTIVE, 0);
        if (rc == 0)
            log.LogInformation("thread.priority promoted to QOS_CLASS_USER_INTERACTIVE (macOS)");
        else
            log.LogWarning("thread.priority pthread_set_qos_class_self_np rc={Rc} — staying at default", rc);
    }

    // ---- Windows -----------------------------------------------------------
    // AvSetMmThreadCharacteristicsW — avrt.dll. Returns NULL on failure;
    // GetLastWin32Error explains. The returned handle should normally be
    // passed to AvRevertMmThreadCharacteristics on cleanup, but the RX
    // thread runs for the entire protocol session and exits with the
    // process, so we intentionally leak the handle — the kernel cleans up
    // task scheduler state at process exit.
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
            // Some stripped Windows containers don't ship avrt.dll. Fall
            // back without spamming the warning twice.
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
