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

namespace Zeus.SupportAgent;

/// <summary>Outcome of waiting on the supervised backend process.</summary>
/// <param name="ProcessFound">False if the PID was already gone when we attached.</param>
/// <param name="ExitCode">The process exit code when readable; null otherwise.</param>
/// <param name="Cancelled">True if the wait was cancelled (the sidecar was told to stop).</param>
public readonly record struct SupervisionResult(bool ProcessFound, int? ExitCode, bool Cancelled);

/// <summary>
/// Waits for the supervised Zeus backend to exit. Crash-vs-clean classification is
/// NOT decided here — it depends on the clean-exit marker the backend writes at a
/// deliberate shutdown — this just blocks until the process is gone (or the
/// sidecar is cancelled).
/// </summary>
public static class ProcessSupervisor
{
    public static async Task<SupervisionResult> WaitForExitAsync(int pid, CancellationToken ct)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            // No such process — it already exited before we attached. Caller still
            // checks the clean-exit marker to classify it.
            return new SupervisionResult(ProcessFound: false, ExitCode: null, Cancelled: false);
        }

        using (process)
        {
            try
            {
                process.EnableRaisingEvents = true;
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new SupervisionResult(ProcessFound: true, ExitCode: null, Cancelled: true);
            }

            int? exitCode = null;
            try
            {
                exitCode = process.ExitCode;
            }
            catch
            {
                // Exit code can be unreadable for a process we didn't start; the
                // crash record simply records a null exit code.
            }

            return new SupervisionResult(ProcessFound: true, ExitCode: exitCode, Cancelled: false);
        }
    }
}
