// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Owns membership/order for receive-side audio insert plugins
/// (<c>rx.post-demod</c>). New RX VSTs default to parked so scanning a
/// directory never changes live receive audio until the operator explicitly
/// adds the RX plugin to the chain.
/// </summary>
public sealed class RxChainOrderService
{
    private readonly RxChainOrderStore _store;
    private readonly ILogger<RxChainOrderService> _log;
    private readonly StreamingHub? _hub;
    private readonly object _sync = new();
    private readonly List<string> _canonical;
    private readonly HashSet<string> _attached = new(StringComparer.Ordinal);
    private readonly HashSet<string> _parked = new(StringComparer.Ordinal);

    public event Action<IReadOnlyList<string>>? OrderChanged;

    public RxChainOrderService(
        RxChainOrderStore store,
        ILogger<RxChainOrderService> log,
        StreamingHub? hub = null)
    {
        _store = store;
        _log = log;
        _hub = hub;
        _canonical = (_store.GetOrder() ?? Array.Empty<string>()).ToList();
        foreach (var id in _store.GetParked()) _parked.Add(id);
        _log.LogInformation(
            "RxChainOrderService loaded {OrderCount} canonical RX plugin(s), {ParkedCount} parked",
            _canonical.Count,
            _parked.Count);
    }

    public IReadOnlyList<string> CurrentOrder
    {
        get { lock (_sync) return RuntimeOrderUnderLock(); }
    }

    public IReadOnlyList<string> ParkedIds
    {
        get { lock (_sync) return _parked.Where(_attached.Contains).ToList(); }
    }

    internal IReadOnlyList<string> CanonicalOrderForTest
    {
        get { lock (_sync) return _canonical.ToList(); }
    }

    internal IReadOnlyList<string> ParkedForTest
    {
        get { lock (_sync) return _parked.ToList(); }
    }

    public void OnPluginAttached(string pluginId)
    {
        List<string>? snapshot = null;
        lock (_sync)
        {
            var before = RuntimeOrderUnderLock();
            bool changed = _attached.Add(pluginId);
            if (!_canonical.Contains(pluginId))
            {
                _canonical.Add(pluginId);
                _parked.Add(pluginId);
                PersistUnderLock();
                changed = true;
            }
            if (changed)
            {
                var after = RuntimeOrderUnderLock();
                if (!before.SequenceEqual(after)) snapshot = after;
            }
        }
        if (snapshot is not null) PublishOrder(snapshot);
    }

    public void OnPluginDetached(string pluginId)
    {
        List<string>? snapshot = null;
        lock (_sync)
        {
            var before = RuntimeOrderUnderLock();
            if (_attached.Remove(pluginId))
            {
                var after = RuntimeOrderUnderLock();
                if (!before.SequenceEqual(after)) snapshot = after;
            }
        }
        if (snapshot is not null) PublishOrder(snapshot);
    }

    public bool TrySetOrder(IReadOnlyList<string> newRuntimeOrder, out string? error)
    {
        List<string>? snapshot;
        lock (_sync)
        {
            var active = RuntimeOrderUnderLock();
            if (newRuntimeOrder.Count != active.Count)
            {
                error =
                    $"rx chain order PUT must contain exactly the active RX plugins " +
                    $"({active.Count} entries); got {newRuntimeOrder.Count}.";
                return false;
            }
            var proposed = new HashSet<string>(newRuntimeOrder, StringComparer.Ordinal);
            if (!proposed.SetEquals(active))
            {
                error = "rx chain order PUT must be exactly the active RX plugins, just reordered.";
                return false;
            }

            int j = 0;
            for (int i = 0; i < _canonical.Count; i++)
            {
                if (IsActiveUnderLock(_canonical[i]))
                    _canonical[i] = newRuntimeOrder[j++];
            }
            PersistUnderLock();
            snapshot = RuntimeOrderUnderLock();
        }
        PublishOrder(snapshot);
        error = null;
        return true;
    }

    public bool TrySetParked(string pluginId, bool parked, out string? error)
    {
        List<string>? snapshot = null;
        lock (_sync)
        {
            if (!_attached.Contains(pluginId))
            {
                error =
                    $"cannot park / un-park RX plugin '{pluginId}': it is not an attached RX audio plugin.";
                return false;
            }

            bool changed = parked ? _parked.Add(pluginId) : _parked.Remove(pluginId);
            if (!changed)
            {
                error = null;
                return true;
            }

            PersistUnderLock();
            snapshot = RuntimeOrderUnderLock();
        }
        PublishOrder(snapshot);
        error = null;
        return true;
    }

    public void ParkAll(IReadOnlyCollection<string> pluginIds)
    {
        if (pluginIds.Count == 0) return;
        List<string>? snapshot = null;
        lock (_sync)
        {
            bool changed = false;
            foreach (var id in pluginIds)
                if (_attached.Contains(id) && _parked.Add(id))
                    changed = true;
            if (!changed) return;
            PersistUnderLock();
            snapshot = RuntimeOrderUnderLock();
        }
        PublishOrder(snapshot);
    }

    /// <summary>
    /// Apply a saved RX profile's membership and active order in one operation.
    /// Plugins named by the profile but not currently attached are ignored; any
    /// attached plugin omitted by the profile is parked.
    /// </summary>
    public void ApplyMembershipAndOrder(
        IReadOnlyList<string> desiredOrder,
        IReadOnlyCollection<string> desiredParked)
    {
        List<string> snapshot;
        lock (_sync)
        {
            var profileIds = new HashSet<string>(
                desiredOrder.Concat(desiredParked),
                StringComparer.Ordinal);
            var parkSet = new HashSet<string>(desiredParked, StringComparer.Ordinal);

            _parked.Clear();
            foreach (var id in _attached)
            {
                if (parkSet.Contains(id) || !profileIds.Contains(id))
                    _parked.Add(id);
            }

            var active = new HashSet<string>(
                _canonical.Where(IsActiveUnderLock),
                StringComparer.Ordinal);
            var ordered = new List<string>(active.Count);
            var placed = new HashSet<string>(StringComparer.Ordinal);

            foreach (var id in desiredOrder)
                if (active.Contains(id) && placed.Add(id))
                    ordered.Add(id);

            foreach (var id in _canonical)
                if (active.Contains(id) && placed.Add(id))
                    ordered.Add(id);

            int j = 0;
            for (int i = 0; i < _canonical.Count && j < ordered.Count; i++)
            {
                if (IsActiveUnderLock(_canonical[i]))
                    _canonical[i] = ordered[j++];
            }

            PersistUnderLock();
            snapshot = RuntimeOrderUnderLock();
        }
        PublishOrder(snapshot);
    }

    private bool IsActiveUnderLock(string id) => _attached.Contains(id) && !_parked.Contains(id);

    private List<string> RuntimeOrderUnderLock()
    {
        var result = new List<string>(_attached.Count);
        foreach (var id in _canonical)
            if (IsActiveUnderLock(id))
                result.Add(id);
        return result;
    }

    private void PersistUnderLock() => _store.SetState(_canonical, _parked.ToList());

    private void PublishOrder(IReadOnlyList<string> runtimeOrder)
    {
        OrderChanged?.Invoke(runtimeOrder);
        if (_hub is null) return;

        try
        {
            _hub.Broadcast(new RxAudioChainOrderFrame(runtimeOrder));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "RxChainOrderService broadcast threw");
        }
    }
}
