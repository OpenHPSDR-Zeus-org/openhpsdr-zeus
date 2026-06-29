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
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Support.Contracts;

namespace Zeus.Server.Hosting.Support;

/// <summary>
/// The backend's SEND side of the support IPC channel — the piece remote-diag P3c
/// was missing. The out-of-process sidecar (Zeus.SupportAgent) runs a named-pipe
/// SERVER and waits; this hosted service is the CLIENT that connects to it and
/// pushes the operator's identity + opt-in posture, so the sidecar can register
/// broker presence and gate crash auto-share against the operator's LIVE decision.
///
/// <para>Lifecycle: the bridge mints the per-session pipe token (handed to the
/// sidecar by <c>SupportSidecarLauncher</c> via <c>--session</c>), then keeps a
/// best-effort connection to the sidecar's pipe. On connect it sends a
/// <see cref="SupportHello"/> with the current posture; thereafter it sends a
/// <see cref="SupportStateChanged"/> whenever the operator toggles availability or
/// signs in/out of QRZ (<see cref="NotifyStateChanged"/>), plus a periodic
/// heartbeat. A dropped pipe (sidecar relaunch) just reconnects.</para>
///
/// <para>This solves the launch-time race: QRZ silent-login usually finishes a few
/// seconds AFTER the backend starts, so the old launch-only identity capture came
/// up empty. Here identity is resolved fresh at send time and re-pushed on the
/// QRZ <c>IdentityChanged</c> event, so presence comes online as soon as the
/// operator is both opted in and signed in — no restart required.</para>
///
/// <para>Strictly best-effort: every pipe failure is swallowed. Presence is a
/// diagnostics convenience and must never destabilise the backend.</para>
/// </summary>
public sealed class SupportSidecarBridge : BackgroundService
{
    // Pipe liveness cadence. Independent of the sidecar→broker presence heartbeat
    // (30s); this just keeps the local pipe active and surfaces a half-open drop.
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    // Backoff between connect attempts while the sidecar's pipe isn't up yet.
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);
    // Bound on resolving QRZ identity at send time so a slow QRZ call never stalls
    // the bridge loop.
    private static readonly TimeSpan IdentityTimeout = TimeSpan.FromSeconds(4);

    private readonly SupportAvailabilityStore _availability;
    private readonly Zeus.Server.QrzService _qrz;
    private readonly Zeus.Server.RadioService _radio;
    private readonly ILogger<SupportSidecarBridge> _log;

    // Signalled (max count 1) when posture changes so the loop pushes a fresh
    // SupportStateChanged without waiting out the heartbeat interval.
    private readonly SemaphoreSlim _dirty = new(0, 1);

    public SupportSidecarBridge(
        SupportAvailabilityStore availability,
        Zeus.Server.QrzService qrz,
        Zeus.Server.RadioService radio,
        ILogger<SupportSidecarBridge> log)
    {
        _availability = availability;
        _qrz = qrz;
        _radio = radio;
        _log = log;

        // The endpoint forwards the operator's availability toggle here.
        SupportSidecar.Bridge = this;
        // QRZ sign-in/out changes identity → re-push so presence can come online
        // (or drop) without a restart.
        _qrz.IdentityChanged += NotifyStateChanged;
    }

    /// <summary>
    /// The per-session pipe token. <c>SupportSidecarLauncher</c> reads this and
    /// passes it to the sidecar as <c>--session</c> so both ends derive the same
    /// pipe name (and two Zeus instances never collide).
    /// </summary>
    public string SessionToken { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Mark the operator's posture dirty so the loop pushes a fresh state frame.
    /// Cheap and non-blocking — safe to call from a request thread or a QRZ event.
    /// The actual identity resolution + send happen on the bridge loop.
    /// </summary>
    public void NotifyStateChanged()
    {
        // Coalesce: a single pending signal is enough; the next send reads fresh state.
        try { _dirty.Release(); }
        catch (SemaphoreFullException) { /* already pending */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipeName = SupportIpc.PipeNameForSession(SessionToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

                // ConnectAsync throws TimeoutException until the sidecar's server
                // pipe exists — caught below and retried after a short backoff.
                await client.ConnectAsync((int)ReconnectDelay.TotalMilliseconds, stoppingToken)
                    .ConfigureAwait(false);

                _log.LogInformation("support.bridge connected to sidecar pipe");
                await PumpAsync(client, stoppingToken).ConfigureAwait(false);
                _log.LogInformation("support.bridge sidecar pipe closed; will reconnect");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Sidecar not up yet / pipe dropped — back off and retry quietly.
                _log.LogDebug("support.bridge connect/pump failed ({Error}); retrying", ex.GetType().Name);
                try { await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // Drive one connected pipe: send the initial hello, then loop sending a fresh
    // state frame on a dirty signal or a heartbeat on the interval. A write failure
    // throws out to the reconnect loop.
    private async Task PumpAsync(Stream stream, CancellationToken ct)
    {
        await SupportIpcFraming.WriteAsync(stream, await BuildHelloAsync(ct).ConfigureAwait(false), ct)
            .ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            bool dirty = await _dirty.WaitAsync(HeartbeatInterval, ct).ConfigureAwait(false);
            if (dirty)
            {
                await SupportIpcFraming.WriteAsync(stream, await BuildStateAsync(ct).ConfigureAwait(false), ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await SupportIpcFraming.WriteAsync(
                    stream, new SupportHeartbeat(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<SupportHello> BuildHelloAsync(CancellationToken ct)
    {
        var (callsign, sessionKey) = await ResolveIdentityAsync(ct).ConfigureAwait(false);
        var (radioBoard, radioModel, radioConnected) = ResolveRadioMetadata();
        return new SupportHello(
            ProtocolVersion: SupportIpc.ProtocolVersion,
            BackendPid: Environment.ProcessId,
            AppVersion: ResolveAppVersion(),
            Platform: RuntimeInformation.OSDescription,
            QrzCallsign: callsign,
            RemoteDiagnosticsEnabled: _availability.IsAvailable,
            AutoShareOnCrash: _availability.AutoShareOnCrash,
            AppLogPath: PrefsDbPath.AppLogPath(),
            StartupLogPath: Path.Combine(PrefsDbPath.DataDir, "zeus-startup.log"),
            QrzSessionKey: sessionKey,
            RadioBoard: radioBoard,
            RadioModel: radioModel,
            RadioConnected: radioConnected);
    }

    private async Task<SupportStateChanged> BuildStateAsync(CancellationToken ct)
    {
        var (callsign, sessionKey) = await ResolveIdentityAsync(ct).ConfigureAwait(false);
        var (radioBoard, radioModel, radioConnected) = ResolveRadioMetadata();
        return new SupportStateChanged(
            QrzCallsign: callsign,
            RemoteDiagnosticsEnabled: _availability.IsAvailable,
            AutoShareOnCrash: _availability.AutoShareOnCrash,
            QrzSessionKey: sessionKey,
            RadioBoard: radioBoard,
            RadioModel: radioModel,
            RadioConnected: radioConnected);
    }

    // Resolve the operator's connected radio for the broker presence body. Null-safe:
    // when nothing is connected we report (null, null, false). The board name is the
    // discovered ConnectedBoardKind, refined by EffectiveOrionMkIIVariant for the
    // 0x0A alias family; the model is the variant name (or null for non-0x0A boards).
    // Best-effort — never throws out of the build path.
    private (string? Board, string? Model, bool Connected) ResolveRadioMetadata()
    {
        try
        {
            if (!_radio.IsConnected) return (null, null, false);

            var board = _radio.ConnectedBoardKind;
            if (board == Zeus.Contracts.HpsdrBoardKind.Unknown) return (null, null, true);

            if (board == Zeus.Contracts.HpsdrBoardKind.OrionMkII)
            {
                var variant = _radio.EffectiveOrionMkIIVariant;
                return (DescribeBoard(board, variant), variant.ToString(), true);
            }

            return (DescribeBoard(board, null), null, true);
        }
        catch
        {
            return (null, null, false);
        }
    }

    // Human-readable board name. The 0x0A wire byte aliases several radios, so the
    // variant refines the display string when present.
    private static string DescribeBoard(Zeus.Contracts.HpsdrBoardKind board, Zeus.Contracts.OrionMkIIVariant? variant) => board switch
    {
        Zeus.Contracts.HpsdrBoardKind.Metis => "Metis",
        Zeus.Contracts.HpsdrBoardKind.Hermes => "Hermes",
        Zeus.Contracts.HpsdrBoardKind.HermesII => "Hermes-II",
        Zeus.Contracts.HpsdrBoardKind.Angelia => "Angelia",
        Zeus.Contracts.HpsdrBoardKind.Orion => "Orion",
        Zeus.Contracts.HpsdrBoardKind.HermesLite2 => "Hermes-Lite 2",
        Zeus.Contracts.HpsdrBoardKind.HermesC10 => "ANAN-G2E",
        Zeus.Contracts.HpsdrBoardKind.OrionMkII => variant switch
        {
            Zeus.Contracts.OrionMkIIVariant.G2 => "ANAN-G2",
            Zeus.Contracts.OrionMkIIVariant.G2_1K => "ANAN-G2-1K",
            Zeus.Contracts.OrionMkIIVariant.Anan7000DLE => "ANAN-7000DLE",
            Zeus.Contracts.OrionMkIIVariant.Anan8000DLE => "ANAN-8000DLE",
            Zeus.Contracts.OrionMkIIVariant.OrionMkII => "OrionMkII",
            Zeus.Contracts.OrionMkIIVariant.AnvelinaPro3 => "ANVELINA-PRO3",
            Zeus.Contracts.OrionMkIIVariant.RedPitaya => "Red Pitaya",
            _ => "ANAN-G2",
        },
        _ => board.ToString(),
    };

    // Resolve the operator's QRZ identity fresh, bounded so a slow/blocked QRZ
    // call never stalls the loop. Null identity → the sidecar stays unconfigured
    // (no presence) until the next push.
    private async Task<(string? Callsign, string? SessionKey)> ResolveIdentityAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(IdentityTimeout);
            var identity = await _qrz.GetChatIdentityAsync(timeout.Token).ConfigureAwait(false);
            return identity is { } id ? (id.Callsign, id.SessionKey) : (null, null);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string ResolveAppVersion()
    {
        var asm = System.Reflection.Assembly.GetEntryAssembly();
        return asm?.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm?.GetName().Version?.ToString()
            ?? "unknown";
    }

    public override void Dispose()
    {
        try { _qrz.IdentityChanged -= NotifyStateChanged; } catch { /* best effort */ }
        if (ReferenceEquals(SupportSidecar.Bridge, this)) SupportSidecar.Bridge = null;
        _dirty.Dispose();
        base.Dispose();
    }
}
