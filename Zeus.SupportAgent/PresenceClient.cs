// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.SupportAgent;

/// <summary>
/// Drives the operator's broker presence: while the L1 "remote diagnostics
/// available" switch is ON, it registers once and then heartbeats on a fixed
/// cadence (well inside the broker's ~90s expiry) so a maintainer sees the
/// operator as online. When availability goes OFF it drops the presence and goes
/// quiet; it never registers while OFF.
///
/// Availability is a live flag (<see cref="SetAvailable"/>) flipped by the
/// backend's <c>SupportStateChanged</c> IPC. The loop is a single long-running
/// task; the delay between heartbeats is injectable so tests can step it
/// deterministically without real time passing.
/// </summary>
public sealed class PresenceClient
{
    /// <summary>Default heartbeat cadence: ~30s, comfortably inside the broker's 90s expiry.</summary>
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly ISupportBrokerClient _broker;
    private readonly TimeSpan _interval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string>? _log;

    private volatile bool _available;
    private bool _registered;

    public PresenceClient(
        ISupportBrokerClient broker,
        bool initiallyAvailable,
        TimeSpan? interval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string>? log = null)
    {
        _broker = broker;
        _available = initiallyAvailable;
        _interval = interval ?? DefaultHeartbeatInterval;
        _delay = delay ?? Task.Delay;
        _log = log;
    }

    /// <summary>Whether presence is currently being advertised (L1 switch on).</summary>
    public bool IsAvailable => _available;

    /// <summary>
    /// Flip the live availability. Going ON makes the next loop iteration register
    /// + heartbeat; going OFF makes the loop drop presence and idle. Safe to call
    /// from the IPC reader thread while the loop runs.
    /// </summary>
    public void SetAvailable(bool available) => _available = available;

    /// <summary>
    /// Run the presence loop until cancelled. Each iteration: if available, ensure
    /// we are registered then heartbeat; if not, drop any standing presence. The
    /// loop owns the register/heartbeat/drop sequencing so callers only flip the
    /// flag. Returns on cancellation, after a best-effort final drop.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await TickAsync(ct).ConfigureAwait(false);
                await _delay(_interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        // Best-effort drop on the way out so the operator goes offline promptly
        // rather than waiting out the broker's expiry window.
        await DropQuietlyAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// One presence iteration, exposed for unit tests so cadence/stop-on-off can be
    /// asserted without driving the full timed loop. Registers on the first
    /// available tick, heartbeats thereafter, and drops once when availability
    /// turns off.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (_available)
        {
            if (!_registered)
            {
                if (await _broker.RegisterAsync(ct).ConfigureAwait(false))
                {
                    _registered = true;
                    _log?.Invoke("presence: registered (remote diagnostics available)");
                }
            }
            else
            {
                await _broker.HeartbeatAsync(ct).ConfigureAwait(false);
            }
        }
        else if (_registered)
        {
            // Availability went off — drop once and stop advertising.
            await _broker.DropAsync(ct).ConfigureAwait(false);
            _registered = false;
            _log?.Invoke("presence: dropped (remote diagnostics unavailable)");
        }
    }

    private async Task DropQuietlyAsync()
    {
        if (!_registered) return;
        try
        {
            await _broker.DropAsync(CancellationToken.None).ConfigureAwait(false);
            _registered = false;
        }
        catch
        {
            // Shutdown drop is best-effort; the broker expires us regardless.
        }
    }
}
