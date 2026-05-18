// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Owns the Audio Suite chain order. Two layered concepts:
///
/// <para><b>Canonical order</b> — the persisted, position-preserving
/// list of every plugin ID ever seen. Includes IDs that are not
/// currently installed so that a reinstall restores the operator's
/// previously chosen position. Seeded on first run with
/// <see cref="DefaultOrder"/>.</para>
///
/// <para><b>Runtime order</b> — the canonical order filtered to only
/// the currently-attached plugins. This is what clients see via
/// <see cref="CurrentOrder"/>, what
/// <c>GET /api/plugins/chain/order</c> returns, and what the
/// AudioChainOrder WS broadcast carries. The Audio Suite tile strip
/// renders this directly with no further filtering.</para>
///
/// <para>Source-of-truth flow:</para>
/// <list type="number">
/// <item>On construction, load the persisted canonical from
/// <see cref="ChainOrderStore"/>. If null (first run), seed with
/// <see cref="DefaultOrder"/> and DO NOT persist (only persist on
/// the first explicit mutation).</item>
/// <item>When a plugin attaches, <see cref="AudioPluginBridge"/>
/// calls <see cref="OnPluginAttached"/>. The ID is added to
/// <c>_attached</c>. If the ID isn't already in the canonical
/// order, insert at the position dictated by
/// <see cref="DefaultOrder"/> (preserves v2 layout for new
/// installs); fall through to "append" if the ID isn't a known
/// v2/v3 plugin. Returns the runtime slot index the plugin should
/// occupy.</item>
/// <item>When a plugin detaches, <see cref="OnPluginDetached"/>
/// removes the ID from <c>_attached</c>. The ID stays in the
/// canonical order so re-install restores position.</item>
/// <item>When the operator drags tiles in the Audio Suite window,
/// the frontend PUTs the new RUNTIME order (installed plugins only)
/// to <c>/api/plugins/chain/order</c>, which calls
/// <see cref="TrySetOrder"/>. The service validates the PUT body
/// is exactly the currently-attached set, then merges by
/// position-replacement back into canonical: walk canonical, and
/// every slot occupied by an attached plugin gets replaced by the
/// next entry from the PUT body. Uninstalled IDs stay where they
/// were in canonical. Persists, raises <see cref="OrderChanged"/>,
/// and broadcasts the new runtime order.</item>
/// </list>
///
/// <para>Thread safety: all mutating methods take <c>_sync</c>.
/// Reads of <see cref="CurrentOrder"/> return a fresh snapshot so
/// callers can iterate without holding the lock.
/// <see cref="OrderChanged"/> and the WS broadcast fire OFF the
/// lock so a subscriber that calls back into the service can't
/// deadlock.</para>
/// </summary>
public sealed class ChainOrderService
{
    /// <summary>
    /// Default Audio Suite chain order (v2 roadmap, KB2UKA confirmed
    /// 2026-05-18). Order is signal flow top → bottom: Gate cleans
    /// mic noise → DownExp shapes dynamics below threshold → Tube
    /// adds saturation color → EQ corrective shaping → Compressor
    /// level control → Exciter harmonic excitement → Bass low-end
    /// enhancement → Reverb spatial. Seeded into the canonical order
    /// so new installs of these IDs land in their intended v2 slot.
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
    // Canonical: the persisted, position-preserving order across all
    // ever-seen plugin IDs. May contain IDs that are not currently
    // attached — that's the whole point (preserves position on reinstall).
    private readonly List<string> _canonical;
    // Runtime: which IDs are currently attached. Mutated by
    // OnPluginAttached / OnPluginDetached. The runtime order is
    // derived as _canonical.Where(_attached.Contains).
    private readonly HashSet<string> _attached = new(StringComparer.Ordinal);

