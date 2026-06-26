// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Net.Http;
using Microsoft.Extensions.Hosting;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Projects the optional KiwiSDR slice receiver into the unified
/// <see cref="StateDto.Receivers"/> list. RadioService consumes this so the Kiwi
/// receiver appears alongside the hardware DDCs without coupling RadioService to
/// the Kiwi transport.
/// </summary>
public interface IKiwiReceiverProvider
{
    /// <summary>The Kiwi receiver entry, or null when the slice is disabled.</summary>
    ReceiverDto? GetKiwiReceiver();

    /// <summary>Fires whenever the Kiwi receiver entry changes (enable/disable,
    /// tuning, mute) so RadioService can re-broadcast state.</summary>
    event Action? KiwiReceiverChanged;
}

/// <summary>
/// Pull-source for the Kiwi slice's demodulated audio. The Kiwi rides the SAME
/// RX-audio mix bus as the hardware receivers: <c>DspPipelineService</c> drains
/// this once per tick (clocked by RX1) and averages it into the single
/// <c>RxId 0</c> output, so every sink (native + WebSocket) hears the Kiwi mixed
/// with the local RX. This is why the Kiwi is NOT published as a second
/// independent <see cref="AudioFrame"/> — two streams on one device ring would
/// concatenate (the "super choppy" symptom) instead of mixing.
/// </summary>
public interface IKiwiAudioBus
{
    /// <summary>True when the slice is enabled, connected and un-muted — i.e.
    /// it should contribute to the mix. A single relaxed read; when false the
    /// DSP tick skips the Kiwi entirely.</summary>
    bool AudioActive { get; }

    /// <summary>Consumer side (DSP tick thread only). Drain up to
    /// <c>dst.Length</c> resampled 48 kHz mono samples for mixing; returns the
    /// count read. Lock-free.</summary>
    int ReadAudio(Span<float> dst);
}

/// <summary>
/// Owns the lifecycle of the KiwiSDR slice receiver: it opens a
/// <see cref="KiwiSdrClient"/> to a remote KiwiSDR, turns the decoded audio +
/// waterfall into the same <see cref="DisplayFrame"/> / <see cref="AudioFrame"/>
/// wire frames a hardware DDC produces (tagged with
/// <see cref="WireContract.KiwiReceiverIndex"/> so the frontend renders it as
/// "Kiwi" beside RX1..RX6), and pushes Zeus tuning down to the Kiwi as SET
/// commands. The Kiwi demodulates server-side, so this is a remote front-end —
/// no WDSP channel is involved.
/// </summary>
public sealed class KiwiSdrService : BackgroundService, IKiwiReceiverProvider, IKiwiAudioBus
{
    private const int RxId = WireContract.KiwiReceiverIndex;
    private const int OutRateHz = 48_000;
    // Lock-free SPSC handoff from the Kiwi audio receive loop (producer) to the
    // DSP mix tick (consumer). Power of two (~1.36 s @ 48 kHz). The Kiwi rides
    // the same RX-audio mix bus as the hardware RXs (see IKiwiAudioBus).
    private const int AudioBusCapacity = 65_536;
    // Waterfall span at zoom level 1 (widest). The slider's zoom level (1..32,
    // shared with the hardware RXs) divides this, so zoom 1 ≈ 192 kHz of band
    // down to ≈ 6 kHz (single-signal) at zoom 32 — the Kiwi follows the same
    // zoom gesture as every other receiver.
    private const double FullSpanHz = 192_000;
    // Fixed panadapter/waterfall width, matching the hardware DDC DisplayFrame
    // width so the frontend renderer treats the Kiwi identically (and never
    // resets on a per-row bin-count wobble).
    private const ushort DisplayWidth = 2048;

    private readonly KiwiSettingsStore _store;
    private readonly StreamingHub _hub;
    // Used to resolve the kiwisdr.com proxy redirect chain to the receiver's real
    // host:port before opening the WebSocket (see ResolveEndpointAsync). Optional
    // so tests can construct the service without an HTTP stack.
    private readonly IHttpClientFactory? _httpFactory;
    // Demodulated 48 kHz mono audio bus, drained by DspPipelineService each tick
    // and averaged into the RX1-clocked mix (IKiwiAudioBus). Single producer
    // (the SND receive loop, via OnAudio), single consumer (the DSP tick).
    private readonly FloatSpscRing _audioBus = new(AudioBusCapacity);
    // Reusable consumer-thread scratch for the drift drop in ReadAudio.
    private readonly float[] _drainScratch = new float[2048];
    private readonly ILogger<KiwiSdrService> _log;
    private readonly ILoggerFactory _loggerFactory;

    private readonly object _sync = new();
    private KiwiSdrClient? _client;
    private string _status = "disabled";
    private string? _statusDetail;
    // Serialises every remote-connection lifecycle transition (startup,
    // /api/kiwi config change, and the radio connect/disconnect callbacks) so
    // they can't race the _client field. Not reentrant — never await another
    // gated method while holding it.
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    // The Kiwi is a receiver like any other: it must not stream until a radio is
    // connected to clock the shared RX audio-mix bus (DspPipelineService.Tick
    // drains the Kiwi bus alongside RX1..n). Tracks the radio link so the slice
    // waits for the radio instead of auto-connecting on a headless/idle server.
    private bool _radioConnected;            // guarded by _sync

