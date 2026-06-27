// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using Zeus.Server.Diagnostics;

namespace Zeus.Server.Hosting.Support;

/// <summary>
/// One read-only maintainer support session over WebRTC. Unlike the operator's
/// own remote tunnel (<see cref="Zeus.Server.Hosting.Remote.RemoteWebRtcSession"/>,
/// a permissive read-write session gated by the SPAKE2+ password), a support
/// session:
///   • is authorised by an operator-approved <see cref="SupportGrant"/> — already
///     consumed by <see cref="SupportWebRtcService"/> before this session is built,
///     so there is no password handshake; the operator's local "Allow" IS the gate.
///   • is strictly READ-ONLY and diagnostics-scoped: the "api" channel proxies only
///     GET/HEAD on the <see cref="SupportSessionPolicy"/> allowlist; there is NO
///     radio-frame bridge, NO audio, NO control verbs, NO TX — nothing can mutate
///     the radio.
///   • streams the live application log on a dedicated "log" data channel.
///
/// Channels are client-initiated (the maintainer sends {t:"hello"} on each channel
/// it wants), mirroring the operator tunnel's pattern — the answerer-side onopen is
/// not relied upon.
/// </summary>
public sealed class SupportWebRtcSession
{
    private readonly ILogger _log;
    private readonly SupportApiProxy? _proxy;
    private readonly DiagnosticLogBuffer? _logBuffer;
    private readonly SupportGrant _grant;
    private readonly RTCPeerConnection _pc;

    private RTCDataChannel? _control;
    private RTCDataChannel? _api;
    private RTCDataChannel? _logChan;

    private int _closed;
    private int _logSubscribed;
    private Action<string>? _logHandler;

    /// <summary>How many recent log lines to replay when the maintainer attaches the log channel.</summary>
    private const int LogBacklogLines = 300;

