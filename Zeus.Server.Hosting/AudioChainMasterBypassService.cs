// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Hosting;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Owns the Audio Suite master-bypass lever. One operator-facing
/// toggle that disengages the entire plugin chain (NoiseGate / EQ /
/// Comp / Exciter / Bass / Reverb) in a single click, instead of
/// requiring per-plugin bypass clicks.
///
/// <para><b>Default on first install:</b> <c>true</c> (bypassed). A
/// brand-new operator who just downloaded the Audio Suite installer
/// gets an inert chain until they click master bypass off. This
/// prevents an unfamiliar processing chain from quietly transforming
/// their first TX before they've configured it.</para>
///
/// <para><b>Persistence:</b> <see cref="AudioChainSettingsStore"/>.
/// On startup, this service reads the persisted state and writes it
/// through to <see cref="AudioPluginBridge.SetMasterBypassed"/> BEFORE
/// any audio flows. On every operator toggle, this service updates
/// in-memory state, persists, writes through to the bridge, and
/// broadcasts <see cref="AudioMasterBypassFrame"/> (0x1F) so all
/// connected clients (LAN-share phone, second browser) stay in sync.</para>
///
/// <para><b>Independence from CFC:</b> master bypass only touches the
/// plugin chain in <see cref="AudioChain"/>. WDSP's CFC sits one layer
/// downstream and honours its own operator setting (TX panel "CFC"
/// toggle) — never read or written by this service.</para>
///
/// <para><b>Independence from per-plugin bypass:</b> the per-slot
/// bypass flags in <see cref="AudioChain"/> are not read or written
/// by this service. Flipping master bypass back off restores the
/// chain to whatever per-plugin bypass states the operator last left.</para>
///
/// <para><b>Thread safety:</b> all mutating methods take <c>_sync</c>.
/// The write-through to the chain is a single <c>volatile bool</c>
/// (no lock needed in the realtime path). Broadcast fires OFF the
/// lock so a subscriber that calls back into the service can't
/// deadlock.</para>
/// </summary>
public sealed class AudioChainMasterBypassService : IHostedService
{
    private readonly AudioChainSettingsStore _store;
    private readonly AudioPluginBridge _bridge;
    private readonly StreamingHub _hub;
    private readonly ILogger<AudioChainMasterBypassService> _log;
    private readonly object _sync = new();
    private bool _bypassed;

    /// <summary>
    /// Fires AFTER the new state is persisted and written through to
    /// the chain. Fired off-lock.
    /// </summary>
    public event Action<bool>? MasterBypassedChanged;

    public AudioChainMasterBypassService(
        AudioChainSettingsStore store,
        AudioPluginBridge bridge,
        StreamingHub hub,
        ILogger<AudioChainMasterBypassService> log)
    {
        _store = store;
        _bridge = bridge;
        _hub = hub;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        // Read persisted state. Null on first run = "default to true
        // (bypassed)" so a brand-new operator's chain is inert.
        var persisted = _store.GetMasterBypassed();
        var initial = persisted ?? true;

        lock (_sync) _bypassed = initial;
        _bridge.SetMasterBypassed(initial);

        if (persisted is null)
        {
            _log.LogInformation(
                "AudioChainMasterBypassService initialised; default master bypass = true (first run, no persisted row).");
        }
        else
        {
            _log.LogInformation(
                "AudioChainMasterBypassService initialised; master bypass = {Bypassed} (persisted)",
                initial);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Current master-bypass state.</summary>
    public bool IsBypassed
    {
        get { lock (_sync) return _bypassed; }
    }

    /// <summary>
    /// Set master bypass. Updates in-memory state, persists, writes
    /// through to the chain, broadcasts <see cref="AudioMasterBypassFrame"/>,
    /// and fires <see cref="MasterBypassedChanged"/>. Idempotent — no
    /// work / no broadcast if the new value matches the current value.
    /// </summary>
    public void SetMasterBypassed(bool bypassed)
    {
        bool changed;
        lock (_sync)
        {
            changed = _bypassed != bypassed;
            if (changed) _bypassed = bypassed;
        }
        if (!changed) return;

        // Persist BEFORE write-through so a crash between the two
        // leaves disk reflecting what the realtime engine will read
        // on next boot (consistency on restart > a single muted block).
        try
        {
            _store.SetMasterBypassed(bypassed);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AudioChainMasterBypassService persist threw");
        }

        _bridge.SetMasterBypassed(bypassed);

        try
        {
            _hub.Broadcast(new AudioMasterBypassFrame(bypassed));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AudioChainMasterBypassService broadcast threw");
        }

        MasterBypassedChanged?.Invoke(bypassed);
        _log.LogInformation("Audio suite master bypass set to {Bypassed}", bypassed);
    }
}