    // Projected Kiwi receiver tuning/state.
    private bool _enabled;
    private long _vfoHz = 14_200_000;       // demod (dial) frequency
    private long _centerHz = 14_200_000;    // waterfall centre; frozen under CTUN
    private RxMode _mode = RxMode.USB;
    private int _filterLowHz = 100;
    private int _filterHighHz = 2_850;
    private bool _muted;
    private int _zoomLevel = 1; // 1..32, shared zoom level (see /api/rx/zoom)

    // True while the radio is transmitting (MOX or TUN). The Kiwi is a remote RX
    // monitoring the same band the operator transmits on, so during TX it would
    // play back the operator's own signal (delayed, off-frequency, often loud) —
    // exactly the monitor-feedback we don't want. Mirror the local RX: drop the
    // Kiwi out of the mix while keyed. _txActive = _moxActive || _tunActive, fed
    // by RadioService.MoxChanged / TunActiveChanged; all guarded by _sync.
    private bool _moxActive;
    private bool _tunActive;
    private bool _txActive;

    // RX squelch. The Kiwi demodulates server-side and rides the mix bus (no
    // WDSP), so Zeus's WDSP / adaptive squelch in DspPipelineService never
    // reaches it — the global SQL control was a no-op on the Kiwi. We gate the
    // Kiwi audio HERE instead, using the Kiwi's own per-SND-frame S-meter (dBm),
    // so the same global squelch config drives the Kiwi too. Config is mirrored
    // from RadioService.StateChanged (guarded by _sync). The gate runtime state
    // (_sqlSignalDbm/_sqlNoiseFloorDbm/_sqlGain/_sqlOpen) is touched only on the
    // SND receive-loop thread (OnSignalLevel + OnAudio run there), so it needs
    // no lock.
    private bool _sqlEnabled;            // guarded by _sync
    private bool _sqlAdaptive = true;    // guarded by _sync
    private int _sqlLevel;               // 0..100, higher = tighter; guarded by _sync
    private double _sqlSignalDbm = double.NaN;     // latest Kiwi S-meter (SND thread)
    private double _sqlNoiseFloorDbm = double.NaN; // adaptive floor estimate (SND thread)
    private double _sqlGain = 1.0;       // smoothed gate gain 0..1 (SND thread)
    private bool _sqlOpen = true;        // current gate state w/ hysteresis (SND thread)

    // Fixed-mode threshold maps Level 0..100 linearly onto the Kiwi S-meter dBm
    // range (~-120 dBm noise floor .. ~-20 dBm strong signal): higher Level =
    // higher (tighter) threshold. Adaptive mode tracks a slow noise floor and
    // opens a fixed margin above it. Hysteresis avoids chatter on signals
    // hovering at the threshold.
    private const double SqlFixedFloorDbm = -120.0;
    private const double SqlFixedSpanDb = 100.0; // dBm at Level 100 = floor + span
    private const double SqlOpenMarginDb = 6.0;
    private const double SqlHysteresisDb = 3.0;
    // Per-SND-frame slew (frames arrive ~20-40/s): the floor follows the signal
    // down quickly but creeps up slowly so a brief carrier doesn't raise it.
    private const double SqlFloorFallDbPerFrame = 3.0;
    private const double SqlFloorRiseDbPerFrame = 0.2;
    // Per-sample gain ramp (native ~12 kHz): fast attack (~5 ms) so signal
    // onset isn't clipped, slower release (~50 ms) so tails fade without a click.
    private const double SqlAttackPerSample = 1.0 / 60.0;
    private const double SqlReleasePerSample = 1.0 / 600.0;

    // Waterfall span for the current zoom level. Caller need not hold _sync.
    private double CurrentSpanHz()
    {
        int z = Math.Clamp(Volatile.Read(ref _zoomLevel), 1, 32);
        return FullSpanHz / z;
    }

    // Frame sequencing + audio resampling (48 kHz output from the ~12 kHz Kiwi).
    private uint _displaySeq;
    // 1 Hz rate instrumentation (bring-up): WF frames/s, audio frames/s, audio
    // samples/s. All touched from both socket loop threads → Interlocked.
    private long _rateWindowStartMs;
    private int _wfFramesWindow;
    private int _audioFramesWindow;
    private long _audioSamplesWindow;
    private double _resamplePhase;
    private float _resamplePrev;
    private int _resampleInRate;

    public event Action? KiwiReceiverChanged;

    // Resolved lazily in ExecuteAsync to subscribe to the radio connect/disconnect
    // lifecycle. Injected via IServiceProvider (not a direct RadioService ctor
    // param) to avoid a DI cycle — RadioService depends on IKiwiReceiverProvider,
    // i.e. this service. Optional so unit tests can construct without a container.
    private readonly IServiceProvider? _services;

    public KiwiSdrService(
        KiwiSettingsStore store,
        StreamingHub hub,
        ILoggerFactory loggerFactory,
        IHttpClientFactory? httpFactory = null,
        IServiceProvider? services = null)
    {
        _store = store;
        _hub = hub;
        _loggerFactory = loggerFactory;
        _httpFactory = httpFactory;
        _services = services;
        _log = loggerFactory.CreateLogger<KiwiSdrService>();
    }

