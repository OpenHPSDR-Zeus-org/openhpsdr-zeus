// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

// Guards the two operator-facing "bandwidth" persistence paths:
//   1. DDC/sample-rate bandwidth is stored per board and must be restored
//      when a Protocol-2 radio reconnects.
//   2. RX/TX filter bandwidth edits must update the persisted per-mode-family
//      memory immediately, not only after a later mode switch.
public sealed class RadioServiceBandwidthPersistenceTests : IDisposable
{
    private readonly string _basePath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-bandwidth-{Guid.NewGuid():N}");
    private readonly List<IDisposable> _owned = new();

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_owned))
        {
            try { d.Dispose(); } catch { }
        }

        foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), Path.GetFileName(_basePath) + "*"))
        {
            try { File.Delete(path); } catch { }
        }
    }

    private RadioStateStore NewStateStore() =>
        new(NullLogger<RadioStateStore>.Instance, _basePath + ".state.db");

    private RadioService NewRadio(RadioStateStore stateStore)
    {
        var dsp = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _basePath + ".dsp.db");
        var pa = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _basePath + ".pa.db");
        _owned.Add(dsp);
        _owned.Add(pa);
        return new RadioService(
            NullLoggerFactory.Instance,
            dsp,
            pa,
            radioStateStore: stateStore);
    }

    [Fact]
    public void P2ConnectSampleRate_RestoresPersistedBoardRate()
    {
        using var store = NewStateStore();
        store.SetBoardSampleRate(HpsdrBoardKind.OrionMkII, 384_000, OrionMkIIVariant.G2);
        using var radio = NewRadio(store);

        var resolved = radio.ResolveConnectSampleRateHz(
            HpsdrBoardKind.OrionMkII,
            requestedHz: 192_000,
            protocol2: true);

        Assert.Equal(384_000, resolved);
    }

    [Fact]
    public void P2ConnectSampleRate_RestoresPersistedWidebandG2Rate()
    {
        using var store = NewStateStore();
        store.SetBoardSampleRate(HpsdrBoardKind.OrionMkII, 1_536_000, OrionMkIIVariant.G2);
        using var radio = NewRadio(store);

        var resolved = radio.ResolveConnectSampleRateHz(
            HpsdrBoardKind.OrionMkII,
            requestedHz: 192_000,
            protocol2: true);

        Assert.Equal(1_536_000, resolved);
    }

    [Fact]
    public void P2ConnectSampleRate_ClampsRequestedRateToBoardCapability()
    {
        using var store = NewStateStore();
        using var radio = NewRadio(store);

        var resolved = radio.ResolveConnectSampleRateHz(
            HpsdrBoardKind.HermesC10,
            requestedHz: 1_536_000,
            protocol2: true);

        Assert.Equal(384_000, resolved);
    }

    [Fact]
    public void P2ConnectSampleRate_IgnoresPersistedRateAboveBoardCapability()
    {
        using var store = NewStateStore();
        store.SetBoardSampleRate(HpsdrBoardKind.HermesC10, 1_536_000, OrionMkIIVariant.G2);
        using var radio = NewRadio(store);

        var resolved = radio.ResolveConnectSampleRateHz(
            HpsdrBoardKind.HermesC10,
            requestedHz: 192_000,
            protocol2: true);

        Assert.Equal(192_000, resolved);
    }

    [Fact]
    public void P2ConnectSampleRate_UnknownBoardUsesOrionFallback()
    {
        using var store = NewStateStore();
        store.SetBoardSampleRate(HpsdrBoardKind.OrionMkII, 96_000, OrionMkIIVariant.G2);
        using var radio = NewRadio(store);

        var resolved = radio.ResolveConnectSampleRateHz(
            HpsdrBoardKind.Unknown,
            requestedHz: 192_000,
            protocol2: true);

        Assert.Equal(96_000, resolved);
    }

    [Fact]
    public void P1ConnectSampleRate_IgnoresPersistedP2OnlyRate()
    {
        using var store = NewStateStore();
        store.SetBoardSampleRate(HpsdrBoardKind.OrionMkII, 768_000, OrionMkIIVariant.G2);
        using var radio = NewRadio(store);

        var resolved = radio.ResolveConnectSampleRateHz(
            HpsdrBoardKind.OrionMkII,
            requestedHz: 192_000,
            protocol2: false);

        Assert.Equal(192_000, resolved);
    }

    [Fact]
    public void SetFilter_FlushesLiveBandwidthAndFamilyMemory()
    {
        using var store = NewStateStore();
        using var radio = NewRadio(store);

        radio.SetMode(RxMode.LSB);
        radio.SetFilter(-3100, -200, "VAR1");

        var entry = store.Get();
        Assert.NotNull(entry);
        Assert.Equal(RxMode.LSB, entry!.Mode);
        Assert.Equal(-3100, entry.FilterLowHz);
        Assert.Equal(-200, entry.FilterHighHz);
        Assert.Equal(200, entry.SsbFilterLoAbs);
        Assert.Equal(3100, entry.SsbFilterHiAbs);
    }

    [Fact]
    public void SetTxFilter_FlushesLiveBandwidthAndFamilyMemory()
    {
        using var store = NewStateStore();
        using var radio = NewRadio(store);

        radio.SetMode(RxMode.CWU);
        radio.SetTxFilter(350, 950);

        var entry = store.Get();
        Assert.NotNull(entry);
        Assert.Equal(RxMode.CWU, entry!.Mode);
        Assert.Equal(350, entry.TxFilterLowHz);
        Assert.Equal(950, entry.TxFilterHighHz);
        Assert.Equal(350, entry.CwTxFilterLoAbs);
        Assert.Equal(950, entry.CwTxFilterHiAbs);
    }

    [Fact]
    public void PreampAndAutoToggles_SurviveRadioServiceReconstruction()
    {
        using (var store1 = NewStateStore())
        using (var radio1 = NewRadio(store1))
        {
            radio1.SetPreamp(true);
            radio1.SetAutoAtt(false);
            radio1.SetAutoAgc(true);
        }

        using var store2 = NewStateStore();
        using var radio2 = NewRadio(store2);
        var snap = radio2.Snapshot();
        Assert.True(snap.PreampOn);
        Assert.True(radio2.PreampOn);
        Assert.False(snap.AutoAttEnabled);
        Assert.True(snap.AutoAgcEnabled);
    }

    [Fact]
    public void ManualNotches_SurviveRadioServiceReconstruction()
    {
        using (var store1 = NewStateStore())
        using (var radio1 = NewRadio(store1))
        {
            radio1.SetNotches(new[]
            {
                new NotchDto(14_200_500, 75, true),
                new NotchDto(14_201_250, 125, false, "auto"),
            });
        }

        using var store2 = NewStateStore();
        using var radio2 = NewRadio(store2);
        var notches = radio2.Notches;
        Assert.Equal(2, notches.Count);
        Assert.Equal(14_200_500, notches[0].CenterHz);
        Assert.Equal(75, notches[0].WidthHz);
        Assert.True(notches[0].Active);
        Assert.Null(notches[0].Source);
        Assert.Equal(14_201_250, notches[1].CenterHz);
        Assert.Equal(125, notches[1].WidthHz);
        Assert.False(notches[1].Active);
        Assert.Equal("auto", notches[1].Source);
    }
}
