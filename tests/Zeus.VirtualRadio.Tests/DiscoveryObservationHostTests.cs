// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using Zeus.Contracts;
using Zeus.Protocol1.Discovery;
using Zeus.VirtualRadio;
using Zeus.VirtualRadio.Discovery;
using Zeus.VirtualRadio.Host;
using Zeus.VirtualRadio.Observation;

namespace Zeus.VirtualRadio.Tests;

/// <summary>
/// Socketless unit tests for the discovery reply, observation surface, and host
/// CLI. The discovery tests are the anti-drift guarantee: the emulator's reply
/// is fed straight through the real <see cref="ReplyParser"/> and must round-trip
/// back to the same board kind / MAC / status.
/// </summary>
public class DiscoveryReplyTests
{
    private static VirtualRadioProfile HermesII() =>
        VirtualRadioProfile.Create(HpsdrBoardKind.HermesII, ProtocolVersion.P1);

    [Fact]
    public void Build_RoundTripsThroughReplyParser_AsHermesII()
    {
        var mac = DiscoveryResponder.DefaultMac;
        Span<byte> buf = stackalloc byte[P1DiscoveryReply.ReplyLength];

        int n = P1DiscoveryReply.Build(buf, HermesII(), mac, codeVersion: 73, busy: false);

        Assert.Equal(P1DiscoveryReply.ReplyLength, n);
        Assert.True(ReplyParser.TryParse(buf[..n].ToArray(), IPAddress.Parse("192.168.1.50"), out var radio));
        Assert.NotNull(radio);
        Assert.Equal(HpsdrBoardKind.HermesII, radio!.Board);
        Assert.Equal(mac, radio.Mac);
        Assert.Equal((byte)73, radio.FirmwareVersion);
        Assert.False(radio.Details.Busy);
        Assert.Equal((byte)0x02, radio.Details.RawBoardId);
    }

    [Fact]
    public void Build_BusyFlag_IsReportedByParser()
    {
        Span<byte> buf = stackalloc byte[P1DiscoveryReply.ReplyLength];
        P1DiscoveryReply.Build(buf, HermesII(), DiscoveryResponder.DefaultMac, 73, busy: true);

        Assert.True(ReplyParser.TryParse(buf.ToArray(), IPAddress.Loopback, out var radio));
        Assert.True(radio!.Details.Busy);
    }

    [Theory]
    [InlineData(HpsdrBoardKind.Metis, 0x00)]
    [InlineData(HpsdrBoardKind.Hermes, 0x01)]
    [InlineData(HpsdrBoardKind.HermesII, 0x02)]
    [InlineData(HpsdrBoardKind.Angelia, 0x04)]
    [InlineData(HpsdrBoardKind.Orion, 0x05)]
    [InlineData(HpsdrBoardKind.HermesLite2, 0x06)]
    [InlineData(HpsdrBoardKind.OrionMkII, 0x0A)]
    [InlineData(HpsdrBoardKind.HermesC10, 0x14)]
    public void BoardByte_IsInverseOfReplyParserMap(HpsdrBoardKind board, int expected)
    {
        Assert.Equal((byte)expected, P1DiscoveryReply.BoardByte(board));
    }

    [Fact]
    public void Build_PreservesMacBytes()
    {
        var mac = new PhysicalAddress(new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 });
        Span<byte> buf = stackalloc byte[P1DiscoveryReply.ReplyLength];
        P1DiscoveryReply.Build(buf, HermesII(), mac, 73, busy: false);

        ReplyParser.TryParse(buf.ToArray(), IPAddress.Loopback, out var radio);
        Assert.Equal(mac, radio!.Mac);
    }

    [Fact]
    public void Build_ThrowsOnTooSmallBuffer()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            byte[] tiny = new byte[10];
            P1DiscoveryReply.Build(tiny, HermesII(), DiscoveryResponder.DefaultMac, 73, false);
        });
    }
}

public class DiscoveryResponderTests
{
    private static DiscoveryResponder Responder() =>
        new(VirtualRadioProfile.Create(HpsdrBoardKind.HermesII, ProtocolVersion.P1));