    // Public KiwiSDR directory entries are "http://&lt;id&gt;.proxy.kiwisdr.com"
    // URLs that 307-redirect (often several hops) to the operator's REAL endpoint
    // — e.g. 21084.proxy.kiwisdr.com → … → http://web888.servehttp.com:8074. A
    // browser follows the chain; a raw ClientWebSocket does not, so it would hang
    // on "connecting". Resolve the chain with HttpClient (which follows redirects)
    // and connect the WebSocket to the FINAL host:port:scheme. Falls back to the
    // original endpoint on any failure, so a directory that needs no redirect (a
    // direct host:port) is unaffected.
    private async Task<(string Host, int Port, bool Secure)> ResolveEndpointAsync(
        string host, int port, bool secure, CancellationToken ct)
    {
        if (_httpFactory is null) return (host, port, secure);
        try
        {
            var scheme = secure ? "https" : "http";
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(8);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{scheme}://{host}:{port}/");
            req.Headers.TryAddWithoutValidation("User-Agent", "OpenHPSDR-Zeus");
            using var resp = await http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            var final = resp.RequestMessage?.RequestUri;
            if (final is not null && !string.IsNullOrEmpty(final.Host))
            {
                bool sec = final.Scheme == Uri.UriSchemeHttps;
                int p = final.Port > 0 ? final.Port : (sec ? 443 : 80);
                if (!string.Equals(final.Host, host, StringComparison.OrdinalIgnoreCase) || p != port || sec != secure)
                    _log.LogInformation("kiwi.resolve {From} -> {Host}:{Port}", $"{host}:{port}", final.Host, p);
                return (final.Host, p, sec);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug("kiwi.resolve failed for {Host}:{Port}, using as-is err={Err}", host, port, ex.Message);
        }
        return (host, port, secure);
    }

    // -------------------------------------------------------------------------
    // IKiwiAudioBus — the Kiwi rides the SAME mix bus as the hardware RXs.
    // -------------------------------------------------------------------------
    public bool AudioActive
    {
        get { lock (_sync) return _enabled && !_muted && !_txActive && _client is not null; }
    }

    public int ReadAudio(Span<float> dst)
    {
        if (dst.Length == 0) return 0;
        // Independent-clock drift guard. The remote KiwiSDR ADC runs on its own
        // clock, so over a long session the bus slowly fills or drains relative
        // to the radio that clocks the mix. If it has run ahead past the latency
        // cap, drop the oldest excess so the monitor delay stays bounded (a
        // remote RX tolerates a small fixed delay, but not an unbounded one).
        int cap = OutRateHz / 4; // ~250 ms
        int over = _audioBus.Count - (cap + dst.Length);
        for (int dropped = 0; dropped < over;)
        {
            int chunk = Math.Min(over - dropped, _drainScratch.Length);
            int n = _audioBus.Read(_drainScratch.AsSpan(0, chunk));
            if (n <= 0) break;
            dropped += n;
        }
        return _audioBus.Read(dst);
    }

    // Test seam: feed the audio bus directly (bypassing the live KiwiSdrClient)
    // so the mix-bus drain + drift-cap behaviour of ReadAudio is unit-testable.
    internal int EnqueueAudioForTest(ReadOnlySpan<float> samples) => _audioBus.Write(samples);
    internal int AudioBusDepthForTest => _audioBus.Count;

    // Test seams for the TX-mute path: drive the keyed flag and the audio-ingest
    // entry point so "Kiwi mutes on TX" is unit-testable without a live client.
    internal void SetTxActiveForTest(bool active) { lock (_sync) _txActive = active; }
    internal void OnAudioForTest(float[] samples, int inRateHz) => OnAudio(samples, inRateHz);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hydrate the persisted enable flag. Unlike PureSignal there is no
        // hardware-safety constraint, so a previously-enabled slice may resume —
        // but ONLY once a radio is connected (the Kiwi rides the radio-clocked
        // mix bus). It does not auto-connect on a headless/idle server.
        var s = _store.Get();
        lock (_sync) { _enabled = s.Enabled; }

        // Subscribe to the radio connect/disconnect lifecycle. Resolved here (not
        // in the ctor) so RadioService — which depends on this service via
        // IKiwiReceiverProvider — is already built, avoiding a DI cycle. P1 and P2
        // connects both mean "a radio is up"; either disconnect means it's down.
        var radio = _services?.GetService(typeof(RadioService)) as RadioService;
        Action<Zeus.Protocol1.IProtocol1Client> onP1Connect = _ => OnRadioConnected();
        Action<Zeus.Protocol2.Protocol2Client> onP2Connect = _ => OnRadioConnected();
        Action onDisconnect = OnRadioDisconnected;
        // Mirror the global RX squelch config so the Kiwi audio gate (ApplySquelchGate)
        // honours the same SQL control as the hardware RXs.
        Action<StateDto> onState = s => ApplySquelchConfig(s.Squelch);
        // Track TX (MOX/TUN) so the Kiwi mutes while the operator is keyed.
        Action<bool> onMox = on => { lock (_sync) { _moxActive = on; _txActive = _moxActive || _tunActive; } };
        Action<bool> onTun = on => { lock (_sync) { _tunActive = on; _txActive = _moxActive || _tunActive; } };
        if (radio is not null)
        {
            radio.Connected += onP1Connect;
            radio.P2Connected += onP2Connect;
            radio.Disconnected += onDisconnect;
            radio.P2Disconnected += onDisconnect;
            radio.StateChanged += onState;
            radio.MoxChanged += onMox;
            radio.TunActiveChanged += onTun;
            lock (_sync) { _radioConnected = radio.IsConnected; }
            ApplySquelchConfig(radio.Snapshot().Squelch);
        }
        else
        {
            // No radio service in this host (some unit/integration setups): keep
            // legacy standalone behaviour so the Kiwi can still be exercised.
            lock (_sync) { _radioConnected = true; }
        }

        // Connect now only if a radio is already up; otherwise sit in "waiting for
        // radio" until OnRadioConnected fires.
        await MaybeStartAsync(stoppingToken).ConfigureAwait(false);

        // Idle until shutdown; all work is event-driven off the client callbacks.
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        if (radio is not null)
        {
            radio.Connected -= onP1Connect;
            radio.P2Connected -= onP2Connect;
            radio.Disconnected -= onDisconnect;
            radio.P2Disconnected -= onDisconnect;
            radio.StateChanged -= onState;
            radio.MoxChanged -= onMox;
            radio.TunActiveChanged -= onTun;
        }
        await StopClientAsync().ConfigureAwait(false);
    }

