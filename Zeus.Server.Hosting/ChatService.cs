// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
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
/// only while chat is enabled (persisted opt-in, default OFF) AND QRZ is logged
/// in (the relay validates the QRZ session on the WS upgrade). Mirrors radio
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
    private static readonly TimeSpan ReconnectMinDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconnectMaxDelay = TimeSpan.FromSeconds(30);

    private readonly QrzService _qrz;
    private readonly RadioService _radio;
    private readonly StreamingHub _hub;
    private readonly ChatEnabledStore _store;
    private readonly ILogger<ChatService> _log;

    private readonly string _relayUrl;
    private readonly string? _relaySecret;

    private readonly ChatMessageRing _messages = new(MessageHistoryCap);
    private readonly object _rosterSync = new();
    private IReadOnlyList<ChatOperator> _roster = Array.Empty<ChatOperator>();

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
    }

    // ── Public API surface (used by ZeusEndpoints) ─────────────────────────

    public bool Enabled => _store.GetEnabled();

    public ChatStatusDto GetStatus() => new(
        Enabled: _store.GetEnabled(),
        Connected: _connected,
        Callsign: _selfCallsign,
        RelayUrl: _relayUrl,
        Error: _lastError);

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
    public async Task SendMessageAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("message text is empty", nameof(text));

        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open || !_connected)
            throw new InvalidOperationException("chat relay is not connected");

        await SendFrameAsync(socket, new { t = "msg", text }, ct);
    }

    // ── Background worker ──────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = ReconnectMinDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_store.GetEnabled())
            {
                await WaitForWakeAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                continue;
            }

            var identity = await TryGetIdentityAsync(stoppingToken);
            if (identity is null)
            {
                // Logged out (or no session) while enabled — wait and retry.
                _lastError = "QRZ not logged in";
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
        socket.Options.SetRequestHeader("X-QRZ-Session", sessionKey);
        socket.Options.SetRequestHeader("X-QRZ-Callsign", callsign);
        if (_relaySecret is not null)
            socket.Options.SetRequestHeader("Authorization", $"Bearer {_relaySecret}");

        _log.LogInformation("chat: connecting to relay {Url} as {Callsign}", _relayUrl, callsign);
        await socket.ConnectAsync(new Uri(_relayUrl), ct);

        _socket = socket;
        _connected = true;
        _lastError = null;
        _selfCallsign = callsign;
        PushStatus();

        // First frame: hello with current presence.
        var snap = _radio.Snapshot();
        var grid = TryGetGrid();
        await SendFrameAsync(socket, new
        {
            t = "hello",
            callsign,
            grid,
            freq = snap.VfoHz,
            mode = snap.Mode.ToString(),
            status = CurrentStatus(),
            client = "zeus",
        }, ct);

        // Seed the throttle so the first idle presence is suppressed (hello
        // already carried it) but a genuine change still fires.
        _presence.Seed(snap.VfoHz, snap.Mode.ToString(), CurrentStatus());

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

            var now = DateTimeOffset.UtcNow;
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
                    UpdateRoster(root, "roster");
                    PushStatus();
                    break;
                case "roster":
                    UpdateRoster(root, "roster");
                    break;
                case "msg":
                    var msg = ParseMessage(root);
                    _messages.Add(msg);
                    _hub.BroadcastChatEvent(ChatEventFrame.Message(msg));
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
        Room: ReadString(root, "room") ?? "lobby");

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
                Since: ReadLong(op, "since") ?? 0));
        }
        return list;
    }

    // ── Presence / status helpers ──────────────────────────────────────────

    private void OnRadioStateChanged(StateDto state)
    {
        if (!_connected) return;
        if (_presence.Offer(state.VfoHz, state.Mode.ToString(), CurrentStatus()))
            Wake();
    }

    private void OnMoxChanged(bool on)
    {
        _moxOn = on;
        if (!_connected) return;
        var snap = _radio.Snapshot();
        if (_presence.Offer(snap.VfoHz, snap.Mode.ToString(), CurrentStatus()))
            Wake();
    }

    private string CurrentStatus() => _moxOn ? "tx" : "rx";

    private string? TryGetGrid() => _qrz.GetStatus().Home?.Grid;

    // ── Snapshot / push helpers ────────────────────────────────────────────

    private IReadOnlyList<byte[]> BuildSnapshotFrames()
    {
        IReadOnlyList<ChatOperator> roster;
        lock (_rosterSync) roster = _roster;
        return new[]
        {
            ChatEventFrame.Status(GetStatus()),
            ChatEventFrame.Roster(roster),
            ChatEventFrame.History(_messages.Snapshot(MessageHistoryCap)),
        };
    }

    private void PushStatus() => _hub.BroadcastChatEvent(ChatEventFrame.Status(GetStatus()));

    private void SetDisconnected()
    {
        _socket = null;
        if (_connected)
        {
            _connected = false;
            PushStatus();
        }
        else
        {
            _connected = false;
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
