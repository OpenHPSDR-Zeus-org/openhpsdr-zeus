// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server;

/// <summary>
/// Orchestrates Audio Suite profiles: capturing the current chain
/// configuration into a named snapshot and applying a saved one back.
/// A profile spans three seams — <see cref="AudioProcessingModeService"/>
/// (Native/VST route), <see cref="ChainOrderService"/> (active order +
/// parked set), and <see cref="AudioChainMasterBypassService"/> (master
/// lever) — so this service is the single place that reads / writes them on
/// the operator's behalf.
/// </summary>
public sealed class AudioProfileService
{
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(3);

    private readonly AudioProfileStore _store;
    private readonly ChainOrderService _chainOrder;
    private readonly AudioChainMasterBypassService _masterBypass;
    private readonly AudioProcessingModeService _mode;
    private readonly ILogger<AudioProfileService> _log;

    public AudioProfileService(
        AudioProfileStore store,
        ChainOrderService chainOrder,
        AudioChainMasterBypassService masterBypass,
        AudioProcessingModeService mode,
        ILogger<AudioProfileService> log)
    {
        _store = store;
        _chainOrder = chainOrder;
        _masterBypass = masterBypass;
        _mode = mode;
        _log = log;
    }

    public IReadOnlyList<AudioProfileEntry> List() => _store.List();

    /// <summary>
    /// Snapshot the live chain config under <paramref name="name"/>
    /// (overwriting an existing profile of the same name). Captures each VST
    /// plugin's full parameter state from the engine so applying the profile
    /// later restores the exact voicing, not just the chain shape.
    /// </summary>
    public async Task<AudioProfileEntry> SaveCurrentAsync(string name)
    {
        var states = await _mode.CaptureChainStatesAsync(CaptureTimeout).ConfigureAwait(false);
        var entry = _store.Save(
            name,
            _mode.Mode,
            _chainOrder.CurrentOrder,
            _chainOrder.ParkedIds,
            _masterBypass.IsBypassed,
            states);
        _log.LogInformation(
            "Audio profile '{Name}' saved (mode={Mode}, {Active} active, {Parked} parked, {States} VST states, masterBypass={Bypass})",
            name, entry.ProcessingMode, entry.Order.Count, entry.Parked.Count, states.Count, entry.MasterBypass);
        return entry;
    }

    /// <summary>
    /// Apply a saved profile by name. Returns null if no such profile.
    /// Order matters: arm the saved per-plugin states FIRST so any VST engine
    /// chain reload restores each plugin's voicing, switch to the route the
    /// profile was saved with, then reconcile membership/order and set the
    /// master lever.
    /// </summary>
    public async Task<AudioProfileEntry?> ApplyAsync(
        string name,
        CancellationToken ct = default)
    {
        var profile = _store.Get(name);
        if (profile is null) return null;

        _mode.SetPluginStates(profile.PluginStates);
        await _mode.SetModeAsync(profile.ProcessingMode, ct).ConfigureAwait(false);
        _chainOrder.ApplyMembershipAndOrder(profile.Order, profile.Parked);
        _masterBypass.SetMasterBypassed(profile.MasterBypass);
        _log.LogInformation(
            "Audio profile '{Name}' applied (mode={Mode}, {States} VST states restored)",
            name, profile.ProcessingMode, profile.PluginStates.Count);
        return profile;
    }

    public bool Delete(string name) => _store.Delete(name);
}