    // Start (or restart) the remote KiwiSDR connection iff the slice is enabled,
    // has a URL, a radio is connected, and we're not already connected. When
    // enabled but the radio is still down, parks the status at "waiting for
    // radio". Serialised by _lifecycleGate. Safe to call from startup, the radio
    // connect callback, or anywhere NOT already holding the gate.
    private async Task MaybeStartAsync(CancellationToken ct)
    {
        var s = _store.Get();
        string? url = s.Url, password = s.Password;
        bool go, waiting;
        lock (_sync)
        {
            go = _enabled && !string.IsNullOrWhiteSpace(url) && _radioConnected && _client is null;
            waiting = _enabled && !_radioConnected && _client is null;
            if (waiting) { _status = "waiting"; _statusDetail = "waiting for radio"; }
        }
        if (!go) { if (waiting) RaiseChanged(); return; }

        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate — state may have changed while awaiting.
            lock (_sync) { go = _enabled && _radioConnected && _client is null; }
            if (!go) return;
            try { await StartClientAsync(url!, password, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _log.LogWarning("kiwi.connect failed url={Url} err={Err}", url, ex.Message);
                lock (_sync) { _status = "error"; _statusDetail = ex.Message; }
            }
        }
        finally { _lifecycleGate.Release(); }
        RaiseChanged();
    }

    // Radio came up: connect the slice if the operator has it enabled.
    private void OnRadioConnected()
    {
        lock (_sync) { if (_radioConnected) return; _radioConnected = true; }
        _log.LogInformation("kiwi.radio connected -> starting slice if enabled");
        // Fire-and-forget so the radio connect path is never blocked; errors are
        // logged inside MaybeStartAsync.
        _ = MaybeStartAsync(CancellationToken.None);
    }

    // Radio went down: tear the slice's remote connection down so it stops
    // feeding a now-unclocked mix bus, and park it back at "waiting for radio".
    private void OnRadioDisconnected()
    {
        bool wasConnected;
        lock (_sync) { wasConnected = _radioConnected; _radioConnected = false; }
        if (!wasConnected) return;
        _log.LogInformation("kiwi.radio disconnected -> stopping slice");
        _ = StopOnRadioDownAsync();
    }