    [Fact]
    public void TryBuildReply_OnDiscoveryProbe_ProducesParseableReply()
    {
        byte[] probe = new byte[63];
        probe[0] = 0xEF;
        probe[1] = 0xFE;
        probe[2] = 0x02;

        Assert.True(Responder().TryBuildReply(probe, out byte[] reply));
        Assert.True(ReplyParser.TryParse(reply, IPAddress.Loopback, out var radio));
        Assert.Equal(HpsdrBoardKind.HermesII, radio!.Board);
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xFE, 0x04, 0x00 })] // command/connect frame, not discovery
    [InlineData(new byte[] { 0xEF, 0xFE })]             // too short
    [InlineData(new byte[] { 0x00, 0x01, 0x02 })]       // wrong preamble
    public void TryBuildReply_OnNonProbe_ReturnsFalse(byte[] datagram)
    {
        Assert.False(Responder().TryBuildReply(datagram, out byte[] reply));
        Assert.Empty(reply);
    }

    [Fact]
    public void IsDiscoveryProbe_DiscriminatesOnThirdByte()
    {
        Assert.True(DiscoveryResponder.IsDiscoveryProbe(new byte[] { 0xEF, 0xFE, 0x02 }));
        Assert.False(DiscoveryResponder.IsDiscoveryProbe(new byte[] { 0xEF, 0xFE, 0x04 }));
    }
}

public class CommandLogTests
{
    private static DecodedHostCommand Cmd(string kind) =>
        new(DateTimeOffset.UnixEpoch, ProtocolVersion.P1, kind, $"summary {kind}");

    [Fact]
    public void Add_EvictsOldest_PastCapacity()
    {
        var log = new CommandLog(capacity: 3);
        log.Add(Cmd("a"));
        log.Add(Cmd("b"));
        log.Add(Cmd("c"));
        log.Add(Cmd("d")); // evicts "a"

        var snap = log.Snapshot();
        Assert.Equal(3, snap.Count);
        Assert.Equal(new[] { "b", "c", "d" }, snap.Select(x => x.CommandKind).ToArray());
    }

    [Fact]
    public void Snapshot_IsOldestFirst()
    {
        var log = new CommandLog();
        log.Add(Cmd("first"));
        log.Add(Cmd("second"));
        var snap = log.Snapshot();
        Assert.Equal("first", snap[0].CommandKind);
        Assert.Equal("second", snap[1].CommandKind);
    }

    [Fact]
    public void Snapshot_IsIsolatedFromLaterMutation()
    {
        var log = new CommandLog();
        log.Add(Cmd("x"));
        var snap = log.Snapshot();
        log.Add(Cmd("y"));
        Assert.Single(snap); // earlier snapshot unaffected
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CommandLog(0));
    }
}

public class StatusJsonServerSerializeTests
{
    private static VirtualRadioStatus SampleStatus()
    {
        var profile = VirtualRadioProfile.Create(HpsdrBoardKind.HermesII, ProtocolVersion.P1) with
        {
            TunedHz = 14_074_000,
            SampleRateKhz = 96,
            Tones = new[] { new ToneSpec(14_074_000, -73) },
            BindAddress = IPAddress.Parse("192.168.1.50"),
        };
        var cmds = new[]
        {
            new DecodedHostCommand(DateTimeOffset.UnixEpoch, ProtocolVersion.P1, "TxFreq", "tx=14074000"),
        };
        return new VirtualRadioStatus(
            profile, ConnectedHost: "192.168.1.10:51000", Mox: true,
            FwdWatts: 5.0, RefWatts: 0.2, Swr: 1.1,
            Ep6PacketsSent: 1234, Ep2PacketsReceived: 56, SeqGaps: 0, LastCommands: cmds);
    }

