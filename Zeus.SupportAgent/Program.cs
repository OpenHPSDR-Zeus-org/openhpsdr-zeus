// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Net.Http;
using System.Runtime.InteropServices;
using Zeus.Support.Contracts;
using Zeus.SupportAgent;

// The Zeus support sidecar. It supervises the backend PID and, on an unexpected
// death, captures the tail of the on-disk logs into a crash record (the in-memory
// log ring dies with the in-process backend). Because it OUTLIVES the backend it
// also owns the broker connection for remote diagnostics:
//
//   * Presence (Phase 3c): while the operator's L1 "remote diagnostics available"
//     switch is on, it registers + heartbeats to the broker so a maintainer sees
//     them online. It learns the live switch state over IPC from the backend.
//   * Crash auto-share (Phase 3c): if the operator pre-authorised auto-share, a
//     captured crash record is uploaded to the broker AFTER the backend dies.
//
// Everything remote is strictly opt-in and best-effort: the sidecar must NEVER add
// its own crash to the operator's machine, so every step is guarded.

if (!SidecarOptions.TryParse(args, out var opts, out var error) || opts is null)
{
    Console.Error.WriteLine($"zeus-support-agent: {error}");
    return 2;
}

Breadcrumb(opts, $"start: supervising pid={opts.SupervisePid} session={opts.SessionToken ?? "-"} " +
                 $"remoteDiag={opts.RemoteDiagnosticsEnabled} autoShare={opts.AutoShareOnCrash}");

using var cts = new CancellationTokenSource();
using var sigint = SafeSignal(PosixSignal.SIGINT, cts);
using var sigterm = SafeSignal(PosixSignal.SIGTERM, cts);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// --- Remote presence wiring -------------------------------------------------
// The broker URL alone decides whether remote presence/crash is even possible;
// the OPERATOR IDENTITY arrives lazily over the IPC pipe (callsign + QRZ session
// key in a SupportHello/SupportStateChanged), NOT from a launch-time env var that
// races QRZ silent-login. So the broker client is created up front with whatever
// seed the launcher passed (often blank) and its identity is refreshed as the
// backend pushes it; the IPC listener and presence loop ALWAYS run when a broker
// URL is configured. Null endpoints → the Phase-1 local-only crash capturer.
var endpoints = BrokerEndpoints.FromBrokerUrl(opts.BrokerUrl);
var qrzSession = Environment.GetEnvironmentVariable(HttpSupportBrokerClient.QrzSessionEnvVar) ?? "";
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

HttpSupportBrokerClient? broker = null;
PresenceCoordinator? coordinator = null;
Task presenceTask = Task.CompletedTask;
Task ipcTask = Task.CompletedTask;

if (endpoints is not null)
{
    // Seed identity from the launch env (best-effort; usually blank because QRZ
    // login hadn't settled at launch). The IPC SupportHello/SupportStateChanged
    // refresh it the moment the operator's QRZ identity is known.
    broker = new HttpSupportBrokerClient(
        http, endpoints, opts.OperatorCallsign ?? "", qrzSession,
        platform: RuntimeInformation.OSDescription,
        appVersion: opts.AppVersion ?? "");

    // initiallyAvailable is false: presence only advertises once the backend
    // confirms BOTH the L1 switch on and a usable identity over IPC.
    var presence = new PresenceClient(
        broker,
        initiallyAvailable: false,
        log: msg => Breadcrumb(opts, msg));
    coordinator = new PresenceCoordinator(
        broker, presence,
        initialAutoShare: opts.AutoShareOnCrash,
        log: msg => Breadcrumb(opts, msg));
    presenceTask = presence.RunAsync(cts.Token);

    var listener = new SupportIpcListener(
        opts.SessionToken,
        onState: coordinator.Apply,
        log: msg => Breadcrumb(opts, msg));
    ipcTask = listener.RunAsync(cts.Token);
}

// --- Supervise the backend --------------------------------------------------
var result = await ProcessSupervisor.WaitForExitAsync(opts.SupervisePid, cts.Token).ConfigureAwait(false);

if (result.Cancelled)
{
    Breadcrumb(opts, "stop: cancelled while supervising; backend still alive");
    await ShutdownRemoteAsync(cts, presenceTask, ipcTask).ConfigureAwait(false);
    return 0;
}

// Classify the exit. A clean-exit marker (written by the backend at window close,
// operator quit, or profile-switch relaunch) means an expected shutdown.
var markerPath = Path.Combine(opts.CrashDir, SupportPaths.CleanExitMarkerName(opts.SupervisePid));
if (File.Exists(markerPath))
{
    TryDelete(markerPath);
    Breadcrumb(opts, $"clean exit: pid={opts.SupervisePid} exitCode={Fmt(result.ExitCode)}");
    await ShutdownRemoteAsync(cts, presenceTask, ipcTask).ConfigureAwait(false);
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

// Crash auto-share: strictly gated on the operator's opt-in (default OFF). With
// the flag off this never reads the record back or touches the broker. The
// authoritative posture is whatever the operator last pushed over IPC (held by
// the coordinator), falling back to the launch-time seed if no IPC ever arrived.
// Pass the broker only when it carries a usable identity so the outcome logging
// distinguishes "not opted in" from "no broker identity".
try
{
    var autoShareEnabled = coordinator?.AutoShareOnCrash ?? opts.AutoShareOnCrash;
    var shareBroker = broker is { IsConfigured: true } ? broker : null;
    string? recordJson = written is not null ? TryReadAllText(written) : null;
    var outcome = await CrashAutoShare.TryShareAsync(autoShareEnabled, recordJson, shareBroker, CancellationToken.None)
        .ConfigureAwait(false);
    Breadcrumb(opts, $"crash auto-share: {outcome}");
}
catch (Exception ex)
{
    Breadcrumb(opts, $"crash auto-share: error ({ex.GetType().Name})");
}

await ShutdownRemoteAsync(cts, presenceTask, ipcTask).ConfigureAwait(false);
return 0;

static string Fmt(int? code) => code?.ToString() ?? "<unknown>";

// Cancel the presence/IPC loops and let them finish their best-effort drop. Bounded
// so a stuck network call can't hang sidecar exit.
static async Task ShutdownRemoteAsync(CancellationTokenSource cts, Task presenceTask, Task ipcTask)
{
    try { cts.Cancel(); } catch { /* already disposed */ }
    try
    {
        await Task.WhenAny(Task.WhenAll(presenceTask, ipcTask), Task.Delay(TimeSpan.FromSeconds(5)))
            .ConfigureAwait(false);
    }
    catch
    {
        // Shutdown is best-effort.
    }
}

static string? TryReadAllText(string path)
{
    try { return File.ReadAllText(path); } catch { return null; }
}

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
