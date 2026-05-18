// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Owns the canonical Audio Suite chain order — the ordered list of
/// installed audio plugin IDs that drives the slot assignment in
/// <see cref="AudioPluginBridge"/> and the tile sequence in the
/// frontend's Audio Suite window.
///
/// <para>Source of truth flow:</para>
/// <list type="number">
/// <item>On construction, load the persisted order from
/// <see cref="ChainOrderStore"/>. If null (first run), seed with
/// <see cref="DefaultOrder"/>.</item>
/// <item>When a plugin attaches, <see cref="AudioPluginBridge"/>
/// calls <see cref="OnPluginAttached"/>; if the ID isn't already
/// in the order, append to the end. Either way return the slot
/// index the plugin should occupy.</item>
/// <item>When a plugin detaches, <see cref="OnPluginDetached"/>
/// is called; the ID stays in the persisted order (so re-install
/// restores the operator's chosen position) but is not present in
/// the runtime chain.</item>
/// <item>When the operator drags tiles in the Audio Suite window,
/// the frontend PUTs the new order to /api/plugins/chain/order
/// which calls <see cref="SetOrder"/>. The service validates
/// (must be a permutation of currently-attached IDs PLUS optional
/// known-but-uninstalled IDs), persists, and raises
/// <see cref="OrderChanged"/> so <see cref="AudioPluginBridge"/>
/// re-slots the chain.</item>
/// </list>
///
/// <para>Default order matches the v2 roadmap (issue #332): Gate
/// → DownExp → Tube → EQ → Comp → Exciter → Bass → Reverb. Plugins
/// not in this list (third-party blocks) append to the end in
/// install order.</para>
///
/// <para>Thread safety: all mutating methods take <c>_sync</c>.
/// Reads of <see cref="CurrentOrder"/> return a fresh snapshot
/// (defensive copy) so callers can iterate without holding the
/// lock. The bridge subscribes to <see cref="OrderChanged"/> off
/// the lock; the event fires AFTER the lock is released so a
/// subscriber that calls back into the service can't deadlock.</para>
/// </summary>
public sealed class ChainOrderService
{
    /// <summary>
    /// Default Audio Suite chain order (v2 roadmap, KB2UKA confirmed
    /// 2026-05-18). Order is signal flow top → bottom: Gate cleans
    /// mic noise → DownExp shapes dynamics below threshold → Tube
    /// adds saturation color → EQ corrective shaping → Compressor
    /// level control → Exciter harmonic excitement → Bass low-end
    /// enhancement → Reverb spatial. Plugins later in the list that
    /// aren't installed (Gate / DownExp / Tube ship in Phase 4) are
    /// skipped at runtime — order honors the IDs that are present.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultOrder = new[]
    {
        "com.openhpsdr.zeus.samples.gate",
        "com.openhpsdr.zeus.samples.downexp",
        "com.openhpsdr.zeus.samples.tube",
        "com.openhpsdr.zeus.samples.eq",
        "com.openhpsdr.zeus.samples.compressor",
        "com.openhpsdr.zeus.samples.exciter",
        "com.openhpsdr.zeus.samples.bass",
        "com.openhpsdr.zeus.samples.reverb",
    };

    private readonly ChainOrderStore _store;
    private readonly StreamingHub _hub;
    private readonly ILogger<ChainOrderService> _log;
    private readonly object _sync = new();
    private readonly List<string> _order;

    /// <summary>
    /// Fires AFTER the order is persisted, with the new order as
    /// the argument. Listeners (notably <see cref="AudioPluginBridge"/>)
    /// react by re-slotting the runtime chain. Fired off-lock.
    /// </summary>
    public event Action<IReadOnlyList<string>>? OrderChanged;

    public ChainOrderService(ChainOrderStore store, StreamingHub hub, ILogger<ChainOrderService> log)
    {
        _store = store;
        _hub = hub;
        _log = log;

        var persisted = _store.GetOrder();
        if (persisted is null || persisted.Count == 0)
        {
            // First run — seed with the default v2 order. Don't
            // persist yet; only persist on the first explicit
            // mutation (operator drag or plugin install/uninstall).
            _order = DefaultOrder.ToList();
            _log.LogInformation(
                "ChainOrderService seeded with default v2 order ({Count} entries)",
                _order.Count);
        }
        else
        {
            _order = persisted.ToList();
            _log.LogInformation(
                "ChainOrderService loaded {Count} persisted entries", _order.Count);
        }
    }

    /// <summary>
    /// Snapshot of the canonical order. Returns a fresh copy so
    /// the caller can iterate / index without holding _sync.
    /// </summary>
    public IReadOnlyList<string> CurrentOrder
    {
        get { lock (_sync) return _order.ToList(); }
    }

