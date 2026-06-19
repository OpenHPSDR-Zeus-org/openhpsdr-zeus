// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

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
    public void ParseRoster_ReturnsNull_WhenPropertyMissingOrNotArray()
    {
        using var doc = JsonDocument.Parse("""{"t":"welcome"}""");
        Assert.Null(ChatService.ParseRoster(doc.RootElement, "roster"));
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
        var historyFrame = ChatEventFrame.History(new[] { msg });
        using (var doc = JsonDocument.Parse(ChatEventFrame.Payload(historyFrame).ToArray()))
        {
            var root = doc.RootElement;
            Assert.Equal("history", root.GetProperty("kind").GetString());
            Assert.Equal("id1", root.GetProperty("messages")[0].GetProperty("id").GetString());
        }
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
        Assert.Equal(ChatService.DefaultRelayUrl, status.RelayUrl);

        // Send must fail with 409-mapped exception when not connected.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => chat.SendMessageAsync("hello", CancellationToken.None));
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
            () => chat.SendMessageAsync("hi", CancellationToken.None));

        chat.SetEnabled(false);
        Assert.False(store.GetEnabled());
    }

    [Fact]
    public async Task EmptySend_Throws_ArgumentException()
    {
        using var chat = BuildChat();
        await Assert.ThrowsAsync<ArgumentException>(
            () => chat.SendMessageAsync("   ", CancellationToken.None));
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static ChatMessage Msg(int i) =>
        new($"m{i}", "N9WAR", $"text {i}", 1700000000000 + i, "lobby");

    private ChatEnabledStore NewEnabledStore() =>
        new(NullLogger<ChatEnabledStore>.Instance, Path.Combine(_root, "chat.db"));

    private QrzService NewLoggedOutQrz() =>
        new(new SingleClientFactory(), NullLogger<QrzService>.Instance,
            new CredentialStore(NullLogger<CredentialStore>.Instance, _root));

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
}
