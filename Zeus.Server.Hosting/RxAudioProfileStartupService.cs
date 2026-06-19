// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Hosting;

namespace Zeus.Server;

/// <summary>
/// Re-applies the operator's selected RX Audio Suite profile during startup so
/// RX VST state blobs reload before receive audio starts relying on the chain.
/// </summary>
public sealed class RxAudioProfileStartupService : IHostedService
{
    private readonly RxAudioProfileService _profiles;
    private readonly ILogger<RxAudioProfileStartupService> _log;

    public RxAudioProfileStartupService(
        RxAudioProfileService profiles,
        ILogger<RxAudioProfileStartupService> log)
    {
        _profiles = profiles;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var selected = _profiles.SelectedProfileName;
        if (string.IsNullOrWhiteSpace(selected)) return;

        var applied = await _profiles.ApplySelectedAsync(cancellationToken).ConfigureAwait(false);
        if (applied is null)
        {
            _profiles.ClearSelectedProfile();
            _log.LogWarning(
                "Cleared stale RX audio profile selection '{Name}' because the profile no longer exists.",
                selected);
            return;
        }

        _log.LogInformation("RX audio profile '{Name}' restored on startup.", applied.Name);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
