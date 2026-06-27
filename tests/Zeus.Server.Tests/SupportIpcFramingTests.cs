// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Buffers.Binary;
using System.Text;
using Zeus.Support.Contracts;

namespace Zeus.Server.Tests;

// Length-prefixed JSON framing + DTO contract for the backend<->sidecar IPC.
public class SupportIpcFramingTests
{
    private static async Task<SupportIpcMessage?> RoundTripAsync(SupportIpcMessage message)
    {
        using var ms = new MemoryStream();
        await SupportIpcFraming.WriteAsync(ms, message);
        ms.Position = 0;
        return await SupportIpcFraming.ReadAsync(ms);
    }

    [Fact]
    public async Task Hello_RoundTrips()
    {
        var hello = new SupportHello(
            ProtocolVersion: SupportIpc.ProtocolVersion,
            BackendPid: 4242,
            AppVersion: "1.2.3",
            Platform: "win32-x64",
            QrzCallsign: "N9WAR",
            RemoteDiagnosticsEnabled: true,
            AutoShareOnCrash: false,
            AppLogPath: @"C:\Users\x\AppData\Local\Zeus\logs\zeus-app.log",
            StartupLogPath: @"C:\Users\x\AppData\Local\Zeus\zeus-startup.log");

        var result = await RoundTripAsync(hello);

        // Records have value equality, so a full round-trip must reproduce the
        // concrete type and every field.
        Assert.Equal(hello, result);
        Assert.IsType<SupportHello>(result);
    }

    [Fact]
    public async Task PromptResult_PreservesEnumDecision()
    {
        var msg = new SupportPromptResult("req-1", SupportPromptDecision.Timeout);
        var result = await RoundTripAsync(msg);
        Assert.Equal(msg, result);
    }

    [Theory]
    [InlineData(SupportPromptDecision.Allow)]
    [InlineData(SupportPromptDecision.Deny)]
    [InlineData(SupportPromptDecision.Timeout)]
    [InlineData(SupportPromptDecision.Unavailable)]
    public async Task AllDecisions_RoundTrip(SupportPromptDecision decision)
    {
        var result = await RoundTripAsync(new SupportPromptResult("r", decision));
        Assert.Equal(decision, Assert.IsType<SupportPromptResult>(result).Decision);
    }

    [Fact]
    public async Task Sequence_OfMessages_RoundTripsInOrder()
    {
        SupportIpcMessage[] sent =
        [
            new SupportHeartbeat(123),
            new SupportPromptRequest("req-9", "KB2UKA", "looking into a crash"),
            new SupportDiagnosticsPull("req-10", "dsp-live"),
            new SupportDiagnosticsSnapshot("req-10", Ok: true, Json: "{\"x\":1}"),
        ];

        using var ms = new MemoryStream();
        foreach (var m in sent)
            await SupportIpcFraming.WriteAsync(ms, m);
        ms.Position = 0;

        foreach (var expected in sent)
            Assert.Equal(expected, await SupportIpcFraming.ReadAsync(ms));

        // Stream is now exhausted exactly at a frame boundary → clean EOF.
        Assert.Null(await SupportIpcFraming.ReadAsync(ms));
    }

    [Fact]
    public async Task Read_OnEmptyStream_ReturnsNull()
    {
        using var ms = new MemoryStream();
        Assert.Null(await SupportIpcFraming.ReadAsync(ms));
    }

    [Fact]
    public async Task Read_ZeroLengthFrame_Throws()
    {
        using var ms = new MemoryStream(new byte[4]); // length prefix = 0
        await Assert.ThrowsAsync<InvalidDataException>(() => SupportIpcFraming.ReadAsync(ms));
    }

    [Fact]
    public async Task Read_OversizedFrame_Throws()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)SupportIpc.MaxFrameBytes + 1);
        using var ms = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => SupportIpcFraming.ReadAsync(ms));
    }

    [Fact]
    public async Task Read_TruncatedPayload_Throws()
    {
        // Header promises 50 bytes; supply only 10 then EOF.
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 50);
        var buf = new byte[4 + 10];
        header.CopyTo(buf, 0);
        Encoding.UTF8.GetBytes("0123456789").CopyTo(buf, 4);

        using var ms = new MemoryStream(buf);
        await Assert.ThrowsAsync<InvalidDataException>(() => SupportIpcFraming.ReadAsync(ms));
    }

    [Fact]
    public void PipeNameForSession_SanitisesAndIsStable()
    {
        Assert.Equal("zeus-support", SupportIpc.PipeNameForSession(null));
        Assert.Equal("zeus-support", SupportIpc.PipeNameForSession("   "));
        // Non-alphanumerics collapse to underscores; the same token is stable.
        var a = SupportIpc.PipeNameForSession("ab/cd-12");
        Assert.Equal("zeus-support-ab_cd_12", a);
        Assert.Equal(a, SupportIpc.PipeNameForSession("ab/cd-12"));
    }
}
