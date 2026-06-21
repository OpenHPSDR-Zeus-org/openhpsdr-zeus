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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;
using Xunit;

namespace Zeus.Server.Tests;

/// <summary>
/// TwoTone latch on TxService — flipped by SetTwoToneOn from the
/// /api/tx/twotone handler so TxTuneDriver pumps WDSP's TXA chain even
/// when the mic ingest pump is idle. Without the latch, PostGen mode=1
/// has nothing to shove its excitation into and the radio sees zero IQ.
/// </summary>
public class TwoToneLatchTests : IDisposable
{
    // Per-fixture temp DBs so xUnit class-level parallelism can't collide on
    // the shared zeus-prefs.db. Without this, parallel construction of LiteDB
    // instances against the same file races the BsonMapper and intermittently
    // fails with "Member Band not found on BsonMapper for type PaBandEntry".
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-twotone-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
    }

    private TxService BuildTxService()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var radio = new RadioService(loggerFactory, dspStore, paStore);
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var pipeline = new DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), loggerFactory);
        return new TxService(radio, pipeline, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
    }

    [Fact]
    public void IsTwoToneOn_DefaultsFalse()
    {
        var tx = BuildTxService();
        Assert.False(tx.IsTwoToneOn);
    }

    [Fact]
    public void SetTwoToneOn_True_FlipsLatch()
    {
        var tx = BuildTxService();
        tx.SetTwoToneOn(true);
        Assert.True(tx.IsTwoToneOn);
    }

    [Fact]
    public void SetTwoToneOn_FalseAfterTrue_ClearsLatch()
    {
        var tx = BuildTxService();
        tx.SetTwoToneOn(true);
        tx.SetTwoToneOn(false);
        Assert.False(tx.IsTwoToneOn);
    }

    [Fact]
    public void SetTwoToneOn_DoesNotAffectMoxOrTun()
    {
        // The latch is independent of MOX/TUN — TxTuneDriver gates on
        // (IsTunOn || IsTwoToneOn), so a TwoTone arm without MOX/TUN
        // should still leave those bits clear.
        var tx = BuildTxService();
        tx.SetTwoToneOn(true);

        Assert.True(tx.IsTwoToneOn);
        Assert.False(tx.IsMoxOn);
        Assert.False(tx.IsTunOn);
    }

    // ---- TrySetTwoTone — round-3 auto-MOX path (Thetis parity).
    // Brian's expectation: pressing 2-Tone produces RF without a separate
    // MOX press. TrySetTwoTone owns the MOX state while armed and drops it
    // unconditionally on disarm (mirrors setup.cs:11162-11165, 11189-11216).

    private (RadioService radio, TxService tx) BuildConnectedRadioAndTx()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var radio = new RadioService(loggerFactory, dspStore, paStore);
        // Mark the radio P2-connected so the connect-interlock in
        // TrySetTwoTone passes — production calls this from
        // ConnectP2Async; here we shortcut.
        radio.MarkProtocol2Connected("127.0.0.1:1024", 48_000);
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var pipeline = new DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), loggerFactory);
        return (radio, new TxService(radio, pipeline, hub, NullBandPlanService.Instance, new NullLogger<TxService>()));
    }

    [Fact]
    public void TrySetTwoTone_NotConnected_ReturnsFalse_AndDoesNotKeyMox()
    {
        // Connect interlock — same shape as TrySetMox / TrySetTun.
        var tx = BuildTxService();    // RadioService not connected

        var ok = tx.TrySetTwoTone(
            new Zeus.Contracts.TwoToneSetRequest(Enabled: true), out var err);

        Assert.False(ok);
        Assert.Equal("not connected", err);
        Assert.False(tx.IsMoxOn);
        Assert.False(tx.IsTwoToneOn);
    }

    [Fact]
    public void TrySetTwoTone_Arm_KeysMox_AndLatchesTwoToneOn()
    {
        var (_, tx) = BuildConnectedRadioAndTx();

        var ok = tx.TrySetTwoTone(
            new Zeus.Contracts.TwoToneSetRequest(
                Enabled: true, Freq1: 700, Freq2: 1900, Mag: 0.49),
            out var err);

        Assert.True(ok);
        Assert.Null(err);
        Assert.True(tx.IsTwoToneOn);
        Assert.True(tx.IsMoxOn);
    }

    [Fact]
    public void TrySetTwoTone_Disarm_DropsMox_AndClearsTwoToneOn()
    {
        var (_, tx) = BuildConnectedRadioAndTx();
        // Arm first.
        tx.TrySetTwoTone(
            new Zeus.Contracts.TwoToneSetRequest(Enabled: true), out _);
        Assert.True(tx.IsMoxOn);

        // Disarm.
        var ok = tx.TrySetTwoTone(
            new Zeus.Contracts.TwoToneSetRequest(Enabled: false), out var err);

        Assert.True(ok);
        Assert.Null(err);
        Assert.False(tx.IsTwoToneOn);
        Assert.False(tx.IsMoxOn);
    }

    [Fact]
    public void TrySetTwoTone_Disarm_AllowedEvenWhenNotConnected()
    {
        // The connect interlock only gates arm — disarm always passes so an
        // operator can clear the latch after a disconnect.
        var tx = BuildTxService();    // RadioService not connected

        var ok = tx.TrySetTwoTone(
            new Zeus.Contracts.TwoToneSetRequest(Enabled: false), out var err);

        Assert.True(ok);
        Assert.Null(err);
    }
}
