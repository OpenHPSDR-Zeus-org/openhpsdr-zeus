// SPDX-License-Identifier: GPL-2.0-or-later
//
// ZeusHost — single source of truth for Zeus's WebApplication pipeline.
// Both Zeus.Server (service mode) and Zeus.Desktop (Photino mode) call into
// this; mode-specific differences (port, bind policy, HTTPS, console banner)
// flow in via ZeusHostOptions.

using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Dsp.Wdsp;
using Zeus.Plugins.Host;
using Zeus.Protocol1;
using Zeus.Protocol1.Discovery;
using Zeus.Server.Diagnostics;
using Zeus.Server.Tci;

namespace Zeus.Server;

public static class ZeusHost
{
    /// <summary>
    /// Build, initialize and run the Zeus host until shutdown. Convenience
    /// wrapper used by the service-mode entry point.
    /// </summary>
    public static async Task<int> RunAsync(
        string[] args,
        ZeusHostOptions options,
        CancellationToken cancellationToken = default)
    {
        var app = Build(args, options);
        await InitializeAsync(app, cancellationToken);
        await app.RunAsync(cancellationToken);
        return 0;
    }

    /// <summary>
    /// Build the WebApplication. Caller owns lifecycle: typical pattern is
    /// <see cref="InitializeAsync"/> then <c>app.StartAsync</c>/<c>RunAsync</c>.
    /// </summary>
    public static WebApplication Build(string[] args, ZeusHostOptions options)
    {
        // Pin ContentRoot to the binary directory so config + UseStaticFiles()
        // resolve relative to the executable regardless of how we were launched
        // ('dotnet run --project X' sets cwd=X/source-dir, an installed .app
        // launches with cwd=/, etc.). appsettings.json and the WDSP
        // zetaHat.bin/calculus model files sit here next to the binary.
        //
        // WebRoot (wwwroot/) is normally ContentRoot/wwwroot, but the macOS
        // installer moves wwwroot/ out to Contents/Resources/ so the .app
        // bundle can be codesigned without --deep (data subdirectories under
        // Contents/MacOS/ break inside-out signing — see installers/
        // create-macos-app.sh and issue gh-389). When the launcher exports
        // ZEUS_WEBROOT we honour it; otherwise we fall back to the default so
        // dev runs and Linux/Windows packages are unaffected.
        var webRoot = Environment.GetEnvironmentVariable("ZEUS_WEBROOT");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = string.IsNullOrWhiteSpace(webRoot) ? null : webRoot,
        });

        // Emit enums as strings on the wire ("USB", not 1) per doc 04 §3. The
        // converter also accepts ordinal integers on read, so older clients that
        // POST numeric mode values keep working.
        //
        // Diagnostics API v2 (perf): prepend the source-generated DiagnosticsJsonContext
        // so the v2 DTOs serialise through fast generated metadata. The reflection
        // resolver stays in the chain (we ensure it's present), so every other DTO —
        // including the anonymous hardware-diagnostics snapshot and all legacy
        // contracts — serialises exactly as before. CamelCase + string enums in the
        // context match the Web defaults, so the wire output is byte-identical.
        builder.Services.Configure<JsonOptions>(o =>
        {
            var json = o.SerializerOptions;
            json.TypeInfoResolverChain.Insert(0, Diagnostics.DiagnosticsJsonContext.Default);
            if (!json.TypeInfoResolverChain.OfType<System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver>().Any())
                json.TypeInfoResolverChain.Add(new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver());
            json.Converters.Add(new JsonStringEnumConverter());
        });

        // Self-diagnostic log capture (the "Report a problem" footer button). A
        // singleton ring buffer always retains the last ~1000 formatted log lines
        // (you can't capture logs retroactively), and the report ships the last
        // 100. The provider and the DI singleton MUST be the same instance so the
        // report builder snapshots what the provider has been filling.
        var diagnosticLogBuffer = new DiagnosticLogBuffer();
        builder.Services.AddSingleton(diagnosticLogBuffer);
        builder.Logging.AddProvider(new RingBufferLoggerProvider(diagnosticLogBuffer));

        // Resolve TCI bind settings from configuration before DI builds, because
        // Kestrel's listeners have to be declared now. TCI shares Kestrel (rather
        // than a separate HttpListener) so clone-and-run on Windows doesn't need
        // an http.sys URL ACL — see #30.
        var tciSection = builder.Configuration.GetSection("Tci");
        var tciEnabled = tciSection.GetValue<bool>("Enabled");
        var tciBindAddress = tciSection.GetValue<string?>("BindAddress") ?? "0.0.0.0";
        var tciPort = tciSection.GetValue<int?>("Port") ?? 40001;

        // Prefs-DB integrity guard (#637). Probe the shared zeus-prefs.db ONCE
        // before any store opens it (the bootstrap TciConfigStore below is the
        // first). A corrupt file — typically an interrupted write after a power
        // loss or network change — would otherwise throw out of a store
        // constructor during DI build and the app would fail to launch (the
        // window just flashes and closes, #635). EnsureUsable moves a genuinely
        // corrupt file aside so the stores recreate a fresh one; a merely
        // busy/locked file is left untouched.
        var prefsPath = PrefsDbPath.Get();
        if (!PrefsDbPath.EnsureUsable(prefsPath))
        {
            // Corrupt AND un-moveable (still locked — e.g. another instance has
            // it). Don't let the natural store open throw silently out of Main
            // under the desktop's detached console: record it to a persistent
            // file and fall back to a throwaway prefs DB so the app still
            // launches (degraded — settings won't persist this session).
            var fatalLog = Path.Combine(
                Path.GetDirectoryName(prefsPath) ?? ".", "zeus-prefs-fatal.log");
            try
            {
                File.AppendAllText(fatalLog,
                    $"{DateTime.UtcNow:o} prefs DB corrupt and locked at {prefsPath}; " +
                    "close any other Zeus instance or delete the file. " +
                    "Running on temporary settings for this session.\n");
            }
            catch { /* best effort — never block launch on the log write */ }

            var tmpPrefs = Path.Combine(
                Path.GetTempPath(), $"zeus-prefs-fallback-{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("ZEUS_PREFS_PATH", tmpPrefs);
            Console.Error.WriteLine(
                $"prefs.guard falling back to temporary prefs DB at {tmpPrefs}; settings will not persist this session.");
        }

        // Persisted runtime override (LiteDB). The TCI management API queues changes
        // here because Kestrel's listener can only be wired before host build; we
        // pick those changes up on the next start. Falls back to appsettings when
        // nothing has ever been persisted.
        TciRuntimeConfig? persistedTci = null;
        try
        {
            using var bootstrapTciStore = new TciConfigStore(NullLogger<TciConfigStore>.Instance);
            persistedTci = bootstrapTciStore.Get();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"tci.config.bootstrap-load failed: {ex.Message}");
        }
        if (persistedTci is not null)
        {
            tciEnabled = persistedTci.Enabled;
            tciBindAddress = persistedTci.BindAddress;
            tciPort = persistedTci.Port;
        }
        // PERF_PASS_3_DEBUG: force-disable TCI bind when running a second
        // instance on the same box (Brian's main session keeps :40001).
        // Uncommitted local edit.
        if (Environment.GetEnvironmentVariable("ZEUS_PERF_TEST") == "1")
        {
            tciEnabled = false;
        }

        // HTTPS bind for mobile-browser parity. Browsers refuse getUserMedia on a
        // non-secure context, which kills mic-uplink TX from any phone reaching
        // the server by LAN IP. Plain desktop mode skips HTTPS entirely (Photino
        // webview is same-origin localhost, no cert needed); desktop + ShareOverLan
        // re-enables HTTPS on the LAN so a phone can pick up the session while the
        // operator is away from the shack PC.
        var httpsPort = options.UseHttpsLanCert
            ? (options.HttpsPort > 0 ? options.HttpsPort : LanCertificate.GetHttpsPort())
            : 0;
        var lanCert = options.UseHttpsLanCert ? LanCertificate.GetOrCreate() : null;
        var httpsAnyIp = options.BindAllInterfaces || options.ShareOverLan;

        builder.WebHost.ConfigureKestrel(k =>
        {
            // HTTP listener: BindAllInterfaces=true (service mode) reaches the
            // SPA + API from other LAN hosts. Desktop mode (false) binds explicit
            // loopback — Photino webview only. Kestrel rejects ListenLocalhost(0),
            // so use Listen(IPAddress.Loopback, 0) for an OS-assigned loopback port.
            if (options.BindAllInterfaces)
                k.ListenAnyIP(options.HttpPort);
            else
                k.Listen(IPAddress.Loopback, options.HttpPort);

            // HTTPS listener: decoupled from the HTTP listener so desktop +
            // ShareOverLan keeps HTTP on loopback (Photino) while exposing HTTPS
            // to the LAN (phone). Plain service mode hits the same ListenAnyIP
            // branch as before the option was introduced.
            if (httpsPort > 0 && lanCert is not null)
            {
                if (httpsAnyIp)
                    k.ListenAnyIP(httpsPort, l => l.UseHttps(lanCert));
                else
                    k.Listen(IPAddress.Loopback, httpsPort, l => l.UseHttps(lanCert));
            }

            if (tciEnabled)
            {
                if (tciBindAddress is "0.0.0.0" or "*" or "")
                    k.ListenAnyIP(tciPort);
                else if (string.Equals(tciBindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
                    k.ListenLocalhost(tciPort);
                else if (IPAddress.TryParse(tciBindAddress, out var tciIp))
                    k.Listen(tciIp, tciPort);
                else
                    k.ListenAnyIP(tciPort);
            }
        });

        // ---------------- DI registrations ------------------------------------

        // DspPipelineService owns engine selection directly: Synthetic while idle,
        // WDSP while a Protocol1Client is attached. No IDspEngine DI registration —
        // swapping requires lifecycle control the container can't express.
        builder.Services.AddSingleton<IRadioDiscovery, RadioDiscoveryService>();
        builder.Services.AddSingleton<
            Zeus.Protocol2.Discovery.IRadioDiscovery,
            Zeus.Protocol2.Discovery.RadioDiscoveryService>();
        // TxIqRing is shared: TxAudioIngest writes modulated IQ into it, Protocol1Client
        // (constructed inside RadioService) reads from it for the EP2 payload.
        builder.Services.AddSingleton<Zeus.Protocol1.TxIqRing>();
        builder.Services.AddSingleton<Zeus.Protocol1.ITxIqSource>(sp =>
            sp.GetRequiredService<Zeus.Protocol1.TxIqRing>());
        builder.Services.AddSingleton<RadioService>();
        builder.Services.AddSingleton<StreamingHub>();
        // WebRTC remote-access data plane (docs/designs/remote-access-webrtc.md).
        // Phase-0 spike service; the dev-only /api/rtc/spike/offer endpoint that
        // uses it is gated behind ZEUS_RTC_SPIKE=1 in ZeusEndpoints.
        builder.Services.AddSingleton<Zeus.Server.Hosting.Remote.WebRtcSpikeService>();
        // Remote-access session password verifier (SPAKE2+, ADR-0008).
        builder.Services.AddSingleton<Zeus.Server.Hosting.Remote.RemotePasswordStore>();
        // RX audio publish seam (Phase 1). DspPipelineService.PublishAudio
        // fans each AudioFrame across every registered IRxAudioSink.
        //
        //  - Server mode → WebSocketAudioSink (default): bit-for-bit
        //    equivalent of the pre-seam direct hub broadcast.
        //  - Desktop mode → NativeAudioSink (Phase 2b): pushes RX audio
        //    straight to the selected OS output device via miniaudio,
        //    bypassing the WS path entirely. The SPA's audio decoder is
        //    opted out by Phase 2c so the browser never tries to play
        //    audio it isn't being sent.
        if (options.HostMode == ZeusHostMode.Desktop)
        {
            // Singleton so the same instance serves both the IRxAudioSink
            // collection (consumed by DspPipelineService) and the
            // IHostedService collection (responsible for opening + closing
            // the playback device alongside the host lifecycle).
            builder.Services.AddSingleton<NativeAudioSink>();
            builder.Services.AddSingleton<IRxAudioSink>(sp =>
                sp.GetRequiredService<NativeAudioSink>());
            // Same singleton serves local mono side-channel playback
            // (currently WAV/local monitor paths) in the same playback path
            // the operator already hears RX audio through. Browser mode (the
            // else branch below) gets a NoOp impl so DI is satisfied without
            // forcing host-mode branches in callers.
            builder.Services.AddSingleton<IPreviewAudioSink>(sp =>
                sp.GetRequiredService<NativeAudioSink>());
            builder.Services.AddHostedService(sp =>
                sp.GetRequiredService<NativeAudioSink>());

            // ShareOverLan: also register the WebSocket sink so any LAN
            // browser hitting https://<lan-ip>:6443 gets RX audio. The hub
            // short-circuits on _clients.IsEmpty, so with zero LAN clients
            // attached this is effectively a no-op (one virtual call + one
            // atomic int read per AudioFrame).
            if (options.ShareOverLan)
            {
                builder.Services.AddSingleton<IRxAudioSink, WebSocketAudioSink>();
            }
            else
            {
                // No LAN sharing, so no ungated WS sink — but the browser-side
                // DeepCW decoder (running in this host's own webview) still
                // needs RX PCM. GatedWebSocketAudioSink streams 0x02 to WS
                // clients only while one has requested it (MsgType
                // .AudioStreamRequest), so it's inert until the decoder panel
                // is open and never duplicates the native playback path.
                builder.Services.AddSingleton<IRxAudioSink, GatedWebSocketAudioSink>();
            }

            // Mic capture: replaces the browser → WS MicPcm uplink in
            // desktop mode. TxAudioIngest still subscribes to
            // StreamingHub.MicPcmReceived — with ShareOverLan on, that
            // subscription becomes the live phone-mic path, so a paired
            // phone can MOX and transmit. NativeMicCapture continues to
            // feed OnMicPcmBytes directly for the shack-PC mic. Either
            // source goes through the same TxAudioIngest entry point so
            // WDSP / IQ ring / protocol packers don't see a transport
            // difference.
            builder.Services.AddSingleton<NativeMicCapture>();
            builder.Services.AddHostedService(sp =>
                sp.GetRequiredService<NativeMicCapture>());
        }
        else
        {
            builder.Services.AddSingleton<IRxAudioSink, WebSocketAudioSink>();
            // The native local playback side-channel is desktop-only; browser
            // mode gets the no-op implementation. Audio Suite preview is
            // exposed in both modes through the TX Monitor path instead.
            builder.Services.AddSingleton<IPreviewAudioSink, NoOpPreviewAudioSink>();
        }
        // WDSPwisdom bootstrap: run FFTW plan caching on a worker at app start so the
        // first /api/connect isn't blocked for ~2 min while WDSP plans FFTs 64..262144.
        // Clients are told to keep Connect disabled until phase=Ready.
        builder.Services.AddSingleton<WdspWisdomInitializer>();
        builder.Services.AddHostedService<WisdomBootstrapService>();
        builder.Services.AddSingleton<DspPipelineService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DspPipelineService>());
        // Per-radio frequency calibration (issue #325). Stateless coordinator —
        // owns no resources, just a SemaphoreSlim to prevent re-entry.
        builder.Services.AddSingleton<FrequencyCalibrationService>();
        builder.Services.AddSingleton<TxService>();
        builder.Services.AddSingleton<TxAudioIngest>();
        // Resolve at startup so the MicPcmReceived subscription attaches before the
        // first client connects (lazy resolution would leave early frames unhandled).
        builder.Services.AddHostedService<TxAudioIngestStartup>();
        builder.Services.AddSingleton<TxMetersService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TxMetersService>());
        // TxTuneDriver pumps silent mic blocks through WDSP TXA while TUN is on so
        // the post-gen tone actually reaches the ring (no mic uplink during TUN).
        builder.Services.AddHostedService<TxTuneDriver>();
        // Host-side CW keyer (zeus-drf). Single instance, drains a job queue
        // and pushes envelope-shaped IQ directly into TxIqRing while holding
        // MOX as MoxSource.Cwx.
        builder.Services.AddSingleton<CwSettingsStore>();
        builder.Services.AddSingleton<CwSidetoneSource>(sp =>
        {
            // Seed the live generator from persisted operator preferences
            // so the first key-down after a cold start uses the saved pitch
            // and gain (not the source's defaults). REST PUT /api/cw/settings
            // pushes subsequent changes — see ZeusEndpoints.MapPut.
            var s = new CwSidetoneSource();
            var cfg = sp.GetRequiredService<CwSettingsStore>().Get();
            s.SetPitchHz(cfg.SidetoneHz);
            s.SetGainDb(cfg.SidetoneGainDb);
            return s;
        });
        builder.Services.AddSingleton<CwEngine>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CwEngine>());
        // The classic server-side CW receive decoder (CwDecoderService) was
        // retired in favour of the browser-side DeepCW neural decoder
        // (zeus-web/src/plugins/deepcw), which taps the RX audio bus client-
        // side. Desktop/native-audio mode feeds it via the on-demand
        // GatedWebSocketAudioSink (see the audio-sink registration above).
        // Voyeur Mode (zeus-la5) was extracted into the installable plugin
        // com.kb2uka.voyeur (openhpsdr-zeus-plugins/monitors/Voyeur). Its host
        // seams remain in core: AudioTapBridge (RX tap), RadioStateReader,
        // PluginQrzLookup, and the voyeur-engines-v1 build workflow.
        // WAV recorder/player: taps DspPipelineService RX + TX-monitor audio to
        // record float32 WAVs (default save folder = Downloads) and plays them
        // back locally via the preview sink. Over-the-air playback is layered
        // on later. Singleton resolved on first /api/wav call; its ctor wires
        // the pipeline event subscriptions.
        builder.Services.AddSingleton<Zeus.Server.Wav.WavRecorderService>();
        // PS auto-attenuate timer2code-equivalent: ramps the radio's TX step
        // attenuator (Protocol2 only today) when calcc feedback level lands outside
        // the 128..181 ideal window, so PS has a recovery path on first arm. Idle
        // when PS is off or AutoAttenuate is off — no wire, no engine pokes.
        builder.Services.AddHostedService<PsAutoAttenuateService>();
        // Promote the radio's hardware-PTT echo (HL2 rear KEY tip, external
        // PTT line) into a host MOX request — without it the gateware-driven
        // CW carrier transmits while Zeus stays unkeyed (UI off, meters at
        // idle cadence, FR-6 timeout disarmed).
        // Per-install hardware-PTT-IN → MOX enable gate (default OFF). Gates
        // whether ExternalPttService promotes a footswitch edge to MOX; the
        // PTT-IN status lamp tracks the footswitch regardless.
        builder.Services.AddSingleton<PttSettingsStore>();
        builder.Services.AddSingleton<ExternalPttService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ExternalPttService>());

        // QRZ.com XML client. HttpClient default timeout is 100 s — cap at 10 s so a
        // hung login surfaces quickly in the UI.
        builder.Services.AddHttpClient("Qrz", c => c.Timeout = TimeSpan.FromSeconds(10));
        // Localhost proxy to the HamClock sidecar's propagation engine. The first
        // P.533-14 prediction for a cold path can take ~20 s upstream; allow for it.
        builder.Services.AddHttpClient("Propagation", c => c.Timeout = TimeSpan.FromSeconds(25));
        // Production download manifest (downloads.openhpsdrzeus.com) for in-app
        // update checks. Plain JSON — no GitHub API headers.
        builder.Services.AddHttpClient("ZeusUpdates", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("OpenHPSDR-Zeus-Updater/1.0");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });
        builder.Services.AddSingleton<CredentialStore>();
        builder.Services.AddSingleton<BandMemoryStore>();
        builder.Services.AddSingleton<LayoutStore>();
        // Relaunch helper for the prefs-database (profile) selector — switching
        // the active DB applies on the next launch, so the endpoint relaunches.
        builder.Services.AddSingleton<AppRestartService>();

        // Self-diagnostic "Report a problem" feature: read-only probes, the
        // symptom→recipe registry, the known-issue rules (seeded from docs/rca +
        // docs/lessons), and the report builder. All strictly read-only — the
        // tx-ps probe reads PsEnabled/HwPeak but never arms PS (burn-zone).
        builder.Services.AddSingleton<IDiagnosticProbe, EnvironmentProbe>();
        builder.Services.AddSingleton<IDiagnosticProbe, ConnectionProbe>();
        builder.Services.AddSingleton<IDiagnosticProbe, BoardProbe>();
        builder.Services.AddSingleton<IDiagnosticProbe, DspAudioProbe>();
        builder.Services.AddSingleton<IDiagnosticProbe, TxPsProbe>();
        builder.Services.AddSingleton<IKnownIssueRule, PsNotArmedRule>();
        builder.Services.AddSingleton<IKnownIssueRule, IqWriteGateRule>();
        builder.Services.AddSingleton<IKnownIssueRule, RxAuxBypassRule>();
        builder.Services.AddSingleton<IKnownIssueRule, AudioUnderrunRule>();
        builder.Services.AddSingleton<IKnownIssueRule, Hl2DriveModelRule>();
        builder.Services.AddSingleton<IKnownIssueRule, RxaAudioSilenceRule>();
        builder.Services.AddSingleton<IKnownIssueRule, DisconnectionRule>();
        builder.Services.AddSingleton<IKnownIssueRule, PsStartupArmedRule>();
        builder.Services.AddSingleton<SymptomRegistry>();
        builder.Services.AddSingleton<DiagnosticReportBuilder>();

        builder.Services.AddSingleton<DspSettingsStore>();
        builder.Services.AddSingleton<CfcPresetStore>();
        builder.Services.AddSingleton<PaSettingsStore>();
        builder.Services.AddSingleton<PreferredRadioStore>();
        builder.Services.AddSingleton<PsSettingsStore>();
        builder.Services.AddSingleton<FilterPresetStore>();
        builder.Services.AddSingleton<DisplaySettingsStore>();
        builder.Services.AddSingleton<DisplayIntelligenceSettingsStore>();
        builder.Services.AddSingleton<ToolbarSettingsStore>();
        builder.Services.AddSingleton<NrUiPrefsStore>();
        builder.Services.AddSingleton<AudioDeviceSettingsStore>();
        builder.Services.AddSingleton<TxStationProfileStore>();
        builder.Services.AddSingleton<TxFidelityPolicyStore>();
        // Unified TX Audio Profile store — the single operator-named macro that
        // replaces both the named audio-suite TX profiles and the fixed 3-up TX
        // station profiles. Registered before RadioService is resolved so the
        // optional ctor param is injected and the startup scalar overlay runs.
        builder.Services.AddSingleton<TxAudioProfileStore>();
        builder.Services.AddSingleton<RepoUpdateService>();
        builder.Services.AddSingleton<ThemeSettingsStore>();
        builder.Services.AddSingleton<BottomPinStore>();
        builder.Services.AddSingleton<PanWfSplitStore>();
        // Desktop main-window geometry (Photino). Only RunDesktop reads it; the
        // store is harmless to register in service/headless modes (no consumer).
        builder.Services.AddSingleton<WindowGeometryStore>();
        builder.Services.AddSingleton<RadioStateStore>();
        builder.Services.AddSingleton<QrzService>();
        builder.Services.AddSingleton<LogService>();
        builder.Services.AddSingleton<FrontendDspSceneDiagnosticsService>();
        builder.Services.AddSingleton<FrontendAudioPlaybackDiagnosticsService>();
        builder.Services.AddSingleton<HardwareDiagnosticsService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HardwareDiagnosticsService>());

        // Live Diagnostics API v2. Each provider self-registers as
        // IDiagnosticsProvider; the registry collects them once at startup and the
        // unified /api/diagnostics/v2 surface + push publisher are driven entirely
        // off that collection. Adding a future provider is a single AddSingleton
        // line here — it then auto-appears on the API, in the health frame, and in
        // the conformance test harness with zero further wiring.
        builder.Services.AddSingleton<Diagnostics.IDiagnosticsProvider, Diagnostics.DspLiveDiagnosticsProvider>();
        builder.Services.AddSingleton<Diagnostics.IDiagnosticsProvider, Diagnostics.DspModernizationProvider>();
        builder.Services.AddSingleton<Diagnostics.IDiagnosticsProvider, Diagnostics.HardwareDiagnosticsProvider>();
        builder.Services.AddSingleton<Diagnostics.IDiagnosticsProvider, Diagnostics.FrontendDspSceneProvider>();
        builder.Services.AddSingleton<Diagnostics.IDiagnosticsProvider, Diagnostics.FrontendAudioPlaybackProvider>();
        builder.Services.AddSingleton<Diagnostics.IDiagnosticsProvider, Diagnostics.PlatformDiagnosticsProvider>();
        builder.Services.AddSingleton<Diagnostics.IDiagnosticsProvider, Diagnostics.StreamingHubProvider>();
        builder.Services.AddSingleton<Diagnostics.IDiagnosticsProvider, Diagnostics.RadioStateProvider>();
        builder.Services.AddSingleton<Diagnostics.IDiagnosticsProvider, Diagnostics.RadioCapabilitiesProvider>();
        builder.Services.AddSingleton<Diagnostics.IDiagnosticsProvider, Diagnostics.HamClockProvider>();
        builder.Services.AddSingleton<Diagnostics.IDiagnosticsProvider, Diagnostics.ExternalPttProvider>();
        builder.Services.AddSingleton<Diagnostics.IDiagnosticsProvider, Diagnostics.Protocol2TxIqProvider>();
        builder.Services.AddSingleton<Diagnostics.IDiagnosticsProvider, Diagnostics.DspPipelineProvider>();
        builder.Services.AddSingleton<Diagnostics.DiagnosticsProviderRegistry>();
        builder.Services.AddSingleton<Diagnostics.DiagnosticsSelfCheckCache>();
        builder.Services.AddSingleton<Diagnostics.DiagnosticsFramePublisher>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Diagnostics.DiagnosticsFramePublisher>());

        // Regional band planning (issue #65 PRD). BandPlanStore loads shipped
        // JSON under BandPlans/ at startup and resolves parent→override chains;
        // BandPlanService owns active region + GetSegment/InBand hot path;
        // BandPrefsStore persists current region + TX-guard override.
        builder.Services.AddSingleton<BandPlanStore>();
        builder.Services.AddSingleton<BandPrefsStore>();
        builder.Services.AddSingleton<BandPlanService>();
        builder.Services.AddSingleton<IBandPlanService>(sp => sp.GetRequiredService<BandPlanService>());

        // rotctld (hamlib rotator daemon) client. BackgroundService with persistent
        // TCP and reconnect-on-failure. Singleton so config/state survive across
        // requests; hosted-service registration runs ExecuteAsync.
        builder.Services.AddSingleton<RotctldConfigStore>();
        builder.Services.AddSingleton<RotctldService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RotctldService>());

        // Capabilities snapshot for /api/capabilities. Captures host-mode,
        // platform, and version info once at construction. The frontend
        // queries this on connect to surface host metadata.
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<CapabilitiesService>();

        // Plugin system v2 (docs/proposals/plugin-system-v2.md). PluginManager
        // is registered as IHostedService so plugin discovery / activation
        // runs as part of normal app startup; plugin REST endpoints are
        // mounted below via PluginEndpoints.MapAll under /api/plugins/...
        builder.Services.AddZeusPlugins(prefsDbPathProvider: PrefsDbPath.Get);

        // ChainOrderService — owns the canonical Audio Suite plugin
        // chain order (drag-droppable tile sequence in the Audio Suite
        // window). Persists to zeus-prefs.db via ChainOrderStore so the
        // operator's order survives backend restarts. Broadcasts
        // AudioChainOrderFrame (0x1E) on every change so other
        // connected clients (LAN-share phone, second browser) update
        // their tile strip without polling. AudioPluginBridge subscribes
        // to OrderChanged and re-slots the runtime chain.
        builder.Services.AddSingleton<ChainOrderStore>();
        builder.Services.AddSingleton<ChainOrderService>();
        builder.Services.AddSingleton<RxChainOrderStore>();
        builder.Services.AddSingleton<RxChainOrderService>();

        // AudioProfileService — named snapshots of the chain config
        // (active order + parked set + master bypass). Persists to
        // zeus-prefs.db via AudioProfileStore. Lets the operator recall
        // a whole rack layout ("Contest" / "Ragchew") in one click.
        builder.Services.AddSingleton<AudioProfileStore>();
        builder.Services.AddSingleton<AudioProfileService>();
        builder.Services.AddSingleton<RxAudioProfileStore>();
        builder.Services.AddSingleton<RxAudioProfileService>();

        // VstEngineController owns the out-of-process VST engine (the opt-in
        // "VST" processing mode). Singleton so AudioPluginBridge (realtime tap)
        // and AudioProcessingModeService (lifecycle) share one instance. Holds
        // the shared-memory bridge for its lifetime; the engine process is only
        // launched while VST mode is active. Inert until opted into.
        builder.Services.AddSingleton<Zeus.Plugins.Host.Audio.VstEngineController>();

        // RX VST engine owns a second, independent out-of-process VST host for
        // receive-side rx.post-demod plugins. TX and RX can therefore run two
        // separate instances of the same VST without sharing engine state.
        builder.Services.AddSingleton<RxVstEngineService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RxVstEngineService>());

        // AudioPluginBridge wires PluginManager's audio-bearing plugins
        // into WdspDspEngine's realtime TX seam. No-op when no plugins
        // declare an audio component; subscribes to engine swaps so it
        // survives a Synthetic→WDSP transition mid-session.
        builder.Services.AddSingleton<AudioPluginBridge>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AudioPluginBridge>());

        // AudioTapBridge fans the RX, raw-TX-mic and processed-TX-air audio
        // streams out to read-only plugin taps (IRxAudioTapPlugin /
        // ITxAudioTapPlugin — recorders, decoders). Independent of the insert
        // chain; adds no code to the audio hot paths (subscribes to events
        // those paths already raise).
        builder.Services.AddSingleton<AudioTapBridge>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AudioTapBridge>());

        // PluginPlaybackSink lets a plugin play a clip locally (preview) or
        // inject it on the air (TX chain, under operator MOX). Surfaced to
        // plugins via IPluginContext.Playback.
        builder.Services.AddSingleton<Zeus.Plugins.Contracts.Audio.IAudioPlaybackSink, PluginPlaybackSink>();

        // RadioController lets a plugin holding the ControlRadio capability key
        // TX (MoxSource.Plugin) and set VFO/mode — the same surfaces the UI and
        // CW engine use. Surfaced via IPluginContext.RadioController (gated on
        // the capability in PluginManager). Enables plugin keyers (RTTY/voice).
        builder.Services.AddSingleton<Zeus.Plugins.Contracts.IRadioController, RadioController>();

        // RadioStateReader gives a plugin holding ReadRadioState the operator's
        // current VFO / mode / band / MOX (read-only), wrapping RadioService.
        // Surfaced via IPluginContext.Radio (gated on the capability in
        // PluginManager). Voyeur uses it for per-session frequency/band metadata.
        builder.Services.AddSingleton<Zeus.Plugins.Contracts.IRadioStateReader, RadioStateReader>();

        // PluginQrzLookup surfaces the core QrzService to plugins (gated on the
        // NetworkAccess capability in PluginManager) so a plugin reuses the
        // operator's stored QRZ credentials + rate-limit gate instead of asking
        // for them again. Surfaced via IPluginContext.Qrz.
        builder.Services.AddSingleton<Zeus.Plugins.Contracts.IQrzLookup, PluginQrzLookup>();

        // AudioChainMasterBypassService — operator's "disengage the
        // whole Audio Suite" lever. Default is true (bypassed) on first
        // install so a brand-new operator's chain is inert until they
        // engage it. Persists via AudioChainSettingsStore; broadcasts
        // AudioMasterBypassFrame (0x1F) for TX changes and
        // RxAudioMasterBypassFrame (0x34) for RX changes. Registered
        // AFTER AudioPluginBridge so its StartAsync runs after the
        // bridge's, and the initial-state write-through finds a
        // constructed chain.
        builder.Services.AddSingleton<AudioChainSettingsStore>();
        builder.Services.AddSingleton<AudioChainMasterBypassService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AudioChainMasterBypassService>());
        builder.Services.AddHostedService<RxAudioProfileStartupService>();

        // AudioProcessingModeService — operator's Native-vs-VST route selector.
        // Default is Native (Brian's in-process chain) so a fresh operator's TX
        // is byte-identical to a build with no VST mode. Selecting VST launches
        // the external engine and arms the realtime tap; if the engine isn't
        // installed the path falls through clean. Persists via
        // AudioProcessingModeStore. No hub frame (would add a Contracts wire
        // type); clients read GET /api/audio-suite/processing-mode.
        builder.Services.AddSingleton<AudioProcessingModeStore>();
        builder.Services.AddSingleton<AudioProcessingModeService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AudioProcessingModeService>());

        // TxAudioProfileService — orchestrates the unified TX Audio Profile
        // system: capture (mic/leveler/CFC/bandpass/processing-mode/chain shape/
        // VST blobs + native plugin dumps/fidelity), apply (write-through the
        // live Set* paths — PureSignal untouched), and the last-loaded pointer.
        // Registered as a hosted service AFTER AudioProcessingModeService so its
        // StartAsync (seed starters + background-replay last-loaded chain) runs
        // once the engine route has begun replaying. Never blocks startup.
        builder.Services.AddSingleton<TxAudioProfileService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TxAudioProfileService>());

        // HamClockService — optional, on-demand embed of OpenHamClock (MIT,
        // github.com/accius/openhamclock) as a Zeus panel. Entirely inert until
        // the operator clicks Install in Settings → HamClock; nothing here
        // touches the radio / DSP / TX path. Registered as a hosted service
        // only so its sidecar Node process is killed on Zeus shutdown.
        builder.Services.AddSingleton<HamClockService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HamClockService>());
        // Point-to-point propagation (proxies the HamClock sidecar's P.533-14 API).
        builder.Services.AddSingleton<PropagationService>();
        // Comprehensive solar / space-weather (proxies the sidecar's N0NBH feed).
        builder.Services.AddSingleton<SpaceWeatherService>();

        // ActivationSpotsService — polls the public POTA + SOTA activation feeds
        // on a timer and caches the merged snapshot for the Spots panel
        // (GET /api/spots/activations). Self-contained background poller; touches
        // nothing on the radio / DSP / TX path. Unrelated to the TCI DX-cluster
        // SpotManager below. SpotsSettingsStore persists the operator's feed /
        // poll-interval / click-to-tune preferences.
        builder.Services.AddSingleton<SpotsSettingsStore>();
        builder.Services.AddSingleton<ActivationSpotsService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ActivationSpotsService>());

        // TCI (Transceiver Control Interface) — ExpertSDR3-compatible WebSocket server
        // for remote control by loggers (Log4OM, N1MM+), digital-mode apps (JTDX, WSJT-X),
        // and SDR display tools. Disabled by default; enable via appsettings.json Tci:Enabled=true.
        builder.Services.Configure<TciOptions>(builder.Configuration.GetSection("Tci"));
        // PostConfigure applies the persisted runtime override (set via /api/tci/config
        // in a previous session) on top of appsettings, so in-process services see the
        // same Enabled/Bind/Port values that Kestrel just bound to.
        if (persistedTci is not null)
        {
            var pendingTci = persistedTci;
            builder.Services.PostConfigure<TciOptions>(o =>
            {
                o.Enabled = pendingTci.Enabled;
                o.BindAddress = pendingTci.BindAddress;
                o.Port = pendingTci.Port;
            });
        }
        builder.Services.AddSingleton<TciConfigStore>();
        builder.Services.AddSingleton<SpotManager>();
        builder.Services.AddSingleton<TciServer>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TciServer>());
        builder.Services.AddSingleton<SpotBroadcastService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SpotBroadcastService>());
        builder.Services.AddSingleton<TciManagementService>();

        // ZeusChat — operator-to-operator chat over the Cloudflare relay.
        // Singleton (API surface) + hosted service (relay connection lifecycle),
        // same shape as SpotBroadcastService above. Opt-in persisted via
        // ChatEnabledStore; default OFF.
        builder.Services.AddSingleton<ChatEnabledStore>();
        builder.Services.AddSingleton<ChatService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ChatService>());

        var app = builder.Build();

        // Surface the listening endpoints up front so the operator can pick one
        // for their phone (service mode). Skipped when HttpPort=0 because the
        // OS-assigned port isn't known until after StartAsync — desktop mode
        // logs its own "hosting backend at <url>" line at that point.
        if (options.HttpPort != 0)
        {
            var startupLog = app.Services.GetRequiredService<ILogger<object>>();
            var lanIps = options.BindAllInterfaces ? LanCertificate.GetLanIps() : new List<IPAddress>();
            var lanLines = (lanIps.Count == 0 || httpsPort == 0)
                ? string.Empty
                : "   (LAN: " + string.Join(", ", lanIps.Select(ip => $"https://{ip}:{httpsPort}")) + ")";
            var httpsBit = httpsPort > 0 ? $"   https://localhost:{httpsPort}" : string.Empty;
            startupLog.LogInformation(
                "Zeus listening:  http://localhost:{HttpPort}{HttpsBit}{LanLines}",
                options.HttpPort, httpsBit, lanLines);

            // Tailscale (CGNAT 100.64.0.0/10) commonly reclassifies the physical
            // LAN adapter as 'Public' on Windows, causing the Windows Firewall to
            // silently drop inbound HPSDR receive UDP — TX works, RX is silent.
            // Warn early so the operator has a clue before they connect.
            if (OperatingSystem.IsWindows())
            {
                var tailscaleIps = lanIps.Where(IsTailscaleAddress).ToList();
                if (tailscaleIps.Count > 0)
                    startupLog.LogWarning(
                        "firewall.tailscale.detected ips={Ips} — Tailscale (or another CGNAT VPN) " +
                        "is active. On Windows this can reclassify your LAN adapter as a 'Public' " +
                        "network, causing Windows Firewall to block incoming HPSDR receive UDP " +
                        "packets silently. TX will work; RX will be silent. Add an inbound " +
                        "Windows Firewall rule for OpenhpsdrZeus.exe to fix this.",
                        string.Join(", ", tailscaleIps));
            }
        }

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

        // Port-branch: any request arriving on the TCI listener (default :40001) is
        // routed straight to TciServer.AcceptAsync. Keeps TCI clients connecting to
        // ws://host:40001/ (root path, per ExpertSDR3 spec) from colliding with the
        // API/SPA on the main port.
        if (tciEnabled)
        {
            app.UseWhen(
                ctx => ctx.Connection.LocalPort == tciPort,
                tciBranch => tciBranch.Run(ctx =>
                    ctx.RequestServices.GetRequiredService<TciServer>().AcceptAsync(ctx)));
        }

        app.UseDefaultFiles();
        // Map the asset types the Vite build emits for the DeepCW decoder.
        // ASP.NET's default provider refuses to serve unknown extensions, so
        // without these the bundled ONNX model and onnxruntime-web wasm/loader
        // would 404. (.wasm/.mjs are known to modern ASP.NET, but we map them
        // explicitly so this doesn't depend on the host's defaults.)
        var staticContentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
        staticContentTypes.Mappings[".onnx"] = "application/octet-stream";
        staticContentTypes.Mappings[".wasm"] = "application/wasm";
        staticContentTypes.Mappings[".mjs"] = "text/javascript";
        app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions { ContentTypeProvider = staticContentTypes });

        // WDSP NR2 EMNR fopen()s "zetaHat.bin" and "calculus" by bare relative name
        // (native/wdsp/emnr.c:215,397). Anchor cwd to the assembly dir so those files
        // — copied next to the binary by Zeus.Server.Hosting.csproj — are reachable.
        // Without this, WDSP silently falls through to the compiled-in CzetaHat / GG /
        // GGS fallback tables; numerically equivalent today, but won't pick up a future
        // retrained .bin without a libwdsp rebuild.
        {
            var log = app.Services.GetRequiredService<ILogger<object>>();
            var baseDir = AppContext.BaseDirectory;
            Directory.SetCurrentDirectory(baseDir);
            var zetaPath = Path.Combine(baseDir, "zetaHat.bin");
            var calcPath = Path.Combine(baseDir, "calculus");
            log.LogInformation(
                "wdsp.nr2.models cwd={Cwd} zetaHat.bin={ZetaState} calculus={CalcState}",
                baseDir,
                File.Exists(zetaPath) ? "loaded" : "missing→compiled-fallback",
                File.Exists(calcPath) ? "loaded" : "missing→compiled-fallback");
        }

        // Wire wisdom initializer → hub so every phase change AND every per-step
        // status update from WDSP's wisdom_get_status() poll is broadcast to all
        // connected clients. Seed the hub's cached phase + status with whatever
        // the initializer currently reports (Idle/empty at first boot, Ready on
        // restart once the file is cached).
        {
            var wisdom = app.Services.GetRequiredService<WdspWisdomInitializer>();
            var hub = app.Services.GetRequiredService<StreamingHub>();
            hub.SetWisdomPhase(wisdom.Phase);
            hub.SetWisdomStatus(wisdom.Status);
            wisdom.PhaseChanged += phase => hub.Broadcast(new WisdomStatusFrame(phase, wisdom.Status));
            wisdom.StatusChanged += status => hub.Broadcast(new WisdomStatusFrame(wisdom.Phase, status));
        }

        // Band plan service → hub: every region change or plan edit fires 0x1B.
        {
            var bandPlan = app.Services.GetRequiredService<BandPlanService>();
            var hub = app.Services.GetRequiredService<StreamingHub>();
            bandPlan.PlanChanged += () => hub.BroadcastBandPlanChanged(bandPlan.CurrentRegion.Id);
        }

        app.MapZeusEndpoints();
        // PluginEndpoints.MapAll iterates manager.Active to wire each
        // IBackendPlugin's MapEndpoints into the route table. The hosted-
        // service StartAsync fires later (during app.Run), so we have to
        // activate plugins synchronously here or their routes never land.
        // ActivateAsync is idempotent per id, so the runtime's StartAsync
        // call below is a no-op.
        var pluginManager = app.Services.GetRequiredService<Zeus.Plugins.Host.PluginManager>();
        pluginManager.StartAsync(default).GetAwaiter().GetResult();
        Zeus.Plugins.Host.PluginEndpoints.MapAll(app, pluginManager);

        // Optional startup banner — service mode prints to its console window
        // (operator-facing UI), desktop mode hides the console and skips this.
        if (options.PrintConsoleBanner && Environment.UserInteractive)
        {
            PrintBanner(options.HttpPort, tciEnabled, tciBindAddress, tciPort);
        }

        return app;
    }

    /// <summary>
    /// Run async post-build setup that has to complete before the host accepts
    /// requests (silent QRZ login restore today). Safe to call multiple times.
    /// </summary>
    public static async Task InitializeAsync(WebApplication app, CancellationToken cancellationToken = default)
    {
        // Initialize QrzService to restore stored credentials (silent login).
        var qrzService = app.Services.GetRequiredService<QrzService>();
        await qrzService.InitializeAsync(cancellationToken);
    }

    static void PrintBanner(int httpPort, bool tciEnabled, string tciBindAddress, int tciPort)
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var attr = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute;
        var version = attr?.InformationalVersion ?? "unknown";

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  Zeus — OpenHPSDR Protocol 1 / Protocol 2 Client");
        Console.WriteLine($"  Version: {version}");
        Console.WriteLine("  Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors");
        Console.WriteLine("  Licensed under GPL-2.0-or-later");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine();
        Console.WriteLine($"  Server listening on: http://localhost:{httpPort}");
        if (tciEnabled)
            Console.WriteLine($"  TCI listening on:    {tciBindAddress}:{tciPort}");
        Console.WriteLine();
        Console.WriteLine("  Open your web browser and navigate to the server address above.");
        Console.WriteLine();
        Console.WriteLine("  To STOP the server:");
        Console.WriteLine("    • Press Ctrl+C in this console window, or");
        Console.WriteLine("    • Close this console window");
        Console.WriteLine();
        Console.WriteLine("  Server starting...");
        Console.WriteLine();
    }

    private static bool IsTailscaleAddress(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }
}
