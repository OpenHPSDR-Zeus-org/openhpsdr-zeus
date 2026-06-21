using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace Zeus.Server.Hosting.Remote;

/// <summary>
/// Signaling entry point for remote access (Phase 1). Answers a browser WebRTC
/// offer with a session that is LOCKED until the SPAKE2+ password handshake
/// completes (ADR-0008). Remote access is refused unless the operator has set a
/// password — there is no unauthenticated remote path.
/// </summary>
public sealed class RemoteWebRtcService
{
    private readonly RemotePasswordStore _passwords;
    private readonly ILogger<RemoteWebRtcService> _log;
    private readonly ConcurrentDictionary<Guid, RemoteWebRtcSession> _sessions = new();

    public RemoteWebRtcService(RemotePasswordStore passwords, ILogger<RemoteWebRtcService> log)
    {
        _passwords = passwords;
        _log = log;
    }

    /// <summary>Whether remote access can be offered at all (a password is set).</summary>
    public bool RemoteEnabled => _passwords.HasPassword();

    public int ActiveSessions => _sessions.Count;

    /// <summary>
    /// Answer a remote offer. Throws <see cref="RemoteAccessDisabledException"/> if
    /// no password is set (deny-by-default — the operator must enable it).
    /// </summary>
    public async Task<string> ConnectAsync(string offerSdp, CancellationToken ct = default)
    {
        var verifier = _passwords.GetVerifier()
            ?? throw new RemoteAccessDisabledException();

        var id = Guid.NewGuid();
        var session = new RemoteWebRtcSession(verifier, _log, IceServers());
        _sessions[id] = session;
        session.Closed += () =>
        {
            if (_sessions.TryRemove(id, out _))
                _log.LogInformation("rtc.remote session {Id} closed ({Count} active)", id, _sessions.Count);
        };
        session.Unlocked += () => _log.LogInformation("rtc.remote session {Id} unlocked", id);

        _log.LogInformation("rtc.remote answering offer, session {Id}", id);
        return await session.CreateAnswerAsync(offerSdp, ct);
    }

    public void CloseAll()
    {
        foreach (var kv in _sessions)
            if (_sessions.TryRemove(kv.Key, out var s))
                s.Close();
    }

    // STUN only here; the production path swaps in Cloudflare TURN credentials
    // minted by the broker (Phase 3) for NAT/CGNAT traversal.
    private static List<RTCIceServer> IceServers() =>
    [
        new RTCIceServer { urls = "stun:stun.cloudflare.com:3478" },
    ];
}

/// <summary>Thrown when a remote connection is attempted with no session password set.</summary>
public sealed class RemoteAccessDisabledException()
    : Exception("Remote access requires a session password (Server menu).");

/// <summary>Body for <c>POST /api/remote/connect</c> (the browser's WebRTC offer).</summary>
public sealed record RemoteConnectRequest(string Sdp);
