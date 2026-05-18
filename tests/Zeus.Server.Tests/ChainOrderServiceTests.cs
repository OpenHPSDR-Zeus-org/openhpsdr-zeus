// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Plugins.Host.Audio;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Unit tests for <see cref="ChainOrderService"/> — the source of
/// truth for the Audio Suite chain order. Verifies the canonical
/// vs runtime split (canonical preserves uninstalled positions,
/// runtime exposes only attached plugins), v2-default seeded
/// position-on-insert, PUT permutation validation against attached
/// set, and persistence round-trip.
/// </summary>
public class ChainOrderServiceTests
{
    private static (ChainOrderService svc, ChainOrderStore store, StreamingHub hub, string dbPath) MakeService()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"chain-order-test-{Guid.NewGuid():N}.db");
        var store = new ChainOrderStore(NullLogger<ChainOrderStore>.Instance, dbPath);
        var hub = new StreamingHub(NullLogger<StreamingHub>.Instance);
        var svc = new ChainOrderService(store, hub, NullLogger<ChainOrderService>.Instance);
        return (svc, store, hub, dbPath);
    }

    /// <summary>
    /// First-run boot with no plugins attached: canonical is seeded
    /// with the v2 default order (so future installs land in the
    /// right slot), but the runtime CurrentOrder is empty because
    /// nothing is attached yet. Nothing persisted yet — only
    /// explicit mutations persist.
    /// </summary>
    [Fact]
    public void First_Run_Runtime_Order_Is_Empty_Even_Though_Canonical_Is_Seeded()
    {
        var (svc, store, _, dbPath) = MakeService();
        try
        {
            Assert.Empty(svc.CurrentOrder);
            Assert.Equal(ChainOrderService.DefaultOrder, svc.CanonicalOrderForTest);
            // No mutation yet = no persistence yet.
            Assert.Null(store.GetOrder());
        }
        finally { store.Dispose(); File.Delete(dbPath); }
    }

    [Fact]
    public void Attaching_A_Known_v2_Id_Returns_Its_Default_Position_Among_Attached()
    {
        var (svc, store, _, dbPath) = MakeService();
        try
        {
            // EQ is at canonical index 3 (after Gate/DownExp/Tube).
            // With nothing else attached, EQ is the only runtime entry,
            // so its runtime slot is 0.
            var eq = ChainOrderService.DefaultOrder[3];
            var comp = ChainOrderService.DefaultOrder[4];

            var eqSlot = svc.OnPluginAttached(eq, new[] { eq });
            Assert.Equal(0, eqSlot);
            Assert.Single(svc.CurrentOrder);
            Assert.Equal(eq, svc.CurrentOrder[0]);

            var compSlot = svc.OnPluginAttached(comp, new[] { eq, comp });
            Assert.Equal(1, compSlot);
            Assert.Equal(new[] { eq, comp }, svc.CurrentOrder);
        }
        finally { store.Dispose(); File.Delete(dbPath); }
    }

    [Fact]
    public void Attaching_A_NonDefault_Id_Appends_To_Canonical_And_Persists()
    {
        var (svc, store, _, dbPath) = MakeService();
        try
        {
            var customId = "com.example.thirdparty.plugin";
            svc.OnPluginAttached(customId, new[] { customId });

            // Custom ID goes to the end of canonical (not in DefaultOrder).
            Assert.Equal(customId, svc.CanonicalOrderForTest[^1]);
            // Only this one is attached, so it's the sole runtime entry.
            Assert.Equal(new[] { customId }, svc.CurrentOrder);
            // First mutation persists.
            var persisted = store.GetOrder();
            Assert.NotNull(persisted);
            Assert.Equal(customId, persisted![^1]);
        }
        finally { store.Dispose(); File.Delete(dbPath); }
    }

    [Fact]
    public void Attaching_Same_Id_Twice_Does_Not_Duplicate_In_Canonical()
    {
        var (svc, store, _, dbPath) = MakeService();
        try
        {
            var eq = ChainOrderService.DefaultOrder[3];
            svc.OnPluginAttached(eq, new[] { eq });
            svc.OnPluginAttached(eq, new[] { eq });

            var canonical = svc.CanonicalOrderForTest;
            Assert.Equal(1, canonical.Count(id => id == eq));
        }
        finally { store.Dispose(); File.Delete(dbPath); }
    }

    [Fact]
    public void Detaching_Removes_From_Runtime_But_Keeps_Position_In_Canonical()
    {
        var (svc, store, _, dbPath) = MakeService();
        try
        {
            var eq = ChainOrderService.DefaultOrder[3];
            svc.OnPluginAttached(eq, new[] { eq });
            Assert.Single(svc.CurrentOrder);

            svc.OnPluginDetached(eq);

            Assert.Empty(svc.CurrentOrder);
            // Canonical still has EQ at its v2-default position so a
            // re-install restores the slot.
            Assert.Contains(eq, svc.CanonicalOrderForTest);
        }
        finally { store.Dispose(); File.Delete(dbPath); }
    }

    [Fact]
    public void TrySetOrder_Accepts_Permutation_Of_Attached_Set()
    {
        var (svc, store, _, dbPath) = MakeService();
        try
        {
            // Attach EQ, Comp, Exciter — 3 of the 8 v2 defaults.
            var eq = ChainOrderService.DefaultOrder[3];
            var comp = ChainOrderService.DefaultOrder[4];
            var exc = ChainOrderService.DefaultOrder[5];
            svc.OnPluginAttached(eq, new[] { eq });
            svc.OnPluginAttached(comp, new[] { eq, comp });
            svc.OnPluginAttached(exc, new[] { eq, comp, exc });
            Assert.Equal(new[] { eq, comp, exc }, svc.CurrentOrder);

            int orderChangedCount = 0;
            IReadOnlyList<string>? lastEvent = null;
            svc.OrderChanged += order => { orderChangedCount++; lastEvent = order; };

            // Reverse the runtime view.
            var reordered = new[] { exc, comp, eq };
            var ok = svc.TrySetOrder(reordered, out var err);

            Assert.True(ok, err);
            Assert.Null(err);
            Assert.Equal(reordered, svc.CurrentOrder);
            Assert.Equal(1, orderChangedCount);
            Assert.Equal(reordered, lastEvent);
        }
        finally { store.Dispose(); File.Delete(dbPath); }
    }

    [Fact]
    public void TrySetOrder_Preserves_Uninstalled_Canonical_Entries_In_Position()
    {
        var (svc, store, _, dbPath) = MakeService();
        try
        {
            // Attach only EQ and Comp; Gate/DownExp/Tube/Exciter/Bass/Reverb
            // are NOT installed but still occupy canonical slots.
            var eq = ChainOrderService.DefaultOrder[3];
            var comp = ChainOrderService.DefaultOrder[4];
            svc.OnPluginAttached(eq, new[] { eq });
            svc.OnPluginAttached(comp, new[] { eq, comp });

            // Operator reorders the runtime view: [Comp, EQ].
            var ok = svc.TrySetOrder(new[] { comp, eq }, out var err);
            Assert.True(ok, err);

            // Canonical should still have all 8 v2 defaults; the two
            // installed slots (originally EQ=idx3, Comp=idx4) now hold
            // [Comp, EQ] respectively — uninstalled IDs unchanged.
            var canonical = svc.CanonicalOrderForTest;
            Assert.Equal(8, canonical.Count);
            Assert.Equal(ChainOrderService.DefaultOrder[0], canonical[0]); // Gate unchanged
            Assert.Equal(ChainOrderService.DefaultOrder[1], canonical[1]); // DownExp unchanged
            Assert.Equal(ChainOrderService.DefaultOrder[2], canonical[2]); // Tube unchanged
            Assert.Equal(comp, canonical[3]); // was EQ, now Comp (position-replacement)
            Assert.Equal(eq, canonical[4]);   // was Comp, now EQ
            Assert.Equal(ChainOrderService.DefaultOrder[5], canonical[5]); // Exciter unchanged
            Assert.Equal(ChainOrderService.DefaultOrder[6], canonical[6]); // Bass unchanged
            Assert.Equal(ChainOrderService.DefaultOrder[7], canonical[7]); // Reverb unchanged
        }
        finally { store.Dispose(); File.Delete(dbPath); }
    }

    [Fact]
    public void TrySetOrder_Rejects_Wrong_Count()
    {
        var (svc, store, _, dbPath) = MakeService();
        try
        {
            var eq = ChainOrderService.DefaultOrder[3];
            var comp = ChainOrderService.DefaultOrder[4];
            svc.OnPluginAttached(eq, new[] { eq });
            svc.OnPluginAttached(comp, new[] { eq, comp });

            int orderChangedCount = 0;
            svc.OrderChanged += _ => orderChangedCount++;

            // Drop one — wrong count.
            var ok = svc.TrySetOrder(new[] { eq }, out var err);

            Assert.False(ok);
            Assert.NotNull(err);
            Assert.Equal(new[] { eq, comp }, svc.CurrentOrder);
            Assert.Equal(0, orderChangedCount);
        }
        finally { store.Dispose(); File.Delete(dbPath); }
    }

    [Fact]
    public void TrySetOrder_Rejects_Different_Id_Set()
    {
        var (svc, store, _, dbPath) = MakeService();
        try
        {
            var eq = ChainOrderService.DefaultOrder[3];
            var comp = ChainOrderService.DefaultOrder[4];
            svc.OnPluginAttached(eq, new[] { eq });
            svc.OnPluginAttached(comp, new[] { eq, comp });

            // Same count but different set — should reject.
            var ok = svc.TrySetOrder(new[] { eq, "com.example.nope" }, out var err);

            Assert.False(ok);
            Assert.NotNull(err);
            Assert.Equal(new[] { eq, comp }, svc.CurrentOrder);
        }
        finally { store.Dispose(); File.Delete(dbPath); }
    }

    [Fact]
    public void Persisted_Canonical_Order_Survives_Service_Restart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"chain-order-restart-{Guid.NewGuid():N}.db");
        try
        {
            // Session 1: attach EQ + Comp, reorder them.
            {
                var store = new ChainOrderStore(NullLogger<ChainOrderStore>.Instance, dbPath);
                var svc = new ChainOrderService(store, new StreamingHub(NullLogger<StreamingHub>.Instance), NullLogger<ChainOrderService>.Instance);
                var eq = ChainOrderService.DefaultOrder[3];
                var comp = ChainOrderService.DefaultOrder[4];
                svc.OnPluginAttached(eq, new[] { eq });
                svc.OnPluginAttached(comp, new[] { eq, comp });
                svc.TrySetOrder(new[] { comp, eq }, out _);
                store.Dispose();
            }
            // Session 2: re-open. Canonical should retain the merged order.
            {
                var store = new ChainOrderStore(NullLogger<ChainOrderStore>.Instance, dbPath);
                var svc = new ChainOrderService(store, new StreamingHub(NullLogger<StreamingHub>.Instance), NullLogger<ChainOrderService>.Instance);
                var canonical = svc.CanonicalOrderForTest;
                var eq = ChainOrderService.DefaultOrder[3];
                var comp = ChainOrderService.DefaultOrder[4];
                Assert.Equal(comp, canonical[3]); // operator's choice preserved
                Assert.Equal(eq, canonical[4]);
                // Runtime is still empty until plugins attach this session.
                Assert.Empty(svc.CurrentOrder);
                store.Dispose();
            }
        }
        finally { if (File.Exists(dbPath)) File.Delete(dbPath); }
    }

    /// <summary>
    /// End-to-end signal-flow proof: reordering the chain via
    /// TrySetOrder MUST actually change the order in which audio
    /// samples flow through the plugins, not just the visual /
    /// metadata order. This test mirrors what AudioPluginBridge
    /// does when ChainOrderService.OrderChanged fires:
    /// ReapplySlotsUnderLock clears all AudioChain slots and
    /// repopulates them in CurrentOrder sequence at sequential
    /// indices. Combined with AudioChain's existing
    /// "TwoSlots_Compose_Left_To_Right" semantics, this proves
    /// that a UI reorder produces a real DSP reorder.
    /// </summary>
    [Fact]
    public void Reorder_Changes_Actual_Signal_Flow_Through_AudioChain()
    {
        var (svc, store, _, dbPath) = MakeService();
        try
        {
            // Two synthetic plugins with non-commutative effects so
            // we can audibly distinguish ordering by inspecting the
            // chain's output. AddOne adds 1.0 to every sample;
            // MultiplyByTwo multiplies. (a+1)*2 != (a*2)+1 for any a.
            var addId = "test.add-one";
            var mulId = "test.multiply-two";
            var addPlugin = new AddOnePlugin();
            var mulPlugin = new MultiplyTwoPlugin();
            svc.OnPluginAttached(addId, new[] { addId });
            svc.OnPluginAttached(mulId, new[] { addId, mulId });

            var chain = new AudioChain();
            // Re-slot helper that mirrors AudioPluginBridge.ReapplySlotsUnderLock:
            // clear all 8 slots, then walk CurrentOrder and SetSlot each
            // plugin at sequential indices. This is the EXACT data flow
            // the production bridge applies on OrderChanged.
            void Reslot()
            {
                for (int i = 0; i < chain.SlotCount; i++) chain.ClearSlot(i);
                int idx = 0;
                foreach (var id in svc.CurrentOrder)
                {
                    IAudioPlugin? p = id == addId ? addPlugin : id == mulId ? mulPlugin : null;
                    if (p is not null) { chain.SetSlot(idx, p); idx++; }
                }
                chain.MasterEnabled = idx > 0;
            }
            // Initial order is [add, mul] (attached in that order, both
            // append to v2-default positions in canonical, runtime view
            // mirrors). Wire the OrderChanged event to the Reslot helper
            // so subsequent TrySetOrder calls re-slot automatically.
            svc.OrderChanged += _ => Reslot();
            Reslot();
            Assert.Equal(new[] { addId, mulId }, svc.CurrentOrder);

            // Process [1, 2, 3, 4] through chain [add, mul] → (x+1)*2.
            Span<float> input = stackalloc float[] { 1f, 2f, 3f, 4f };
            Span<float> output = stackalloc float[4];
            var ctx = new AudioBlockContext(48000, 1, 4, 0, false);
            chain.Process(input, output, ctx);
            Assert.Equal<float>(new float[] { 4f, 6f, 8f, 10f }, output.ToArray());

            // Reorder to [mul, add]; this should change the signal flow.
            var ok = svc.TrySetOrder(new[] { mulId, addId }, out var err);
            Assert.True(ok, err);
            Assert.Equal(new[] { mulId, addId }, svc.CurrentOrder);

            // Re-run with the same input through chain [mul, add] → (x*2)+1.
            // If only the UI changed and the chain ignored the reorder,
            // we'd still see (x+1)*2 → [4, 6, 8, 10]. The new expected
            // values [3, 5, 7, 9] PROVE the signal flow follows the
            // reorder, not just the metadata.
            chain.Process(input, output, ctx);
            Assert.Equal<float>(new float[] { 3f, 5f, 7f, 9f }, output.ToArray());
        }
        finally { store.Dispose(); File.Delete(dbPath); }
    }

    private sealed class AddOnePlugin : IAudioPlugin
    {
        public string DisplayName => "add+1";
        public AudioPluginRequirements Requirements => new(48000, 1, 256);
        public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct) => Task.CompletedTask;
        public Task ShutdownAudioAsync(CancellationToken ct) => Task.CompletedTask;
        public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
        {
            for (int i = 0; i < input.Length; i++) output[i] = input[i] + 1f;
        }
    }

    private sealed class MultiplyTwoPlugin : IAudioPlugin
    {
        public string DisplayName => "mul*2";
        public AudioPluginRequirements Requirements => new(48000, 1, 256);
        public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct) => Task.CompletedTask;
        public Task ShutdownAudioAsync(CancellationToken ct) => Task.CompletedTask;
        public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
        {
            for (int i = 0; i < input.Length; i++) output[i] = input[i] * 2f;
        }
    }

    [Fact]
    public void Reattach_Restores_Operator_Chosen_Position()
    {
        var (svc, store, _, dbPath) = MakeService();
        try
        {
            // Attach three, reorder to [c, b, a], detach b, re-attach b.
            // b should land back between c and a (where the operator
            // put it), not at the end.
            var a = ChainOrderService.DefaultOrder[3]; // eq
            var b = ChainOrderService.DefaultOrder[4]; // comp
            var c = ChainOrderService.DefaultOrder[5]; // exciter
            svc.OnPluginAttached(a, new[] { a });
            svc.OnPluginAttached(b, new[] { a, b });
            svc.OnPluginAttached(c, new[] { a, b, c });
            svc.TrySetOrder(new[] { c, b, a }, out _);
            Assert.Equal(new[] { c, b, a }, svc.CurrentOrder);

            svc.OnPluginDetached(b);
            Assert.Equal(new[] { c, a }, svc.CurrentOrder);

            svc.OnPluginAttached(b, new[] { a, b, c });
            Assert.Equal(new[] { c, b, a }, svc.CurrentOrder); // b restored to chosen slot
        }
        finally { store.Dispose(); File.Delete(dbPath); }
    }
}
