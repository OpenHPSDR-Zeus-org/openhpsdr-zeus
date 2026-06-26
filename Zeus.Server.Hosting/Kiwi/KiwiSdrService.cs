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

    // Projected Kiwi receiver tuning/state.
    private bool _enabled;
    private long _vfoHz = 14_200_000;       // demod (dial) frequency
    private long _centerHz = 14_200_000;    // waterfall centre; frozen under CTUN
    private RxMode _mode = RxMode.USB;
    private int _filterLowHz = 100;
    private int _filterHighHz = 2_850;
    private bool _muted;
    private int _zoomLevel = 1; // 1..32, shared zoom level (see /api/rx/zoom)

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

    public KiwiSdrService(
        KiwiSettingsStore store,
        StreamingHub hub,
        ILoggerFactory loggerFactory,
        IHttpClientFactory? httpFactory = null)
    {
        _store = store;
        _hub = hub;
        _loggerFactory = loggerFactory;
        _httpFactory = httpFactory;
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
        get { lock (_sync) return _enabled && !_muted && _client is not null; }
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hydrate from persisted settings: reconnect a previously-enabled slice
        // on startup (no hardware-safety constraint — unlike PureSignal — so a
        // remote receiver may auto-resume).
        var s = _store.Get();
        lock (_sync) { _enabled = s.Enabled; }
        if (s.Enabled && !string.IsNullOrWhiteSpace(s.Url))
        {
            try { await StartClientAsync(s.Url!, s.Password, stoppingToken).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning("kiwi.startup.connect failed err={Err}", ex.Message); }
        }
        // Idle until shutdown; all work is event-driven off the client callbacks.
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await StopClientAsync().ConfigureAwait(false);
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
        bool wantOn = s.Enabled && !string.IsNullOrWhiteSpace(s.Url);

        await StopClientAsync().ConfigureAwait(false);
        lock (_sync) { _enabled = s.Enabled; }

        if (wantOn)
        {
            try { await StartClientAsync(s.Url!, s.Password, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _log.LogWarning("kiwi.connect failed url={Url} err={Err}", s.Url, ex.Message);
                lock (_sync) { _status = "error"; _statusDetail = ex.Message; }
            }
        }
        else
        {
            lock (_sync) { _status = "disabled"; _statusDetail = null; }
        }

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
        // SignalLevel is intentionally not wired: RxMeterFrame carries no RxId,
        // so a Kiwi S-meter broadcast would overwrite RX1's meter. The
        // panadapter/waterfall convey the Kiwi signal level visually.

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
        lock (_sync) muted = _muted;
        if (muted) return;
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
