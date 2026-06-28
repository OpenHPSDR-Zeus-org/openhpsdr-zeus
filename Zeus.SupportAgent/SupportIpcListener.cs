// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.IO.Pipes;
using Zeus.Support.Contracts;

namespace Zeus.SupportAgent;

/// <summary>
/// The sidecar's RECEIVE side of the support IPC channel. The sidecar owns the
/// named-pipe SERVER (it outlives the backend, so it must be listening before the
/// backend connects); the backend is the client that connects and pushes
/// identity/opt-in updates.
///
/// This phase consumes exactly the two backend → sidecar messages the presence /
/// crash subsystem needs: <see cref="SupportHello"/> (initial identity + posture)
/// and <see cref="SupportStateChanged"/> (the operator flipped the L1 master
/// switch or the crash auto-share sub-toggle, or signed in/out of QRZ). Each is
/// surfaced to the caller via <see cref="OnState"/> so the presence loop and the
/// crash-share gate stay in sync with the operator's live decision. Unrelated
/// message kinds are ignored (forward-compatible).
///
/// Best-effort and resilient: a dropped pipe (backend restart) just loops back to
/// waiting for the next connection; nothing here ever throws out to the sidecar's
/// main flow.
/// </summary>
public sealed class SupportIpcListener
{
    /// <summary>A snapshot of the operator's live support posture, as learned over IPC.</summary>
    /// <param name="QrzCallsign">Operator callsign, or null when signed out.</param>
    /// <param name="RemoteDiagnosticsEnabled">L1 master switch.</param>
    /// <param name="AutoShareOnCrash">Crash auto-share sub-toggle.</param>
    public readonly record struct SupportState(
        string? QrzCallsign,
        bool RemoteDiagnosticsEnabled,
        bool AutoShareOnCrash);

    private readonly string _pipeName;
    private readonly Action<SupportState> _onState;
    private readonly Action<string>? _log;

    /// <param name="sessionToken">The per-session token; same value the backend derives its connect target from.</param>
    /// <param name="onState">Invoked whenever a hello/state message updates the operator's posture.</param>
    /// <param name="log">Optional breadcrumb sink.</param>
    public SupportIpcListener(string? sessionToken, Action<SupportState> onState, Action<string>? log = null)
    {
        _pipeName = SupportIpc.PipeNameForSession(sessionToken);
        _onState = onState;
        _log = log;
    }

    /// <summary>
    /// Listen for backend connections until cancelled. Accepts one client at a
    /// time (a single backend), reads framed messages, and forwards posture updates
    /// to <see cref="_onState"/>. On any pipe error it loops back to accept the next
    /// connection (e.g. after a backend restart).
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                _log?.Invoke("ipc: backend connected");
                await ReadLoopAsync(server, ct).ConfigureAwait(false);
                _log?.Invoke("ipc: backend disconnected");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A pipe in use / transient error: pause briefly and re-listen so a
                // backend restart reconnects without spinning the CPU.
                _log?.Invoke($"ipc: listener error ({ex.GetType().Name}); retrying");
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ReadLoopAsync(Stream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            SupportIpcMessage? message;
            try
            {
                message = await SupportIpcFraming.ReadAsync(stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Framing/desync error — drop this connection and re-listen.
                return;
            }

            if (message is null) return; // clean EOF: backend closed the pipe

            switch (message)
            {
                case SupportHello hello:
                    _onState(new SupportState(
                        hello.QrzCallsign, hello.RemoteDiagnosticsEnabled, hello.AutoShareOnCrash));
                    break;
                case SupportStateChanged state:
                    _onState(new SupportState(
                        state.QrzCallsign, state.RemoteDiagnosticsEnabled, state.AutoShareOnCrash));
                    break;
                // Heartbeats and other kinds are not needed by the presence/crash
                // subsystem; ignore them so the contract can grow without breaking us.
            }
        }
    }
}
