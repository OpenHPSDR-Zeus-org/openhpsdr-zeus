// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeus.Contracts;

/// <summary>
/// Wire frame for ZeusChat events (MsgType.ChatEvent, 0x35). The payload is a
/// single byte type prefix followed by a UTF-8 JSON envelope discriminated by
/// a camelCase <c>kind</c> field:
///
/// <code>
/// {"kind":"status","status":{...ChatStatusDto...}}
/// {"kind":"roster","roster":[{...ChatOperator...}, ...]}
/// {"kind":"message","message":{...ChatMessage...}}
/// {"kind":"history","room":"lobby","messages":[{...ChatMessage...}, ...]}
/// {"kind":"friends","friends":{...ChatFriendsDto...}}
/// {"kind":"rooms","rooms":[{...ChatRoomDto...}, ...]}
/// {"kind":"banned","message":"..."}
/// </code>
///
/// JSON (rather than fixed binary) because the payload is small, low-rate, and
/// the shapes are richer than the other 0x3x control frames. Clients ignore
/// unknown kinds so additions stay backward compatible.
/// </summary>
public static class ChatEventFrame
{
    /// <summary>camelCase, no indentation — matches the project's web JSON
    /// convention (JsonSerializerDefaults.Web).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Encodes a status envelope into a 0x35 frame ([type][UTF-8 JSON]).</summary>
    public static byte[] Status(ChatStatusDto status) =>
        Encode(new StatusEnvelope(status));

    /// <summary>Encodes a roster envelope into a 0x35 frame.</summary>
    public static byte[] Roster(IReadOnlyList<ChatOperator> roster) =>
        Encode(new RosterEnvelope(roster));

    /// <summary>Encodes a single-message envelope into a 0x35 frame.</summary>
    public static byte[] Message(ChatMessage message) =>
        Encode(new MessageEnvelope(message));

    /// <summary>Encodes a history (message list) envelope for a room into a 0x35 frame.</summary>
    public static byte[] History(string room, IReadOnlyList<ChatMessage> messages) =>
        Encode(new HistoryEnvelope(room, messages));

    /// <summary>Encodes a friend-graph envelope into a 0x35 frame.</summary>
    public static byte[] Friends(ChatFriendsDto friends) =>
        Encode(new FriendsEnvelope(friends));

    /// <summary>Encodes the operator's visible-rooms list into a 0x35 frame.</summary>
    public static byte[] Rooms(IReadOnlyList<ChatRoomDto> rooms) =>
        Encode(new RoomsEnvelope(rooms));

    /// <summary>Encodes a ban/kick notice into a 0x35 frame.</summary>
    public static byte[] Banned(string message) =>
        Encode(new BannedEnvelope(message));

    /// <summary>Serialises <paramref name="envelope"/> to UTF-8 JSON and
    /// prefixes the ChatEvent type byte.</summary>
    public static byte[] Encode<T>(T envelope)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        var frame = new byte[1 + json.Length];
        frame[0] = (byte)MsgType.ChatEvent;
        json.CopyTo(frame, 1);
        return frame;
    }

    /// <summary>Returns the UTF-8 JSON payload of a 0x35 frame (the byte[] with
    /// the type prefix stripped), or throws if the prefix is wrong. Provided
    /// for tests / decoders.</summary>
    public static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 1)
            throw new InvalidDataException("ChatEvent frame is empty");
        if (frame[0] != (byte)MsgType.ChatEvent)
            throw new InvalidDataException(
                $"expected ChatEvent (0x{(byte)MsgType.ChatEvent:X2}), got 0x{frame[0]:X2}");
        return frame.Slice(1);
    }

    // Envelope records. The `kind` literal is emitted via the property default;
    // System.Text.Json serialises it like any other property (camelCase "kind").
    public sealed record StatusEnvelope(ChatStatusDto Status)
    {
        public string Kind => "status";
    }

    public sealed record RosterEnvelope(IReadOnlyList<ChatOperator> Roster)
    {
        public string Kind => "roster";
    }

    public sealed record MessageEnvelope(ChatMessage Message)
    {
        public string Kind => "message";
    }

    public sealed record HistoryEnvelope(string Room, IReadOnlyList<ChatMessage> Messages)
    {
        public string Kind => "history";
    }

    public sealed record FriendsEnvelope(ChatFriendsDto Friends)
    {
        public string Kind => "friends";
    }

    public sealed record RoomsEnvelope(IReadOnlyList<ChatRoomDto> Rooms)
    {
        public string Kind => "rooms";
    }

    public sealed record BannedEnvelope(string Message)
    {
        public string Kind => "banned";
    }
}
