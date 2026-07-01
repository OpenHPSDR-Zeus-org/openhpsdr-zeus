// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. See
// ATTRIBUTIONS.md at the repository root for the full provenance statement.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Owns the operator's connection to the ZeusChat Cloudflare relay. Connects
/// while chat is enabled (persisted opt-in, default OFF) AND QRZ is logged in
/// (the relay validates the QRZ session on the WS upgrade). Mirrors radio
/// presence to the relay, fans incoming relay events out to web clients via
/// <see cref="StreamingHub"/> as ChatEvent (0x35) frames, and keeps an in-memory
/// roster + bounded message history for push-on-attach.
/// </summary>
public sealed class ChatService : BackgroundService
{
    /// <summary>Default public relay endpoint. Override with ZEUSCHAT_RELAY_URL.</summary>
    public const string DefaultRelayUrl =
        "wss://chat.openhpsdrzeus.com/chat";

    private const int MessageHistoryCap = 200;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PresenceThrottleWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleStatusTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ReconnectMinDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconnectMaxDelay = TimeSpan.FromSeconds(30);

    private readonly QrzService _qrz;
    private readonly RadioService _radio;
    private readonly StreamingHub _hub;
    private readonly ChatEnabledStore _store;
    private readonly ILogger<ChatService> _log;

    private readonly string _relayUrl;
    private readonly string? _relaySecret;

    private static readonly ChatFriendsDto EmptyFriends =
        new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    private readonly ChatMessageRing _messages = new(MessageHistoryCap);
    private readonly object _rosterSync = new();
    private IReadOnlyList<ChatOperator> _roster = Array.Empty<ChatOperator>();

    // The operator's friend graph, mirrored from the relay (accepted / incoming
    // / outgoing). Replaced wholesale on each relay "friends" frame.
    private volatile ChatFriendsDto _friends = EmptyFriends;

    // Rooms the operator can see (public + groups + DMs), mirrored from the relay.
    private readonly object _roomsSync = new();
    private IReadOnlyList<ChatRoomDto> _rooms = Array.Empty<ChatRoomDto>();

    // Whether the operator is a relay moderator (from the welcome frame).
    private volatile bool _isAdmin;

    // Admin "see all frequencies" override (session-scoped, default OFF). When
    // armed by an admin it is re-asserted to the relay after each (re)connect so
    // a network blip doesn't silently drop it. Meaningless for non-admins.
    private volatile bool _seeAllFreq;

    // Live connection state, set on the worker loop and read by the API.
    private volatile bool _connected;
    private volatile string? _lastError;
    private volatile string? _selfCallsign;

