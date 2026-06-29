// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Hosting;

namespace Zeus.Server;

/// <summary>
/// Early repair for native TX audio profiles imported by older builds that
/// accidentally unparked VST/AU plugins. Runs before AudioPluginBridge adopts
/// active plugins, so a poisoned last-loaded profile cannot force a VST/AU load
/// during startup before TxAudioProfileService gets to sanitize the replay.
/// </summary>
public sealed class TxAudioProfileStartupRepairService : IHostedService
{
    private readonly TxAudioProfileStore _profiles;
    private readonly ChainOrderStore _chainOrder;
    private readonly ILogger<TxAudioProfileStartupRepairService> _log;

    public TxAudioProfileStartupRepairService(
        TxAudioProfileStore profiles,
        ChainOrderStore chainOrder,
        ILogger<TxAudioProfileStartupRepairService> log)
    {
        _profiles = profiles;
        _chainOrder = chainOrder;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try { RepairNativeProfileVstChain(); }
        catch (Exception ex) { _log.LogWarning(ex, "TX audio profile startup repair failed"); }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void RepairNativeProfileVstChain()
    {
        var lastId = _profiles.GetLastLoadedId();
        if (string.IsNullOrWhiteSpace(lastId)) return;

        var profile = _profiles.Get(lastId);
        if (profile is null) return;
        if (string.Equals(profile.ProcessingMode, "vst", StringComparison.OrdinalIgnoreCase)) return;

        var unsafeIds = profile.ChainOrder
            .Where(LooksLikeVstOrAuPluginId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (unsafeIds.Length == 0) return;

        var order = _chainOrder.GetOrder();
        if (order is null || order.Count == 0) return;

        var parked = new HashSet<string>(_chainOrder.GetParked(), StringComparer.Ordinal);
        var ordered = new HashSet<string>(order, StringComparer.Ordinal);
        var changed = false;
        foreach (var id in unsafeIds)
        {
            if (ordered.Contains(id))
                changed |= parked.Add(id);
        }

        if (!changed) return;

        _chainOrder.SetState(order, parked.ToList());
        _log.LogWarning(
            "Parked {Count} VST/AU plugin(s) from native last-loaded TX audio profile '{ProfileId}' before audio-chain startup",
            unsafeIds.Length,
            profile.Id);
    }

    private static bool LooksLikeVstOrAuPluginId(string pluginId) =>
        pluginId.Contains(".vst.", StringComparison.OrdinalIgnoreCase)
        || pluginId.Contains(".rxvst.", StringComparison.OrdinalIgnoreCase)
        || pluginId.Contains(".au.", StringComparison.OrdinalIgnoreCase)
        || pluginId.Contains(".rxau.", StringComparison.OrdinalIgnoreCase);
}
