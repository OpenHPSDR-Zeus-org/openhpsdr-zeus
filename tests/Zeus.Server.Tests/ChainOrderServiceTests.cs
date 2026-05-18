// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Unit tests for <see cref="ChainOrderService"/> — the source of
/// truth for the Audio Suite chain order. Covers default seeding,
/// persistence round-trip, attach/detach order maintenance, and the
/// PUT-permutation validation that protects against the frontend
/// trying to "install" plugins via the order endpoint.
/// </summary>
public class ChainOrderServiceTests
{
    private static (ChainOrderService svc, ChainOrderStore store, StreamingHub hub, string dbPath) MakeService()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"chain-order-test-{Guid.NewGuid():N}.db");
        var store = new ChainOrderStore(NullLogger<ChainOrderStore>.Instance, dbPath);
        // Real StreamingHub with no clients — Broadcast(...) short-
        // circuits on _clients.IsEmpty so the test doesn't exercise
        // the wire path. Broadcast invocation correctness is covered
        // by AudioChainOrderFrame's own round-trip tests + manual
        // integration testing.
        var hub = new StreamingHub(NullLogger<StreamingHub>.Instance);
        var svc = new ChainOrderService(store, hub, NullLogger<ChainOrderService>.Instance);
        return (svc, store, hub, dbPath);
    }

    [Fact]
    public void First_Run_Seeds_With_Default_Order()
    {
        var (svc, store, _, dbPath) = MakeService();
        try
        {
            var order = svc.CurrentOrder;
            Assert.Equal(ChainOrderService.DefaultOrder, order);
            // First run does NOT persist — only explicit mutations
            // persist. A fresh process should see null from the store.
            Assert.Null(store.GetOrder());
        }
        finally { store.Dispose(); File.Delete(dbPath); }
    }

    [Fact]
    public void OnPluginAttached_NewId_Appends_And_Persists()
    {
        var (svc, store, _, dbPath) = MakeService();
        try
        {
            var attached = new HashSet<string>(StringComparer.Ordinal);

            // Attach an ID not in the default order — should append.
            var customId = "com.example.thirdparty.plugin";
            attached.Add(customId);
            svc.OnPluginAttached(customId, attached);

            var order = svc.CurrentOrder;
            Assert.Equal(customId, order[^1]);
            // First mutation persists; subsequent boots restore.
            var persisted = store.GetOrder();
            Assert.NotNull(persisted);
            Assert.Equal(customId, persisted![^1]);
        }
        finally { store.Dispose(); File.Delete(dbPath); }
    }

    [Fact]
    public void OnPluginAttached_KnownId_Does_Not_Duplicate()
    {
        var (svc, store, _, dbPath) = MakeService();
        try
        {
            var attached = new HashSet<string>(StringComparer.Ordinal);
            var knownId = ChainOrderService.DefaultOrder[3]; // EQ

            attached.Add(knownId);
            svc.OnPluginAttached(knownId, attached);
            svc.OnPluginAttached(knownId, attached);

            var order = svc.CurrentOrder;
            Assert.Equal(ChainOrderService.DefaultOrder.Count,
                order.Count(id => id == knownId == false ? false : true) +
                order.Count(id => id != knownId));
            // Sharper assertion: EQ appears exactly once.
            Assert.Equal(1, order.Count(id => id == knownId));
        }
        finally { store.Dispose(); File.Delete(dbPath); }
    }

    [Fact]
    public void OnPluginAttached_Returns_Slot_Index_Among_Attached_Subset()
    {
        var (svc, store, _, dbPath) = MakeService();
        try
        {
            var attached = new HashSet<string>(StringComparer.Ordinal);
            // Default order: gate(0) downexp(1) tube(2) eq(3) comp(4) exciter(5) bass(6) reverb(7)
            // Attach only EQ and Compressor — slots should be 0 and 1
            // (their position within the attached subset), not 3 and 4
            // (their position in the full canonical order).
            var eqId = ChainOrderService.DefaultOrder[3];
            var compId = ChainOrderService.DefaultOrder[4];

            attached.Add(eqId);
            var eqSlot = svc.OnPluginAttached(eqId, attached);

            attached.Add(compId);
            var compSlot = svc.OnPluginAttached(compId, attached);

            Assert.Equal(0, eqSlot);
            Assert.Equal(1, compSlot);
        }
        finally { store.Dispose(); File.Delete(dbPath); }
    }

    [Fact]
    public void TrySetOrder_Permutation_Succeeds_And_Fires_OrderChanged()
    {
        var (svc, store, _, dbPath) = MakeService();
        try
        {
            int orderChangedCount = 0;
            IReadOnlyList<string>? lastOrder = null;
            svc.OrderChanged += order => { orderChangedCount++; lastOrder = order; };

            var current = svc.CurrentOrder.ToList();
            current.Reverse(); // still a permutation

            var ok = svc.TrySetOrder(current, out var err);

            Assert.True(ok);
            Assert.Null(err);
            Assert.Equal(current, svc.CurrentOrder);
            Assert.Equal(1, orderChangedCount);
            Assert.Equal(current, lastOrder);
            Assert.Equal(current, store.GetOrder());
        }
        finally { store.Dispose(); File.Delete(dbPath); }
    }

    [Fact]
    public void TrySetOrder_NonPermutation_Fails_Without_Mutating()
    {
        var (svc, store, _, dbPath) = MakeService();
        try
        {
            int orderChangedCount = 0;
            svc.OrderChanged += _ => orderChangedCount++;
            var before = svc.CurrentOrder.ToList();

            // Drop one entry — set membership change, not allowed.
            var bad = before.Take(before.Count - 1).ToList();
            var ok = svc.TrySetOrder(bad, out var err);

            Assert.False(ok);
            Assert.NotNull(err);
            Assert.Equal(before, svc.CurrentOrder);
            Assert.Equal(0, orderChangedCount);
        }
        finally { store.Dispose(); File.Delete(dbPath); }
    }

    [Fact]
    public void Persisted_Order_Survives_Service_Restart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"chain-order-restart-{Guid.NewGuid():N}.db");
        try
        {
            // Session 1 — seed + reorder + dispose.
            {
                var store = new ChainOrderStore(NullLogger<ChainOrderStore>.Instance, dbPath);
                var svc = new ChainOrderService(store, new StreamingHub(NullLogger<StreamingHub>.Instance), NullLogger<ChainOrderService>.Instance);
                var reordered = svc.CurrentOrder.Reverse().ToList();
                svc.TrySetOrder(reordered, out _);
                store.Dispose();
            }
            // Session 2 — re-open; the reversed order should be loaded.
            {
                var store = new ChainOrderStore(NullLogger<ChainOrderStore>.Instance, dbPath);
                var svc = new ChainOrderService(store, new StreamingHub(NullLogger<StreamingHub>.Instance), NullLogger<ChainOrderService>.Instance);
                var expected = ChainOrderService.DefaultOrder.Reverse().ToList();
                Assert.Equal(expected, svc.CurrentOrder);
                store.Dispose();
            }
        }
        finally { if (File.Exists(dbPath)) File.Delete(dbPath); }
    }

}