    private async Task StopOnRadioDownAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopClientAsync().ConfigureAwait(false);
            lock (_sync)
            {
                if (_enabled) { _status = "waiting"; _statusDetail = "waiting for radio"; }
                else { _status = "disabled"; _statusDetail = null; }
            }
        }
        finally { _lifecycleGate.Release(); }
        RaiseChanged();
    }

    // -------------------------------------------------------------------------
    // Public API (driven by the REST endpoints).
    // -------------------------------------------------------------------------
    public KiwiConfigDto GetConfig()
    {
        var s = _store.Get();
        lock (_sync)
            return new KiwiConfigDto(_enabled, s.Url, s.Password is not null, _status, _statusDetail);
    }

    public async Task<KiwiConfigDto> SetConfigAsync(bool? enabled, string? url, string? password, CancellationToken ct)
    {
        var s = _store.Set(enabled, url, password);
        lock (_sync) { _enabled = s.Enabled; }
        bool wantOn = s.Enabled && !string.IsNullOrWhiteSpace(s.Url);

        // Tear down any existing connection, then reconnect ONLY if a radio is up.
        // If the operator enables the Kiwi with no radio connected, park it at
        // "waiting for radio" — OnRadioConnected will start it when the radio comes
        // up. All of this is serialised against the radio callbacks by the gate.
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopClientAsync().ConfigureAwait(false);
            bool radioUp; lock (_sync) radioUp = _radioConnected;
            if (wantOn && radioUp)
            {
                try { await StartClientAsync(s.Url!, s.Password, ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _log.LogWarning("kiwi.connect failed url={Url} err={Err}", s.Url, ex.Message);
                    lock (_sync) { _status = "error"; _statusDetail = ex.Message; }
                }
            }
            else if (wantOn)
            {
                lock (_sync) { _status = "waiting"; _statusDetail = "waiting for radio"; }
            }
            else
            {
                lock (_sync) { _status = "disabled"; _statusDetail = null; }
            }
        }
        finally { _lifecycleGate.Release(); }

        RaiseChanged();
        return GetConfig();
    }

    /// <summary>Apply tuning/mode/filter from the focused-RX controls (the
    /// <c>/api/receivers/{KiwiReceiverIndex}</c> endpoint routes here).</summary>
    public void SetTuning(long? vfoHz, RxMode? mode, int? filterLowHz, int? filterHighHz, bool ctun = false)
    {
        KiwiSdrClient? client;
        long freq, center; RxMode m; int lo, hi;
        lock (_sync)
        {
            if (vfoHz.HasValue)
            {
                _vfoHz = vfoHz.Value;
                // CTUN off: the waterfall re-centres on the dial. CTUN on: the
                // centre is frozen and only the demod (dial) moves within the
                // displayed span — the whole point of click-tune.
                if (!ctun) _centerHz = _vfoHz;
            }
            bool modeChanged = mode.HasValue && mode.Value != _mode;
            if (mode.HasValue) _mode = mode.Value;
            if (filterLowHz.HasValue) _filterLowHz = filterLowHz.Value;
            if (filterHighHz.HasValue) _filterHighHz = filterHighHz.Value;
            // On a mode change with no explicit filter, load that mode's default
            // passband. The cuts are SIGNED for the KiwiSDR sideband convention
            // (LSB/CWL are negative), so USB/LSB/AM/CW demod the correct sideband
            // AND the projected ReceiverDto drives a matching on-screen overlay.
            if (modeChanged && !filterLowHz.HasValue && !filterHighHz.HasValue)
                (_filterLowHz, _filterHighHz) = DefaultPassband(_mode);
            client = _client;
            freq = _vfoHz; center = _centerHz; m = _mode; lo = _filterLowHz; hi = _filterHighHz;
        }
        client?.Tune(freq, center, KiwiMode(m), lo, hi, CurrentSpanHz());
        RaiseChanged();
    }

    /// <summary>Pan the waterfall centre (the CTUN "LO"). The demod (dial) is
    /// left where it is; only the displayed window moves. Routed from
    /// <c>/api/receivers/{KiwiReceiverIndex}/lo</c>.</summary>
    public void SetCenter(long centerHz)
    {
        KiwiSdrClient? client;
        long freq, center; RxMode m; int lo, hi;
        lock (_sync)
        {
            _centerHz = centerHz;
            client = _client;
            freq = _vfoHz; center = _centerHz; m = _mode; lo = _filterLowHz; hi = _filterHighHz;
        }
        client?.Tune(freq, center, KiwiMode(m), lo, hi, CurrentSpanHz());
        RaiseChanged();
    }

    // Per-mode default passband edges (Hz, relative to carrier) in the KiwiSDR
    // signed convention: upper-sideband positive, lower-sideband negative,
    // double-sideband symmetric. low &lt; high always.
    internal static (int Low, int High) DefaultPassband(RxMode mode) => mode switch
    {
        RxMode.LSB or RxMode.DIGL => (-2850, -100),
        RxMode.USB or RxMode.DIGU or RxMode.FreeDv => (100, 2850),
        RxMode.CWL => (-700, -300),
        RxMode.CWU => (300, 700),
        RxMode.AM or RxMode.SAM or RxMode.DSB => (-4000, 4000),
        RxMode.FM => (-6000, 6000),
        _ => (100, 2850),
    };

    public void SetMuted(bool muted)
    {
        lock (_sync) _muted = muted;
        // Not clearing the bus here on purpose: SetMuted runs on the REST thread,
        // and FloatSpscRing.Clear is a CONSUMER-side reset — racing it with the
        // DSP tick's ReadAudio would break the single-consumer contract. While
        // muted the producer stops writing and the consumer stops draining, so
        // the bus simply freezes; on un-mute ReadAudio's drift cap trims it back
        // to ~250 ms, bounding any stale-audio replay safely.
        RaiseChanged();
    }

    /// <summary>Apply the global zoom level (1..32) to the Kiwi waterfall. The
    /// span shrinks as the level grows (<see cref="FullSpanHz"/> / level); the
    /// client re-tunes the remote KiwiSDR's <c>SET zoom</c> and starts emitting
    /// frames at the new Hz/pixel, which the self-scaled frontend renders.</summary>
    public void SetZoom(int level)
    {
        Volatile.Write(ref _zoomLevel, Math.Clamp(level, 1, 32));
        KiwiSdrClient? client;
        long freq, center; RxMode m; int lo, hi;
        lock (_sync) { client = _client; freq = _vfoHz; center = _centerHz; m = _mode; lo = _filterLowHz; hi = _filterHighHz; }
        double span = CurrentSpanHz();
        _log.LogInformation("kiwi.zoom level={Level} spanHz={Span:F0}", _zoomLevel, span);
        client?.Tune(freq, center, KiwiMode(m), lo, hi, span);
    }

    // -------------------------------------------------------------------------
    // IKiwiReceiverProvider.
    // -------------------------------------------------------------------------
    public ReceiverDto? GetKiwiReceiver()
    {
        lock (_sync)
        {
            if (!_enabled) return null;
            return new ReceiverDto(
                Index: RxId,
                Enabled: true,
                AdcSource: 0,
                VfoHz: _vfoHz,
                Mode: _mode,
                FilterLowHz: _filterLowHz,
                FilterHighHz: _filterHighHz,
                FilterPresetName: null,
                AfGainDb: 0.0,
                SampleRateHz: OutRateHz,
                Muted: _muted,
                Name: "Kiwi");
        }
    }

    private void RaiseChanged()
    {
        try { KiwiReceiverChanged?.Invoke(); }
        catch (Exception ex) { _log.LogDebug("kiwi.changed handler threw err={Err}", ex.Message); }
    }

    // -------------------------------------------------------------------------
    // Client lifecycle + frame production.
    // -------------------------------------------------------------------------
    private async Task StartClientAsync(string url, string? password, CancellationToken ct)
    {
        if (!TryParseEndpoint(url, out var host, out var port, out var secure))
        {
            lock (_sync) { _status = "error"; _statusDetail = "invalid URL"; }
            return;
        }

        // Follow the kiwisdr.com proxy redirect chain to the real host:port so a
        // "<id>.proxy.kiwisdr.com" entry connects instead of hanging.
        (host, port, secure) = await ResolveEndpointAsync(host, port, secure, ct).ConfigureAwait(false);

        var client = new KiwiSdrClient(host, port, secure, password, "ZeusSDR", _loggerFactory.CreateLogger<KiwiSdrClient>());
        client.AudioReceived = OnAudio;
        client.WaterfallReceived = OnWaterfall;
        client.StatusChanged = OnClientStatus;
        // The Kiwi S-meter is NOT broadcast (RxMeterFrame carries no RxId, so it
        // would overwrite RX1's meter — the panadapter/waterfall convey the Kiwi
        // level visually). It IS consumed internally to drive the audio squelch
        // gate (ApplySquelchGate), since the Kiwi rides the mix bus and never
        // sees Zeus's WDSP/adaptive squelch.
        client.SignalLevel = dbm => _sqlSignalDbm = dbm;

        lock (_sync)
        {
            _client = client;
            _status = "connecting";
            _statusDetail = $"{host}:{port}";
            _resamplePhase = 0; _resamplePrev = 0; _resampleInRate = 0;
        }
        await client.StartAsync(ct).ConfigureAwait(false);

        // Push the current tuning so the freshly-opened channel lands on the
        // operator's frequency.
        long freq, center; RxMode m; int lo, hi;
        lock (_sync) { freq = _vfoHz; center = _centerHz; m = _mode; lo = _filterLowHz; hi = _filterHighHz; }
        client.Tune(freq, center, KiwiMode(m), lo, hi, CurrentSpanHz());
    }

    private async Task StopClientAsync()
    {
        KiwiSdrClient? client;
        lock (_sync) { client = _client; _client = null; }
        if (client is not null)
        {
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug("kiwi.stop err={Err}", ex.Message); }
        }
    }

    private void OnClientStatus(string status, string? detail)
    {
        lock (_sync) { _status = status; _statusDetail = detail; }
        RaiseChanged();
    }

    private void OnWaterfall(float[] binsDb, long centerHz, double hzPerBin)
    {
        if (binsDb.Length == 0) return;
        // Resample the Kiwi's native bin count (~1024, and it can wobble by ±1
        // between rows) to the fixed 2048-wide frame a hardware DDC produces.
        // A constant width is load-bearing: the frontend renderer does a full
        // reset (texture realloc) on any width change, so a per-row-varying width
        // would thrash it (the "extremely laggy" symptom). hzPerPixel scales with
        // the new width so the frequency mapping is unchanged.
        Interlocked.Increment(ref _wfFramesWindow);
        double span = binsDb.Length * hzPerBin;
        LogWfDbRangeIfDue(binsDb);
        var db = ResampleBins(binsDb, DisplayWidth);
        var frame = new DisplayFrame(
            Seq: unchecked(++_displaySeq),
            TsUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            RxId: RxId,
            BodyFlags: DisplayBodyFlags.PanValid | DisplayBodyFlags.WfValid,
            Width: DisplayWidth,
            CenterHz: centerHz,
            HzPerPixel: (float)(span / DisplayWidth),
            // The Kiwi waterfall row is the spectrum; reuse it for both the
            // panadapter trace and the waterfall history.
            PanDb: db,
            WfDb: db);
        _hub.Broadcast(frame);
    }

    // 1 Hz diagnostic of the incoming Kiwi waterfall dB span. The Kiwi sends raw
    // dBm (byte-255) on a different absolute scale than the hardware RX's dBFS,
    // and an operator reported the Kiwi pane washing out bright (noise floor not
    // dark like RX1). The frontend cross-band floor-normalisation
    // (floor-normalization.ts) is supposed to align it, but to calibrate that we
    // need to SEE the real distribution rather than guess — log min / robust
    // floor (10th pct) / median / max once per second. Cheap: one pass + a tiny
    // partial sort over ~1k bins.
    private long _wfDbLogMs;
    private void LogWfDbRangeIfDue(float[] binsDb)
    {
        if (!_log.IsEnabled(LogLevel.Debug)) return;
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _wfDbLogMs);
        if (now - last < 1000) return;
        if (Interlocked.CompareExchange(ref _wfDbLogMs, now, last) != last) return;

        float mn = float.MaxValue, mx = float.MinValue;
        foreach (var v in binsDb) { if (v < mn) mn = v; if (v > mx) mx = v; }
        var sorted = (float[])binsDb.Clone();
        Array.Sort(sorted);
        float floor = sorted[(int)(sorted.Length * 0.10)];
        float median = sorted[sorted.Length / 2];
        _log.LogDebug("kiwi.wf.db min={Min:F1} floor10={Floor:F1} median={Median:F1} max={Max:F1} bins={N}",
            mn, floor, median, mx, binsDb.Length);
    }

    // Linear-interpolate a per-bin dB array to a fixed destination length.
    private static float[] ResampleBins(float[] src, int dstLen)
    {
        var dst = new float[dstLen];
        if (src.Length == dstLen) { Array.Copy(src, dst, dstLen); return dst; }
        if (src.Length == 1) { Array.Fill(dst, src[0]); return dst; }
        double scale = (double)(src.Length - 1) / (dstLen - 1);
        for (int i = 0; i < dstLen; i++)
        {
            double pos = i * scale;
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, src.Length - 1);
            float frac = (float)(pos - i0);
            dst[i] = src[i0] + frac * (src[i1] - src[i0]);
        }
        return dst;
    }

    private void OnAudio(float[] samples, int inRateHz)
    {
        if (samples.Length == 0) return;
        // Per-RX mute, mirroring the hardware secondary-RX path
        // (DspPipelineService skips a muted receiver's audio contribution): when
        // the Kiwi is muted we simply stop publishing its frames so it drops out
        // of the client-side mix.
        bool muted;
        lock (_sync) muted = _muted || _txActive;
        if (muted) return;
        // Squelch the native-rate audio (ties to the per-frame S-meter) before
        // resampling, so the gate's attack/release constants match the ~12 kHz
        // native cadence.
        ApplySquelchGate(samples);
        var output = Resample(samples, inRateHz);
        if (output.Length == 0) return;

        // Hand the demodulated audio to the RX mix bus; DspPipelineService drains
        // and averages it into the single RxId-0 output (IKiwiAudioBus), so the
        // Kiwi mixes with the local RX instead of fighting it on the device ring.
        // On overflow (no consumer draining — e.g. no radio connected to clock
        // the mix) the ring drops the excess, the right bounded-latency policy
        // for a live monitor receiver.
        _audioBus.Write(output);

        Interlocked.Increment(ref _audioFramesWindow);
        Interlocked.Add(ref _audioSamplesWindow, output.Length);
        LogRatesIfDue();
    }

    // Emit a 1 Hz rate line so we can see the real WF/audio cadence (audioSps
    // should sit near 48000; a large excess means we're over-feeding the client
    // audio buffer, the usual cause of growing latency / "lag").
    private void LogRatesIfDue()
    {
        long now = Environment.TickCount64;
        long start = Interlocked.Read(ref _rateWindowStartMs);
        if (start == 0) { Interlocked.CompareExchange(ref _rateWindowStartMs, now, 0); return; }
        if (now - start < 1000) return;
        if (Interlocked.CompareExchange(ref _rateWindowStartMs, now, start) != start) return;
        int wf = Interlocked.Exchange(ref _wfFramesWindow, 0);
        int af = Interlocked.Exchange(ref _audioFramesWindow, 0);
        long sps = Interlocked.Exchange(ref _audioSamplesWindow, 0);
        double dt = (now - start) / 1000.0;
        _log.LogDebug("kiwi.rate wfFps={Wf:F1} audioFps={Af:F1} audioSps={Sps:F0}",
            wf / dt, af / dt, sps / dt);
    }

    // Linear-interpolation resampler from the native Kiwi rate (~12 kHz) to
    // 48 kHz, carrying the fractional phase + last sample across frames so the
    // stream stays continuous (no per-frame discontinuity click).
    private float[] Resample(float[] input, int inRateHz)
    {
        if (inRateHz <= 0) inRateHz = 12_000;
        if (inRateHz != _resampleInRate)
        {
            // Rate changed (or first frame): reset phase to avoid a transient.
            _resampleInRate = inRateHz;
            _resamplePhase = 0;
            _resamplePrev = input[0];
        }
        double step = (double)inRateHz / OutRateHz; // input samples per output sample
        // Worst-case output length plus a little slack.
        var outList = new List<float>((int)(input.Length / step) + 2);
        double phase = _resamplePhase;
        float prev = _resamplePrev;
        for (int i = 0; i < input.Length; i++)
        {
            float cur = input[i];
            // Emit every output sample whose source position falls in [i-1, i].
            while (phase < 1.0)
            {
                outList.Add(prev + (float)phase * (cur - prev));
                phase += step;
            }
            phase -= 1.0;
            prev = cur;
        }
        _resamplePhase = phase;
        _resamplePrev = prev;
        return outList.ToArray();
    }

    // -------------------------------------------------------------------------
    // Squelch — the Kiwi rides the mix bus, so Zeus's WDSP/adaptive squelch can't
    // reach it. Gate the audio here off the Kiwi's own S-meter (dBm) so the
    // global SQL control works on the Kiwi too. Mirrors the spirit of
    // DspPipelineService's adaptive squelch (noise-floor tracking + margin +
    // hysteresis) but operates on the Kiwi's server-side-demodulated stream.
    // -------------------------------------------------------------------------
    internal void ApplySquelchConfig(SquelchConfig? cfg)
    {
        cfg ??= new SquelchConfig();
        lock (_sync)
        {
            _sqlEnabled = cfg.Enabled;
            _sqlAdaptive = cfg.Adaptive;
            _sqlLevel = Math.Clamp(cfg.Level, 0, 100);
        }
    }

    // SND-thread only. Updates the gate state from the latest S-meter and applies
    // a smoothed gain ramp to the native-rate samples in place.
    internal void ApplySquelchGate(float[] samples)
    {
        bool enabled, adaptive; int level;
        lock (_sync) { enabled = _sqlEnabled; adaptive = _sqlAdaptive; level = _sqlLevel; }
        if (!enabled)
        {
            // Off: pass through and reset the gate so a later enable doesn't open
            // muted or replay a stale floor estimate.
            _sqlGain = 1.0; _sqlOpen = true; _sqlNoiseFloorDbm = double.NaN;
            return;
        }

        double sig = _sqlSignalDbm;
        bool open;
        if (!double.IsFinite(sig))
        {
            // No S-meter yet (just connected): never mute on missing data.
            open = true;
        }
        else if (adaptive)
        {
            if (!double.IsFinite(_sqlNoiseFloorDbm)) _sqlNoiseFloorDbm = sig;
            else if (sig < _sqlNoiseFloorDbm)
                _sqlNoiseFloorDbm = Math.Max(sig, _sqlNoiseFloorDbm - SqlFloorFallDbPerFrame);
            else
                _sqlNoiseFloorDbm = Math.Min(sig, _sqlNoiseFloorDbm + SqlFloorRiseDbPerFrame);

            double openThresh = _sqlNoiseFloorDbm + SqlOpenMarginDb;
            double closeThresh = openThresh - SqlHysteresisDb;
            open = _sqlOpen ? sig >= closeThresh : sig >= openThresh;
        }
        else
        {
            double thresh = SqlFixedFloorDbm + (level / 100.0) * SqlFixedSpanDb;
            double closeThresh = thresh - SqlHysteresisDb;
            open = _sqlOpen ? sig >= closeThresh : sig >= thresh;
        }
        _sqlOpen = open;

        double target = open ? 1.0 : 0.0;
        double g = _sqlGain;
        if (g == target)
        {
            if (g != 1.0)
                for (int i = 0; i < samples.Length; i++) samples[i] *= (float)g;
            return;
        }
        for (int i = 0; i < samples.Length; i++)
        {
            if (g < target) g = Math.Min(target, g + SqlAttackPerSample);
            else if (g > target) g = Math.Max(target, g - SqlReleasePerSample);
            samples[i] *= (float)g;
        }
        _sqlGain = g;
    }

    // Test seam: feed an S-meter reading so the gate is unit-testable without a
    // live KiwiSdrClient.
    internal void SetSignalDbmForTest(double dbm) => _sqlSignalDbm = dbm;

    // -------------------------------------------------------------------------
    // Helpers.
    // -------------------------------------------------------------------------
    // Map a Zeus RX mode to a KiwiSDR demod word. The Kiwi demodulates
    // server-side, so this is what the slice actually decodes.
    internal static string KiwiMode(RxMode mode) => mode switch
    {
        RxMode.LSB or RxMode.DIGL => "lsb",
        RxMode.USB or RxMode.DIGU or RxMode.FreeDv => "usb",
        RxMode.CWL or RxMode.CWU => "cw",
        RxMode.AM or RxMode.DSB => "am",
        RxMode.SAM => "sam",
        RxMode.FM => "nbfm",
        _ => "usb",
    };

    // Back-compat overload (drops the secure flag) for call sites/tests that
    // only need host+port.
    internal static bool TryParseEndpoint(string url, out string host, out int port) =>
        TryParseEndpoint(url, out host, out port, out _);

    // Accepts "host", "host:port", "ws[s]://host[:port]", "http[s]://host[:port][/path]".
    //
    // The default port follows the URL SCHEME, which is load-bearing: ~half of
    // the public KiwiSDR directory is published as "http://<id>.proxy.kiwisdr.com"
    // with NO explicit port, and the kiwi proxy serves those on port 80 (the http
    // default) — NOT 8073. Defaulting a scheme-less "http://" host to 8073 made
    // every proxied receiver fail to connect (it would hang on the wrong port and
    // the pane kept showing the previously-tuned receiver). So: http/ws → 80,
    // https/wss → 443, and a BARE host with no scheme keeps the KiwiSDR default
    // 8073. An explicit ":port" always wins. <paramref name="secure"/> selects
    // ws:// vs wss:// for the actual socket.
    internal static bool TryParseEndpoint(string url, out string host, out int port, out bool secure)
    {
        host = string.Empty;
        port = 8073;
        secure = false;
        if (string.IsNullOrWhiteSpace(url)) return false;
        var s = url.Trim();

        int defaultPort = 8073;
        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            switch (s[..scheme].ToLowerInvariant())
            {
                case "https" or "wss": secure = true; defaultPort = 443; break;
                case "http" or "ws": secure = false; defaultPort = 80; break;
            }
            s = s[(scheme + 3)..];
        }
        int slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        if (s.Length == 0) return false;

        int colon = s.LastIndexOf(':');
        if (colon > 0)
        {
            var portStr = s[(colon + 1)..];
            port = int.TryParse(portStr, out var p) && p is > 0 and <= 65535 ? p : defaultPort;
            host = s[..colon];
        }
        else
        {
            host = s;
            port = defaultPort;
        }
        return host.Length > 0;
    }
}
