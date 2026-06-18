// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server;

/// <summary>
/// Saves and applies receive-side Audio Suite profiles without touching TX.
/// </summary>
public sealed class RxAudioProfileService
{
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(3);

    private readonly RxAudioProfileStore _store;
    private readonly RxChainOrderService _chainOrder;
    private readonly AudioChainMasterBypassService _masterBypass;
    private readonly RxVstEngineService _rxVst;
    private readonly ILogger<RxAudioProfileService> _log;

    public RxAudioProfileService(
        RxAudioProfileStore store,
        RxChainOrderService chainOrder,
        AudioChainMasterBypassService masterBypass,
        RxVstEngineService rxVst,
        ILogger<RxAudioProfileService> log)
    {
        _store = store;
        _chainOrder = chainOrder;
        _masterBypass = masterBypass;
        _rxVst = rxVst;
        _log = log;
    }

    public IReadOnlyList<RxAudioProfileEntry> List() => _store.List();

    public async Task<RxAudioProfileEntry> SaveCurrentAsync(string name)
    {
        var states = await _rxVst.CaptureChainStatesAsync(CaptureTimeout).ConfigureAwait(false);
        var entry = _store.Save(
            name,
            _chainOrder.CurrentOrder,
            _chainOrder.ParkedIds,
            _masterBypass.IsRxBypassed,
            states);
        _log.LogInformation(
            "RX audio profile '{Name}' saved ({Active} active, {Parked} parked, {States} VST states, masterBypass={Bypass})",
            name,
            entry.Order.Count,
            entry.Parked.Count,
            states.Count,
            entry.MasterBypass);
        return entry;
    }

    public Task<RxAudioProfileEntry?> ApplyAsync(string name, CancellationToken ct = default)
    {
        var profile = _store.Get(name);
        if (profile is null) return Task.FromResult<RxAudioProfileEntry?>(null);

        _rxVst.SetPluginStates(profile.PluginStates);
        _chainOrder.ApplyMembershipAndOrder(profile.Order, profile.Parked);
        _masterBypass.SetRxMasterBypassed(profile.MasterBypass);
        _log.LogInformation(
            "RX audio profile '{Name}' applied ({States} VST states restored)",
            name,
            profile.PluginStates.Count);
        return Task.FromResult<RxAudioProfileEntry?>(profile);
    }

    public bool Delete(string name) => _store.Delete(name);
}
