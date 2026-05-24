// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Ownership rules on TxService.TrySetMox(bool, MoxSource, …). The release
// path must refuse to drop MOX on behalf of a foreign source, with two
// exceptions: UI is a master override, and TryTripForAlert always wins.
// Without these rules a TCI peer's `trx:false` could truncate a host-side
// CW message, and a hardware-PTT falling edge could drop a UI-keyed TX.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class MoxSourceOwnershipTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-moxsrc-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
    }

    private (RadioService radio, TxService tx) BuildConnectedRadioAndTx()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var radio = new RadioService(loggerFactory, dspStore, paStore);
        radio.MarkProtocol2Connected("127.0.0.1:1024", 48_000);
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var pipeline = new DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), loggerFactory);
        var tx = new TxService(radio, pipeline, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
        return (radio, tx);
    }

    [Fact]
    public void Hardware_CannotRelease_CwxOwnedMox()
    {
        var (_, tx) = BuildConnectedRadioAndTx();

        Assert.True(tx.TrySetMox(true, MoxSource.Cwx, out _));
        Assert.True(tx.IsMoxOn);
        Assert.Equal(MoxSource.Cwx, tx.MoxOwner);

        // External PTT falling edge — must NOT drop a CW-driven transmission.
        bool ok = tx.TrySetMox(false, MoxSource.Hardware, out var err);

        Assert.False(ok);
        Assert.NotNull(err);
        Assert.Contains("Cwx", err);
        Assert.True(tx.IsMoxOn);
        Assert.Equal(MoxSource.Cwx, tx.MoxOwner);
    }

    [Fact]
    public void Cwx_CannotRelease_HardwareOwnedMox()
    {
        // Mirror of the above so the rule holds symmetrically — a stray
        // /api/cw/abort while a hardware key is held shouldn't kill the
        // operator's hand-keyed CW.
        var (_, tx) = BuildConnectedRadioAndTx();

        Assert.True(tx.TrySetMox(true, MoxSource.Hardware, out _));
        Assert.Equal(MoxSource.Hardware, tx.MoxOwner);

        bool ok = tx.TrySetMox(false, MoxSource.Cwx, out var err);

        Assert.False(ok);
        Assert.NotNull(err);
        Assert.Contains("Hardware", err);
        Assert.True(tx.IsMoxOn);
    }

    [Fact]
    public void UI_AlwaysReleases_NoMatterWhoOwns()
    {
        // The operator's on-screen MOX button is the master override:
        // pressing it must drop MOX no matter who claimed it.
        foreach (var owner in new[] { MoxSource.Cwx, MoxSource.Hardware, MoxSource.Tci })
        {
            var (_, tx) = BuildConnectedRadioAndTx();

            Assert.True(tx.TrySetMox(true, owner, out _));
            Assert.Equal(owner, tx.MoxOwner);

            bool ok = tx.TrySetMox(false, MoxSource.UI, out var err);

            Assert.True(ok);
            Assert.Null(err);
            Assert.False(tx.IsMoxOn);
            Assert.Null(tx.MoxOwner);
        }
    }

    [Fact]
    public void OwningSource_CanReleaseItsOwnMox()
    {
        var (_, tx) = BuildConnectedRadioAndTx();

        Assert.True(tx.TrySetMox(true, MoxSource.Tci, out _));
        Assert.Equal(MoxSource.Tci, tx.MoxOwner);

        bool ok = tx.TrySetMox(false, MoxSource.Tci, out var err);

        Assert.True(ok);
        Assert.Null(err);
        Assert.False(tx.IsMoxOn);
    }

    [Fact]
    public void TripForAlert_AlwaysWins_RegardlessOfOwner()
    {
        // SWR / TX-timeout trips bypass the source rule completely — RF must
        // cut even if the owning source isn't around to release voluntarily.
        var (_, tx) = BuildConnectedRadioAndTx();

        Assert.True(tx.TrySetMox(true, MoxSource.Cwx, out _));
        Assert.True(tx.IsMoxOn);

        tx.TryTripForAlert(AlertKind.SwrTrip, "test");

        Assert.False(tx.IsMoxOn);
        Assert.Null(tx.MoxOwner);
    }

    [Fact]
    public void Owner_LatchesOnFirstClaim_RedundantKeyOnDoesNotReseat()
    {
        // First-claim semantics: if Cwx keys MOX and then UI also calls
        // TrySetMox(true), ownership stays with Cwx. (UI hasn't actually
        // changed state; the call is a no-op edge.) Without this, a UI poll
        // that touches MOX while CW is running would convert the message's
        // owner to UI and let foreign sources drop it.
        var (_, tx) = BuildConnectedRadioAndTx();

        Assert.True(tx.TrySetMox(true, MoxSource.Cwx, out _));
        Assert.Equal(MoxSource.Cwx, tx.MoxOwner);

        Assert.True(tx.TrySetMox(true, MoxSource.UI, out _));
        Assert.Equal(MoxSource.Cwx, tx.MoxOwner);

        // And a foreign drop is still rejected.
        Assert.False(tx.TrySetMox(false, MoxSource.Tci, out _));
        Assert.True(tx.IsMoxOn);
    }

    [Fact]
    public void BackCompatOverload_DefaultsToUiSource()
    {
        // Callers that use the old 2-arg TrySetMox get MoxSource.UI behaviour
        // implicitly. This keeps every existing test and call site working
        // unchanged after the source-tag addition.
        var (_, tx) = BuildConnectedRadioAndTx();

        Assert.True(tx.TrySetMox(true, out _));
        Assert.Equal(MoxSource.UI, tx.MoxOwner);

        // Foreign drop still rejected (UI owns now).
        Assert.False(tx.TrySetMox(false, MoxSource.Cwx, out _));
        Assert.True(tx.IsMoxOn);

        // UI can release its own MOX via the back-compat overload too.
        Assert.True(tx.TrySetMox(false, out _));
        Assert.False(tx.IsMoxOn);
    }
}