    [Fact]
    public void Serialize_ProducesExpectedShape()
    {
        string json = StatusJsonServer.Serialize(SampleStatus());
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        JsonElement profile = root.GetProperty("profile");
        Assert.Equal("HermesII", profile.GetProperty("board").GetString());
        Assert.Equal("P1", profile.GetProperty("protocol").GetString());
        Assert.Equal("192.168.1.50", profile.GetProperty("bindAddress").GetString());
        Assert.Equal(96, profile.GetProperty("sampleRateKhz").GetInt32());
        Assert.Equal(14_074_000L, profile.GetProperty("tones")[0].GetProperty("freqHz").GetInt64());

        Assert.Equal("192.168.1.10:51000", root.GetProperty("connectedHost").GetString());
        Assert.True(root.GetProperty("mox").GetBoolean());
        Assert.Equal(5.0, root.GetProperty("fwdWatts").GetDouble());
        Assert.Equal(1234L, root.GetProperty("ep6PacketsSent").GetInt64());
        Assert.Equal("TxFreq", root.GetProperty("lastCommands")[0].GetProperty("commandKind").GetString());
    }

    [Fact]
    public void Serialize_NullConnectedHost_IsJsonNull()
    {
        var s = SampleStatus() with { ConnectedHost = null };
        string json = StatusJsonServer.Serialize(s);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("connectedHost").ValueKind);
    }
}

public class HostCliParseTests
{
    [Fact]
    public void ParseArgs_Defaults()
    {
        HostConfig c = Program.ParseArgs(Array.Empty<string>());
        Assert.Equal(HpsdrBoardKind.HermesII, c.Profile.Board);
        Assert.Equal(ProtocolVersion.P1, c.Profile.Protocol);
        Assert.Equal(OrionMkIIVariant.G2, c.Profile.Variant);
        Assert.Equal(IPAddress.Loopback, c.Profile.BindAddress);
        Assert.Equal(48, c.Profile.SampleRateKhz);
        Assert.Equal(-110.0, c.Profile.NoiseFloorDbc);
        Assert.Empty(c.Profile.Tones);
        Assert.Equal(StatusJsonServer.DefaultPort, c.StatusPort);
    }

    [Fact]
    public void ParseArgs_FullCommandLine()
    {
        HostConfig c = Program.ParseArgs(new[]
        {
            "--board", "HermesII",
            "--protocol", "P1",
            "--variant", "G2",
            "--bind", "192.168.1.77",
            "--rate", "192",
            "--tone", "14074000:-73",
            "--tone", "7074000:-80",
            "--noise", "-120",
            "--status-port", "9000",
        });

        Assert.Equal(IPAddress.Parse("192.168.1.77"), c.Profile.BindAddress);
        Assert.Equal(192, c.Profile.SampleRateKhz);
        Assert.Equal(-120.0, c.Profile.NoiseFloorDbc);
        Assert.Equal(2, c.Profile.Tones.Count);
        Assert.Equal(14_074_000L, c.Profile.Tones[0].FreqHz);
        Assert.Equal(-73.0, c.Profile.Tones[0].Dbc);
        Assert.Equal(9000, c.StatusPort);
    }

    [Fact]
    public void ParseArgs_UnknownArg_Throws()
    {
        Assert.Throws<ArgumentException>(() => Program.ParseArgs(new[] { "--bogus" }));
    }

    [Fact]
    public void ParseArgs_MissingValue_Throws()
    {
        Assert.Throws<ArgumentException>(() => Program.ParseArgs(new[] { "--rate" }));
    }

    [Fact]
    public void ParseArgs_IllegalTriple_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Program.ParseArgs(new[] { "--board", "HermesLite2", "--protocol", "P2" }));
    }

    [Fact]
    public void ParseArgs_StatusPortOutOfRange_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Program.ParseArgs(new[] { "--status-port", "70000" }));
    }

    [Theory]
    [InlineData("14074000:-73", 14_074_000L, -73.0)]
    [InlineData("7074000:-80.5", 7_074_000L, -80.5)]
    public void ParseTone_Valid(string spec, long freq, double dbc)
    {
        ToneSpec t = Program.ParseTone(spec);
        Assert.Equal(freq, t.FreqHz);
        Assert.Equal(dbc, t.Dbc);
    }

    [Theory]
    [InlineData("14074000")]   // no colon
    [InlineData(":-73")]       // no freq
    public void ParseTone_Invalid_Throws(string spec)
    {
        Assert.Throws<ArgumentException>(() => Program.ParseTone(spec));
    }
}
