using System.Text.Json;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace Zeus.Server.Hosting.Remote;

/// <summary>
/// One remote-access WebRTC session (Phase 1). Answers a browser offer, runs the
/// SPAKE2+ password handshake over a reliable "control" DataChannel, and gates
/// all radio egress behind <see cref="RemoteSession"/> — nothing flows on the
/// "frames" channel until the password proves out (ADR-0008).
///
/// Auth wire protocol on the control channel (JSON, one message per send):
///   server → {t:"auth-params", salt, iterations, memoryKib, parallelism}
///   client → {t:"auth-share",  share}        (shareP)
///   server → {t:"auth-share",  share}        (shareV; still LOCKED)
///   client → {t:"auth-confirm",confirm}      (confirmP)
///   server → {t:"auth-ok",     confirm} + UNLOCK   | {t:"auth-fail"} + close
///
/// The real radio-frame bridge (StreamingHub → frames channel) attaches on
/// <see cref="Unlocked"/>; this class owns the gate, not the DSP wiring.
/// </summary>
public sealed class RemoteWebRtcSession
{
    private readonly ILogger _log;
    private readonly RemoteVerifierMaterial _verifier;
    private readonly RemoteSession _session;
    private readonly RTCPeerConnection _pc;
    private readonly Zeus.Server.StreamingHub? _hub;
    private readonly Guid _sinkId = Guid.NewGuid();

    private RTCDataChannel? _control;
    private RTCDataChannel? _frames;
    private RemoteFrameSink? _sink;

    // Post-unlock binary stream-request control frames (RX monitoring, Phase A):
    //   first byte 0x22 = display request, 0x21 = audio request; byte[1] 1/0 = enable/disable.
    // We track the session's current wanted-state so a duplicate enable can't
    // double-count the hub's global gate and a disconnect always unwinds it.
    private const byte MsgTypeAudioStreamRequest = 0x21;
    private const byte MsgTypeDisplayStreamRequest = 0x22;
    private bool _wantsDisplay;
    private bool _wantsAudio;

