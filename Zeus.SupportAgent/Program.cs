// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Runtime.InteropServices;
using Zeus.Support.Contracts;
using Zeus.SupportAgent;

// The Zeus support sidecar. Phase 1 scope is LOCAL ONLY: supervise the backend
// PID, and on an unexpected death capture the tail of the on-disk logs into a
// crash record. No broker / remote exposure yet — that arrives in later phases,
// at which point this process (which survives the backend) will own the broker
// connection.
//
// Lifecycle: wait for the supervised backend to exit, classify it via the
// clean-exit marker the backend writes at a deliberate shutdown, capture a crash
// record if it died unexpectedly, then exit. The sidecar must NEVER add its own
// crash to the operator's machine, so every step is best-effort.

if (!SidecarOptions.TryParse(args, out var opts, out var error) || opts is null)
{
    Console.Error.WriteLine($"zeus-support-agent: {error}");
    return 2;
}

Breadcrumb(opts, $"start: supervising pid={opts.SupervisePid} session={opts.SessionToken ?? "-"}");

using var cts = new CancellationTokenSource();
using var sigint = SafeSignal(PosixSignal.SIGINT, cts);
using var sigterm = SafeSignal(PosixSignal.SIGTERM, cts);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var result = await ProcessSupervisor.WaitForExitAsync(opts.SupervisePid, cts.Token).ConfigureAwait(false);

if (result.Cancelled)
{
    Breadcrumb(opts, "stop: cancelled while supervising; backend still alive");
    return 0;
}

// Classify the exit. A clean-exit marker (written by the backend at window close,
// operator quit, or profile-switch relaunch) means an expected shutdown.
var markerPath = Path.Combine(opts.CrashDir, SupportPaths.CleanExitMarkerName(opts.SupervisePid));
if (File.Exists(markerPath))
{
    TryDelete(markerPath);
    Breadcrumb(opts, $"clean exit: pid={opts.SupervisePid} exitCode={Fmt(result.ExitCode)}");
    return 0;
}

// No marker ⇒ the backend died unexpectedly. Capture a crash record.
long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var note = result.ProcessFound ? null : "PID was already gone when the sidecar attached.";
var written = CrashCapture.WriteCrashRecord(opts, result.ExitCode, nowUnixMs, note);
Breadcrumb(opts,
    written is null
        ? $"crash detected: pid={opts.SupervisePid} exitCode={Fmt(result.ExitCode)} (record write FAILED)"
        : $"crash detected: pid={opts.SupervisePid} exitCode={Fmt(result.ExitCode)} -> {Path.GetFileName(written)}");
return 0;

static string Fmt(int? code) => code?.ToString() ?? "<unknown>";

// Register a POSIX signal handler without throwing on a platform/runtime that
// doesn't support it (the sidecar still works; it just relies on Ctrl+C / exit).
static IDisposable? SafeSignal(PosixSignal signal, CancellationTokenSource cts)
{
    try
    {
        return PosixSignalRegistration.Create(signal, ctx => { ctx.Cancel = true; cts.Cancel(); });
    }
    catch
    {
        return null;
    }
}

static void TryDelete(string path)
{
    try { File.Delete(path); } catch { /* best effort */ }
}

// A one-line breadcrumb log for diagnosing the sidecar itself (it has no console
// when spawned detached). Strictly best-effort and tiny.
static void Breadcrumb(SidecarOptions opts, string message)
{
    try
    {
        Directory.CreateDirectory(opts.CrashDir);
        var line = $"{DateTimeOffset.UtcNow:o} {message}{Environment.NewLine}";
        File.AppendAllText(Path.Combine(opts.CrashDir, "support-agent.log"), line);
    }
    catch
    {
        // Never let breadcrumb logging affect the sidecar's behaviour.
    }
}
