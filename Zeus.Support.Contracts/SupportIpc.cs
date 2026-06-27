// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Text.Json.Serialization;

namespace Zeus.Support.Contracts;

/// <summary>
/// Local IPC contract between the in-process Zeus backend and the out-of-process
/// support <b>sidecar</b> (Zeus.SupportAgent). The sidecar is launched detached
/// by the desktop host so it OUTLIVES a backend crash; it owns the broker
/// connection and persists crash/log state to disk. The two processes talk over
/// a per-session named pipe (Windows) / Unix-domain-style local pipe using the
/// length-prefixed JSON framing in <see cref="SupportIpcFraming"/>.
///
/// This is a same-machine, same-user channel only. It is NOT the server↔web wire
/// format (that lives in Zeus.Contracts and is red-light). Evolve this contract
/// by ADDING optional fields / new <see cref="SupportIpcMessage"/> subtypes and
/// bumping <see cref="ProtocolVersion"/>; never repurpose an existing field.
/// </summary>
public static class SupportIpc
{
    /// <summary>
    /// Contract version. The backend stamps it into <see cref="SupportHello"/> so a
    /// mismatched sidecar can refuse to attach rather than misread later frames.
    /// </summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// Hard ceiling on a single framed message (a diagnostics snapshot is the
    /// largest payload). Anything bigger is treated as a desync / hostile peer and
    /// the channel is dropped rather than allocating unbounded memory.
    /// </summary>
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    private const string PipePrefix = "zeus-support";

    /// <summary>
    /// Stable pipe name for a Zeus session. The desktop host mints a random
    /// session token at launch, passes it to the sidecar (its listen name) and
    /// exposes it to the backend (its connect target) so two Zeus instances on
    /// one machine never collide. The token is sanitised to the pipe-safe
    /// alphabet; a null/blank token falls back to the bare prefix (single-instance
    /// dev default).
    /// </summary>
    public static string PipeNameForSession(string? sessionToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken)) return PipePrefix;
        var sb = new System.Text.StringBuilder(PipePrefix.Length + 1 + sessionToken.Length);
        sb.Append(PipePrefix).Append('-');
        foreach (var ch in sessionToken)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        return sb.ToString();
    }

    /// <summary>Environment variable the desktop host uses to hand the session token to the backend.</summary>
    public const string SessionTokenEnvVar = "ZEUS_SUPPORT_SESSION";
}

/// <summary>An operator's decision on an admin's request for a live support session.</summary>
public enum SupportPromptDecision
{
    /// <summary>Operator explicitly approved a read-only diagnostics session.</summary>
    Allow,
    /// <summary>Operator explicitly declined.</summary>
    Deny,
    /// <summary>No answer within the prompt window — treated as a denial, fail-closed.</summary>
    Timeout,
    /// <summary>Backend could not show a prompt (no UI client connected). Fail-closed.</summary>
    Unavailable,
}

/// <summary>
/// Base type for every message on the support IPC channel. Polymorphic on a
/// <c>kind</c> discriminator so the framing layer can round-trip any message
/// without the reader knowing the concrete type in advance.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SupportHello), "hello")]
[JsonDerivedType(typeof(SupportStateChanged), "state")]
[JsonDerivedType(typeof(SupportHeartbeat), "heartbeat")]
[JsonDerivedType(typeof(SupportPromptRequest), "prompt-request")]
[JsonDerivedType(typeof(SupportPromptResult), "prompt-result")]
[JsonDerivedType(typeof(SupportDiagnosticsPull), "diag-pull")]
[JsonDerivedType(typeof(SupportDiagnosticsSnapshot), "diag-snapshot")]
public abstract record SupportIpcMessage;

/// <summary>
/// Backend → sidecar. First message after the pipe connects, and re-sent if the
/// backend reconnects. Carries identity + the operator's current opt-in posture
/// and the on-disk log paths the sidecar will tail.
/// </summary>
public sealed record SupportHello(
    int ProtocolVersion,
    int BackendPid,
    string AppVersion,
    string Platform,
    string? QrzCallsign,
    bool RemoteDiagnosticsEnabled,
    bool AutoShareOnCrash,
    string AppLogPath,
    string StartupLogPath) : SupportIpcMessage;

/// <summary>
/// Backend → sidecar. The operator changed an opt-in setting (the L1 master
/// switch, the crash auto-share sub-toggle) or signed in/out of QRZ. The sidecar
/// uses this to register/deregister with the broker and to gate crash sharing.
/// </summary>
public sealed record SupportStateChanged(
    string? QrzCallsign,
    bool RemoteDiagnosticsEnabled,
    bool AutoShareOnCrash) : SupportIpcMessage;

/// <summary>Either direction. Liveness ping; the peer is considered gone if these stop.</summary>
public sealed record SupportHeartbeat(long UnixMs) : SupportIpcMessage;

/// <summary>
/// Sidecar → backend. An authenticated admin has requested a live read-only
/// diagnostics session. The backend MUST surface an in-app Allow/Deny prompt and
/// reply with a <see cref="SupportPromptResult"/> carrying the same
/// <see cref="RequestId"/>. (When the backend is dead the sidecar never sees a
/// reply and falls back to the separate crash-auto-share pre-authorisation.)
/// </summary>
public sealed record SupportPromptRequest(
    string RequestId,
    string AdminCallsign,
    string? Reason) : SupportIpcMessage;

/// <summary>Backend → sidecar. The operator's answer to a <see cref="SupportPromptRequest"/>.</summary>
public sealed record SupportPromptResult(
    string RequestId,
    SupportPromptDecision Decision) : SupportIpcMessage;

/// <summary>
/// Sidecar → backend. Ask the live backend for a diagnostics snapshot. A null
/// <see cref="RouteSegment"/> means the whole v2 index; otherwise a single
/// provider's route segment (e.g. "dsp-live").
/// </summary>
public sealed record SupportDiagnosticsPull(
    string RequestId,
    string? RouteSegment) : SupportIpcMessage;

/// <summary>
/// Backend → sidecar. The JSON result of a <see cref="SupportDiagnosticsPull"/>.
/// <see cref="Json"/> is the already-serialised diagnostics payload (redacted by
/// the backend's existing diagnostics layer); the sidecar relays it verbatim.
/// </summary>
public sealed record SupportDiagnosticsSnapshot(
    string RequestId,
    bool Ok,
    string Json) : SupportIpcMessage;