    // Signals the worker loop to re-evaluate (enable/disable toggled, or a
    // presence change is pending). The send side runs on the worker so frames
    // never interleave across threads.
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);

    // Presence the worker should publish on its next opportunity, gated by the
    // throttle. Written by event handlers, drained by the worker.
    private readonly PresenceThrottle _presence = new(PresenceThrottleWindow);
    private volatile bool _moxOn;

    // UTC ticks of the last operator activity that keeps the roster light green:
    // outgoing chat, a TX edge, or a VFO-A frequency change. After
    // IdleStatusTimeout, the backend publishes "away" (yellow in the UI).
    private long _lastOperatorActivityTicks;
    private long _lastObservedVfoHz;

    // The live socket, set while connected so SendMessageAsync (API path) can
    // post a {t:"msg"} frame. Null when disconnected.
    private ClientWebSocket? _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public ChatService(
        QrzService qrz,
        RadioService radio,
        StreamingHub hub,
        ChatEnabledStore store,
        ILogger<ChatService> log)
    {
        _qrz = qrz;
        _radio = radio;
        _hub = hub;
        _store = store;
        _log = log;

        var url = Environment.GetEnvironmentVariable("ZEUSCHAT_RELAY_URL");
        _relayUrl = string.IsNullOrWhiteSpace(url) ? DefaultRelayUrl : url.Trim();
        var secret = Environment.GetEnvironmentVariable("ZEUSCHAT_RELAY_SECRET");
        _relaySecret = string.IsNullOrWhiteSpace(secret) ? null : secret.Trim();

        _hub.SetChatSnapshotProvider(BuildSnapshotFrames);
        _radio.StateChanged += OnRadioStateChanged;
        _radio.MoxChanged += OnMoxChanged;

        var initial = _radio.Snapshot();
        _lastObservedVfoHz = initial.VfoHz;
        MarkOperatorActivity(DateTimeOffset.UtcNow);
    }

    // ── Public API surface (used by ZeusEndpoints) ─────────────────────────

    public bool Enabled => _store.GetEnabled();

    public ChatStatusDto GetStatus() => new(
        Enabled: _store.GetEnabled(),
        Connected: _connected,
        Callsign: _selfCallsign,
        RelayUrl: _relayUrl,
        Error: _lastError,
        IsAdmin: _isAdmin,
        FreqPublic: _store.GetFreqPublic(),
        SeeAllFreq: _seeAllFreq);

    /// <summary>Persists the opt-in and wakes the worker to connect/disconnect.</summary>
    public ChatStatusDto SetEnabled(bool enabled)
    {
        _store.SetEnabled(enabled);
        if (!enabled)
        {
            _lastError = null;
        }
        Wake();
        return GetStatus();
    }

    /// <summary>
    /// Accepts the web client's legacy Chat-panel visibility heartbeat. The
    /// relay connection is intentionally no longer gated on panel mount state:
    /// the persisted chat opt-in owns presence, which avoids roster churn from
    /// missed browser timers or workspace tile unmounts.
    /// </summary>
    public ChatStatusDto ReportPanelVisible(bool visible)
    {
        Wake();
        return GetStatus();
    }

    public IReadOnlyList<ChatMessage> GetMessages(int limit) => _messages.Snapshot(limit);

    public IReadOnlyList<ChatOperator> GetRoster()
    {
        lock (_rosterSync) return _roster;
    }

    /// <summary>
    /// Sends an outgoing chat message to the relay. Throws
    /// <see cref="InvalidOperationException"/> when not connected and
    /// <see cref="ArgumentException"/> when the text is empty — the endpoint
    /// maps these to 409 / 400.
    /// </summary>
    public async Task SendMessageAsync(string text, string? room, ChatAttachment? attachment, CancellationToken ct)
    {
        attachment = ValidateAttachment(attachment);
        // An attachment may stand on its own (image-only message); only require
        // text when there is no attachment.
        if (string.IsNullOrWhiteSpace(text) && attachment is null)
            throw new ArgumentException("message text is empty", nameof(text));

        var socket = RequireSocket();
        var roomId = string.IsNullOrWhiteSpace(room) ? "lobby" : room.Trim();
        await SendFrameAsync(socket, attachment is null
            ? new { t = "msg", text, room = roomId }
            : new { t = "msg", text, room = roomId, attachment }, ct);
        NoteOperatorActivity();
    }

    /// <summary>Sends a direct message to <paramref name="to"/> (creates the DM on demand).</summary>
    public async Task SendDmAsync(string to, string text, ChatAttachment? attachment, CancellationToken ct)
    {
        var call = (to ?? string.Empty).Trim().ToUpperInvariant();
        if (call.Length == 0) throw new ArgumentException("recipient is empty", nameof(to));
        attachment = ValidateAttachment(attachment);
        if (string.IsNullOrWhiteSpace(text) && attachment is null)
            throw new ArgumentException("message text is empty", nameof(text));
        var socket = RequireSocket();
        await SendFrameAsync(socket, attachment is null
            ? new { t = "dm", to = call, text }
            : new { t = "dm", to = call, text, attachment }, ct);
        NoteOperatorActivity();
    }

    /// <summary>
    /// Validates an outgoing attachment: image or audio kind with a matching
    /// MIME family + data-URL scheme, non-empty data URL within
    /// <see cref="ChatAttachment.MaxDataUrlLength"/>. Returns the attachment
    /// unchanged (passthrough) or null when none was supplied. Throws
    /// <see cref="ArgumentException"/> (mapped to 400 by the endpoint) on a bad
    /// or oversized attachment so a buggy/hostile client can't push payloads the
    /// relay would store. The web client downscales photos / records voice at a
    /// low bitrate to fit before sending; this is the backend guardrail, not the
    /// primary size control.
    /// </summary>
    private static ChatAttachment? ValidateAttachment(ChatAttachment? attachment)
    {
        if (attachment is null) return null;
        var isImage = string.Equals(attachment.Kind, "image", StringComparison.Ordinal);
        var isAudio = string.Equals(attachment.Kind, "audio", StringComparison.Ordinal);
        if (!isImage && !isAudio)
            throw new ArgumentException("unsupported attachment kind", nameof(attachment));
        var mimePrefix = isAudio ? "audio/" : "image/";
        var dataPrefix = isAudio ? "data:audio/" : "data:image/";
        if (string.IsNullOrEmpty(attachment.Mime) ||
            !attachment.Mime.StartsWith(mimePrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"attachment must be {(isAudio ? "audio" : "an image")}", nameof(attachment));
        if (string.IsNullOrEmpty(attachment.DataUrl) ||
            !attachment.DataUrl.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("attachment data is empty or malformed", nameof(attachment));
        if (attachment.DataUrl.Length > ChatAttachment.MaxDataUrlLength)
            throw new ArgumentException("attachment is too large", nameof(attachment));
        return attachment;
    }

    /// <summary>Asks the relay for recent history of a room (pushed back as a 0x35 frame).</summary>
    public async Task RequestHistoryAsync(string room, CancellationToken ct)
    {
        var roomId = string.IsNullOrWhiteSpace(room) ? "lobby" : room.Trim();
        var socket = RequireSocket();
        await SendFrameAsync(socket, new { t = "history", room = roomId }, ct);
    }

    /// <summary>Rooms the operator can currently see.</summary>
    public IReadOnlyList<ChatRoomDto> GetRooms()
    {
        lock (_roomsSync) return _rooms;
    }

    // ── Admin / moderation ─────────────────────────────────────────────────

    public Task CreateRoomAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("room name is empty", nameof(name));
        return SendFrameAsync(RequireSocket(), new { t = "admin_create_room", name = name.Trim() }, ct);
    }

    public Task DeleteRoomAsync(string room, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(room)) throw new ArgumentException("room is empty", nameof(room));
        return SendFrameAsync(RequireSocket(), new { t = "admin_delete_room", room = room.Trim() }, ct);
    }

    public Task AddMemberAsync(string room, string callsign, CancellationToken ct) =>
        SendRoomMemberAsync("admin_add_member", room, callsign, ct);

    public Task RemoveMemberAsync(string room, string callsign, CancellationToken ct) =>
        SendRoomMemberAsync("admin_remove_member", room, callsign, ct);

    public Task BanAsync(string callsign, CancellationToken ct) =>
        SendFriendActionAsync("admin_ban", "callsign", callsign, ct);

    public Task UnbanAsync(string callsign, CancellationToken ct) =>
        SendFriendActionAsync("admin_unban", "callsign", callsign, ct);

    /// <summary>Admin: asks the relay for the current ban list (pushed back as a
    /// 0x35 <c>bans</c> frame). The relay ignores this unless the caller is an
    /// admin.</summary>
    public Task ListBansAsync(CancellationToken ct) =>
        SendFrameAsync(RequireSocket(), new { t = "admin_list_bans" }, ct);

    /// <summary>Admin: clears a room's history (defaults to the public lobby).</summary>
    public Task ClearRoomAsync(string? room, CancellationToken ct)
    {
        var roomId = string.IsNullOrWhiteSpace(room) ? "lobby" : room.Trim();
        return SendFrameAsync(RequireSocket(), new { t = "admin_clear_room", room = roomId }, ct);
    }

    /// <summary>Admin: broadcasts a one-off global announcement to all operators.</summary>
    public Task BroadcastAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("message text is empty", nameof(text));
        return SendFrameAsync(RequireSocket(), new { t = "admin_broadcast", text = text.Trim() }, ct);
    }

    private Task SendRoomMemberAsync(string verb, string room, string callsign, CancellationToken ct)
    {
        var call = (callsign ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(room)) throw new ArgumentException("room is empty", nameof(room));
        if (call.Length == 0) throw new ArgumentException("callsign is empty", nameof(callsign));
        return SendFrameAsync(RequireSocket(),
            new Dictionary<string, string> { ["t"] = verb, ["room"] = room.Trim(), ["callsign"] = call }, ct);
    }

    // ── Frequency visibility (eye toggle) ──────────────────────────────────

    /// <summary>Persists the eye toggle and, if connected, tells the relay at once.</summary>
    public async Task SetFreqVisibilityAsync(bool isPublic, CancellationToken ct)
    {
        _store.SetFreqPublic(isPublic);
        PushStatus();
        var socket = _socket;
        if (socket is not null && socket.State == WebSocketState.Open && _connected)
        {
            var snap = _radio.Snapshot();
            await SendFrameAsync(socket, new
            {
                t = "presence",
                freq = snap.VfoHz,
                mode = snap.Mode.ToString(),
                status = CurrentStatus(),
                freqPublic = isPublic,
            }, ct);
        }
    }

    /// <summary>
    /// Admin: arms/disarms the "see all frequencies" override. Persists the
    /// session-scoped flag, reflects it in status, and — if connected — tells the
    /// relay so the next roster carries (or strips) everyone's freq. The relay
    /// ignores the toggle unless it has flagged this connection as an admin, so a
    /// non-admin calling this is a harmless no-op on the relay side.
    /// </summary>
    public async Task SetSeeAllFreqAsync(bool on, CancellationToken ct)
    {
        _seeAllFreq = on;
        PushStatus();
        var socket = _socket;
        if (socket is not null && socket.State == WebSocketState.Open && _connected)
            await SendFrameAsync(socket, new { t = "admin_see_all", on }, ct);
    }

    /// <summary>Re-sends the current see-all override to the relay after a
    /// reconnect. Best-effort: swallows send failures (the loop will reconnect
    /// and try again) so it can be safely fire-and-forgotten from the receive
    /// loop.</summary>
    private async Task ReassertSeeAllAsync()
    {
        try
        {
            var socket = _socket;
            if (socket is not null && socket.State == WebSocketState.Open && _connected)
                await SendFrameAsync(socket, new { t = "admin_see_all", on = _seeAllFreq }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "chat: failed to re-assert see-all override");
        }
    }

    private ClientWebSocket RequireSocket()
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open || !_connected)
            throw new InvalidOperationException("chat relay is not connected");
        return socket;
    }

    /// <summary>The operator's current friend graph (accepted / incoming / outgoing).</summary>
    public ChatFriendsDto GetFriends() => _friends;

    /// <summary>Sends a friend request to <paramref name="callsign"/>.</summary>
    public Task SendFriendRequestAsync(string callsign, CancellationToken ct) =>
        SendFriendActionAsync("friend_req", "to", callsign, ct);

    /// <summary>Accepts the incoming friend request from <paramref name="callsign"/>.</summary>
    public Task AcceptFriendAsync(string callsign, CancellationToken ct) =>
        SendFriendActionAsync("friend_accept", "from", callsign, ct);

    /// <summary>Declines the incoming friend request from <paramref name="callsign"/>.</summary>
    public Task DenyFriendAsync(string callsign, CancellationToken ct) =>
        SendFriendActionAsync("friend_deny", "from", callsign, ct);

    /// <summary>Removes a friendship and/or cancels a pending request with
    /// <paramref name="callsign"/>.</summary>
    public Task RemoveFriendAsync(string callsign, CancellationToken ct) =>
        SendFriendActionAsync("friend_remove", "callsign", callsign, ct);

    // Common path for the four friend verbs. Validates like SendMessageAsync so
    // the endpoints map empty callsign → 400 and not-connected → 409. Builds the
    // frame as a dictionary so the field name (to/from/callsign) is emitted
    // verbatim (lowercase) rather than camelCased.
    private async Task SendFriendActionAsync(string verb, string field, string callsign, CancellationToken ct)
    {
        var call = (callsign ?? string.Empty).Trim().ToUpperInvariant();
        if (call.Length == 0)
            throw new ArgumentException("callsign is empty", nameof(callsign));

        await SendFrameAsync(RequireSocket(), new Dictionary<string, string> { ["t"] = verb, [field] = call }, ct);
    }

    // ── Background worker ──────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = ReconnectMinDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            // The persisted opt-in owns roster presence. Older builds tied the
            // relay socket to ChatPanel visibility, which made harmless browser
            // timer stalls look like operators connecting/disconnecting.
            if (!_store.GetEnabled())
            {
                await WaitForWakeAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                continue;
            }

            var identity = await TryGetIdentityAsync(stoppingToken);
            if (identity is null)
            {
                // Logged out (or no session) while enabled — surface the reason
                // so the panel can say "Login to QRZ" instead of spinning on
                // "Connecting…", then wait and retry. Push only on transition to
                // avoid spamming a status frame every backoff tick.
                if (_lastError != "QRZ not logged in")
                {
                    _lastError = "QRZ not logged in";
                    PushStatus();
                }
                await WaitForWakeAsync(backoff, stoppingToken);
                continue;
            }

            try
            {
                await RunConnectionAsync(identity.Value.Callsign, identity.Value.SessionKey, stoppingToken);
                backoff = ReconnectMinDelay; // clean cycle resets backoff
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _log.LogWarning(ex, "chat relay connection ended; will retry in {Delay}", backoff);
            }
            finally
            {
                SetDisconnected();
            }

            if (!_store.GetEnabled()) continue; // disabled during the cycle → loop, will idle

            await WaitForWakeAsync(backoff, stoppingToken);
            backoff = NextBackoff(backoff);
        }

        SetDisconnected();
    }

    private async Task<(string Callsign, string SessionKey)?> TryGetIdentityAsync(CancellationToken ct)
    {
        try { return await _qrz.GetChatIdentityAsync(ct); }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "chat: failed to acquire QRZ chat identity");
            return null;
        }
    }

    private async Task RunConnectionAsync(string callsign, string sessionKey, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        // Capture the HTTP status of a rejected upgrade so we can tell a stale
        // QRZ session (403/401) apart from a transient transport failure.
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("X-QRZ-Session", sessionKey);
        socket.Options.SetRequestHeader("X-QRZ-Callsign", callsign);
        if (_relaySecret is not null)
            socket.Options.SetRequestHeader("Authorization", $"Bearer {_relaySecret}");

        _log.LogInformation("chat: connecting to relay {Url} as {Callsign}", _relayUrl, callsign);
        try
        {
            await socket.ConnectAsync(new Uri(_relayUrl), ct);
        }
        catch (WebSocketException) when (
            socket.HttpStatusCode is System.Net.HttpStatusCode.Forbidden
                                  or System.Net.HttpStatusCode.Unauthorized)
        {
            // The relay validates our QRZ session on the upgrade and rejected it
            // (403 = "qrz session invalid", 401 = "qrz auth required"). Our local
            // 1-hour session cache still believes the key is live, so without
            // evicting it the reconnect loop would replay the dead key forever.
            // Drop it so the next attempt re-authenticates with QRZ.
            _log.LogWarning(
                "chat: relay rejected QRZ session ({Status}); forcing re-auth",
                socket.HttpStatusCode);
            await _qrz.InvalidateSessionAsync(ct);
            throw new InvalidOperationException(
                "QRZ session expired — re-authenticating");
        }

        _socket = socket;
        _connected = true;
        _lastError = null;
        _selfCallsign = callsign;
        PushStatus();

        // First frame: hello with current presence.
        var snap = _radio.Snapshot();
        Interlocked.Exchange(ref _lastObservedVfoHz, snap.VfoHz);
        MarkOperatorActivity(DateTimeOffset.UtcNow);
        var grid = TryGetGrid();
        var status = CurrentStatus();
        await SendFrameAsync(socket, new
        {
            t = "hello",
            callsign,
            grid,
            freq = snap.VfoHz,
            mode = snap.Mode.ToString(),
            status,
            freqPublic = _store.GetFreqPublic(),
            client = "zeus",
        }, ct);

        // Seed the throttle so the first idle presence is suppressed (hello
        // already carried it) but a genuine change still fires.
        _presence.Seed(snap.VfoHz, snap.Mode.ToString(), status);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receiveTask = ReceiveLoopAsync(socket, linked.Token);
        var pumpTask = PresenceAndHeartbeatLoopAsync(socket, linked.Token);

        var done = await Task.WhenAny(receiveTask, pumpTask);
        linked.Cancel();
        try { await Task.WhenAll(receiveTask, pumpTask); } catch { /* one side already failed */ }
        await done; // surface its exception, if any
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        var accum = new MemoryStream();
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            accum.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buf, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    throw new WebSocketException("relay closed the connection");
                }
                accum.Write(buf, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;
            var text = Encoding.UTF8.GetString(accum.GetBuffer(), 0, (int)accum.Length);
            HandleRelayFrame(text);
        }
    }

    private async Task PresenceAndHeartbeatLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var nextHeartbeat = DateTimeOffset.UtcNow + HeartbeatInterval;
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            // Short wait so pending presence changes flush near the throttle
            // boundary without a busy loop.
            await WaitForWakeAsync(TimeSpan.FromSeconds(1), ct);

            // If the operator disabled chat, tear the link down so the relay
            // drops them from everyone's roster.
            if (!_store.GetEnabled())
                return;

            var now = DateTimeOffset.UtcNow;
            OfferCurrentPresence(now);
            if (_presence.TryTake(now, out var p))
                await SendFrameAsync(socket, new { t = "presence", freq = p.FreqHz, mode = p.Mode, status = p.Status }, ct);

            if (now >= nextHeartbeat)
            {
                await SendPingAsync(socket, ct);
                nextHeartbeat = now + HeartbeatInterval;
            }
        }
    }

    // ── Relay frame handling ───────────────────────────────────────────────

    private void HandleRelayFrame(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("t", out var tEl)) return;
            switch (tEl.GetString())
            {
                case "welcome":
                    if (root.TryGetProperty("self", out var selfEl))
                        _selfCallsign = ReadString(selfEl, "callsign") ?? _selfCallsign;
                    _isAdmin = root.TryGetProperty("isAdmin", out var adminEl)
                        && adminEl.ValueKind == JsonValueKind.True;
                    UpdateRoster(root, "roster");
                    PushStatus();
                    // Re-assert a sticky "see all" override on (re)connect so a
                    // blip doesn't silently drop it. Only meaningful for admins;
                    // the relay ignores it otherwise. Fire-and-forget — we're on
                    // the receive loop and must not block it.
                    if (_isAdmin && _seeAllFreq)
                        _ = ReassertSeeAllAsync();
                    break;
                case "roster":
                    UpdateRoster(root, "roster");
                    break;
                case "msg":
                    var msg = ParseMessage(root);
                    // The in-memory ring is the public-room snapshot cache only;
                    // group/DM history comes from the relay on demand.
                    if (string.IsNullOrEmpty(msg.Room) || msg.Room == "lobby")
                        _messages.Add(msg);
                    _hub.BroadcastChatEvent(ChatEventFrame.Message(msg));
                    break;
                case "friends":
                    _friends = ParseFriends(root);
                    _hub.BroadcastChatEvent(ChatEventFrame.Friends(_friends));
                    break;
                case "rooms":
                    var rooms = ParseRooms(root);
                    lock (_roomsSync) _rooms = rooms;
                    _hub.BroadcastChatEvent(ChatEventFrame.Rooms(rooms));
                    break;
                case "history":
                    var histRoom = ReadString(root, "room") ?? "lobby";
                    _hub.BroadcastChatEvent(ChatEventFrame.History(histRoom, ParseHistory(root)));
                    break;
                case "banned":
                    _lastError = ReadString(root, "message") ?? "You have been banned from ZeusChat.";
                    _hub.BroadcastChatEvent(ChatEventFrame.Banned(_lastError));
                    PushStatus();
                    break;
                case "cleared":
                    var clearedRoom = ReadString(root, "room") ?? "lobby";
                    // The in-memory ring caches only the public room; group/DM
                    // scrollback lives on the relay, so nothing local to drop.
                    if (clearedRoom == "lobby") _messages.Clear();
                    _hub.BroadcastChatEvent(ChatEventFrame.Cleared(clearedRoom));
                    break;
                case "notice":
                    _hub.BroadcastChatEvent(ChatEventFrame.Notice(
                        ReadString(root, "from") ?? string.Empty,
                        ReadString(root, "text") ?? string.Empty,
                        ReadLong(root, "ts") ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                    break;
                case "bans":
                    _hub.BroadcastChatEvent(ChatEventFrame.Bans(ReadStringArray(root, "bans")));
                    break;
                case "error":
                    var code = ReadString(root, "code");
                    var message = ReadString(root, "message");
                    _lastError = string.IsNullOrEmpty(code) ? message : $"{code}: {message}";
                    _log.LogWarning("chat relay error {Code}: {Message}", code, message);
                    PushStatus();
                    break;
                case "pong":
                    break;
            }
        }
        catch (JsonException ex)
        {
            _log.LogDebug(ex, "chat: failed to parse relay frame");
        }
    }

    private void UpdateRoster(JsonElement root, string property)
    {
        var list = ParseRoster(root, property);
        if (list is null) return;
        lock (_rosterSync) _roster = list;
        _hub.BroadcastChatEvent(ChatEventFrame.Roster(list));
    }

    /// <summary>
    /// Parses a relay <c>{t:"msg",...}</c> frame into a <see cref="ChatMessage"/>.
    /// Tolerant of missing fields (id/ts are synthesised; room defaults to
    /// "lobby"). Internal for unit tests — no network needed.
    /// </summary>
    internal static ChatMessage ParseMessage(JsonElement root) => new(
        Id: ReadString(root, "id") ?? Guid.NewGuid().ToString("N"),
        From: ReadString(root, "from") ?? string.Empty,
        Text: ReadString(root, "text") ?? string.Empty,
        Ts: ReadLong(root, "ts") ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Room: ReadString(root, "room") ?? "lobby",
        Attachment: ParseAttachment(root));

    /// <summary>
    /// Parses an optional <c>attachment</c> object out of a relay message frame.
    /// Returns null when absent, malformed, an unsupported media type, or
    /// oversized — a bad attachment degrades to a plain message rather than
    /// dropping it. Accepts image and audio (voice snippet) kinds, deriving the
    /// <c>kind</c> from the MIME/scheme family so a mislabelled payload can't slip
    /// through. Internal for unit tests.
    /// </summary>
    internal static ChatAttachment? ParseAttachment(JsonElement root)
    {
        if (!root.TryGetProperty("attachment", out var a) || a.ValueKind != JsonValueKind.Object)
            return null;
        var dataUrl = ReadString(a, "dataUrl");
        var mime = ReadString(a, "mime");
        if (string.IsNullOrEmpty(dataUrl) || string.IsNullOrEmpty(mime)) return null;
        var isImage = mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
            dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);
        var isAudio = mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) &&
            dataUrl.StartsWith("data:audio/", StringComparison.OrdinalIgnoreCase);
        if (!isImage && !isAudio) return null;
        if (dataUrl.Length > ChatAttachment.MaxDataUrlLength) return null;
        return new ChatAttachment(
            Kind: isAudio ? "audio" : "image",
            Mime: mime,
            DataUrl: dataUrl,
            Name: ReadString(a, "name"),
            Width: (int?)ReadLong(a, "width"),
            Height: (int?)ReadLong(a, "height"),
            Size: (int?)ReadLong(a, "size"));
    }

    /// <summary>
    /// Parses the <paramref name="property"/> roster array out of a relay
    /// <c>welcome</c>/<c>roster</c> frame. Returns null when the property is
    /// absent or not an array. Internal for unit tests.
    /// </summary>
    internal static IReadOnlyList<ChatOperator>? ParseRoster(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<ChatOperator>(arr.GetArrayLength());
        foreach (var op in arr.EnumerateArray())
        {
            list.Add(new ChatOperator(
                Callsign: ReadString(op, "callsign") ?? string.Empty,
                Grid: ReadString(op, "grid"),
                FreqHz: ReadLong(op, "freq"),
                Mode: ReadString(op, "mode"),
                Status: ReadString(op, "status"),
                Since: ReadLong(op, "since") ?? 0,
                Admin: op.TryGetProperty("admin", out var adminEl) && adminEl.ValueKind == JsonValueKind.True));
        }
        return list;
    }

    /// <summary>
    /// Parses a relay <c>{t:"friends",...}</c> frame into a <see cref="ChatFriendsDto"/>.
    /// Missing arrays default to empty. Internal for unit tests.
    /// </summary>
    internal static ChatFriendsDto ParseFriends(JsonElement root) => new(
        Accepted: ReadStringArray(root, "accepted"),
        Incoming: ReadStringArray(root, "incoming"),
        Outgoing: ReadStringArray(root, "outgoing"));

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray())
        {
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
        }
        return list;
    }

    /// <summary>Parses a relay <c>{t:"rooms",rooms:[...]}</c> frame. Internal for tests.</summary>
    internal static IReadOnlyList<ChatRoomDto> ParseRooms(JsonElement root)
    {
        if (!root.TryGetProperty("rooms", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<ChatRoomDto>();
        var list = new List<ChatRoomDto>(arr.GetArrayLength());
        foreach (var r in arr.EnumerateArray())
        {
            list.Add(new ChatRoomDto(
                Id: ReadString(r, "id") ?? string.Empty,
                Name: ReadString(r, "name") ?? string.Empty,
                Kind: ReadString(r, "kind") ?? "group",
                Members: ReadStringArray(r, "members")));
        }
        return list;
    }

    /// <summary>Parses a relay <c>{t:"history",messages:[...]}</c> frame. Internal for tests.</summary>
    internal static IReadOnlyList<ChatMessage> ParseHistory(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<ChatMessage>();
        var list = new List<ChatMessage>(arr.GetArrayLength());
        foreach (var m in arr.EnumerateArray()) list.Add(ParseMessage(m));
        return list;
    }

    // ── Presence / status helpers ──────────────────────────────────────────

    private void OnRadioStateChanged(StateDto state)
    {
        var previous = Interlocked.Exchange(ref _lastObservedVfoHz, state.VfoHz);
        if (previous != state.VfoHz)
            MarkOperatorActivity(DateTimeOffset.UtcNow);

        if (!_connected) return;
        if (_presence.Offer(state.VfoHz, state.Mode.ToString(), CurrentStatus()))
            Wake();
    }

    private void OnMoxChanged(bool on)
    {
        _moxOn = on;
        MarkOperatorActivity(DateTimeOffset.UtcNow);
        if (!_connected) return;
        var snap = _radio.Snapshot();
        if (_presence.Offer(snap.VfoHz, snap.Mode.ToString(), CurrentStatus()))
            Wake();
    }

    private string CurrentStatus() => CurrentStatus(DateTimeOffset.UtcNow);

    private string CurrentStatus(DateTimeOffset now)
    {
        var ticks = Interlocked.Read(ref _lastOperatorActivityTicks);
        var last = ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : now;
        return ChatPresence.StatusFor(_moxOn, now, last, IdleStatusTimeout);
    }

    private void MarkOperatorActivity(DateTimeOffset now) =>
        Interlocked.Exchange(ref _lastOperatorActivityTicks, now.UtcTicks);

    private void NoteOperatorActivity()
    {
        var now = DateTimeOffset.UtcNow;
        MarkOperatorActivity(now);
        if (_connected && OfferCurrentPresence(now))
            Wake();
    }

    private bool OfferCurrentPresence(DateTimeOffset now)
    {
        var snap = _radio.Snapshot();
        return _presence.Offer(snap.VfoHz, snap.Mode.ToString(), CurrentStatus(now));
    }

    private string? TryGetGrid() => _qrz.GetStatus().Home?.Grid;

    // ── Snapshot / push helpers ────────────────────────────────────────────

    private IReadOnlyList<byte[]> BuildSnapshotFrames()
    {
        IReadOnlyList<ChatOperator> roster;
        lock (_rosterSync) roster = _roster;
        IReadOnlyList<ChatRoomDto> rooms;
        lock (_roomsSync) rooms = _rooms;
        return new[]
        {
            ChatEventFrame.Status(GetStatus()),
            ChatEventFrame.Roster(roster),
            ChatEventFrame.Friends(_friends),
            ChatEventFrame.Rooms(rooms),
            ChatEventFrame.History("lobby", _messages.Snapshot(MessageHistoryCap)),
        };
    }

    private void PushStatus() => _hub.BroadcastChatEvent(ChatEventFrame.Status(GetStatus()));

    private void SetDisconnected()
    {
        _socket = null;
        var wasConnected = _connected;
        _connected = false;
        // Push when we drop a live link, or when there's an error to report:
        // a first-attempt connect failure never set _connected, but the operator
        // still needs to see why (e.g. relay unreachable) rather than "Connecting…".
        _isAdmin = false;
        if (wasConnected || _lastError is not null)
            PushStatus();
        // Relay-sourced state (friend graph + rooms) is dropped so nothing stale
        // lingers while offline. Fresh snapshots arrive on reconnect.
        if (wasConnected)
        {
            _friends = EmptyFriends;
            _hub.BroadcastChatEvent(ChatEventFrame.Friends(_friends));
            lock (_roomsSync) _rooms = Array.Empty<ChatRoomDto>();
            _hub.BroadcastChatEvent(ChatEventFrame.Rooms(Array.Empty<ChatRoomDto>()));
        }
    }

    // ── Low-level frame send ───────────────────────────────────────────────

    private async Task SendFrameAsync(ClientWebSocket socket, object frame, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, ChatEventFrame.JsonOptions);
        await _sendGate.WaitAsync(ct);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally { _sendGate.Release(); }
    }

    // The relay auto-responds to the EXACT string {"t":"ping"} without waking
    // the durable object — send it verbatim, not via the serializer (which
    // might add spacing).
    private async Task SendPingAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes("{\"t\":\"ping\"}");
        await _sendGate.WaitAsync(ct);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally { _sendGate.Release(); }
    }

    // ── Wake / backoff plumbing ────────────────────────────────────────────

    private void Wake()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { /* already pending */ }
    }

    private async Task WaitForWakeAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { await _wake.WaitAsync(timeout, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    }

    private static TimeSpan NextBackoff(TimeSpan current)
    {
        var doubled = TimeSpan.FromTicks(current.Ticks * 2);
        return doubled > ReconnectMaxDelay ? ReconnectMaxDelay : doubled;
    }

    // ── JSON read helpers (tolerant of missing / mistyped fields) ───────────

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? ReadLong(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    }

    public override void Dispose()
    {
        _radio.StateChanged -= OnRadioStateChanged;
        _radio.MoxChanged -= OnMoxChanged;
        _wake.Dispose();
        _sendGate.Dispose();
        base.Dispose();
    }
}