    public SupportWebRtcSession(
        SupportGrant grant,
        ILogger log,
        IReadOnlyList<RTCIceServer>? iceServers = null,
        SupportApiProxy? proxy = null,
        DiagnosticLogBuffer? logBuffer = null)
    {
        _grant = grant;
        _log = log;
        _proxy = proxy;
        _logBuffer = logBuffer;

        _pc = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = iceServers?.ToList() ?? new List<RTCIceServer>(),
        });
        _pc.ondatachannel += OnDataChannel;
        _pc.onconnectionstatechange += state =>
        {
            if (state is RTCPeerConnectionState.closed
                or RTCPeerConnectionState.failed
                or RTCPeerConnectionState.disconnected)
                Close();
        };
    }

    /// <summary>The request this session serves (for owner bookkeeping / logging).</summary>
    public string RequestId => _grant.RequestId;

    /// <summary>The maintainer callsign the operator approved.</summary>
    public string AdminCallsign => _grant.AdminCallsign;

    /// <summary>Raised once, when the session is torn down (for owner cleanup).</summary>
    public event Action? Closed;

    /// <summary>Answer the maintainer's offer with a self-contained (vanilla-ICE) SDP.</summary>
    public async Task<string> CreateAnswerAsync(string offerSdp, CancellationToken ct = default)
    {
        var setResult = _pc.setRemoteDescription(
            new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp });
        if (setResult != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException($"setRemoteDescription failed: {setResult}");

        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer);
        await WaitForIceGatheringAsync(_pc, TimeSpan.FromMilliseconds(750), ct);
        return _pc.localDescription.sdp.ToString();
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        // Detach the live-log tail so a closed session stops touching the buffer.
        if (_logBuffer is not null && _logHandler is not null)
        {
            _logBuffer.LineAdded -= _logHandler;
            _logHandler = null;
        }

        try { _pc.close(); } catch { /* already torn down */ }
        Closed?.Invoke();
    }

    private void OnDataChannel(RTCDataChannel dc)
    {
        switch (dc.label)
        {
            case "control":
                _control = dc;
                dc.onmessage += (_, _, data) => OnControlMessage(data);
                break;
            case "api":
                _api = dc;
                dc.onmessage += (_, _, data) => OnApiMessage(data);
                break;
            case "log":
                _logChan = dc;
                dc.onmessage += (_, _, data) => OnLogMessage(data);
                break;
            default:
                _log.LogWarning("support.rtc unexpected data channel '{Label}'", dc.label);
                break;
        }
    }

    // -- control: client hello → readiness ack -------------------------------

    private void OnControlMessage(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("t", out var tEl) && tEl.GetString() == "hello")
            {
                _control?.send(JsonSerializer.Serialize(new
                {
                    t = "support-ready",
                    requestId = _grant.RequestId,
                    admin = _grant.AdminCallsign,
                }));
            }
        }
        catch { /* malformed control message — ignore (read-only session, nothing to abuse) */ }
    }

    // -- api: read-only allowlist loopback proxy -----------------------------

    private void OnApiMessage(byte[] data)
    {
        if (Volatile.Read(ref _closed) != 0 || _proxy is null) return;
        _ = HandleApiAsync(data);
    }

    private async Task HandleApiAsync(byte[] data)
    {
        int id = 0;
        try
        {
            string method, path;
            using (var doc = JsonDocument.Parse(data))
            {
                var root = doc.RootElement;
                id = root.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
                method = (root.TryGetProperty("method", out var mEl) ? mEl.GetString() : null) ?? "GET";
                path = (root.TryGetProperty("path", out var pEl) ? pEl.GetString() : null) ?? "";
            }

            var reply = await _proxy!.HandleAsync(new SupportApiRequest(id, method, path)).ConfigureAwait(false);
            SendApiReply(reply);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "support.rtc api dispatch failed — replying 502");
            try { SendApiReply(new SupportApiReply(id, 502, null, null)); } catch { /* channel gone */ }
        }
    }

    private void SendApiReply(SupportApiReply reply)
    {
        if (_api is null) return;
        var payload = new Dictionary<string, object?>
        {
            ["id"] = reply.Id,
            ["status"] = reply.Status,
        };
        if (reply.ContentType is not null) payload["contentType"] = reply.ContentType;
        if (reply.Body is not null) payload["body"] = reply.Body;
        try { _api.send(JsonSerializer.Serialize(payload)); } catch { /* channel closed */ }
    }

    // -- log: backlog replay + live tail -------------------------------------

    private void OnLogMessage(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (!(doc.RootElement.TryGetProperty("t", out var tEl) && tEl.GetString() == "hello"))
                return;
        }
        catch { return; }

        // Subscribe exactly once, even if the client sends multiple hellos.
        if (_logBuffer is null || Interlocked.Exchange(ref _logSubscribed, 1) != 0) return;

        // Replay the recent backlog first so the maintainer sees context, then
        // stream new lines as they arrive.
        try
        {
            var backlog = _logBuffer.Snapshot(LogBacklogLines);
            _logChan?.send(JsonSerializer.Serialize(new { t = "backlog", lines = backlog }));
        }
        catch (Exception ex) { _log.LogWarning(ex, "support.rtc log backlog send failed"); }

        _logHandler = OnLogLine;
        _logBuffer.LineAdded += _logHandler;
    }

    private void OnLogLine(string line)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        try { _logChan?.send(JsonSerializer.Serialize(new { t = "line", line })); }
        catch { /* channel gone — the connection-state handler will Close() us */ }
    }

    private static async Task WaitForIceGatheringAsync(RTCPeerConnection pc, TimeSpan timeout, CancellationToken ct)
    {
        if (pc.iceGatheringState == RTCIceGatheringState.complete)
            return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChange(RTCIceGatheringState s)
        {
            if (s == RTCIceGatheringState.complete) tcs.TrySetResult();
        }

        pc.onicegatheringstatechange += OnChange;
        try
        {
            if (pc.iceGatheringState == RTCIceGatheringState.complete) return;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await using (cts.Token.Register(() => tcs.TrySetResult()))
                await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            pc.onicegatheringstatechange -= OnChange;
        }
    }
}
