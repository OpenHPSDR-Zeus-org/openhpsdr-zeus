// SPDX-License-Identifier: GPL-2.0-or-later
//
// AudioChainMasterBypassService — startup default, persisted-state
// restoration, bridge write-through, and orthogonality with per-slot
// bypass (operator's per-plugin bypass choices must survive master
// toggles).

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class AudioChainMasterBypassServiceTests : IDisposable
{
    private readonly string _dbPath;

    public AudioChainMasterBypassServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-masterbypass-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private (AudioChainMasterBypassService svc, AudioPluginBridge bridge, AudioChainSettingsStore store) MakeService()
    {
        var store = new AudioChainSettingsStore(NullLogger<AudioChainSettingsStore>.Instance, _dbPath);
        var bridge = new AudioPluginBridge(
            isMoxOn: () => false,
            isMonitorOn: () => false,
            log: NullLogger<AudioPluginBridge>.Instance);
        var hub = new StreamingHub(NullLogger<StreamingHub>.Instance);
        var svc = new AudioChainMasterBypassService(
            store, bridge, hub, NullLogger<AudioChainMasterBypassService>.Instance);
        return (svc, bridge, store);
    }

    [Fact]
    public async Task FirstRun_DefaultsBypassedTrue_AndWritesThroughToBridge()
    {
        var (svc, bridge, store) = MakeService();
        try
        {
            await svc.StartAsync(CancellationToken.None);

            Assert.True(svc.IsBypassed);
            Assert.True(bridge.IsMasterBypassed);
            // Persistence semantics: first run should NOT have written a
            // row — only an explicit operator mutation writes to disk.
            // (Matches ChainOrderStore's seed-but-don't-persist pattern.)
            Assert.Null(store.GetMasterBypassed());
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public async Task PersistedFalse_RestoresChainHotOnRestart()
    {
        // Pre-seed the store as if a previous session had disengaged
        // master bypass.
        using (var store = new AudioChainSettingsStore(
            NullLogger<AudioChainSettingsStore>.Instance, _dbPath))
        {
            store.SetMasterBypassed(false);
        }

        var (svc, bridge, store2) = MakeService();
        try
        {
            await svc.StartAsync(CancellationToken.None);
            Assert.False(svc.IsBypassed);
            Assert.False(bridge.IsMasterBypassed);
        }
        finally
        {
            store2.Dispose();
        }
    }

    [Fact]
    public async Task SetMasterBypassed_PersistsAndWritesThrough()
    {
        var (svc, bridge, store) = MakeService();
        try
        {
            await svc.StartAsync(CancellationToken.None);
            // First-run default = true; operator clicks off.
            svc.SetMasterBypassed(false);

            Assert.False(svc.IsBypassed);
            Assert.False(bridge.IsMasterBypassed);
            Assert.NotNull(store.GetMasterBypassed());
            Assert.False(store.GetMasterBypassed());
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public async Task SetMasterBypassed_IsIdempotent_NoOpOnSameValue()
    {
        var (svc, _, store) = MakeService();
        try
        {
            await svc.StartAsync(CancellationToken.None);
            // First-run state is true with no persisted row.
            int eventFires = 0;
            svc.MasterBypassedChanged += _ => eventFires++;
            // Setting to true (same as current) must not write to store
            // or fire the event.
            svc.SetMasterBypassed(true);
            Assert.Equal(0, eventFires);
            Assert.Null(store.GetMasterBypassed());
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public async Task MasterToggle_DoesNotMutatePerSlotBypass()
    {
        var (svc, bridge, store) = MakeService();
        try
        {
            await svc.StartAsync(CancellationToken.None);

            // Set per-plugin bypass on slot 0 BEFORE master toggle.
            bridge.Chain.SetSlotBypass(0, bypassed: true);
            bridge.Chain.SetSlotBypass(1, bypassed: false);
            bridge.Chain.SetSlotBypass(2, bypassed: true);

            // Operator flips master off → on → off. Per-slot bypass
            // states must be unchanged after each transition.
            svc.SetMasterBypassed(false);
            Assert.True(bridge.Chain.IsSlotBypassed(0));
            Assert.False(bridge.Chain.IsSlotBypassed(1));
            Assert.True(bridge.Chain.IsSlotBypassed(2));

            svc.SetMasterBypassed(true);
            Assert.True(bridge.Chain.IsSlotBypassed(0));
            Assert.False(bridge.Chain.IsSlotBypassed(1));
            Assert.True(bridge.Chain.IsSlotBypassed(2));

            svc.SetMasterBypassed(false);
            Assert.True(bridge.Chain.IsSlotBypassed(0));
            Assert.False(bridge.Chain.IsSlotBypassed(1));
            Assert.True(bridge.Chain.IsSlotBypassed(2));
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public async Task MasterBypassedChanged_Fires_OnChange()
    {
        var (svc, _, store) = MakeService();
        try
        {
            await svc.StartAsync(CancellationToken.None);
            bool? lastSeen = null;
            int fires = 0;
            svc.MasterBypassedChanged += b => { lastSeen = b; fires++; };

            svc.SetMasterBypassed(false);
            Assert.Equal(1, fires);
            Assert.False(lastSeen);

            svc.SetMasterBypassed(true);
            Assert.Equal(2, fires);
            Assert.True(lastSeen);
        }
        finally
        {
            store.Dispose();
        }
    }
}