    /// <summary>
    /// Called by <see cref="AudioPluginBridge"/> when a plugin
    /// activates. Appends the ID to the end of the order if it's
    /// not already present (preserving any persisted position
    /// from a previous install). Returns the slot index the
    /// plugin should occupy among the currently-installed subset
    /// (computed off the order). The bridge uses the returned
    /// index as <c>_chain.SetSlot(index, plugin)</c>.
    /// </summary>
    public int OnPluginAttached(string pluginId, IReadOnlyCollection<string> currentlyAttachedIds)
    {
        bool changed = false;
        IReadOnlyList<string>? snapshotForEvent = null;
        lock (_sync)
        {
            if (!_order.Contains(pluginId))
            {
                _order.Add(pluginId);
                changed = true;
            }
            if (changed)
            {
                _store.SetOrder(_order);
                snapshotForEvent = _order.ToList();
            }
        }
        if (changed)
        {
            BroadcastOrder(snapshotForEvent!);
            // We don't raise OrderChanged here — the bridge is
            // the caller, it's already in the middle of slotting,
            // and raising the event would re-enter the bridge
            // with a stale snapshot. The bridge uses the slot
            // index we return directly.
        }
        return ComputeSlotForAttached(pluginId, currentlyAttachedIds);
    }

    /// <summary>
    /// Called by <see cref="AudioPluginBridge"/> when a plugin
    /// deactivates. The ID stays in the persisted order — if the
    /// operator reinstalls it, the position is restored. We don't
    /// re-slot the rest of the chain on detach; the bridge just
    /// clears its slot and other plugins keep their indices.
    /// </summary>
    public void OnPluginDetached(string pluginId)
    {
        // No state change on detach — we keep the ID so reinstall
        // restores position. This is a hook for future telemetry
        // / logging; today it's a no-op aside from the log line.
        _log.LogDebug("ChainOrderService noted detach of {Id}", pluginId);
    }

    /// <summary>
    /// REST endpoint entry point — operator drag-dropped a tile
    /// and the frontend sent a new ordering. Validates the new
    /// order is a permutation of the current persisted order
    /// (no IDs added, no IDs dropped; just resequenced), persists,
    /// raises <see cref="OrderChanged"/>, and broadcasts the new
    /// order to all connected clients.
    ///
    /// <para>Returns true on success, false with <paramref name="error"/>
    /// populated on validation failure.</para>
    /// </summary>
    public bool TrySetOrder(IReadOnlyList<string> newOrder, out string? error)
    {
        IReadOnlyList<string>? snapshotForEvent = null;
        lock (_sync)
        {
            var current = new HashSet<string>(_order, StringComparer.Ordinal);
            var proposed = new HashSet<string>(newOrder, StringComparer.Ordinal);
            if (!current.SetEquals(proposed))
            {
                error =
                    $"chain order PUT must be a permutation of the current order ({_order.Count} entries); " +
                    $"got {newOrder.Count} entries with {proposed.Count - current.Count} difference. " +
                    $"Install / uninstall plugins to change set membership.";
                return false;
            }
            _order.Clear();
            _order.AddRange(newOrder);
            _store.SetOrder(_order);
            snapshotForEvent = _order.ToList();
        }
        // Raise + broadcast OFF the lock so subscribers (including
        // ones that call back into ChainOrderService) can't deadlock.
        OrderChanged?.Invoke(snapshotForEvent!);
        BroadcastOrder(snapshotForEvent!);
        error = null;
        return true;
    }

    /// <summary>
    /// Compute the canonical slot index for a plugin given the
    /// current set of attached plugin IDs. The slot index is the
    /// plugin's position in the persisted order among the subset
    /// that is currently attached — so if the persisted order is
    /// [Gate, DownExp, Tube, EQ, Comp] but only [EQ, Comp] are
    /// installed, EQ gets slot 0 and Comp gets slot 1 (Gate /
    /// DownExp / Tube are skipped as they're not present).
    /// </summary>
    private int ComputeSlotForAttached(string pluginId, IReadOnlyCollection<string> currentlyAttachedIds)
    {
        IReadOnlyList<string> snapshot;
        lock (_sync) snapshot = _order.ToList();

        var attachedSet = currentlyAttachedIds is HashSet<string> hs
            ? hs
            : new HashSet<string>(currentlyAttachedIds, StringComparer.Ordinal);

        int slotIndex = 0;
        for (int i = 0; i < snapshot.Count; i++)
        {
            var id = snapshot[i];
            if (string.Equals(id, pluginId, StringComparison.Ordinal))
                return slotIndex;
            if (attachedSet.Contains(id)) slotIndex++;
        }
        // Defensive — pluginId wasn't in _order. OnPluginAttached
        // should have already appended it before this is reached;
        // fall back to the end of the chain.
        return slotIndex;
    }

    private void BroadcastOrder(IReadOnlyList<string> order)
    {
        try
        {
            _hub.Broadcast(new AudioChainOrderFrame(order));
        }
        catch (Exception ex)
        {
            // Broadcast failure is non-fatal — the persisted order
            // is the source of truth; clients reconnect and pull
            // it via GET /api/plugins/chain/order if they missed
            // the broadcast.
            _log.LogWarning(ex, "ChainOrderService broadcast threw");
        }
    }
}