    /// <summary>
    /// Fires AFTER the order is persisted, with the new RUNTIME
    /// order as the argument. Listeners (notably
    /// <see cref="AudioPluginBridge"/>) react by re-slotting the
    /// runtime chain. Fired off-lock.
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
            // First run — seed canonical with the default v2 order so
            // the FIRST install of each v2 plugin lands in its
            // intended slot. Don't persist yet; only persist on the
            // first explicit mutation (plugin attach with new ID OR
            // operator drag).
            _canonical = DefaultOrder.ToList();
            _log.LogInformation(
                "ChainOrderService seeded canonical with default v2 order ({Count} entries)",
                _canonical.Count);
        }
        else
        {
            _canonical = persisted.ToList();
            _log.LogInformation(
                "ChainOrderService loaded {Count} canonical entries", _canonical.Count);
        }
    }

    /// <summary>
    /// Snapshot of the RUNTIME order — the canonical order filtered
    /// to only the currently-attached plugins. Returns a fresh copy
    /// so the caller can iterate without holding _sync. This is what
    /// the REST GET / WS broadcast carry — clients never see the
    /// uninstalled entries that may be sitting in canonical.
    /// </summary>
    public IReadOnlyList<string> CurrentOrder
    {
        get { lock (_sync) return RuntimeOrderUnderLock(); }
    }

    /// <summary>
    /// Test helper — snapshot of the canonical (persisted) order
    /// including any uninstalled entries. Tests use this to verify
    /// that position-preserve-on-detach behaviour works.
    /// </summary>
    internal IReadOnlyList<string> CanonicalOrderForTest
    {
        get { lock (_sync) return _canonical.ToList(); }
    }

    private List<string> RuntimeOrderUnderLock()
    {
        var result = new List<string>(_attached.Count);
        for (int i = 0; i < _canonical.Count; i++)
        {
            if (_attached.Contains(_canonical[i])) result.Add(_canonical[i]);
        }
        return result;
    }

    /// <summary>
    /// Called by <see cref="AudioPluginBridge"/> when a plugin
    /// activates. Marks the ID as attached, inserts into the
    /// canonical order at the v2-default position if it's a new
    /// (never-seen) ID, persists if anything changed, broadcasts
    /// the new runtime order. Returns the runtime slot index the
    /// plugin should occupy — the bridge uses it as
    /// <c>_chain.SetSlot(index, plugin)</c>.
    /// </summary>
    /// <param name="pluginId">Plugin ID being attached.</param>
    /// <param name="_">Currently-attached IDs (legacy parameter, no
    /// longer used — the service now tracks attachment internally
    /// via <c>_attached</c>). Kept on the signature so the bridge
    /// call site doesn't need to change.</param>
    public int OnPluginAttached(string pluginId, IReadOnlyCollection<string> _)
    {
        bool changed;
        List<string>? snapshot = null;
        int slotIndex;
        lock (_sync)
        {
            changed = _attached.Add(pluginId);
            if (!_canonical.Contains(pluginId))
            {
                InsertByDefaultOrderUnderLock(pluginId);
                _store.SetOrder(_canonical);
                changed = true; // canonical mutated even if _attached.Add returned false (shouldn't happen but defensive)
            }
            if (changed)
            {
                snapshot = RuntimeOrderUnderLock();
            }
            slotIndex = SlotIndexUnderLock(pluginId);
        }
        if (snapshot is not null) BroadcastOrder(snapshot);
        return slotIndex;
    }

    /// <summary>
    /// Called by <see cref="AudioPluginBridge"/> when a plugin
    /// deactivates. Removes the ID from <c>_attached</c> so it stops
    /// appearing in the runtime order, but leaves it in the
    /// canonical order so the operator's chosen position is
    /// restored on re-install. Broadcasts the new runtime order so
    /// clients refresh their tile strip.
    /// </summary>
    public void OnPluginDetached(string pluginId)
    {
        List<string>? snapshot = null;
        lock (_sync)
        {
            if (_attached.Remove(pluginId))
                snapshot = RuntimeOrderUnderLock();
        }
        if (snapshot is not null) BroadcastOrder(snapshot);
    }

    /// <summary>
    /// REST endpoint entry point — operator drag-dropped a tile and
    /// the frontend sent a new RUNTIME ordering (installed plugins
    /// only). Validates the PUT body is exactly the currently-
    /// attached set, then merges by position-replacement back into
    /// canonical so uninstalled entries keep their slot. Persists,
    /// raises <see cref="OrderChanged"/>, broadcasts the new
    /// runtime order.
    ///
    /// <para>Returns true on success, false with <paramref name="error"/>
    /// populated on validation failure.</para>
    /// </summary>
    public bool TrySetOrder(IReadOnlyList<string> newRuntimeOrder, out string? error)
    {
        List<string>? snapshot = null;
        lock (_sync)
        {
            if (newRuntimeOrder.Count != _attached.Count)
            {
                error =
                    $"chain order PUT must contain exactly the currently-attached plugins " +
                    $"({_attached.Count} entries); got {newRuntimeOrder.Count}. " +
                    $"Install / uninstall plugins to change set membership.";
                return false;
            }
            var proposed = new HashSet<string>(newRuntimeOrder, StringComparer.Ordinal);
            if (!_attached.SetEquals(proposed))
            {
                error =
                    "chain order PUT must be exactly the currently-attached plugins (same set, just reordered); " +
                    "got a different set of IDs.";
                return false;
            }
            // Merge by position-replacement: walk canonical; each slot
            // occupied by an attached ID gets replaced with the next
            // entry from the PUT body. Uninstalled IDs stay put.
            int j = 0;
            for (int i = 0; i < _canonical.Count; i++)
            {
                if (_attached.Contains(_canonical[i]))
                    _canonical[i] = newRuntimeOrder[j++];
            }
            _store.SetOrder(_canonical);
            snapshot = RuntimeOrderUnderLock();
        }
        // Raise + broadcast OFF the lock so subscribers (including
        // ones that call back into ChainOrderService) can't deadlock.
        OrderChanged?.Invoke(snapshot!);
        BroadcastOrder(snapshot!);
        error = null;
        return true;
    }

    /// <summary>
    /// Returns the plugin's runtime slot index — its position in
    /// canonical among the currently-attached subset. Caller must
    /// hold <c>_sync</c>.
    /// </summary>
    private int SlotIndexUnderLock(string pluginId)
    {
        int idx = 0;
        for (int i = 0; i < _canonical.Count; i++)
        {
            var id = _canonical[i];
            if (string.Equals(id, pluginId, StringComparison.Ordinal)) return idx;
            if (_attached.Contains(id)) idx++;
        }
        // Defensive — pluginId not in canonical. OnPluginAttached
        // always inserts before reaching here; fall back to end.
        return idx;
    }

    /// <summary>
    /// Insert a new plugin ID into canonical at the position
    /// dictated by <see cref="DefaultOrder"/>. If the ID isn't in
    /// the default list (third-party plugin), append to the end so
    /// it lands at the bottom of the chain. Caller must hold
    /// <c>_sync</c>.
    /// </summary>
    private void InsertByDefaultOrderUnderLock(string pluginId)
    {
        int defaultIdx = IndexInDefaultOrder(pluginId);
        if (defaultIdx < 0)
        {
            _canonical.Add(pluginId);
            return;
        }
        // Find the first existing canonical entry with a HIGHER
        // default index — insert before it.
        for (int i = 0; i < _canonical.Count; i++)
        {
            int existingDefaultIdx = IndexInDefaultOrder(_canonical[i]);
            if (existingDefaultIdx < 0 || existingDefaultIdx > defaultIdx)
            {
                _canonical.Insert(i, pluginId);
                return;
            }
        }
        _canonical.Add(pluginId);
    }

    private static int IndexInDefaultOrder(string pluginId)
    {
        for (int i = 0; i < DefaultOrder.Count; i++)
        {
            if (string.Equals(DefaultOrder[i], pluginId, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private void BroadcastOrder(IReadOnlyList<string> runtimeOrder)
    {
        try
        {
            _hub.Broadcast(new AudioChainOrderFrame(runtimeOrder));
        }
        catch (Exception ex)
        {
            // Broadcast failure is non-fatal — the persisted canonical
            // is the source of truth; clients reconnect and pull the
            // runtime order via GET /api/plugins/chain/order if they
            // missed the broadcast.
            _log.LogWarning(ex, "ChainOrderService broadcast threw");
        }
    }
}
