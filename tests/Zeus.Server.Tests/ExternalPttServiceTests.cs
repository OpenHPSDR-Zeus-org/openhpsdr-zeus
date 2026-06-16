// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// External-ports plan §4 (PASS C): hardware PTT-IN → MOX, both protocols.
//
// PASS B added the radio-mic TX pipeline; PASS C adds the P2 hardware-PTT→MOX
// promotion (which did NOT exist — ExternalPttService was P1-only and a G2/P2
// connect never reached it) plus the per-protocol PTT-IN status lamp.
//
// These tests drive the SHARED debounce/hang/_owned engine through the P1 and
// P2 entry-point seams and assert:
//   • P2 PttIn rising → MOX-on with MoxSource.Hardware.
//   • P2 PttIn falling → 250 ms hang → MOX-off.
//   • P1 path behaviour unchanged (rising→MOX, falling→hang→off).
//   • The lamp tracks the raw input per protocol (and the hub frame fires).
//   • A re-key inside the hang window cancels the release (CW inter-char gap).
//   • Hardware can never drop a UI/CWX-owned MOX (arbitration preserved → PS
//     keying untouched).
//   • The Enable gate stops MOX promotion but the lamp still tracks the input.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Protocol2;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class ExternalPttServiceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-extptt-{Guid.NewGuid():N}.db");
    private readonly string _pttDbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-extptt-ptt-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var p in new[] { _dbPath, _dbPath + ".pa", _pttDbPath })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }

    private sealed class Harness : IDisposable
    {
        public RadioService Radio { get; }
        public TxService Tx { get; }
        public StreamingHub Hub { get; }
        public PttSettingsStore Settings { get; }
        public ExternalPttService Service { get; }

        public Harness(string dbPath, string pttDbPath, bool enabled = true)
        {
            var loggerFactory = NullLoggerFactory.Instance;
            var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, dbPath);
            var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, dbPath + ".pa");
            Radio = new RadioService(loggerFactory, dspStore, paStore);
            // Connect P2 so TrySetMox's FR-1 "not connected" interlock passes —
            // exactly how a live G2 looks to the service.
            Radio.MarkProtocol2Connected("127.0.0.1:1024", 48_000);
            Hub = new StreamingHub(new NullLogger<StreamingHub>());
            var pipeline = new DspPipelineService(Radio, Hub, Array.Empty<IRxAudioSink>(), loggerFactory);
            Tx = new TxService(Radio, pipeline, Hub, NullBandPlanService.Instance, new NullLogger<TxService>());
            Settings = new PttSettingsStore(NullLogger<PttSettingsStore>.Instance, pttDbPath);
            // Set explicitly (PTT-IN now defaults OFF / opt-in) so the harness is
            // deterministic regardless of the store's power-on default.
            Settings.Set(enabled);
            Service = new ExternalPttService(Radio, Tx, Hub, Settings, new NullLogger<ExternalPttService>());
        }

        public void Dispose()
        {
            Service.Dispose();
            Settings.Dispose();
        }
    }

    // The MOX takeover/release run on Task.Run; poll briefly for the expected
    // state instead of racing the ThreadPool.
    private static bool WaitFor(Func<bool> cond, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(5);
        }
        return cond();
    }

    private static P2TelemetryReading Ptt(bool on) =>
        new P2TelemetryReading(FwdAdc: 0, RevAdc: 0, ExciterAdc: 0, PttIn: on, PllLocked: true);

    [Fact]
    public void P2_PttInRising_KeysMox_WithHardwareSource()
    {
        using var h = new Harness(_dbPath, _pttDbPath);

        h.Service.TestP2Telemetry(Ptt(true));

        Assert.True(WaitFor(() => h.Tx.IsMoxOn));
        Assert.Equal(MoxSource.Hardware, h.Tx.MoxOwner);
        Assert.True(h.Service.IsKeyed);
    }

    [Fact]
    public void P2_PttInFalling_HoldsForHang_ThenReleases()
    {
        using var h = new Harness(_dbPath, _pttDbPath);

        h.Service.TestP2Telemetry(Ptt(true));
        Assert.True(WaitFor(() => h.Tx.IsMoxOn));

        h.Service.TestP2Telemetry(Ptt(false));
        // Within the 250 ms hang MOX must still be up (bridges CW gaps).
        Assert.True(h.Tx.IsMoxOn, "MOX dropped before the 250 ms hang elapsed");
        Assert.False(h.Service.IsKeyed); // lamp tracks the raw input immediately

        // After the hang the release fires (timer → Task on the ThreadPool).
        Assert.True(WaitFor(() => !h.Tx.IsMoxOn, timeoutMs: 2000));
        Assert.Null(h.Tx.MoxOwner);
    }

    [Fact]
    public void P2_ReKeyInsideHang_CancelsRelease()
    {
        using var h = new Harness(_dbPath, _pttDbPath);

        h.Service.TestP2Telemetry(Ptt(true));
        Assert.True(WaitFor(() => h.Tx.IsMoxOn));

        h.Service.TestP2Telemetry(Ptt(false)); // arm hang
        Thread.Sleep(50);
        h.Service.TestP2Telemetry(Ptt(true));  // re-key inside the window

        // MOX must stay up well past the hang window — the release was cancelled.
        Thread.Sleep(350);
        Assert.True(h.Tx.IsMoxOn);
        Assert.True(h.Service.IsKeyed);
    }

    [Fact]
    public void P2_LevelSampled_NoEdge_DoesNotReKey()
    {
        // P2 telemetry is level-sampled at high rate; repeated identical levels
        // must not be treated as edges (no MOX churn, no ownership reseat).
        using var h = new Harness(_dbPath, _pttDbPath);

        h.Service.TestP2Telemetry(Ptt(true));
        Assert.True(WaitFor(() => h.Tx.IsMoxOn));

        // Many redundant "still keyed" samples — must remain a single claim.
        for (int i = 0; i < 20; i++) h.Service.TestP2Telemetry(Ptt(true));
        Assert.True(h.Tx.IsMoxOn);
        Assert.Equal(MoxSource.Hardware, h.Tx.MoxOwner);
    }

    [Fact]
    public void P1_PttInRising_KeysMox_WithHardwareSource_Unchanged()
    {
        using var h = new Harness(_dbPath, _pttDbPath);

        h.Service.TestRawPttP1(true);

        Assert.True(WaitFor(() => h.Tx.IsMoxOn));
        Assert.Equal(MoxSource.Hardware, h.Tx.MoxOwner);
        Assert.True(h.Service.IsKeyed);

        h.Service.TestRawPttP1(false);
        Assert.True(h.Tx.IsMoxOn, "P1 MOX dropped before the hang elapsed");
        Assert.True(WaitFor(() => !h.Tx.IsMoxOn, timeoutMs: 2000));
    }

    [Fact]
    public void Hardware_CannotDrop_UiOwnedMox_ArbitrationPreserved()
    {
        // PURESIGNAL / UI keying is paramount: a footswitch release must never
        // truncate a UI-keyed transmission. The hardware path goes through the
        // EXISTING TrySetMox(MoxSource.Hardware) arbitration.
        using var h = new Harness(_dbPath, _pttDbPath);

        Assert.True(h.Tx.TrySetMox(true, MoxSource.UI, out _));
        Assert.Equal(MoxSource.UI, h.Tx.MoxOwner);

        // Hardware rising while UI owns: the IsMoxOn gate ignores the echo, so
        // no takeover and ownership stays UI.
        h.Service.TestP2Telemetry(Ptt(true));
        Thread.Sleep(50);
        Assert.True(h.Tx.IsMoxOn);
        Assert.Equal(MoxSource.UI, h.Tx.MoxOwner);

        // Hardware falling + full hang: the release is attempted but rejected by
        // the arbitration (foreign source can't drop UI's MOX).
        h.Service.TestP2Telemetry(Ptt(false));
        Thread.Sleep(350);
        Assert.True(h.Tx.IsMoxOn);
        Assert.Equal(MoxSource.UI, h.Tx.MoxOwner);
    }

    [Fact]
    public void EnableGate_Off_SuppressesMox_ButLampStillTracks()
    {
        using var h = new Harness(_dbPath, _pttDbPath, enabled: false);

        h.Service.TestP2Telemetry(Ptt(true));
        Thread.Sleep(50);

        // No MOX promotion when the gate is off…
        Assert.False(h.Tx.IsMoxOn);
        // …but the lamp still reflects the physical footswitch press.
        Assert.True(h.Service.IsKeyed);

        h.Service.TestP2Telemetry(Ptt(false));
        Assert.False(h.Service.IsKeyed);
    }

    [Fact]
    public void EnableGate_DefaultsOff()
    {
        using var store = new PttSettingsStore(NullLogger<PttSettingsStore>.Instance, _pttDbPath);
        Assert.False(store.Get());
        store.Set(true);
        Assert.True(store.Get());
        store.Set(false);
        Assert.False(store.Get());
    }
}
