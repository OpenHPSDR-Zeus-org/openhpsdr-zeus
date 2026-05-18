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
        // Pin ContentRoot to the binary directory so UseStaticFiles() finds
        // wwwroot/ next to the executable regardless of how we were launched
        // ('dotnet run --project X' sets cwd=X/source-dir, an installed .app
        // launches with cwd=/, etc.). The wwwroot is copied next to the
        // binary via Zeus.Server.Hosting.csproj's <Content Include="wwwroot/**">
        // — same place appsettings.json and the WDSP zetaHat.bin/calculus
        // model files land.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Emit enums as strings on the wire ("USB", not 1) per doc 04 §3. The
        // converter also accepts ordinal integers on read, so older clients that
        // POST numeric mode values keep working.
        builder.Services.Configure<JsonOptions>(o =>
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        // Resolve TCI bind settings from configuration before DI builds, because
        // Kestrel's listeners have to be declared now. TCI shares Kestrel (rather
        // than a separate HttpListener) so clone-and-run on Windows doesn't need
        // an http.sys URL ACL — see #30.
        var tciSection = builder.Configuration.GetSection("Tci");
        var tciEnabled = tciSection.GetValue<bool>("Enabled");
        var tciBindAddress = tciSection.GetValue<string?>("BindAddress") ?? "0.0.0.0";
        var tciPort = tciSection.GetValue<int?>("Port") ?? 40001;

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
        // RX audio publish seam (Phase 1). DspPipelineService.PublishAudio
        // fans each AudioFrame across every registered IRxAudioSink.
        //
        //  - Server mode → WebSocketAudioSink (default): bit-for-bit
        //    equivalent of the pre-seam direct hub broadcast.
        //  - Desktop mode → NativeAudioSink (Phase 2b): pushes RX audio
        //    straight to the OS default output device via miniaudio,
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
            // Same singleton serves the Audio Suite pre-MOX audition sink
            // so the audition mix happens in the same playback path the
            // operator already hears RX audio through. Browser mode (the
            // else branch below) gets a NoOp impl so DI is satisfied
            // without forcing audition feature branches in callers.
            builder.Services.AddSingleton<IAuditionAudioSink>(sp =>
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
            // Audition is desktop-only in v1; browser mode gets the no-op
            // implementation so AudioPluginBridge has a non-null sink to
            // call into. Browser parity is a future phase that will
            // stream audition over the SignalR hub for the worklet to
            // mix client-side.
            builder.Services.AddSingleton<IAuditionAudioSink, NoOpAuditionAudioSink>();
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
        // PS auto-attenuate timer2code-equivalent: ramps the radio's TX step
        // attenuator (Protocol2 only today) when calcc feedback level lands outside
        // the 128..181 ideal window, so PS has a recovery path on first arm. Idle
        // when PS is off or AutoAttenuate is off — no wire, no engine pokes.
        builder.Services.AddHostedService<PsAutoAttenuateService>();

        // QRZ.com XML client. HttpClient default timeout is 100 s — cap at 10 s so a
        // hung login surfaces quickly in the UI.
        builder.Services.AddHttpClient("Qrz", c => c.Timeout = TimeSpan.FromSeconds(10));
        builder.Services.AddSingleton<CredentialStore>();
        builder.Services.AddSingleton<BandMemoryStore>();
        builder.Services.AddSingleton<LayoutStore>();
        builder.Services.AddSingleton<DspSettingsStore>();
        builder.Services.AddSingleton<PaSettingsStore>();
        builder.Services.AddSingleton<PreferredRadioStore>();
        builder.Services.AddSingleton<PsSettingsStore>();
        builder.Services.AddSingleton<FilterPresetStore>();
        builder.Services.AddSingleton<DisplaySettingsStore>();
        builder.Services.AddSingleton<NrUiPrefsStore>();
        builder.Services.AddSingleton<ThemeSettingsStore>();
        builder.Services.AddSingleton<BottomPinStore>();
        builder.Services.AddSingleton<RadioStateStore>();
        builder.Services.AddSingleton<QrzService>();
        builder.Services.AddSingleton<LogService>();

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

        // AudioPluginBridge wires PluginManager's audio-bearing plugins
        // into WdspDspEngine's realtime TX seam. No-op when no plugins
        // declare an audio component; subscribes to engine swaps so it
        // survives a Synthetic→WDSP transition mid-session.
        builder.Services.AddSingleton<AudioPluginBridge>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AudioPluginBridge>());

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
        builder.Services.AddSingleton<TciManagementService>();

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
        app.UseStaticFiles();

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
        Console.WriteLine("  Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors");
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
}