    public RemoteWebRtcSession(
        RemoteVerifierMaterial verifier, ILogger log,
        IReadOnlyList<RTCIceServer>? iceServers = null, Zeus.Server.StreamingHub? hub = null)
    {
        _verifier = verifier;
        _log = log;
        _hub = hub;

        var gate = new Spake2PlusAuthGate(
            RemoteAuthConstants.Context, RemoteAuthConstants.IdProver, RemoteAuthConstants.IdVerifier,
            verifier.W0, verifier.L);
        _session = new RemoteSession(gate);

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

    /// <summary>True once the password handshake has succeeded.</summary>
    public bool IsUnlocked => _session.IsUnlocked;

    /// <summary>Raised once, when the session transitions to UNLOCKED (attach the frame bridge here).</summary>
    public event Action? Unlocked;

    /// <summary>Raised once, when the session is torn down (for owner cleanup).</summary>
    public event Action? Closed;

    private int _closed;

    /// <summary>Answer the browser's offer with a self-contained (vanilla-ICE) SDP.</summary>
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

    /// <summary>
    /// Egress a radio frame on the unreliable "frames" channel — refused (returns
    /// false, never throws) until the session is UNLOCKED.
    /// </summary>
    public bool TrySendFrame(byte[] frame)
    {
        if (!_session.TryEgress() || _frames is null)
            return false;
        _frames.send(frame);
        return true;
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _session.Close();
        if (_hub is not null)
        {
            // Release any RX display/audio request still held so a remote
            // disconnect can't pin the global gates on (mirrors the /ws
            // ClientSession cleanup in StreamingHub.AttachClientAsync).
            if (_wantsDisplay) { _wantsDisplay = false; _hub.AdjustDisplayRequests(-1); }
            if (_wantsAudio) { _wantsAudio = false; _hub.AdjustAudioRequests(-1); }
            _hub.DetachSink(_sinkId);
        }
        _sink?.Dispose();
        try { _pc.close(); } catch { /* already torn down */ }
        Closed?.Invoke();
    }

    private void OnDataChannel(RTCDataChannel dc)
    {
        switch (dc.label)
        {
            case "control":
                _control = dc;
                // Client-initiated: it sends {t:"hello"} once its channel opens
                // and we reply with auth-params. Avoids depending on the
                // answerer-side onopen firing.
                dc.onmessage += (_, _, data) => OnControlMessage(data);
                break;
            case "frames":
                _frames = dc;
                break;
            default:
                _log.LogWarning("rtc.remote unexpected data channel '{Label}'", dc.label);
                break;
        }
    }

    private void SendAuthParams()
    {
        _control!.send(JsonSerializer.Serialize(new
        {
            t = "auth-params",
            salt = Convert.ToBase64String(_verifier.Salt),
            iterations = _verifier.Iterations,
            memoryKib = _verifier.MemoryKib,
            parallelism = _verifier.Parallelism,
        }));
    }

    /// <summary>
    /// Split control-channel input: pre-unlock and JSON auth messages go to the
    /// SPAKE2+ gate; post-unlock binary stream-request frames (0x21/0x22) drive
    /// the RX display/audio gates. The two are unambiguous on the wire — auth is
    /// always JSON (first byte '{' = 0x7B), stream-requests lead with 0x21/0x22,
    /// so a 0x21/0x22 first byte is treated as binary control, never JSON.
    /// Deny-by-default: binary control is honoured ONLY after unlock; anything
    /// non-auth while LOCKED still fails the session closed via HandleControlAsync.
    /// </summary>
    private void OnControlMessage(byte[] data)
    {
        if (_session.IsUnlocked
            && data.Length >= 1
            && data[0] is MsgTypeDisplayStreamRequest or MsgTypeAudioStreamRequest)
        {
            HandleStreamRequest(data);
            return;
        }

        _ = HandleControlAsync(data);
    }

    /// <summary>
    /// Apply a post-unlock binary stream-request frame: [type][enable:u8].
    /// 0x22 toggles the display gate, 0x21 the audio gate. Tracks the session's
    /// current wanted-state so the hub's global counter moves at most once per
    /// transition (and Close can unwind it exactly).
    /// </summary>
    private void HandleStreamRequest(byte[] data)
    {
        if (_hub is null) return;
        bool enable = data.Length > 1 && data[1] != 0;
        switch (data[0])
        {
            case MsgTypeDisplayStreamRequest:
                if (enable == _wantsDisplay) return;
                _wantsDisplay = enable;
                _hub.AdjustDisplayRequests(enable ? 1 : -1);
                break;
            case MsgTypeAudioStreamRequest:
                if (enable == _wantsAudio) return;
                _wantsAudio = enable;
                _hub.AdjustAudioRequests(enable ? 1 : -1);
                break;
        }
    }

    private async Task HandleControlAsync(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var t = doc.RootElement.GetProperty("t").GetString();

            if (_session.IsUnlocked)
                return; // post-unlock control plane (inbound VFO etc.) attaches with the frame bridge

            switch (t)
            {
                case "hello":
                    SendAuthParams();
                    break;
                case "auth-share":
                {
                    var share = Convert.FromBase64String(doc.RootElement.GetProperty("share").GetString()!);
                    var outcome = await _session.SubmitAuthAsync(share);
                    if (outcome.Action == RemoteSessionAction.Reply)
                        _control!.send(Json("auth-share", "share", outcome.Reply.ToArray()));
                    else
                        FailAndClose();
                    break;
                }
                case "auth-confirm":
                {
                    var confirm = Convert.FromBase64String(doc.RootElement.GetProperty("confirm").GetString()!);
                    var outcome = await _session.SubmitAuthAsync(confirm);
                    if (outcome.Action == RemoteSessionAction.Unlock)
                    {
                        _control!.send(Json("auth-ok", "confirm", outcome.Reply.ToArray()));
                        _log.LogInformation("rtc.remote session UNLOCKED");

                        // Arm the radio data path: register a sink so the hub's
                        // broadcast fan-out reaches this session's frames channel
                        // (gated again by TrySendFrame). Only happens post-unlock.
                        if (_hub is not null)
                        {
                            _sink = new RemoteFrameSink(TrySendFrame);
                            _hub.AttachSink(_sinkId, _sink);
                        }

                        Unlocked?.Invoke();
                    }
                    else
                    {
                        FailAndClose();
                    }
                    break;
                }
                default:
                    FailAndClose(); // anything else while LOCKED is a protocol violation
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "rtc.remote auth error — failing closed");
            FailAndClose();
        }
    }

    private void FailAndClose()
    {
        try { _control?.send("{\"t\":\"auth-fail\"}"); } catch { /* best effort */ }
        Close();
    }

    private static string Json(string t, string field, byte[] value)
        => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["t"] = t,
            [field] = Convert.ToBase64String(value),
        });

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
