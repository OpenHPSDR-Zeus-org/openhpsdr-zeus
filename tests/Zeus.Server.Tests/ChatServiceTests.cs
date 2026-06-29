// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

// Backend coverage for ZeusChat: the message ring-buffer cap, the presence
// throttle, the relay-frame JSON (de)serialization (both the inbound relay
// frames and the outbound 0x35 ChatEvent envelopes the frontend consumes), and
// the connect gating (no relay activity when disabled or logged out). All
// network + real QRZ is avoided — the QrzService under test is simply never
// logged in, so GetChatIdentityAsync returns null without any HTTP call.
public class ChatServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "zeus-chat-" + Guid.NewGuid().ToString("N"));

    public ChatServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Message ring-buffer cap ────────────────────────────────────────────

    [Fact]
    public void Ring_KeepsOnlyMostRecent_WhenOverCap()
    {
        var ring = new ChatMessageRing(200);
        for (int i = 0; i < 250; i++) ring.Add(Msg(i));

        Assert.Equal(200, ring.Count);
        var all = ring.Snapshot(0); // 0 = all
        Assert.Equal(200, all.Count);
        // Oldest 50 evicted; the buffer holds 50..249 in order.
        Assert.Equal("m50", all[0].Id);
        Assert.Equal("m249", all[^1].Id);
    }

    [Fact]
    public void Ring_Snapshot_RespectsLimit_AndReturnsNewest()
    {
        var ring = new ChatMessageRing(200);
        for (int i = 0; i < 10; i++) ring.Add(Msg(i));

        var last3 = ring.Snapshot(3);
        Assert.Equal(3, last3.Count);
        Assert.Equal("m7", last3[0].Id);
        Assert.Equal("m9", last3[^1].Id);
    }

    // ── Presence throttle ──────────────────────────────────────────────────

    [Fact]
    public void Throttle_SuppressesDuplicates_AndCoalescesWithinWindow()
    {
        var t = new PresenceThrottle(TimeSpan.FromSeconds(2));
        var t0 = DateTimeOffset.UnixEpoch;

        // No-op offer of the same value as a fresh (unseeded) throttle still
        // counts as a change the first time.
        Assert.True(t.Offer(14_000_000, "USB", "rx"));
        // First take flushes immediately (lastSentAt = MinValue).
        Assert.True(t.TryTake(t0, out var p1));
        Assert.Equal(14_000_000, p1.FreqHz);

        // Re-offering the identical presence is a no-op.
        Assert.False(t.Offer(14_000_000, "USB", "rx"));

        // A change within the window is recorded but held back until the
        // window elapses.
        Assert.True(t.Offer(14_001_000, "USB", "rx"));
        Assert.False(t.TryTake(t0 + TimeSpan.FromSeconds(1), out _)); // 1s < 2s window
        Assert.True(t.TryTake(t0 + TimeSpan.FromSeconds(2), out var p2));
        Assert.Equal(14_001_000, p2.FreqHz);
    }

    [Fact]
    public void Throttle_Seed_SuppressesEqualPresence_ButAllowsImmediateChange()
    {
        var t = new PresenceThrottle(TimeSpan.FromSeconds(2));
        t.Seed(7_000_000, "LSB", "rx");

        // Same as seeded → no change to wake on.
        Assert.False(t.Offer(7_000_000, "LSB", "rx"));

        // A genuine change after seeding flushes immediately (seed left
        // lastSentAt at MinValue so the window has already elapsed).
        Assert.True(t.Offer(7_000_000, "LSB", "tx"));
        Assert.True(t.TryTake(DateTimeOffset.UnixEpoch, out var p));
        Assert.Equal("tx", p.Status);
    }

    // ── Relay-frame parsing (inbound) ──────────────────────────────────────

    [Fact]
    public void ParseMessage_ReadsAllFields()
    {
        using var doc = JsonDocument.Parse(
            """{"t":"msg","id":"abc","from":"N9WAR","text":"hi there","ts":1700000000000,"room":"lobby"}""");
        var msg = ChatService.ParseMessage(doc.RootElement);

        Assert.Equal("abc", msg.Id);
        Assert.Equal("N9WAR", msg.From);
        Assert.Equal("hi there", msg.Text);
        Assert.Equal(1700000000000, msg.Ts);
        Assert.Equal("lobby", msg.Room);
    }

    [Fact]
    public void ParseMessage_ToleratesMissingFields()
    {
        using var doc = JsonDocument.Parse("""{"t":"msg","from":"EI6LF","text":"hello"}""");
        var msg = ChatService.ParseMessage(doc.RootElement);

        Assert.False(string.IsNullOrEmpty(msg.Id)); // synthesised
        Assert.Equal("EI6LF", msg.From);
        Assert.Equal("hello", msg.Text);
        Assert.True(msg.Ts > 0); // synthesised
        Assert.Equal("lobby", msg.Room); // defaulted
        Assert.Null(msg.Attachment); // plain message has no attachment
    }

    [Fact]
    public void ParseMessage_ReadsImageAttachment()
    {
        using var doc = JsonDocument.Parse(
            """{"t":"msg","from":"N9WAR","text":"look","room":"lobby","attachment":{"kind":"image","mime":"image/jpeg","dataUrl":"data:image/jpeg;base64,/9j/AA==","name":"rig.jpg","width":1024,"height":768,"size":4096}}""");
        var msg = ChatService.ParseMessage(doc.RootElement);

        Assert.NotNull(msg.Attachment);
        Assert.Equal("image", msg.Attachment!.Kind);
        Assert.Equal("image/jpeg", msg.Attachment.Mime);
        Assert.StartsWith("data:image/jpeg;base64,", msg.Attachment.DataUrl);
        Assert.Equal("rig.jpg", msg.Attachment.Name);
        Assert.Equal(1024, msg.Attachment.Width);
        Assert.Equal(768, msg.Attachment.Height);
        Assert.Equal("look", msg.Text); // text rides alongside the image (caption)
    }

    [Fact]
    public void ParseMessage_ReadsAudioAttachment()
    {
        using var doc = JsonDocument.Parse(
            """{"t":"msg","from":"N9WAR","text":"","room":"lobby","attachment":{"kind":"audio","mime":"audio/webm","dataUrl":"data:audio/webm;base64,GkXfAA==","name":"voice-message.webm","size":2048}}""");
        var msg = ChatService.ParseMessage(doc.RootElement);

        Assert.NotNull(msg.Attachment);
        Assert.Equal("audio", msg.Attachment!.Kind);
        Assert.Equal("audio/webm", msg.Attachment.Mime);
        Assert.StartsWith("data:audio/webm;base64,", msg.Attachment.DataUrl);
        Assert.Equal("voice-message.webm", msg.Attachment.Name);
    }

    [Fact]
    public void ParseAttachment_DropsNonImageOrOversize()
    {
        // Non-image / non-audio data URL → dropped (message degrades to text-only).
        using var bad = JsonDocument.Parse(
            """{"t":"msg","from":"N9WAR","text":"x","attachment":{"kind":"image","mime":"application/pdf","dataUrl":"data:application/pdf;base64,AAAA"}}""");
        Assert.Null(ChatService.ParseMessage(bad.RootElement).Attachment);

        // Mismatched family (audio MIME but image data scheme) → dropped.
        using var mismatch = JsonDocument.Parse(
            """{"t":"msg","from":"N9WAR","text":"x","attachment":{"kind":"audio","mime":"audio/webm","dataUrl":"data:image/png;base64,AAAA"}}""");
        Assert.Null(ChatService.ParseMessage(mismatch.RootElement).Attachment);

        // Oversized data URL → dropped.
        var huge = new string('A', ChatAttachment.MaxDataUrlLength + 1);
        var bigJson =
            "{\"t\":\"msg\",\"from\":\"N9WAR\",\"attachment\":{\"kind\":\"image\"," +
            "\"mime\":\"image/png\",\"dataUrl\":\"data:image/png;base64," + huge + "\"}}";
        using var big = JsonDocument.Parse(bigJson);
        Assert.Null(ChatService.ParseMessage(big.RootElement).Attachment);
    }

    [Fact]
    public void ParseRoster_ReadsOperators_AndUsesFreqAsHz()
    {
        using var doc = JsonDocument.Parse(
            """{"t":"roster","roster":[{"callsign":"N9WAR","grid":"EL96","freq":14250000,"mode":"USB","status":"rx","since":1700000000000},{"callsign":"EI6LF","status":"tx","since":1700000001000}]}""");

        var roster = ChatService.ParseRoster(doc.RootElement, "roster");
        Assert.NotNull(roster);
        Assert.Equal(2, roster!.Count);

        Assert.Equal("N9WAR", roster[0].Callsign);
        Assert.Equal("EL96", roster[0].Grid);
        Assert.Equal(14250000, roster[0].FreqHz);
        Assert.Equal("USB", roster[0].Mode);
        Assert.Equal("rx", roster[0].Status);
        Assert.Equal(1700000000000, roster[0].Since);

        Assert.Equal("EI6LF", roster[1].Callsign);
        Assert.Null(roster[1].Grid);
        Assert.Null(roster[1].FreqHz);
        Assert.Equal("tx", roster[1].Status);
    }

    [Fact]
    public void ParseRoster_ReadsAdminFlag_DefaultingFalse()
    {
        using var doc = JsonDocument.Parse(
            """{"t":"roster","roster":[{"callsign":"N9WAR","admin":true,"since":1},{"callsign":"EI6LF","since":2}]}""");

        var roster = ChatService.ParseRoster(doc.RootElement, "roster");
        Assert.NotNull(roster);
        Assert.True(roster![0].Admin);  // explicit admin:true
        Assert.False(roster[1].Admin);  // absent → default false
    }

    [Fact]
    public void ParseRoster_ReturnsNull_WhenPropertyMissingOrNotArray()
    {
        using var doc = JsonDocument.Parse("""{"t":"welcome"}""");
        Assert.Null(ChatService.ParseRoster(doc.RootElement, "roster"));
    }

    [Fact]
    public void ParseFriends_ReadsArrays_AndDefaultsMissingToEmpty()
    {
        using var doc = JsonDocument.Parse(
            """{"t":"friends","accepted":["N9WAR","EI6LF"],"incoming":["KB2UKA"]}""");
        var f = ChatService.ParseFriends(doc.RootElement);

        Assert.Equal(new[] { "N9WAR", "EI6LF" }, f.Accepted);
        Assert.Equal(new[] { "KB2UKA" }, f.Incoming);
        Assert.Empty(f.Outgoing); // missing array → empty, not null
    }

    // ── ChatEvent (0x35) envelope serialization (outbound, frontend wire) ───

    [Fact]
    public void ChatEvent_StatusEnvelope_IsCamelCaseWithKind()
    {
        var status = new ChatStatusDto(
            Enabled: true, Connected: false, Callsign: "N9WAR",
            RelayUrl: "wss://relay/chat", Error: null);

        var frame = ChatEventFrame.Status(status);
        Assert.Equal((byte)MsgType.ChatEvent, frame[0]);

        using var doc = JsonDocument.Parse(ChatEventFrame.Payload(frame).ToArray());
        var root = doc.RootElement;
        Assert.Equal("status", root.GetProperty("kind").GetString());
        var s = root.GetProperty("status");
        Assert.True(s.GetProperty("enabled").GetBoolean());
        Assert.False(s.GetProperty("connected").GetBoolean());
        Assert.Equal("N9WAR", s.GetProperty("callsign").GetString());
        Assert.Equal("wss://relay/chat", s.GetProperty("relayUrl").GetString());
        // Null error is omitted (WhenWritingNull).
        Assert.False(s.TryGetProperty("error", out _));
    }

    [Fact]
    public void ChatEvent_MessageEnvelope_RoundTripsFields()
    {
        var msg = new ChatMessage("id1", "EI6LF", "hi", 1700000000000, "lobby");
        var frame = ChatEventFrame.Message(msg);

        using var doc = JsonDocument.Parse(ChatEventFrame.Payload(frame).ToArray());
        var root = doc.RootElement;
        Assert.Equal("message", root.GetProperty("kind").GetString());
        var m = root.GetProperty("message");
        Assert.Equal("id1", m.GetProperty("id").GetString());
        Assert.Equal("EI6LF", m.GetProperty("from").GetString());
        Assert.Equal("hi", m.GetProperty("text").GetString());
        Assert.Equal(1700000000000, m.GetProperty("ts").GetInt64());
        Assert.Equal("lobby", m.GetProperty("room").GetString());
    }

    [Fact]
    public void ChatEvent_RosterAndHistoryEnvelopes_UseCamelCaseArrays()
    {
        var op = new ChatOperator("N9WAR", "EL96", 14_250_000, "USB", "rx", 1700000000000);
        var rosterFrame = ChatEventFrame.Roster(new[] { op });
        using (var doc = JsonDocument.Parse(ChatEventFrame.Payload(rosterFrame).ToArray()))
        {
            var root = doc.RootElement;
            Assert.Equal("roster", root.GetProperty("kind").GetString());
            var first = root.GetProperty("roster")[0];
            Assert.Equal("N9WAR", first.GetProperty("callsign").GetString());
            Assert.Equal(14_250_000, first.GetProperty("freqHz").GetInt64());
        }

        var msg = new ChatMessage("id1", "EI6LF", "hi", 1700000000000, "lobby");
        var historyFrame = ChatEventFrame.History("lobby", new[] { msg });
        using (var doc = JsonDocument.Parse(ChatEventFrame.Payload(historyFrame).ToArray()))
        {
            var root = doc.RootElement;
            Assert.Equal("history", root.GetProperty("kind").GetString());
            Assert.Equal("id1", root.GetProperty("messages")[0].GetProperty("id").GetString());
        }
    }

    [Fact]
    public void ChatEvent_FriendsEnvelope_UsesCamelCaseArrays()
    {
        var friends = new ChatFriendsDto(
            Accepted: new[] { "N9WAR" }, Incoming: new[] { "EI6LF" }, Outgoing: Array.Empty<string>());
        var frame = ChatEventFrame.Friends(friends);

        using var doc = JsonDocument.Parse(ChatEventFrame.Payload(frame).ToArray());
        var root = doc.RootElement;
        Assert.Equal("friends", root.GetProperty("kind").GetString());
        var f = root.GetProperty("friends");
        Assert.Equal("N9WAR", f.GetProperty("accepted")[0].GetString());
        Assert.Equal("EI6LF", f.GetProperty("incoming")[0].GetString());
        Assert.Empty(f.GetProperty("outgoing").EnumerateArray());
    }

    [Fact]
    public void ChatEventPayload_Throws_OnWrongPrefix()
    {
        Assert.Throws<InvalidDataException>(() => ChatEventFrame.Payload(new byte[] { 0x00, 0x01 }));
    }

    // ── Gating: no relay activity when disabled or logged out ──────────────

    [Fact]
    public async Task Disabled_ByDefault_AndNotConnected_NoSendPossible()
    {
        using var chat = BuildChat();

        var status = chat.GetStatus();
        Assert.False(status.Enabled);   // opt-in default OFF
        Assert.False(status.Connected); // worker never ran a connection
        Assert.False(status.SeeAllFreq); // admin see-all override default OFF
        Assert.Equal(ChatService.DefaultRelayUrl, status.RelayUrl);

        // Send must fail with 409-mapped exception when not connected.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => chat.SendMessageAsync("hello", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task SetSeeAllFreq_ReflectsInStatus_WhenDisconnected()
    {
        using var chat = BuildChat();

        // Toggling the override while offline persists the session flag and shows
        // in status (no relay frame is sent — there's no socket), so the admin
        // console reflects their choice immediately.
        await chat.SetSeeAllFreqAsync(true, CancellationToken.None);
        Assert.True(chat.GetStatus().SeeAllFreq);

        await chat.SetSeeAllFreqAsync(false, CancellationToken.None);
        Assert.False(chat.GetStatus().SeeAllFreq);
    }

    [Fact]
    public async Task Enable_WhenLoggedOut_PersistsOptIn_ButCannotSend()
    {
        using var store = NewEnabledStore();
        using var chat = BuildChat(store);

        var status = chat.SetEnabled(true);
        Assert.True(status.Enabled);
        Assert.False(status.Connected); // QRZ logged out → no relay connection

        // Persisted across a fresh store instance.
        Assert.True(store.GetEnabled());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => chat.SendMessageAsync("hi", null, null, CancellationToken.None));

        chat.SetEnabled(false);
        Assert.False(store.GetEnabled());
    }

    [Fact]
    public async Task EmptySend_Throws_ArgumentException()
    {
        using var chat = BuildChat();
        await Assert.ThrowsAsync<ArgumentException>(
            () => chat.SendMessageAsync("   ", null, null, CancellationToken.None));
    }

    [Fact]
    public void Friends_DefaultEmpty_WhenNeverConnected()
    {
        using var chat = BuildChat();
        var f = chat.GetFriends();
        Assert.Empty(f.Accepted);
        Assert.Empty(f.Incoming);
        Assert.Empty(f.Outgoing);
    }

    [Fact]
    public async Task FriendRequest_WhenNotConnected_Throws()
    {
        using var chat = BuildChat();
        // Not connected → 409-mapped exception, like SendMessageAsync.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => chat.SendFriendRequestAsync("EI6LF", CancellationToken.None));
        // Empty callsign → 400-mapped exception.
        await Assert.ThrowsAsync<ArgumentException>(
            () => chat.AcceptFriendAsync("  ", CancellationToken.None));
    }

    // ── QRZ session self-heal (relay 403/401 → drop the cached key) ──────────

    // The chat path hands the relay a QRZ session key that the local cache
    // optimistically trusts for an hour. When the relay rejects the upgrade
    // (the key died QRZ-side before that hour elapsed), ChatService calls
    // InvalidateSessionAsync so the *next* attempt re-authenticates instead of
    // replaying the dead key into a 403 loop. This proves that contract.
    [Fact]
    public async Task InvalidateSessionAsync_forces_fresh_login_on_next_identity()
    {
        var handler = new StubQrzHandler();
        var qrz = new QrzService(
            new StubFactory(handler), NullLogger<QrzService>.Instance,
            new CredentialStore(NullLogger<CredentialStore>.Instance, Path.Combine(_root, "creds2.db")));

        var status = await qrz.LoginAsync("N9WAR", "pw", CancellationToken.None);
        Assert.True(status.Connected);
        Assert.Equal(1, handler.LoginCount);

        // Cached: a second identity fetch must not re-hit the QRZ login endpoint.
        var first = await qrz.GetChatIdentityAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal("N9WAR", first!.Value.Callsign);
        Assert.Equal("SESSION123", first.Value.SessionKey);
        Assert.Equal(1, handler.LoginCount);

        // Relay rejected the key → evict it; the next fetch re-authenticates.
        await qrz.InvalidateSessionAsync(CancellationToken.None);
        var second = await qrz.GetChatIdentityAsync(CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal("SESSION123", second!.Value.SessionKey);
        Assert.Equal(2, handler.LoginCount);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static ChatMessage Msg(int i) =>
        new($"m{i}", "N9WAR", $"text {i}", 1700000000000 + i, "lobby");

    private ChatEnabledStore NewEnabledStore() =>
        new(NullLogger<ChatEnabledStore>.Instance, Path.Combine(_root, "chat.db"));

    private QrzService NewLoggedOutQrz() =>
        new(new SingleClientFactory(), NullLogger<QrzService>.Instance,
            new CredentialStore(NullLogger<CredentialStore>.Instance, Path.Combine(_root, "creds.db")));

    private RadioService NewRadio() =>
        new(NullLoggerFactory.Instance,
            new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, Path.Combine(_root, "dsp.db")),
            new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, Path.Combine(_root, "pa.db")));

    private ChatService BuildChat(ChatEnabledStore? store = null) =>
        new(NewLoggedOutQrz(), NewRadio(),
            new StreamingHub(NullLogger<StreamingHub>.Instance),
            store ?? NewEnabledStore(),
            NullLogger<ChatService>.Instance);

    // Minimal IHttpClientFactory so QrzService constructs without DI. It is
    // never exercised — the QRZ service is never logged in, so no HTTP flows.
    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // Canned QRZ XML so the self-heal test can drive a real login → cache →
    // re-login cycle without touching the network. Login requests (those with a
    // username=) are counted; lookups return a minimal home-callsign record.
    private sealed class StubQrzHandler : HttpMessageHandler
    {
        public int LoginCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.Query;
            string xml;
            if (query.Contains("username=", StringComparison.Ordinal))
            {
                LoginCount++;
                xml = "<?xml version=\"1.0\" encoding=\"utf-8\" ?>"
                    + "<QRZDatabase version=\"1.36\" xmlns=\"http://xmldata.qrz.com\">"
                    + "<Session><Key>SESSION123</Key>"
                    + "<SubExp>Wed Dec 31 23:59:59 2031</SubExp></Session></QRZDatabase>";
            }
            else
            {
                xml = "<?xml version=\"1.0\" encoding=\"utf-8\" ?>"
                    + "<QRZDatabase version=\"1.36\" xmlns=\"http://xmldata.qrz.com\">"
                    + "<Session><Key>SESSION123</Key></Session>"
                    + "<Callsign><call>N9WAR</call><fname>Test</fname></Callsign></QRZDatabase>";
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(xml),
            });
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
