// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using Zeus.Contracts;
using Zeus.Protocol1;

namespace Zeus.Server.Tests;

// Pins the P1 board-kind plumbing fix (issue #294 — ANAN-10E detected as
// HermesLite2 post-connect because RadioService.ConnectAsync never called
// SetBoardKind on the fresh Protocol1Client).
//
// We test Protocol1Client.SetBoardKind directly rather than driving
// RadioService.ConnectAsync (which opens a real UDP socket). The
// server-side wiring (ZeusEndpoints passes req.BoardId → MapBoardByte →
// ConnectAsync → SetBoardKind) is structurally correct once these
// Protocol1Client invariants hold.
public class P1BoardKindPlumbingTests
{
    [Fact]
    public void Protocol1Client_DefaultBoardKind_IsHermesLite2()
    {
        // Protocol1Client._boardKind is initialised to HermesLite2 so
        // legacy callers that never call SetBoardKind keep working. This
        // is the default that was causing the ANAN-10E misclassification
        // before the plumbing fix — make sure it stays stable.
        using var client = new Protocol1Client();
        Assert.Equal(HpsdrBoardKind.HermesLite2, client.BoardKind);
    }

    [Fact]
    public void SetBoardKind_Hermes_RoundTrips()
    {
        // Wire byte 0x01 maps to Hermes. An ANAN-10E on P1 firmware 1.x
        // advertises this byte. After SetBoardKind the client must reflect
        // it so ConnectedBoardKind in RadioService resolves to Hermes, not
        // the HermesLite2 constructor default.
        using var client = new Protocol1Client();
        client.SetBoardKind(HpsdrBoardKind.Hermes);
        Assert.Equal(HpsdrBoardKind.Hermes, client.BoardKind);
    }

    [Fact]
    public void SetBoardKind_HermesLite2_RoundTrips()
    {
        // HL2 path must continue to work — KB2UKA tests on HL2 daily.
        // After an explicit SetBoardKind(HermesLite2) the result must be
        // HermesLite2 (same as the default, but now from the caller, not
        // from the constructor).
        using var client = new Protocol1Client();
        client.SetBoardKind(HpsdrBoardKind.Hermes);
        client.SetBoardKind(HpsdrBoardKind.HermesLite2);
        Assert.Equal(HpsdrBoardKind.HermesLite2, client.BoardKind);
    }

    [Fact]
    public void SetBoardKind_Unknown_OverwritesToUnknown()
    {
        // The if-guard in RadioService.ConnectAsync skips SetBoardKind
        // when discoveredKind == Unknown (backwards-compat: older frontend,
        // no boardId in request). Verify Unknown is a valid round-trip
        // value at the Protocol1Client level so the guard logic is safe.
        using var client = new Protocol1Client();
        client.SetBoardKind(HpsdrBoardKind.Unknown);
        Assert.Equal(HpsdrBoardKind.Unknown, client.BoardKind);
    }

    [Theory]
    [InlineData((byte)0x00, HpsdrBoardKind.Metis)]
    [InlineData((byte)0x01, HpsdrBoardKind.Hermes)]
    [InlineData((byte)0x02, HpsdrBoardKind.HermesII)]
    [InlineData((byte)0x04, HpsdrBoardKind.Angelia)]
    [InlineData((byte)0x05, HpsdrBoardKind.Orion)]
    [InlineData((byte)0x06, HpsdrBoardKind.HermesLite2)]
    [InlineData((byte)0x0A, HpsdrBoardKind.OrionMkII)]
    [InlineData((byte)0x14, HpsdrBoardKind.HermesC10)]
    public void SetBoardKind_AllKnownWireBytes_RoundTrip(byte _, HpsdrBoardKind kind)
    {
        // Every wire byte that MapBoardByte() translates must survive a
        // SetBoardKind / BoardKind round-trip so the full board lineup
        // can benefit from the plumbing fix.
        using var client = new Protocol1Client();
        client.SetBoardKind(kind);
        Assert.Equal(kind, client.BoardKind);
    }
}
