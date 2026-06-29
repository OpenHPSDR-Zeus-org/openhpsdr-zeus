using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

// KiwiSDR slice receiver: the Zeus-mode -> Kiwi demod word mapping, URL/endpoint
// parsing, the persisted settings round-trip, and the IKiwiReceiverProvider
// projection into RadioService's unified receiver list (reserved index
// WireContract.KiwiReceiverIndex, labelled "Kiwi").
public sealed class KiwiSdrTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-kiwi-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", ".pa", ".dsp", ".kiwi" })
        {
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); } catch { }
        }
    }

    [Theory]
    [InlineData(RxMode.USB, "usb")]
    [InlineData(RxMode.DIGU, "usb")]
    [InlineData(RxMode.FreeDv, "usb")]
    [InlineData(RxMode.LSB, "lsb")]
    [InlineData(RxMode.DIGL, "lsb")]
    [InlineData(RxMode.AM, "am")]
    [InlineData(RxMode.DSB, "am")]
    [InlineData(RxMode.SAM, "sam")]
    [InlineData(RxMode.FM, "nbfm")]
    [InlineData(RxMode.CWL, "cw")]
    [InlineData(RxMode.CWU, "cw")]
    public void KiwiMode_maps_zeus_mode_to_kiwi_word(RxMode mode, string expected)
    {
        Assert.Equal(expected, KiwiSdrService.KiwiMode(mode));
    }

    [Theory]
    [InlineData("sdr.example.org", "sdr.example.org", 8073)]
    [InlineData("sdr.example.org:8074", "sdr.example.org", 8074)]
    [InlineData("ws://sdr.example.org:8075", "sdr.example.org", 8075)]
    [InlineData("http://sdr.example.org:8076/", "sdr.example.org", 8076)]
    [InlineData("http://sdr.example.org:8077/path/here", "sdr.example.org", 8077)]
    [InlineData("  sdr.example.org:8078  ", "sdr.example.org", 8078)]
    public void TryParseEndpoint_handles_url_forms(string url, string expHost, int expPort)
    {
        Assert.True(KiwiSdrService.TryParseEndpoint(url, out var host, out var port));
        Assert.Equal(expHost, host);
        Assert.Equal(expPort, port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ws://")]
    public void TryParseEndpoint_rejects_empty(string url)
    {
        Assert.False(KiwiSdrService.TryParseEndpoint(url, out _, out _));
    }

    [Theory]
    // A bare host (no scheme, no port) keeps the KiwiSDR default 8073.
    [InlineData("sdr.example.org", "sdr.example.org", 8073, false)]
    // http/ws with NO port → 80 (the kiwi proxy default — ~half the directory).
    [InlineData("http://pb8w.proxy.kiwisdr.com", "pb8w.proxy.kiwisdr.com", 80, false)]
    [InlineData("ws://sdr.example.org", "sdr.example.org", 80, false)]
    // https/wss with NO port → 443 + secure.
    [InlineData("https://sdr.example.org", "sdr.example.org", 443, true)]
    [InlineData("wss://sdr.example.org/path", "sdr.example.org", 443, true)]
    // An explicit :port always wins over the scheme default.
    [InlineData("http://sdr.example.org:8076/", "sdr.example.org", 8076, false)]
    [InlineData("sdr.example.org:8074", "sdr.example.org", 8074, false)]
    public void TryParseEndpoint_port_follows_scheme(string url, string expHost, int expPort, bool expSecure)
    {
        Assert.True(KiwiSdrService.TryParseEndpoint(url, out var host, out var port, out var secure));
        Assert.Equal(expHost, host);
        Assert.Equal(expPort, port);
        Assert.Equal(expSecure, secure);
    }

    [Theory]
    [InlineData(RxMode.USB, 100, 2850)]
    [InlineData(RxMode.DIGU, 100, 2850)]
    [InlineData(RxMode.LSB, -2850, -100)]
    [InlineData(RxMode.DIGL, -2850, -100)]
    [InlineData(RxMode.AM, -4000, 4000)]
    [InlineData(RxMode.SAM, -4000, 4000)]
    [InlineData(RxMode.CWU, 300, 700)]
    [InlineData(RxMode.CWL, -700, -300)]
    [InlineData(RxMode.FM, -6000, 6000)]
    public void DefaultPassband_is_signed_for_the_sideband(RxMode mode, int low, int high)
    {
        var (lo, hi) = KiwiSdrService.DefaultPassband(mode);
        Assert.Equal(low, lo);
        Assert.Equal(high, hi);
        Assert.True(lo < hi); // KiwiSDR requires low_cut < high_cut
    }

    [Fact]
    public void SettingsStore_round_trips_and_empty_password_clears()
    {
        using var store = new KiwiSettingsStore(NullLogger<KiwiSettingsStore>.Instance, _dbPath + ".kiwi");

        // Default: disabled, nothing stored.
        var def = store.Get();
        Assert.False(def.Enabled);
        Assert.Null(def.Url);
        Assert.Null(def.Password);

        store.Set(enabled: true, url: "sdr.example.org:8073", password: "secret");
        var s1 = store.Get();
        Assert.True(s1.Enabled);
        Assert.Equal("sdr.example.org:8073", s1.Url);
        Assert.Equal("secret", s1.Password);

        // null password leaves it unchanged; empty string clears it.
        store.Set(enabled: null, url: null, password: null);
        Assert.Equal("secret", store.Get().Password);
        store.Set(password: "");
        Assert.Null(store.Get().Password);
    }

    [Fact]
    public void RadioService_appends_kiwi_receiver_when_provider_returns_one()
    {
        using var pa = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        using var dsp = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath + ".dsp");
        var provider = new FakeKiwiProvider();
        var radio = new RadioService(NullLoggerFactory.Instance, dsp, pa, kiwiReceiverProvider: provider);

        // Disabled: no Kiwi entry in the projected list.
        Assert.DoesNotContain(
            radio.Snapshot().Receivers!,
            r => r.Index == WireContract.KiwiReceiverIndex);

        // Enabled: the projected list carries the named entry at the reserved index.
        provider.Receiver = new ReceiverDto(
            Index: WireContract.KiwiReceiverIndex, Enabled: true, AdcSource: 0,
            VfoHz: 7_055_000, Mode: RxMode.AM,
            FilterLowHz: -4_000, FilterHighHz: 4_000, FilterPresetName: null,
            AfGainDb: 0, SampleRateHz: 48_000, Muted: false, Name: "Kiwi");

        var kiwi = radio.Snapshot().Receivers!.Single(r => r.Index == WireContract.KiwiReceiverIndex);
        Assert.Equal("Kiwi", kiwi.Name);
        Assert.Equal(7_055_000, kiwi.VfoHz);
        Assert.Equal(RxMode.AM, kiwi.Mode);
    }

    // ---- IKiwiAudioBus: the Kiwi rides the shared RX mix bus ----------------

    private KiwiSdrService NewService() => new(
        new KiwiSettingsStore(NullLogger<KiwiSettingsStore>.Instance, _dbPath + ".kiwi"),
        new StreamingHub(NullLogger<StreamingHub>.Instance),
        NullLoggerFactory.Instance);

    [Fact]
    public void AudioActive_is_false_until_enabled_and_connected()
    {
        using var svc = NewService();
        // Disabled by default → nothing to mix; the DSP tick skips the Kiwi.
        Assert.False(svc.AudioActive);
    }

    [Fact]
    public async Task SetConfig_enabled_without_radio_parks_at_waiting_for_radio()
    {
        using var svc = NewService();
        // No radio connected (the hosted service's ExecuteAsync never ran, so
        // _radioConnected is false). Enabling the Kiwi must NOT open a remote
        // connection — it parks at "waiting for radio" instead, because the Kiwi
        // only streams once a radio is up to clock the shared RX mix bus.
        var cfg = await svc.SetConfigAsync(
            enabled: true, url: "sdr.example.org:8073", password: null, CancellationToken.None);

        Assert.True(cfg.Enabled);
        Assert.Equal("waiting", cfg.Status);
        Assert.Equal("waiting for radio", cfg.StatusDetail);
        Assert.False(svc.AudioActive);   // no client was opened
    }

    [Fact]
    public void ReadAudio_drains_what_was_enqueued_in_order()
    {
        using var svc = NewService();
        svc.EnqueueAudioForTest(new float[] { 0.1f, 0.2f, 0.3f });

        var dst = new float[5];
        int n = svc.ReadAudio(dst);

        Assert.Equal(3, n);
        Assert.Equal(0.1f, dst[0]);
        Assert.Equal(0.2f, dst[1]);
        Assert.Equal(0.3f, dst[2]);
        // Fully drained.
        Assert.Equal(0, svc.ReadAudio(dst));
    }

    [Fact]
    public void ReadAudio_drops_oldest_when_over_the_latency_cap()
    {
        using var svc = NewService();
        // The remote runs on its own clock; if the bus runs far ahead of the
        // radio that drains it, ReadAudio caps buffered latency to ~250 ms
        // (12 000 samples @ 48 kHz) + the read size, dropping the OLDEST excess.
        const int total = 30_000;
        var enq = new float[total];
        for (int i = 0; i < total; i++) enq[i] = i;        // ascending = age marker
        svc.EnqueueAudioForTest(enq);

        var dst = new float[1024];
        int n = svc.ReadAudio(dst);

        Assert.Equal(1024, n);
        // After the drop, buffered depth must be within the cap (not the full
        // 30 000) so monitor latency stays bounded.
        Assert.True(svc.AudioBusDepthForTest <= 12_000,
            $"expected bounded buffer, got {svc.AudioBusDepthForTest}");
        // The samples returned are the NEWEST, not the stale head: the oldest
        // were discarded, so the first returned sample is well past index 0.
        Assert.True(dst[0] >= total - 12_000 - 1024,
            $"expected freshest samples, got {dst[0]}");
    }

    // ---- TX mute: the Kiwi drops out of the mix while the radio is keyed -------

    [Fact]
    public void OnAudio_writes_to_the_bus_when_not_keyed()
    {
        using var svc = NewService();
        svc.OnAudioForTest(new float[] { 0.2f, 0.2f, 0.2f, 0.2f }, 48_000);
        Assert.True(svc.AudioBusDepthForTest > 0);
    }

    [Fact]
    public void OnAudio_is_muted_while_transmitting()
    {
        using var svc = NewService();
        svc.SetTxActiveForTest(true);
        svc.OnAudioForTest(new float[] { 0.2f, 0.2f, 0.2f, 0.2f }, 48_000);
        Assert.Equal(0, svc.AudioBusDepthForTest);

        // Un-key: audio flows again.
        svc.SetTxActiveForTest(false);
        svc.OnAudioForTest(new float[] { 0.2f, 0.2f, 0.2f, 0.2f }, 48_000);
        Assert.True(svc.AudioBusDepthForTest > 0);
    }

    // ---- Squelch: the Kiwi rides the mix bus, so it gets its own S-meter gate --

    private static float[] Tone(int n)
    {
        var b = new float[n];
        Array.Fill(b, 1.0f);
        return b;
    }

    [Fact]
    public void SquelchGate_off_passes_audio_through_untouched()
    {
        using var svc = NewService();
        svc.ApplySquelchConfig(new SquelchConfig(Enabled: false));
        svc.SetSignalDbmForTest(-130); // dead-quiet would mute if squelch were on
        var buf = Tone(256);
        svc.ApplySquelchGate(buf);
        Assert.All(buf, s => Assert.Equal(1.0f, s));
    }

    [Fact]
    public void SquelchGate_fixed_mutes_weak_then_opens_on_strong()
    {
        using var svc = NewService();
        // Fixed, Level 100 → threshold = -120 + 100% * 100 = -20 dBm.
        svc.ApplySquelchConfig(new SquelchConfig(Enabled: true, Level: 100, Adaptive: false));

        // Weak signal well below the threshold: the gate closes, ramping audio to
        // silence (release ~50 ms @ 12 kHz, so a ~1 k buffer fully settles).
        svc.SetSignalDbmForTest(-100);
        var weak = Tone(1500);
        svc.ApplySquelchGate(weak);
        Assert.True(weak[^1] < 0.01f, $"expected muted tail, got {weak[^1]}");

        // Strong signal above the threshold: the gate re-opens (attack ~5 ms), so
        // a couple hundred samples restore full gain.
        svc.SetSignalDbmForTest(-10);
        var strong = Tone(600);
        svc.ApplySquelchGate(strong);
        Assert.True(strong[^1] > 0.99f, $"expected open tail, got {strong[^1]}");
    }

    [Fact]
    public void SquelchGate_adaptive_tracks_floor_and_opens_above_margin()
    {
        using var svc = NewService();
        svc.ApplySquelchConfig(new SquelchConfig(Enabled: true, Level: 0, Adaptive: true));

        // Sit at the noise floor for a while: the gate stays closed (signal is
        // below floor + open-margin), muting the audio.
        svc.SetSignalDbmForTest(-120);
        for (int i = 0; i < 4; i++) svc.ApplySquelchGate(Tone(1500));
        var quiet = Tone(1500);
        svc.ApplySquelchGate(quiet);
        Assert.True(quiet[^1] < 0.01f, $"expected muted floor, got {quiet[^1]}");

        // A signal well above the tracked floor opens the gate.
        svc.SetSignalDbmForTest(-80);
        var sig = Tone(600);
        svc.ApplySquelchGate(sig);
        Assert.True(sig[^1] > 0.99f, $"expected open on strong signal, got {sig[^1]}");
    }

    // ---- Auto-reconnect when the W/F (or SND) socket drops mid-session ------
    // Issue #1114: the operator's panadapter/waterfall went blank while audio
    // kept playing because the W/F WebSocket closed silently and Zeus never
    // reconnected. The slice now schedules a back-off reconnect on any
    // unsolicited socket close, but only when the slice is enabled AND a radio
    // is connected (the Kiwi rides the radio-clocked mix bus).

    private static async Task WaitForAsync(Func<bool> pred, int timeoutMs = 1000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (pred()) return;
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Drop_while_enabled_and_radio_up_schedules_reconnect()
    {
        using var svc = NewService();
        // Test-seam: skip the real 2..30s back-off so the reconnect completes
        // (and fails on the unreachable URL) quickly.
        svc.ReconnectDelayForTest = _ => TimeSpan.FromMilliseconds(1);
        svc.SetEnabledForTest(true);
        svc.SetRadioConnectedForTest(true);

        // The W/F (or SND) socket just closed unexpectedly mid-session.
        svc.OnClientStatusForTest("dropped", "waterfall socket closed");

        // The reconnect path runs fire-and-forget; observe the synchronous
        // bookkeeping it sets BEFORE the back-off delay.
        Assert.True(svc.ReconnectBusyForTest, "expected a reconnect to be in flight");
        Assert.Equal(1, svc.ReconnectAttemptForTest);

        // Let the (1 ms) delay + reconnect attempt run to completion. The fake
        // URL is unset so the attempt is a no-op, but the busy flag must clear.
        await WaitForAsync(() => !svc.ReconnectBusyForTest);
        Assert.False(svc.ReconnectBusyForTest, "reconnect should release the busy flag");
    }

    [Fact]
    public void Drop_while_disabled_does_not_reconnect()
    {
        using var svc = NewService();
        svc.ReconnectDelayForTest = _ => TimeSpan.FromMilliseconds(1);
        // Slice OFF (operator never enabled it).
        svc.SetEnabledForTest(false);
        svc.SetRadioConnectedForTest(true);

        svc.OnClientStatusForTest("dropped", "waterfall socket closed");

        Assert.False(svc.ReconnectBusyForTest);
        Assert.Equal(0, svc.ReconnectAttemptForTest);
    }

    [Fact]
    public void Drop_while_radio_down_does_not_reconnect()
    {
        using var svc = NewService();
        svc.ReconnectDelayForTest = _ => TimeSpan.FromMilliseconds(1);
        // Slice enabled but radio link is down: there is no mix-bus clock to
        // feed, so reconnect must NOT run. OnRadioConnected reseeds the slice.
        svc.SetEnabledForTest(true);
        svc.SetRadioConnectedForTest(false);

        svc.OnClientStatusForTest("dropped", "waterfall socket closed");

        Assert.False(svc.ReconnectBusyForTest);
        Assert.Equal(0, svc.ReconnectAttemptForTest);
    }

    [Fact]
    public async Task Connected_resets_the_reconnect_attempt_counter()
    {
        using var svc = NewService();
        svc.ReconnectDelayForTest = _ => TimeSpan.FromMilliseconds(1);
        svc.SetEnabledForTest(true);
        svc.SetRadioConnectedForTest(true);

        // Two drops in quick succession: the second short-circuits on the
        // single-in-flight guard, but the first leaves attempt=1.
        svc.OnClientStatusForTest("dropped", null);
        Assert.Equal(1, svc.ReconnectAttemptForTest);

        // A successful handshake on the freshly-opened client clears the
        // counter so the next mid-session drop restarts back-off at attempt 1.
        svc.OnClientStatusForTest("connected", null);
        Assert.Equal(0, svc.ReconnectAttemptForTest);

        // Let the in-flight task drain so the test cleanly disposes.
        await WaitForAsync(() => !svc.ReconnectBusyForTest);
    }

    [Fact]
    public void Drop_surfaces_as_error_on_the_status_pill()
    {
        using var svc = NewService();
        // Even with no radio (no reconnect scheduled), a "dropped" event must
        // still flip the visible status to "error" so the operator sees
        // something is wrong in Settings — the existing pill set has no
        // "dropped" entry.
        svc.SetEnabledForTest(true);
        svc.SetRadioConnectedForTest(false);

        svc.OnClientStatusForTest("dropped", "waterfall socket closed");

        var cfg = svc.GetConfig();
        Assert.Equal("error", cfg.Status);
        Assert.Equal("waterfall socket closed", cfg.StatusDetail);
    }

    [Fact]
    public async Task Failed_reconnect_keeps_retrying_with_escalating_backoff()
    {
        using var svc = NewService();
        // Fixed tiny back-off so the loop iterates fast.
        svc.ReconnectDelayForTest = _ => TimeSpan.FromMilliseconds(1);
        // Simulate a persistently-down host: every reconnect attempt fails to go
        // live (no real socket I/O — keeps the test deterministic cross-platform
        // and avoids a runaway network loop).
        int connects = 0;
        svc.ReconnectConnectForTest = () => { Interlocked.Increment(ref connects); return Task.FromResult(false); };

        // Store a URL with the radio still DOWN so SetConfig only parks it (no
        // connect attempt), then bring enabled+radio up via the seams.
        await svc.SetConfigAsync(true, "ws://kiwi.invalid:8073", null, CancellationToken.None);
        svc.SetEnabledForTest(true);
        svc.SetRadioConnectedForTest(true);

        // Mid-session drop kicks off the back-off loop.
        svc.OnClientStatusForTest("dropped", "waterfall socket closed");

        // The one-shot bug stuck at attempt=1 forever; the loop must keep retrying
        // a down host, so the attempt counter (and connect calls) climb past 1.
        // Wait on the connect count itself, not the attempt counter: the attempt
        // counter is bumped at the TOP of each loop pass (before the delay+connect),
        // so attempt can read 3 while only 2 connects have actually fired. On a
        // loaded macOS CI runner the poll can land in that window and trip the
        // assertion — gating on the asserted quantity removes the race.
        await WaitForAsync(() => Volatile.Read(ref connects) >= 3, timeoutMs: 3000);
        Assert.True(svc.ReconnectAttemptForTest >= 3,
            $"expected escalating retries against a down host, got attempt={svc.ReconnectAttemptForTest}");
        Assert.True(connects >= 3, $"expected >=3 reconnect attempts, got {connects}");

        // Operator disables the slice: the loop must observe !wantOn and stop,
        // releasing the single-in-flight guard.
        svc.SetEnabledForTest(false);
        await WaitForAsync(() => !svc.ReconnectBusyForTest, timeoutMs: 3000);
        Assert.False(svc.ReconnectBusyForTest, "disabling the slice must end the reconnect loop");
    }

    [Fact]
    public async Task Reconnect_loop_stops_when_radio_drops_midflight()
    {
        using var svc = NewService();
        svc.ReconnectDelayForTest = _ => TimeSpan.FromMilliseconds(20);
        svc.ReconnectConnectForTest = () => Task.FromResult(false);   // never goes live
        await svc.SetConfigAsync(true, "ws://kiwi.invalid:8073", null, CancellationToken.None);
        svc.SetEnabledForTest(true);
        svc.SetRadioConnectedForTest(true);

        svc.OnClientStatusForTest("dropped", null);
        Assert.True(svc.ReconnectBusyForTest);

        // Radio goes down mid-back-off (OnRadioDisconnected bumps _reconnectGen and
        // clears _radioConnected). The loop must abort and not resurrect a client.
        svc.OnRadioDisconnectedForTest();
        await WaitForAsync(() => !svc.ReconnectBusyForTest, timeoutMs: 3000);
        Assert.False(svc.ReconnectBusyForTest, "radio-down must end the reconnect loop");
    }

    // ---- KiwiDirectoryService: parse the public receiver list ---------------

    [Theory]
    [InlineData("(50.850000, -0.660000)", 50.85, -0.66)]
    [InlineData("( 12.0 , -3.5 )", 12.0, -3.5)]
    public void TryParseGps_parses_lat_lon(string gps, double lat, double lon)
    {
        Assert.True(KiwiDirectoryService.TryParseGps(gps, out var la, out var lo));
        Assert.Equal(lat, la, 3);
        Assert.Equal(lon, lo, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("(0, 0)")]          // Null Island placeholder — rejected
    [InlineData("(200, 5)")]        // out of range
    [InlineData("not coords")]
    public void TryParseGps_rejects_bad_or_placeholder(string gps)
    {
        Assert.False(KiwiDirectoryService.TryParseGps(gps, out _, out _));
    }

    [Fact]
    public void Parse_extracts_receivers_from_the_js_wrapper()
    {
        const string body = """
            // KiwiSDR.com receiver list
            // banner comment
            var kiwisdr_com =
            [
                {"name":"Chichester UK","url":"http://g8ure.ddns.net:8077","gps":"(50.85, -0.66)","users":"2","users_max":"4","offline":"no","status":"active","loc":"Chichester UK","snr":"42,45"},
                {"name":"No GPS","url":"http://nogps.example:8073","users":"0","users_max":"8"},
                {"name":"No URL","gps":"(10, 10)"},
                {"name":"Dead","url":"http://dead.example:8073","gps":"(5, 5)","offline":"yes"}
            ]
            """;

        var list = KiwiDirectoryService.Parse(body);

        // Only the two entries with BOTH url and valid gps survive.
        Assert.Equal(2, list.Count);
        var ch = list[0];
        Assert.Equal("Chichester UK", ch.Name);
        Assert.Equal("http://g8ure.ddns.net:8077", ch.Url);
        Assert.Equal(50.85, ch.Lat, 3);
        Assert.Equal(-0.66, ch.Lon, 3);
        Assert.Equal(2, ch.Users);
        Assert.Equal(4, ch.UsersMax);
        Assert.True(ch.Online);
        // The url survives KiwiSdrService's own endpoint parser (round-trip).
        Assert.True(KiwiSdrService.TryParseEndpoint(ch.Url, out _, out var port));
        Assert.Equal(8077, port);
        // The offline="yes" receiver is parsed but flagged not-online.
        Assert.False(list[1].Online);
    }

    [Fact]
    public void Parse_returns_empty_on_garbage()
    {
        Assert.Empty(KiwiDirectoryService.Parse(""));
        Assert.Empty(KiwiDirectoryService.Parse("not json at all"));
    }

    [Fact]
    public void Parse_tolerates_the_trailing_comma_the_generator_emits()
    {
        // The real rx.linkfanel.net file ends the array with `},\n]` — a trailing
        // comma that System.Text.Json rejects by default. Regression guard.
        const string body = """
            // banner
            var kiwisdr_com =
            [
                {"name":"A","url":"http://a.example:8073","gps":"(1, 2)"},
                {"name":"B","url":"http://b.example:8073","gps":"(3, 4)"},
            ]
            ;
            """;
        var list = KiwiDirectoryService.Parse(body);
        Assert.Equal(2, list.Count);
        Assert.Equal("http://b.example:8073", list[1].Url);
    }

    private sealed class FakeKiwiProvider : IKiwiReceiverProvider
    {
        public ReceiverDto? Receiver;
        public event Action? KiwiReceiverChanged;
        public ReceiverDto? GetKiwiReceiver() => Receiver;
        public void Raise() => KiwiReceiverChanged?.Invoke();
    }
}
