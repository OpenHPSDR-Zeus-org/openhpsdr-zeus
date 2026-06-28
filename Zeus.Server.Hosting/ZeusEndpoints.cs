// SPDX-License-Identifier: GPL-2.0-or-later
//
// Route surface for the Zeus host. Extracted from the original
// Zeus.Server/Program.cs top-level statements so that both Zeus.Server
// (service mode) and Zeus.Desktop (Photino in-process mode) share one
// endpoint definition.

using System.Net;
using Zeus.Contracts;
using Zeus.Plugins.Host;
using Zeus.Dsp;
using Zeus.Dsp.Wdsp;
using Zeus.Protocol1;
using Zeus.Protocol1.Discovery;
using Zeus.Protocol2;
using Zeus.Server.Diagnostics;
using Zeus.Server.Tci;
using Zeus.Server.Wsjtx;

namespace Zeus.Server;

public static class ZeusEndpoints
{
    /// <summary>
    /// Maps every Zeus HTTP/WS endpoint onto <paramref name="app"/>. Single
    /// source of truth shared by service-mode and desktop-mode entry points.
    /// </summary>
    public static WebApplication MapZeusEndpoints(this WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILogger<object>>();

        // Live Diagnostics API v2 — unified, registry-driven surface
        // (/api/diagnostics/v2). Legacy diagnostics routes below delegate to
        // the same providers for wire-compatible snapshots.
        app.MapDiagnosticsV2();

        app.MapGet("/api/version", () =>
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var attr = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute;
            var version = attr?.InformationalVersion ?? "unknown";
            return Results.Ok(new { version });
        });

        // Operator manual. The PDF is built fresh from docs/manual each release
        // and bundled next to the host binary (release.yml `build-manual` job),
        // so it resolves under AppContext.BaseDirectory on every platform —
        // including inside the macOS .app bundle and the Linux AppImage, where
        // it is otherwise unreachable. Served inline (no download disposition)
        // so the About-panel link opens it in a new tab. Dev builds don't carry
        // the PDF, so this 404s locally — the link simply does nothing then.
        app.MapGet("/manual", () =>
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Zeus-Operator-Manual.pdf");
            return System.IO.File.Exists(path)
                ? Results.File(path, "application/pdf", enableRangeProcessing: true)
                : Results.NotFound("Operator manual is not bundled in this build.");
        });

        // Self-diagnostic "Report a problem" feature. The picker fetches the
        // symptom list; submitting a symptom + free text returns a redacted,
        // paste-ready report (Markdown + last-100 log lines) plus a prefilled
        // GitHub "new issue" URL. Strictly read-only.
        app.MapGet("/api/diagnostics/symptoms",
            (DiagnosticReportBuilder diag) => Results.Ok(diag.Symptoms()));

        app.MapPost("/api/diagnostics/report",
            (DiagnosticRequest req, DiagnosticReportBuilder diag) =>
            {
                log.LogInformation("api.diagnostics.report symptom={Symptom}", req.SymptomId ?? "(none)");
                return Results.Ok(diag.Build(req));
            });

        // Maintainer remote-support: the OPERATOR's local decision surface for an
        // inbound read-only diagnostics request (remote-diag P3). The request
        // arrives over the broker and is parked by SupportRequestCoordinator; these
        // endpoints let the in-app Allow/Deny UI (P5) list and answer it. They are
        // intentionally LOCAL only — the support tunnel's allowlist excludes
        // /api/support and the operator tunnel denies it, so a remote peer can
        // never approve a session for itself.
        app.MapGet("/api/support/pending",
            (Zeus.Server.Hosting.Support.SupportRequestCoordinator coord) => Results.Ok(coord.Pending()));

        app.MapPost("/api/support/approve",
            (SupportDecisionRequest req, Zeus.Server.Hosting.Support.SupportRequestCoordinator coord) =>
            {
                var id = req.RequestId ?? "";
                var ok = coord.Approve(id);
                log.LogInformation("api.support.approve id={RequestId} ok={Ok}", id, ok);
                return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { ok = false });
            });

        app.MapPost("/api/support/deny",
            (SupportDecisionRequest req, Zeus.Server.Hosting.Support.SupportRequestCoordinator coord) =>
            {
                var id = req.RequestId ?? "";
                var ok = coord.Deny(id);
                log.LogInformation("api.support.deny id={RequestId} ok={Ok}", id, ok);
                return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { ok = false });
            });

        // L1 "Remote Diagnostics available" master switch + crash auto-share
        // sub-toggle (remote-diag P5). OFF by default; while OFF the coordinator
        // refuses inbound requests outright. The Settings → Server panel drives
        // PUT; the in-app Allow/Deny prompt + active-session badge poll /status.
        app.MapGet("/api/support/availability",
            (Zeus.Server.Hosting.Support.SupportAvailabilityStore store) =>
                Results.Ok(new { available = store.IsAvailable, autoShareCrashes = store.AutoShareOnCrash }));

        app.MapPut("/api/support/availability",
            (SupportAvailabilityRequest req, Zeus.Server.Hosting.Support.SupportAvailabilityStore store) =>
            {
                var available = req.Available ?? false;
                var autoShare = req.AutoShareCrashes ?? false;
                var (newAvailable, newAutoShare) = store.Set(available, autoShare);
                // Push the change to the out-of-process sidecar so it can
                // register/deregister broker presence (P3c consumes this).
                Zeus.Server.SupportSidecar.NotifyAvailabilityChanged(newAvailable, newAutoShare, log: log);
                log.LogInformation(
                    "api.support.availability available={Available} autoShareCrashes={AutoShare}",
                    newAvailable, newAutoShare);
                return Results.Ok(new { available = newAvailable, autoShareCrashes = newAutoShare });
            });

        // Single poll surface for the operator UI: opt-in posture + live pending
        // requests + how many maintainer sessions are currently viewing.
        app.MapGet("/api/support/status",
            (Zeus.Server.Hosting.Support.SupportAvailabilityStore store,
             Zeus.Server.Hosting.Support.SupportRequestCoordinator coord,
             Zeus.Server.Hosting.Support.SupportWebRtcService rtc) =>
                Results.Ok(new
                {
                    available = store.IsAvailable,
                    autoShareCrashes = store.AutoShareOnCrash,
                    pending = coord.Pending(),
                    activeSessions = rtc.ActiveSessions,
                }));

        // Capabilities snapshot — host-mode + platform metadata. Frontend
        // fetches once on app mount; future feature gates will reattach as
        // the new plugin system fills the FeatureMatrix. The HttpContext-
        // aware Snapshot overload lets desktop + ShareOverLan report
        // host="server" to LAN clients while loopback Photino keeps
        // host="desktop" — see CapabilitiesService.Snapshot(HttpContext).
        app.MapGet("/api/capabilities",
            (HttpContext ctx, CapabilitiesService caps) => Results.Ok(caps.Snapshot(ctx)));

        // FT8/FT4 native decode control. The FT8 workspace POSTs enable on
        // entering the mode and disable on leaving; decodes arrive out-of-band as
        // 0x38 Ft8Decode WS frames. nativeAvailable is false on a platform whose
        // zeus_ft8 binary wasn't shipped (the workspace then shows "unavailable").
        app.MapGet("/api/ft8",
            (Ft8Service ft8) => Results.Ok(new
            {
                nativeAvailable = ft8.NativeAvailable,
                enabled = ft8.IsEnabled,
                receiver = ft8.ActiveReceiver,
                protocol = ft8.ActiveProtocol == Zeus.Dsp.Ft8.Ft8Protocol.Ft4 ? "FT4" : "FT8",
                passes = ft8.DecodePasses,
            }));

        app.MapPost("/api/ft8/enable",
            (Zeus.Contracts.Ft8EnableRequest body, Ft8Service ft8) =>
            {
                var proto = string.Equals(body.Protocol, "FT4", StringComparison.OrdinalIgnoreCase)
                    ? Zeus.Dsp.Ft8.Ft8Protocol.Ft4 : Zeus.Dsp.Ft8.Ft8Protocol.Ft8;
                if (body.Passes is int p) ft8.DecodePasses = Math.Clamp(p, 1, 4);
                bool ok = ft8.Enable(body.Receiver ?? 0, proto);
                log.LogInformation("api.ft8.enable rx={Rx} proto={Proto} ok={Ok}",
                    body.Receiver ?? 0, proto, ok);
                return ok
                    ? Results.Ok(new { enabled = true, nativeAvailable = true })
                    : Results.Ok(new { enabled = false, nativeAvailable = ft8.NativeAvailable });
            });

        // FT8/FT4 + WSPR ARMED auto-sequence TX keyer control surface (arm /
        // stage / halt / status). Kept in its own extension file so this one
        // stays manageable.
        app.MapFt8TxEndpoints();

        app.MapPost("/api/ft8/disable", (Ft8Service ft8) =>
        {
            ft8.Disable();
            log.LogInformation("api.ft8.disable");
            return Results.Ok(new { enabled = false });
        });

        // Shared operator identity (callsign + Maidenhead grid). This is the SAME
        // identity the spotting uploaders and FreeDV Reporter resolve from — set
        // it once here and FT8/FT4 TX ungates everywhere. Server-persisted so the
        // desktop webview no longer loses it on restart. GET returns both the saved
        // override and the effective resolved value (override → QRZ home fallback),
        // so the Settings page can grey QRZ-sourced values.
        app.MapGet("/api/operator",
            (OperatorIdentityStore store, QrzService qrz) =>
                Results.Ok(OperatorIdentityResolver.Status(store, qrz)));

        app.MapPost("/api/operator",
            (Zeus.Contracts.OperatorIdentity body, OperatorIdentityStore store, QrzService qrz) =>
            {
                store.Set(body);
                log.LogInformation("api.operator.set call={Call} grid={Grid}",
                    body.Callsign, body.Grid);
                return Results.Ok(OperatorIdentityResolver.Status(store, qrz));
            });

        // FT8/FT4/WSPR workspace behaviour + display preferences (auto-seq, decode
        // depth, macros, logging, waterfall/display). Persisted server-side PER
        // MODE so each digital mode remembers its own config across desktop
        // restarts. The `mode` query param selects FT8/FT4/WSPR; omitted → FT8
        // (back-compat with the pre-per-mode mode-less caller). Pure behaviour/UI —
        // none of these transmit; TX still requires an explicit arm.
        app.MapGet("/api/ft8/settings",
            (string? mode, Ft8SettingsStore store) =>
                Results.Ok(store.Get(Ft8SettingsStore.NormalizeMode(mode))));

        app.MapPost("/api/ft8/settings",
            (Zeus.Contracts.Ft8Settings body, string? mode, Ft8SettingsStore store) =>
            {
                var m = Ft8SettingsStore.NormalizeMode(mode);
                var saved = store.Set(m, body);
                log.LogInformation(
                    "api.ft8.settings mode={Mode} autoseq={Auto} passes={Passes} autolog={Log}",
                    m, saved.AutoSequence, saved.DecodePasses, saved.AutoLog);
                return Results.Ok(saved);
            });

        // WSPR native spotting control. nativeAvailable is false where the
        // zeus_wspr decoder isn't shipped (e.g. Windows encode-only build today).
        app.MapGet("/api/wspr",
            (WsprService wspr) => Results.Ok(new
            {
                nativeAvailable = wspr.NativeAvailable,
                enabled = wspr.IsEnabled,
                receiver = wspr.ActiveReceiver,
                dialFreqMhz = wspr.DialFreqMhz,
            }));

        app.MapPost("/api/wspr/enable",
            (Zeus.Contracts.WsprEnableRequest body, WsprService wspr) =>
            {
                double dial = body.DialFreqMhz ?? 14.0956; // 20 m default
                bool ok = wspr.Enable(body.Receiver ?? 0, dial);
                log.LogInformation("api.wspr.enable rx={Rx} dial={Dial:F4} ok={Ok}",
                    body.Receiver ?? 0, dial, ok);
                return ok
                    ? Results.Ok(new { enabled = true, nativeAvailable = true })
                    : Results.Ok(new { enabled = false, nativeAvailable = wspr.NativeAvailable });
            });

        app.MapPost("/api/wspr/disable", (WsprService wspr) =>
        {
            wspr.Disable();
            log.LogInformation("api.wspr.disable");
            return Results.Ok(new { enabled = false });
        });

        // Activation spots — merged POTA + SOTA feed, polled server-side by
        // ActivationSpotsService. The Spots panel polls this and offers
        // click-to-tune. Returns whatever's currently cached (empty list until
        // the first upstream poll completes).
        app.MapGet("/api/spots/activations",
            (ActivationSpotsService spots) => Results.Ok(spots.GetCurrentSpots()));

        // Spots feature settings — feed toggles, poll interval, and click-to-tune
        // behaviour. Persisted in zeus-prefs.db; the poller is nudged to refresh
        // immediately on change via ActivationSpotsService.Wake().
        app.MapGet("/api/spots/settings",
            (SpotsSettingsStore store) => Results.Ok(store.Get()));

        app.MapPost("/api/spots/settings",
            (Zeus.Contracts.SpotsSettings req, SpotsSettingsStore store, ActivationSpotsService spots) =>
            {
                var saved = store.Set(req);
                spots.Wake();
                log.LogInformation(
                    "api.spots.settings enabled={Enabled} pota={Pota} sota={Sota} poll={Poll}s",
                    saved.Enabled, saved.PotaEnabled, saved.SotaEnabled, saved.PollIntervalSeconds);
                return Results.Ok(saved);
            });

        // Self-update status. The latest PRODUCTION build is read from the Zeus
        // download domain (downloads.openhpsdrzeus.com), which is published from
        // `main` only — never develop/nightly. The GET response carries the
        // platform-matched installer/DMG/AppImage/tarball URL + its SHA-256; the
        // app opens that download. Zeus never pulls/rebuilds/restarts itself.
        app.MapGet("/api/system/update",
            async (RepoUpdateService updates, bool? fetch, CancellationToken ct) =>
                Results.Ok(await updates.GetStatusAsync(fetch ?? true, ct)));

        // Native RX audio (miniaudio) — desktop-mode mute control. The
        // Mute/Unmute button in the Photino window POSTs here to silence
        // the OS playback device. NativeAudioSink is only registered in
        // desktop mode, so GetService returns null in server mode and the
        // endpoint reports supported=false; the SPA's AudioToggle uses
        // its in-browser AudioContext path there instead.
        app.MapGet("/api/audio/native", (IServiceProvider sp) =>
        {
            var sink = sp.GetService<NativeAudioSink>();
            return sink is null
                ? Results.Ok(new { supported = false, muted = false })
                : Results.Ok(new { supported = true, muted = sink.IsMuted, diagnostics = sink.GetDiagnostics() });
        });
        app.MapPost("/api/audio/native/mute", (NativeMuteRequest body, IServiceProvider sp) =>
        {
            var sink = sp.GetService<NativeAudioSink>();
            if (sink is null) return Results.NotFound(new { error = "native audio not active in this host mode" });
            sink.SetMuted(body.Muted);
            return Results.Ok(new { supported = true, muted = sink.IsMuted });
        });
        app.MapGet("/api/audio/devices", GetNativeAudioDevices);
        app.MapPut("/api/audio/devices", SetNativeAudioDevices);

        static IResult GetAudioSuitePreview(RadioService radio)
        {
            return Results.Ok(new { supported = true, enabled = radio.Snapshot().TxMonitorEnabled });
        }

        static IResult SetAudioSuitePreview(PreviewSetRequest body, RadioService radio, DspPipelineService pipe)
        {
            // Meter-only is requested by Auto Tune so it can run the chain for
            // metering without the operator hearing the demodulated monitor.
            // Apply it before flipping the monitor on so the first monitor tick
            // already honours suppression; clearing on disable is handled by the
            // monitor latch in DspPipelineService.
            bool meterOnly = body.Enabled && (body.MeterOnly ?? false);
            pipe.SetTxMonitorMeterOnly(meterOnly);
            var state = radio.SetTxMonitor(new TxMonitorSetRequest(body.Enabled));
            return Results.Ok(new { supported = true, enabled = state.TxMonitorEnabled, meterOnly });
        }

        // Audio Suite Preview toggle — operator-facing alias for TX Monitor.
        // It drives the WDSP TX-monitor path, which demodulates post-TXA IQ
        // back to mono audio after the full transmit chain (Audio Suite/VST
        // route, leveler, compressor, CFC, ALC, bandpass, CFIR). This keeps
        // Audio Suite "Preview ON" a 1:1 off-air comparison with what would
        // reach the radio, rather than the older plugin-chain-only preview.
        app.MapGet("/api/audio-suite/preview", GetAudioSuitePreview);
        app.MapPut("/api/audio-suite/preview", SetAudioSuitePreview);
        app.MapGet("/api/tx-audio-suite/preview", GetAudioSuitePreview);
        app.MapPut("/api/tx-audio-suite/preview", SetAudioSuitePreview);

        // Audio Suite master bypass — single operator-facing toggle that
        // disengages the entire plugin chain in one click. Default on first
        // install is true (chain inert) so a fresh operator isn't surprised
        // by an unfamiliar processing chain transforming their first TX.
        // The state persists across server restarts via
        // AudioChainSettingsStore. CFC is downstream in WDSP and unaffected;
        // per-plugin bypass states are likewise untouched.
        app.MapGet("/api/audio-suite/master-bypass", (AudioChainMasterBypassService svc) =>
        {
            return Results.Ok(new { bypassed = svc.IsBypassed });
        });
        app.MapPut("/api/audio-suite/master-bypass", (MasterBypassSetRequest body, AudioChainMasterBypassService svc) =>
        {
            svc.SetMasterBypassed(body.Bypassed);
            return Results.Ok(new { bypassed = svc.IsBypassed });
        });
        app.MapGet("/api/tx-audio-suite/master-bypass", (AudioChainMasterBypassService svc) =>
        {
            return Results.Ok(new { bypassed = svc.IsBypassed });
        });
        app.MapPut("/api/tx-audio-suite/master-bypass", (MasterBypassSetRequest body, AudioChainMasterBypassService svc) =>
        {
            svc.SetMasterBypassed(body.Bypassed);
            return Results.Ok(new { bypassed = svc.IsBypassed });
        });

        // Audio Suite processing mode — "native" (Brian's in-process plugin
        // chain, the default) vs "vst" (the out-of-process VST engine). The two
        // routes are mutually exclusive. Default on first run is native, so the
        // TX path is byte-identical to a build with no VST mode until the
        // operator opts in. GET reports the current mode plus whether a VST
        // engine is installed (for the "Get VSTHost" affordance) and whether the
        // engine is currently live. PUT switches mode (launching / stopping the
        // engine) and persists the choice via AudioProcessingModeStore.
        app.MapGet("/api/audio-suite/processing-mode", (AudioProcessingModeService svc) =>
        {
            return Results.Ok(new
            {
                mode = svc.Mode.ToString().ToLowerInvariant(),
                engineActive = svc.EngineActive,
                engineAvailable = AudioProcessingModeService.FindEngineExe() is not null,
            });
        });
        app.MapPut("/api/audio-suite/processing-mode", async (ProcessingModeSetRequest body, AudioProcessingModeService svc) =>
        {
            if (body?.Mode is null || !Enum.TryParse<AudioProcessingMode>(body.Mode, ignoreCase: true, out var mode))
                return Results.BadRequest(new { error = "mode must be 'native' or 'vst'" });
            var applied = await svc.SetModeAsync(mode);
            return Results.Ok(new
            {
                mode = applied.ToString().ToLowerInvariant(),
                engineActive = svc.EngineActive,
                engineAvailable = AudioProcessingModeService.FindEngineExe() is not null,
            });
        });
        // Processing-mode + engine health. The health fields let the frontend
        // distinguish "engine still starting" (transient — wait) from "engine
        // keeps crashing" (Faulted — offer Repair) instead of a vague hint.
        static object TxModeDto(AudioProcessingModeService svc, AudioProcessingMode mode) => new
        {
            mode = mode.ToString().ToLowerInvariant(),
            engineActive = svc.EngineActive,
            engineAvailable = AudioProcessingModeService.FindEngineExe() is not null,
            engineState = svc.EngineState.ToString().ToLowerInvariant(),
            engineCrashLooping = svc.EngineCrashLooping,
            engineRestartCount = svc.EngineRestartCount,
            engineLastFault = svc.EngineLastFault,
        };
        app.MapGet("/api/tx-audio-suite/processing-mode", (AudioProcessingModeService svc) =>
            Results.Ok(TxModeDto(svc, svc.Mode)));
        app.MapPut("/api/tx-audio-suite/processing-mode", async (ProcessingModeSetRequest body, AudioProcessingModeService svc) =>
        {
            if (body?.Mode is null || !Enum.TryParse<AudioProcessingMode>(body.Mode, ignoreCase: true, out var mode))
                return Results.BadRequest(new { error = "mode must be 'native' or 'vst'" });
            var applied = await svc.SetModeAsync(mode);
            return Results.Ok(TxModeDto(svc, applied));
        });

        // VST engine provisioning — the in-app "Get VST Engine" flow. The engine
        // is the upstream KlayaR/VSTHost headless binary, fetched from its latest
        // release and staged at the Zeus-managed path; it is never bundled in the
        // Zeus installer (GPLv3 isolation — see VstEngineInstaller). GET reports
        // install progress + whether the engine is already present; POST starts a
        // background download (idempotent — no-op if already installed/running).
        // The frontend polls GET until phase is "done" / "failed", then re-reads
        // the processing-mode endpoint so the new engine is picked up.
        static object EngineInstallDto(VstEngineInstaller installer)
        {
            var s = installer.Current;
            return new
            {
                phase = s.Phase.ToString().ToLowerInvariant(),
                percent = s.Percent,
                message = s.Message,
                version = s.Version,
                engineAvailable = installer.EngineInstalled,
                // Platform affordance flags for the Audio Tools GUI. The
                // out-of-process JUCE/VSTHost engine is Windows-only by design
                // (VstEngineInstaller throws "Windows-only" elsewhere), so the
                // "Download VST Engine" path is only meaningful on Windows. The
                // native in-process bridges (zeus-vst-bridge for VST3, plus the
                // macOS-only zeus-au-bridge for Audio Units) are how macOS/Linux
                // host plugins — no engine download required. These additive
                // flags let the frontend pick the right affordance per OS.
                engineSupported = OperatingSystem.IsWindows(),
                inProcessHostSupported = true,
                auSupported = OperatingSystem.IsMacOS(),
            };
        }
        app.MapGet("/api/audio-suite/vst-engine/install", (VstEngineInstaller installer) =>
            Results.Ok(EngineInstallDto(installer)));
        app.MapGet("/api/tx-audio-suite/vst-engine/install", (VstEngineInstaller installer) =>
            Results.Ok(EngineInstallDto(installer)));
        app.MapPost("/api/audio-suite/vst-engine/install", (VstEngineInstaller installer) =>
        {
            installer.Start();
            return Results.Ok(EngineInstallDto(installer));
        });
        app.MapPost("/api/tx-audio-suite/vst-engine/install", (VstEngineInstaller installer) =>
        {
            installer.Start();
            return Results.Ok(EngineInstallDto(installer));
        });
        // Repair/reinstall — force a re-download of the manifest's verified engine
        // even when one is already present, replacing a stale/corrupt/crash-looping
        // binary. Backs the "Repair engine" affordance the UI shows when the engine
        // is Faulted; also runs automatically on crash-loop (AudioProcessingModeService).
        app.MapPost("/api/audio-suite/vst-engine/repair", (VstEngineInstaller installer) =>
        {
            installer.Start(force: true);
            return Results.Ok(EngineInstallDto(installer));
        });
        app.MapPost("/api/tx-audio-suite/vst-engine/repair", (VstEngineInstaller installer) =>
        {
            installer.Start(force: true);
            return Results.Ok(EngineInstallDto(installer));
        });

        // Audio plugin chain order — operator's preferred sequence for
        // the plugins in the Audio Suite window. GET returns the
        // canonical ordered list of plugin IDs; PUT accepts a new
        // ordering and validates it's a permutation of the current
        // set (no IDs added, no IDs dropped — install / uninstall
        // plugins to change membership). On PUT, the bridge re-slots
        // the runtime chain via ChainOrderService.OrderChanged and
        // broadcasts AudioChainOrderFrame (0x1E) so other connected
        // clients update their tile strip without polling.
        app.MapGet("/api/plugins/chain/order", (ChainOrderService chainOrder) =>
        {
            return Results.Ok(new { pluginIds = chainOrder.CurrentOrder });
        });
        app.MapPut("/api/plugins/chain/order", (ChainOrderSetRequest body, ChainOrderService chainOrder) =>
        {
            if (body?.PluginIds is null)
                return Results.BadRequest(new { error = "pluginIds is required" });
            if (chainOrder.TrySetOrder(body.PluginIds, out var err))
                return Results.Ok(new { pluginIds = chainOrder.CurrentOrder });
            return Results.BadRequest(new { error = err });
        });
        app.MapGet("/api/tx-audio-suite/chain/order", (ChainOrderService chainOrder) =>
        {
            return Results.Ok(new { pluginIds = chainOrder.CurrentOrder });
        });
        app.MapPut("/api/tx-audio-suite/chain/order", (ChainOrderSetRequest body, ChainOrderService chainOrder) =>
        {
            if (body?.PluginIds is null)
                return Results.BadRequest(new { error = "pluginIds is required" });
            if (chainOrder.TrySetOrder(body.PluginIds, out var err))
                return Results.Ok(new { pluginIds = chainOrder.CurrentOrder });
            return Results.BadRequest(new { error = err });
        });

        // Audio plugin chain membership — "park" / "un-park". An
        // installed audio plugin is by default IN the active chain;
        // the operator can pull it OUT (active=false) so it stops
        // processing audio and drops from the rack, WITHOUT
        // uninstalling — it stays in the sidebar's available list and
        // can be slotted back in (active=true) anytime. Parking removes
        // the ID from the runtime CurrentOrder; un-parking restores it
        // at its canonical position. The bridge re-slots via
        // ChainOrderService.OrderChanged and an AudioChainOrderFrame
        // (0x1E) broadcast refreshes other clients. Returns the new
        // active order.
        app.MapPut("/api/plugins/{id}/chain-membership",
            (string id, ChainMembershipSetRequest body, ChainOrderService chainOrder) =>
        {
            if (body is null)
                return Results.BadRequest(new { error = "active is required" });
            if (chainOrder.TrySetParked(id, parked: !body.Active, out var err))
                return Results.Ok(new { pluginIds = chainOrder.CurrentOrder });
            return Results.BadRequest(new { error = err });
        });
        app.MapPut("/api/tx-audio-suite/plugins/{id}/chain-membership",
            (string id, ChainMembershipSetRequest body, ChainOrderService chainOrder) =>
        {
            if (body is null)
                return Results.BadRequest(new { error = "active is required" });
            if (chainOrder.TrySetParked(id, parked: !body.Active, out var err))
                return Results.Ok(new { pluginIds = chainOrder.CurrentOrder });
            return Results.BadRequest(new { error = err });
        });

        // RX Audio Suite order/membership. Same control contract as the TX
        // Audio Suite, but targets receive-side inserts declared as
        // audio.slot = rx.post-demod.
        app.MapGet("/api/rx-audio-suite/chain/order", (RxChainOrderService rxChainOrder) =>
        {
            return Results.Ok(new { pluginIds = rxChainOrder.CurrentOrder });
        });
        app.MapPut("/api/rx-audio-suite/chain/order", (ChainOrderSetRequest body, RxChainOrderService rxChainOrder) =>
        {
            if (body?.PluginIds is null)
                return Results.BadRequest(new { error = "pluginIds is required" });
            if (rxChainOrder.TrySetOrder(body.PluginIds, out var err))
                return Results.Ok(new { pluginIds = rxChainOrder.CurrentOrder });
            return Results.BadRequest(new { error = err });
        });
        app.MapPut("/api/rx-audio-suite/plugins/{id}/chain-membership",
            (string id, ChainMembershipSetRequest body, RxChainOrderService rxChainOrder) =>
        {
            if (body is null)
                return Results.BadRequest(new { error = "active is required" });
            if (rxChainOrder.TrySetParked(id, parked: !body.Active, out var err))
                return Results.Ok(new { pluginIds = rxChainOrder.CurrentOrder });
            return Results.BadRequest(new { error = err });
        });

        app.MapGet("/api/rx-audio-suite/master-bypass", (AudioChainMasterBypassService svc) =>
        {
            return Results.Ok(new { bypassed = svc.IsRxBypassed });
        });
        app.MapPut("/api/rx-audio-suite/master-bypass", (MasterBypassSetRequest body, AudioChainMasterBypassService svc) =>
        {
            svc.SetRxMasterBypassed(body.Bypassed);
            return Results.Ok(new { bypassed = svc.IsRxBypassed });
        });

        app.MapGet("/api/rx-audio-suite/processing-mode", (RxVstEngineService rxVst) =>
        {
            return Results.Ok(new
            {
                mode = "vst",
                engineActive = rxVst.EngineActive,
                engineAvailable = rxVst.EngineAvailable,
                activePlugins = rxVst.ActivePluginCount,
                degradedBlocks = rxVst.DegradedBlocks,
            });
        });

        // NOTE: the legacy TX audio-suite profile endpoints
        // (/api/audio-suite/profiles* and /api/tx-audio-suite/profiles*) and the
        // TX station-profile endpoints (/api/tx/station-profiles*) are RETIRED —
        // their capability is folded into the unified TX Audio Profile endpoints
        // below. AudioProfileService is retained internally only as VST-capture
        // machinery, reused by TxAudioProfileService.

        // ---- Unified TX Audio Profiles ----------------------------------
        // The single operator-named macro that captures the ENTIRE TX-audio
        // shaping state. REPLACES /api/tx-audio-suite/profiles* and
        // /api/tx/station-profiles*. The RX route (/api/rx-audio-suite/...) is
        // a separate, untouched system.
        app.MapGet("/api/tx-audio-profiles", (TxAudioProfileService svc) =>
            Results.Ok(new TxAudioProfilesResponse(svc.List())));

        // POST save-current-as-name. The body carries only the name; the backend
        // snapshots the live state (single source of truth).
        app.MapPost("/api/tx-audio-profiles", async (
            SaveTxAudioProfileRequest req, TxAudioProfileService svc, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "profile name is required" });
            var saved = await svc.SaveCurrentAsync(req.Name, ct);
            return Results.Ok(saved);
        });

        // POST apply by id — drives the live state and records last-loaded.
        // Returns the applied profile plus the resulting StateDto so the frontend
        // snaps the live UI to it without a second round-trip.
        app.MapPost("/api/tx-audio-profiles/{id}/apply", async (
            string id, TxAudioProfileService svc, RadioService radio,
            ChainOrderService chainOrder, AudioProcessingModeService mode,
            AudioChainMasterBypassService masterBypass, CancellationToken ct) =>
        {
            var applied = await svc.ApplyAsync(id, ct);
            if (applied is null)
                return Results.NotFound(new { error = $"no TX audio profile '{id}'" });
            return Results.Ok(new
            {
                profile = applied,
                state = radio.Snapshot(),
                pluginIds = chainOrder.CurrentOrder,
                parked = chainOrder.ParkedIds,
                processingMode = mode.Mode.ToString().ToLowerInvariant(),
                engineActive = mode.EngineActive,
                engineAvailable = AudioProcessingModeService.FindEngineExe() is not null,
                masterBypass = masterBypass.IsBypassed,
            });
        });

        app.MapDelete("/api/tx-audio-profiles/{id}", (string id, TxAudioProfileService svc) =>
            svc.Delete(id)
                ? Results.Ok(new { deleted = TxAudioProfileStore.NormalizeId(id) })
                : Results.NotFound(new { error = $"no TX audio profile '{id}'" }));

        // GET/PUT the persisted "last loaded profile" pointer. The dropdown reads
        // this to show the selected profile; the backend applies it at startup.
        app.MapGet("/api/tx-audio-profiles/last-loaded", (TxAudioProfileService svc) =>
            Results.Ok(new LastLoadedTxAudioProfileDto(svc.LastLoadedId)));

        app.MapPut("/api/tx-audio-profiles/last-loaded", (
            LastLoadedTxAudioProfileDto req, TxAudioProfileService svc) =>
        {
            svc.SetLastLoadedId(req?.Id);
            return Results.Ok(new LastLoadedTxAudioProfileDto(svc.LastLoadedId));
        });

        // Import a TX audio profile from an uploaded .json file (multipart). The
        // webview can't hand the server a filesystem path, so the chosen file's
        // bytes are uploaded here, parsed, and ADDED to the collection (never
        // applied). The import is non-destructive — a slug collision bumps the
        // name rather than overwriting. Every profile is also mirrored to the
        // on-disk tx-audio-profiles folder. Antiforgery disabled — this is a
        // loopback / LAN-token API, not a cookie-auth form post.
        app.MapPost("/api/tx-audio-profiles/import", async (HttpRequest req, TxAudioProfileService svc) =>
        {
            try
            {
                if (!req.HasFormContentType)
                    return Results.BadRequest(new { error = "Expected a multipart file upload." });
                var form = await req.ReadFormAsync();
                var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
                if (file is null || file.Length == 0)
                    return Results.BadRequest(new { error = "No file uploaded." });

                var nameField = form.TryGetValue("name", out var n) ? n.ToString() : null;
                var fallback = string.IsNullOrWhiteSpace(nameField)
                    ? Path.GetFileNameWithoutExtension(file.FileName)
                    : nameField;

                using var reader = new StreamReader(file.OpenReadStream());
                var json = await reader.ReadToEndAsync();
                var imported = svc.ImportProfile(json, fallback);
                return Results.Ok(imported);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).DisableAntiforgery();

        // Download a saved TX audio profile as a .json file so the operator can
        // back it up or share it (the same bytes are mirrored in the
        // tx-audio-profiles folder on disk).
        app.MapGet("/api/tx-audio-profiles/{id}/export", (string id, TxAudioProfileService svc) =>
        {
            var export = svc.ExportProfile(id);
            return export is null
                ? Results.NotFound(new { error = $"no TX audio profile '{id}'" })
                : Results.File(export.Value.Bytes, "application/json", export.Value.FileName);
        });

        app.MapGet("/api/rx-audio-suite/profiles", (RxAudioProfileService profiles) =>
        {
            var list = profiles.List().Select(p => new
            {
                name = p.Name,
                processingMode = "vst",
                order = p.Order,
                parked = p.Parked,
                masterBypass = p.MasterBypass,
                createdUtc = p.CreatedUtc,
                updatedUtc = p.UpdatedUtc,
            });
            return Results.Ok(new
            {
                profiles = list,
                selectedProfile = profiles.SelectedProfileName,
            });
        });
        app.MapPut("/api/rx-audio-suite/profiles/{name}", async (string name, RxAudioProfileService profiles) =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "profile name is required" });
            var entry = await profiles.SaveCurrentAsync(name.Trim());
            return Results.Ok(new
            {
                name = entry.Name,
                processingMode = "vst",
                order = entry.Order,
                parked = entry.Parked,
                masterBypass = entry.MasterBypass,
                vstStates = entry.PluginStates.Count,
                selectedProfile = profiles.SelectedProfileName,
                createdUtc = entry.CreatedUtc,
                updatedUtc = entry.UpdatedUtc,
            });
        });
        app.MapPost("/api/rx-audio-suite/profiles/{name}/apply", async (
            string name,
            RxAudioProfileService profiles,
            RxChainOrderService rxChainOrder,
            RxVstEngineService rxVst,
            AudioChainMasterBypassService masterBypass,
            CancellationToken ct) =>
        {
            var profile = await profiles.ApplyAsync(name, ct);
            if (profile is not null)
                return Results.Ok(new
                {
                    pluginIds = rxChainOrder.CurrentOrder,
                    processingMode = "vst",
                    engineActive = rxVst.EngineActive,
                    engineAvailable = rxVst.EngineAvailable,
                    activePlugins = rxVst.ActivePluginCount,
                    degradedBlocks = rxVst.DegradedBlocks,
                    selectedProfile = profiles.SelectedProfileName,
                    masterBypass = masterBypass.IsRxBypassed,
                });
            return Results.NotFound(new { error = $"no RX audio profile named '{name}'" });
        });
        app.MapDelete("/api/rx-audio-suite/profiles/{name}", (string name, RxAudioProfileService profiles) =>
        {
            return profiles.Delete(name)
                ? Results.Ok(new { deleted = name })
                : Results.NotFound(new { error = $"no RX audio profile named '{name}'" });
        });

        // Scan a directory for VST3 plugins and register each as an
        // installed Zeus plugin so it flows into the Audio Suite chain.
        // Each .vst3 becomes a generated plugin package (stub assembly +
        // synthesized manifest); already-registered VSTs are skipped.
        // Returns what was registered / skipped / failed.
        app.MapPost("/api/audio-suite/scan-vst-directory",
            async (ScanVstDirectoryRequest body, VstDirectoryScanService scanner,
                   ChainOrderService chainOrder, RxChainOrderService rxChainOrder, CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Directory))
                return Results.BadRequest(new { error = "directory is required" });
            try
            {
                var result = await scanner.ScanAsync(body.Directory, body.Route, ct);
                // Scanned VSTs always land in Available, never the active chain: a
                // scan must not change what's processing audio. Without this, a
                // freshly-registered id that was previously active would rejoin the
                // live chain on its own.
                chainOrder.ParkAll(result.Registered
                    .Where(r => VstDirectoryScanService.IsTxPluginId(r.Id))
                    .Select(r => r.Id)
                    .ToList());
                rxChainOrder.ParkAll(result.Registered
                    .Where(r => VstDirectoryScanService.IsRxPluginId(r.Id))
                    .Select(r => r.Id)
                    .ToList());
                return Results.Ok(new
                {
                    directory = result.Directory,
                    registered = result.Registered.Select(r => new { id = r.Id, name = r.Name }),
                    skipped = result.Skipped.Select(r => new { id = r.Id, name = r.Name }),
                    errors = result.Errors.Select(e => new { source = e.Vst3Source, message = e.Message }),
                });
            }
            catch (DirectoryNotFoundException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapPost("/api/tx-audio-suite/scan-vst-directory",
            async (ScanVstDirectoryRequest body, VstDirectoryScanService scanner,
                   ChainOrderService chainOrder, RxChainOrderService rxChainOrder, CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Directory))
                return Results.BadRequest(new { error = "directory is required" });
            try
            {
                var route = string.IsNullOrWhiteSpace(body.Route) || string.Equals(body.Route, "auto", StringComparison.OrdinalIgnoreCase)
                    ? "tx"
                    : body.Route;
                var result = await scanner.ScanAsync(body.Directory, route, ct);
                chainOrder.ParkAll(result.Registered
                    .Where(r => VstDirectoryScanService.IsTxPluginId(r.Id))
                    .Select(r => r.Id)
                    .ToList());
                rxChainOrder.ParkAll(result.Registered
                    .Where(r => VstDirectoryScanService.IsRxPluginId(r.Id))
                    .Select(r => r.Id)
                    .ToList());
                return Results.Ok(new
                {
                    directory = result.Directory,
                    registered = result.Registered.Select(r => new { id = r.Id, name = r.Name }),
                    skipped = result.Skipped.Select(r => new { id = r.Id, name = r.Name }),
                    errors = result.Errors.Select(e => new { source = e.Vst3Source, message = e.Message }),
                });
            }
            catch (DirectoryNotFoundException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapPost("/api/rx-audio-suite/scan-vst-directory",
            async (ScanVstDirectoryRequest body, VstDirectoryScanService scanner,
                   ChainOrderService chainOrder, RxChainOrderService rxChainOrder, CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Directory))
                return Results.BadRequest(new { error = "directory is required" });
            try
            {
                var route = string.IsNullOrWhiteSpace(body.Route) || string.Equals(body.Route, "auto", StringComparison.OrdinalIgnoreCase)
                    ? "rx"
                    : body.Route;
                var result = await scanner.ScanAsync(body.Directory, route, ct);
                chainOrder.ParkAll(result.Registered
                    .Where(r => VstDirectoryScanService.IsTxPluginId(r.Id))
                    .Select(r => r.Id)
                    .ToList());
                rxChainOrder.ParkAll(result.Registered
                    .Where(r => VstDirectoryScanService.IsRxPluginId(r.Id))
                    .Select(r => r.Id)
                    .ToList());
                return Results.Ok(new
                {
                    directory = result.Directory,
                    registered = result.Registered.Select(r => new { id = r.Id, name = r.Name }),
                    skipped = result.Skipped.Select(r => new { id = r.Id, name = r.Name }),
                    errors = result.Errors.Select(e => new { source = e.Vst3Source, message = e.Message }),
                });
            }
            catch (DirectoryNotFoundException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Scan installed Audio Units (macOS) and register each as an installed
        // Zeus plugin so it flows into the Audio Suite chain in-process — the
        // AU sibling of scan-vst-directory. Unlike VST3, AUs are resolved from
        // the OS AudioComponent registry, so there is no directory argument.
        // The whole flow is a no-op off macOS: AuComponentScanService.ScanAsync
        // returns an empty result, so these endpoints stay 200/empty on
        // Windows/Linux (nothing AU-shaped ever runs there). As with VST3
        // scans, registered AUs are parked into Available — a scan must never
        // change what is processing audio.
        static async Task<IResult> ScanAuAsync(
            string defaultRoute,
            ScanAuRequest? body,
            AuComponentScanService scanner,
            ChainOrderService chainOrder,
            RxChainOrderService rxChainOrder,
            CancellationToken ct)
        {
            var requested = body?.Route;
            var route = string.IsNullOrWhiteSpace(requested)
                || string.Equals(requested, "auto", StringComparison.OrdinalIgnoreCase)
                ? defaultRoute
                : requested;
            try
            {
                var result = await scanner.ScanAsync(route, ct);
                chainOrder.ParkAll(result.Registered
                    .Where(r => AuComponentScanService.IsTxPluginId(r.Id))
                    .Select(r => r.Id)
                    .ToList());
                rxChainOrder.ParkAll(result.Registered
                    .Where(r => AuComponentScanService.IsRxPluginId(r.Id))
                    .Select(r => r.Id)
                    .ToList());
                return Results.Ok(new
                {
                    supported = OperatingSystem.IsMacOS(),
                    registered = result.Registered.Select(r => new { id = r.Id, name = r.Name }),
                    skipped = result.Skipped.Select(r => new { id = r.Id, name = r.Name }),
                    errors = result.Errors.Select(e => new { source = e.ComponentId, message = e.Message }),
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }
        app.MapPost("/api/audio-suite/scan-au",
            (ScanAuRequest? body, AuComponentScanService scanner,
             ChainOrderService chainOrder, RxChainOrderService rxChainOrder, CancellationToken ct) =>
                ScanAuAsync("tx", body, scanner, chainOrder, rxChainOrder, ct));
        app.MapPost("/api/tx-audio-suite/scan-au",
            (ScanAuRequest? body, AuComponentScanService scanner,
             ChainOrderService chainOrder, RxChainOrderService rxChainOrder, CancellationToken ct) =>
                ScanAuAsync("tx", body, scanner, chainOrder, rxChainOrder, ct));
        app.MapPost("/api/rx-audio-suite/scan-au",
            (ScanAuRequest? body, AuComponentScanService scanner,
             ChainOrderService chainOrder, RxChainOrderService rxChainOrder, CancellationToken ct) =>
                ScanAuAsync("rx", body, scanner, chainOrder, rxChainOrder, ct));

        // Audio Suite chain-level IN/OUT signal meters. Linear peak of
        // the block entering / leaving the TX insert chain, plus dBFS.
        // Reads 0 until the chain processes audio — which only happens
        // during MOX/TX or desktop-mode preview (mic preview). The
        // Audio Suite window polls this ~15 Hz while open, mirroring the
        // per-plugin /meters polling (no new WS frame / wire-format
        // change).
        static object ChainMetersDto((float In, float Out) meters)
        {
            static double ToDb(float lin) => lin <= 1e-6f ? -120.0 : 20.0 * Math.Log10(lin);
            var (inPk, outPk) = meters;
            return new
            {
                inputPeak = inPk,
                outputPeak = outPk,
                inputDb = ToDb(inPk),
                outputDb = ToDb(outPk),
            };
        }

        app.MapGet("/api/audio-suite/chain/meters", (AudioPluginBridge bridge) =>
        {
            return Results.Ok(ChainMetersDto(bridge.ChainMeters));
        });
        app.MapGet("/api/tx-audio-suite/chain/meters", (AudioPluginBridge bridge) =>
        {
            return Results.Ok(ChainMetersDto(bridge.ChainMeters));
        });
        app.MapGet("/api/rx-audio-suite/chain/meters", (AudioPluginBridge bridge) =>
        {
            return Results.Ok(ChainMetersDto(bridge.RxChainMeters));
        });

        // Audio Suite VST editor (plug-in GUI). Opens the plugin's REAL
        // native editor window on the host desktop via the in-process VST
        // bridge — the same way a standalone VST host shows a plugin
        // window (Windows-only at present). POST opens, DELETE closes,
        // GET reports whether the window is currently up.
        static IResult MapEditorResult(EditorActionResult r, bool open) => r switch
        {
            EditorActionResult.Ok        => Results.Ok(new { open }),
            EditorActionResult.NotFound  => Results.NotFound(new { error = "No such plugin in the Audio Suite chain." }),
            EditorActionResult.NotAVst   => Results.BadRequest(new { error = "This plugin has no native VST editor." }),
            EditorActionResult.NotLoaded => Results.Json(
                new { error = "This VST didn't load, so it has no editor to show. It may not be a valid VST3 audio-effect (check the server log), or native VST hosting is off (headless/server mode keeps it gated — run the desktop app, or set ZEUS_ENABLE_VST_LOAD=1)." },
                statusCode: 409),
            EditorActionResult.Failed    => Results.Json(
                new { error = "Failed to open the plugin editor (unsupported on this platform?)." },
                statusCode: 500),
            _                            => Results.StatusCode(500),
        };

        // When an editor open can't succeed in the current mode, a naive open
        // would fall through to the in-process bridge and surface its "set
        // ZEUS_ENABLE_VST_LOAD=1" hint — a developer-only escape hatch that sends
        // a new operator down the wrong (and unsafe) path. Point them at the
        // supported fix instead: the crash-isolated out-of-process engine
        // ("Download VST Engine" + VST processing mode). This covers two cases:
        // VST mode with no routing engine, and Native mode where the in-process
        // bridge won't host the plugin (TX native load is opt-in). Returns null
        // when the open should proceed — the engine is routing, or the bridge can
        // host this plugin (RX VSTs load in-process; TX only with the dev opt-in).
        static IResult? VstEngineEditorGuard(string id, AudioProcessingModeService mode)
        {
            // NB: the TX term is TxNativeEditorOptIn (legacy explicit opt-in),
            // NOT the TX load default. The load default flipped on (2026-06-26)
            // but the editor engine-redirect UX is deliberately decoupled so it
            // doesn't change on platforms where in-process TX editor hosting is
            // unverified. A genuinely-loaded TX plugin bypasses this guard via
            // HostsPlugin upstream.
            var nativeLoadEnabled = VstDirectoryScanService.IsRxPluginId(id)
                || Zeus.Plugins.Host.Audio.VstHostAudioPlugin.TxNativeEditorOptIn;
            var error = VstEditorHint.EngineUnavailableMessage(
                mode.Mode, mode.EngineActive,
                AudioProcessingModeService.FindEngineExe() is not null,
                nativeLoadEnabled);
            return error is null ? null : Results.Json(new { error }, statusCode: 409);
        }

        // Editor routing is mode-aware (host consolidation): when the
        // out-of-process engine is active (VST processing mode) the editor is
        // hosted crash-isolated in the engine process — the same instance that
        // is processing audio — so we drive it via open_editor/close_editor.
        // Otherwise we fall back to the in-process zeus-vst-bridge editor.
        app.MapGet("/api/audio-suite/plugins/{id}/editor",
            (string id, AudioPluginBridge bridge, AudioProcessingModeService mode, RxVstEngineService rxVst) =>
            {
                if (rxVst.HasEngineSlot(id))
                    return Results.Ok(new { open = rxVst.IsEditorOpen(id) });
                if (mode.EngineActive && mode.HasEngineSlot(id))
                    return Results.Ok(new { open = mode.IsEditorOpen(id) });
                return Results.Ok(new { open = bridge.IsEditorOpen(id) });
            });

        app.MapPost("/api/audio-suite/plugins/{id}/editor",
            (string id, AudioPluginBridge bridge, AudioProcessingModeService mode, RxVstEngineService rxVst) =>
            {
                // RX engine slots route to the RX engine regardless of the TX
                // processing mode; only the TX path is gated on the VST engine.
                if (rxVst.HasEngineSlot(id))
                    return MapEditorResult(rxVst.OpenEditor(id), open: true);
                // Skip the "install the VST engine" guard when the bridge hosts
                // this plugin in-process (a natively-loaded AU/VST3) — the editor
                // opens via bridge.OpenEditor below. Engine-routed plugins are not
                // natively loaded, so HostsPlugin is false and the guard still
                // fires for them (Windows path unchanged).
                if (!bridge.HostsPlugin(id) && VstEngineEditorGuard(id, mode) is { } guard)
                    return guard;
                var result = mode.EngineActive && mode.HasEngineSlot(id)
                    ? mode.OpenEditor(id)
                    : bridge.OpenEditor(id);
                return MapEditorResult(result, open: true);
            });

        app.MapDelete("/api/audio-suite/plugins/{id}/editor",
            (string id, AudioPluginBridge bridge, AudioProcessingModeService mode, RxVstEngineService rxVst) =>
            {
                var result = rxVst.HasEngineSlot(id)
                    ? rxVst.CloseEditor(id)
                    : mode.EngineActive && mode.HasEngineSlot(id)
                        ? mode.CloseEditor(id)
                        : bridge.CloseEditor(id);
                return MapEditorResult(result, open: false);
            });

        app.MapGet("/api/tx-audio-suite/plugins/{id}/editor",
            (string id, AudioPluginBridge bridge, AudioProcessingModeService mode) =>
            {
                if (mode.EngineActive && mode.HasEngineSlot(id))
                    return Results.Ok(new { open = mode.IsEditorOpen(id) });
                return Results.Ok(new { open = bridge.IsEditorOpen(id) });
            });

        app.MapPost("/api/tx-audio-suite/plugins/{id}/editor",
            (string id, AudioPluginBridge bridge, AudioProcessingModeService mode) =>
            {
                // As with the generic route: a bridge-hosted in-process plugin
                // (natively-loaded AU/VST3) opens via bridge.OpenEditor and must
                // not be blocked by the engine guard. Engine-routed TX VSTs are
                // not natively loaded, so HostsPlugin is false and the guard fires
                // exactly as today (Windows path unchanged).
                if (!bridge.HostsPlugin(id) && VstEngineEditorGuard(id, mode) is { } guard)
                    return guard;
                var result = mode.EngineActive && mode.HasEngineSlot(id)
                    ? mode.OpenEditor(id)
                    : bridge.OpenEditor(id);
                return MapEditorResult(result, open: true);
            });

        app.MapDelete("/api/tx-audio-suite/plugins/{id}/editor",
            (string id, AudioPluginBridge bridge, AudioProcessingModeService mode) =>
            {
                var result = mode.EngineActive && mode.HasEngineSlot(id)
                    ? mode.CloseEditor(id)
                    : bridge.CloseEditor(id);
                return MapEditorResult(result, open: false);
            });

        // Explicit RX Audio Suite editor route. The older /api/audio-suite
        // editor endpoint remains as the TX/back-compat path and still
        // auto-detects RX slots, but route-aware callers should use this
        // receive-side URL so separate TX/RX VST instances never depend on
        // ambiguous editor routing.
        // RX VSTs are hosted out-of-process only when the dedicated RX engine is
        // installed AND active; otherwise (Native mode — the default, and the
        // only option until the operator installs the VST engine) the in-process
        // AudioPluginBridge loads and renders the RX VST editor. So fall back to
        // the bridge exactly like the generic /api/audio-suite route does. Without
        // this fallback a fresh install 404s the editor for a perfectly-loaded
        // in-process RX VST and surfaces the wrong "engine missing" hint.
        app.MapGet("/api/rx-audio-suite/plugins/{id}/editor",
            (string id, RxVstEngineService rxVst, AudioPluginBridge bridge) =>
            {
                if (rxVst.HasEngineSlot(id))
                    return Results.Ok(new { open = rxVst.IsEditorOpen(id) });
                return Results.Ok(new { open = bridge.IsEditorOpen(id) });
            });

        app.MapPost("/api/rx-audio-suite/plugins/{id}/editor",
            (string id, RxVstEngineService rxVst, AudioPluginBridge bridge) =>
            {
                var result = rxVst.HasEngineSlot(id)
                    ? rxVst.OpenEditor(id)
                    : bridge.OpenEditor(id);
                return MapEditorResult(result, open: true);
            });

        app.MapDelete("/api/rx-audio-suite/plugins/{id}/editor",
            (string id, RxVstEngineService rxVst, AudioPluginBridge bridge) =>
            {
                var result = rxVst.HasEngineSlot(id)
                    ? rxVst.CloseEditor(id)
                    : bridge.CloseEditor(id);
                return MapEditorResult(result, open: false);
            });

        // WAV recorder / tape deck. Records RX or processed-TX audio to float32
        // WAVs under the managed root (<Downloads>/Zeus Recordings), organises
        // them into folders, and plays recordings back to the local monitor or
        // over the air. All paths on the wire are root-relative with forward
        // slashes; the service traversal-guards every one.
        app.MapGet("/api/wav/status", (Zeus.Server.Wav.WavRecorderService wav) =>
            Results.Ok(wav.GetStatus()));
        app.MapGet("/api/wav/list", (Zeus.Server.Wav.WavRecorderService wav) =>
            Results.Ok(new
            {
                root = wav.RecordingsDir,
                folders = wav.ListFolders(),
                recordings = wav.ListRecordings(),
            }));
        app.MapGet("/api/wav/waveform",
            (string? file, int? buckets, Zeus.Server.Wav.WavRecorderService wav) =>
        {
            if (string.IsNullOrWhiteSpace(file))
                return Results.BadRequest(new { error = "file is required" });
            try { return Results.Ok(new { file, buckets = wav.ComputeWaveform(file, buckets ?? 400) }); }
            catch (FileNotFoundException) { return Results.NotFound(new { error = "recording not found" }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapPost("/api/wav/record/start",
            (WavRecordStartRequest body, Zeus.Server.Wav.WavRecorderService wav) =>
        {
            var source = string.Equals(body?.Source, "tx", StringComparison.OrdinalIgnoreCase)
                ? Zeus.Server.Wav.WavRecordSource.Tx
                : Zeus.Server.Wav.WavRecordSource.Rx;
            try { return Results.Ok(new { relPath = wav.StartRecording(source, body?.Folder) }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost("/api/wav/record/stop", (Zeus.Server.Wav.WavRecorderService wav) =>
        {
            var r = wav.StopRecording();
            return r is { } x
                ? Results.Ok(new { relPath = x.RelPath, samples = x.Samples })
                : Results.Ok(new { relPath = (string?)null, samples = 0L });
        });
        app.MapPost("/api/wav/play",
            (WavPlayRequest body, Zeus.Server.Wav.WavRecorderService wav) =>
        {
            if (string.IsNullOrWhiteSpace(body?.File))
                return Results.BadRequest(new { error = "file is required" });
            var dest = string.Equals(body?.Dest, "air", StringComparison.OrdinalIgnoreCase)
                ? Zeus.Server.Wav.WavPlayDest.Air
                : Zeus.Server.Wav.WavPlayDest.Local;
            try { wav.Play(body!.File!, dest); return Results.Ok(wav.GetStatus()); }
            catch (FileNotFoundException) { return Results.NotFound(new { error = "recording not found" }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost("/api/wav/stop", (Zeus.Server.Wav.WavRecorderService wav) =>
        {
            wav.StopPlayback();
            return Results.Ok(wav.GetStatus());
        });
        app.MapPost("/api/wav/rename",
            (WavRenameRequest body, Zeus.Server.Wav.WavRecorderService wav) =>
        {
            if (string.IsNullOrWhiteSpace(body?.From) || string.IsNullOrWhiteSpace(body?.Name))
                return Results.BadRequest(new { error = "from and name are required" });
            try { return Results.Ok(new { relPath = wav.RenameRecording(body.From, body.Name) }); }
            catch (FileNotFoundException) { return Results.NotFound(new { error = "recording not found" }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost("/api/wav/move",
            (WavMoveRequest body, Zeus.Server.Wav.WavRecorderService wav) =>
        {
            if (string.IsNullOrWhiteSpace(body?.From))
                return Results.BadRequest(new { error = "from is required" });
            try { return Results.Ok(new { relPath = wav.MoveRecording(body.From, body.Folder ?? "") }); }
            catch (FileNotFoundException) { return Results.NotFound(new { error = "recording not found" }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost("/api/wav/folder/create",
            (WavFolderRequest body, Zeus.Server.Wav.WavRecorderService wav) =>
        {
            if (string.IsNullOrWhiteSpace(body?.Path))
                return Results.BadRequest(new { error = "path is required" });
            try { return Results.Ok(new { folder = wav.CreateFolder(body.Path) }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost("/api/wav/folder/delete",
            (WavFolderRequest body, Zeus.Server.Wav.WavRecorderService wav) =>
        {
            if (string.IsNullOrWhiteSpace(body?.Path))
                return Results.BadRequest(new { error = "path is required" });
            try { return Results.Ok(new { deleted = wav.DeleteFolder(body.Path) }); }
            catch (FileNotFoundException) { return Results.NotFound(new { error = "folder not found" }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            // Sharing violation (a file in the folder is open/in use) on Windows.
            catch (IOException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost("/api/wav/delete",
            (WavDeleteRequest body, Zeus.Server.Wav.WavRecorderService wav) =>
        {
            if (string.IsNullOrWhiteSpace(body?.File))
                return Results.BadRequest(new { error = "file is required" });
            try { wav.DeleteRecording(body.File); return Results.Ok(new { deleted = body.File }); }
            catch (FileNotFoundException) { return Results.NotFound(new { error = "recording not found" }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            // Active recording/playback target — refused up front.
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            // Sharing violation (file open/in use) on Windows.
            catch (IOException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        // The recordings root: where new captures land and the listing reads
        // from. Operator-selectable and persisted across restarts.
        app.MapGet("/api/wav/root", (Zeus.Server.Wav.WavRecorderService wav) =>
        {
            var (root, isDefault) = wav.GetRecordingsRoot();
            return Results.Ok(new { root, isDefault });
        });
        app.MapPost("/api/wav/root",
            (WavRootRequest body, Zeus.Server.Wav.WavRecorderService wav) =>
        {
            try
            {
                // Null/empty/missing path resets to the platform default.
                var (root, isDefault) = wav.SetRecordingsRoot(body?.Path);
                return Results.Ok(new { root, isDefault });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        // Read-only server-side directory browser so the web UI can pick a
        // recordings root on the machine the backend runs on. Deliberately not
        // confined to the recordings root.
        app.MapGet("/api/wav/dirs", (string? path) =>
        {
            try
            {
                var listing = Zeus.Server.Wav.WavLibrary.BrowseDirectories(path);
                return Results.Ok(new
                {
                    path = listing.Path,
                    parent = listing.Parent,
                    separator = listing.Separator,
                    dirs = listing.Dirs.Select(d => new { name = d.Name, path = d.Path }),
                });
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException or IOException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/state", (RadioService r) => r.Snapshot());

        // Compatibility route for the TX diagnostics snapshot. The read path is
        // owned by TxDiagnosticsProvider so legacy and v2 diagnostics cannot drift.
        app.MapGet("/api/tx/diag", (DiagnosticsProviderRegistry diagnostics) =>
            Results.Ok(DiagnosticsSnapshot(diagnostics, "tx")));

        app.MapGet("/api/radios", async (
            IRadioDiscovery p1Discovery,
            Zeus.Protocol2.Discovery.IRadioDiscovery p2Discovery,
            HttpContext ctx) =>
        {
            var timeout = TimeSpan.FromMilliseconds(1500);
            var p1Task = p1Discovery.DiscoverAsync(timeout, ctx.RequestAborted);
            var p2Task = p2Discovery.DiscoverAsync(timeout, ctx.RequestAborted);
            await Task.WhenAll(p1Task, p2Task);

            var p1Infos = p1Task.Result.Select(MapP1);
            var p2Infos = p2Task.Result.Select(MapP2);
            return p1Infos.Concat(p2Infos).ToArray();

            static RadioInfo MapP1(DiscoveredRadio r) => new(
                MacAddress: r.Mac.ToString(),
                IpAddress: r.Ip.ToString(),
                BoardId: r.Board.ToString(),
                FirmwareVersion: r.FirmwareString,
                Busy: r.Details.Busy,
                Details: BuildP1Details(r));

            static RadioInfo MapP2(Zeus.Protocol2.Discovery.DiscoveredRadio r) => new(
                MacAddress: r.Mac.ToString(),
                IpAddress: r.Ip.ToString(),
                BoardId: r.Board.ToString(),
                FirmwareVersion: r.FirmwareString,
                Busy: r.Details.Busy,
                Details: BuildP2Details(r));

            static IReadOnlyDictionary<string, string> BuildP1Details(DiscoveredRadio r)
            {
                var d = new Dictionary<string, string>
                {
                    ["protocol"] = "P1",
                    ["rawBoardId"] = $"0x{r.Details.RawBoardId:X2}",
                    ["firmwareCode"] = r.FirmwareVersion.ToString(),
                    ["gatewareBuild"] = r.Details.GatewareBuild.ToString(),
                    ["rawReplyHex"] = Convert.ToHexString(r.Details.RawReply),
                };
                if (r.Details.FixedIpEnabled) d["fixedIpEnabled"] = "true";
                if (r.Details.FixedIpOverridesDhcp) d["fixedIpOverridesDhcp"] = "true";
                if (r.Details.MacAddressModified) d["macAddressModified"] = "true";
                if (r.Details.FixedIpAddress is { } ip) d["fixedIpAddress"] = ip.ToString();
                if (r.Details.HermesLite2MinorVersion is { } minor) d["hl2MinorVersion"] = minor.ToString();
                return d;
            }

            static IReadOnlyDictionary<string, string> BuildP2Details(Zeus.Protocol2.Discovery.DiscoveredRadio r)
            {
                var d = new Dictionary<string, string>
                {
                    ["protocol"] = "P2",
                    ["rawBoardId"] = $"0x{r.Details.RawBoardId:X2}",
                    ["firmwareCode"] = r.FirmwareVersion.ToString(),
                    ["protocolSupported"] = r.Details.ProtocolSupported.ToString(),
                    ["numReceivers"] = r.Details.NumReceivers.ToString(),
                    ["mercuryVersion0"] = r.Details.MercuryVersion0.ToString(),
                    ["mercuryVersion1"] = r.Details.MercuryVersion1.ToString(),
                    ["mercuryVersion2"] = r.Details.MercuryVersion2.ToString(),
                    ["mercuryVersion3"] = r.Details.MercuryVersion3.ToString(),
                    ["pennyVersion"] = r.Details.PennyVersion.ToString(),
                    ["metisVersion"] = r.Details.MetisVersion.ToString(),
                    ["rawReplyHex"] = Convert.ToHexString(r.Details.RawReply),
                };
                if (r.Details.BetaVersion != 0) d["betaVersion"] = r.Details.BetaVersion.ToString();
                return d;
            }
        });

        // Take over a Busy radio. Discovery reports status 0x03 when another
        // client owns the radio; the UI normally disables Connect for those.
        // This sends a protocol stop to the radio so it drops the current owner,
        // freeing it for an immediate connect. Outward-facing and deliberate —
        // the Connect panel gates it behind an explicit operator confirmation,
        // because it can kick another (possibly transmitting) operator off.
        app.MapPost("/api/radios/reclaim", async (ReclaimRadioRequest req, RadioReclaimService reclaim, HttpContext ctx) =>
        {
            if (!TryParseIpEndpoint(req.Endpoint ?? string.Empty, out var ipEndpoint))
                return Results.BadRequest(new { error = $"Invalid endpoint '{req.Endpoint}'." });

            var isP2 = string.Equals(req.Protocol, "P2", StringComparison.OrdinalIgnoreCase);
            log.LogInformation("api.radios.reclaim ip={Ip} protocol={Proto}", ipEndpoint.Address, isP2 ? "P2" : "P1");

            await reclaim.ReclaimAsync(ipEndpoint.Address, isP2, ctx.RequestAborted);
            return Results.Ok(new { freed = true });
        });

        app.MapPost("/api/connect", async (ConnectRequest req, RadioService r, WdspWisdomInitializer wisdom, IRadioDiscovery p1Discovery, HttpContext ctx) =>
        {
            log.LogInformation(
                "api.connect endpoint={Ep} rate={Rate} preamp={Pre} atten={Atten}",
                req.Endpoint, req.SampleRate, req.PreampOn, req.Atten);

            // WDSPwisdom must finish before OpenChannel, otherwise FFTW runs its slow
            // per-size planner on the pipeline thread and RX packets pile up until
            // the radio drops. The UI keeps Connect disabled during build; this is
            // the server-side guard for non-UI callers (curl, older clients).
            if (wisdom.Phase != WisdomPhase.Ready)
                return Results.Json(
                    new { error = "DSP is preparing FFTW plans — try again in a moment." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            if (!TryValidateSampleRate(req.SampleRate, out var rateErr))
                return Results.BadRequest(new { error = rateErr });
            if (req.Atten is int a && !TryValidateAttenDb(a, out var attenErr))
                return Results.BadRequest(new { error = attenErr });

            if (req.PreampOn is bool preamp) r.SetPreamp(preamp);
            if (req.Atten is int atten) r.SetAttenuator(new HpsdrAtten(atten));

            // Plumb the discovered board byte through so RadioService can
            // set the real board kind on the Protocol1Client rather than
            // defaulting to HermesLite2 for every P1 connection — issue #294.
            var p1BoardKind = req.BoardId is byte bid ? MapBoardByteP1(bid) : HpsdrBoardKind.Unknown;

            // Best-effort firmware capture for the "Report a problem" diagnostic
            // snapshot. A short discovery broadcast (the radio isn't connected
            // yet, so it answers) lets us match the code-version byte for this
            // endpoint. Fail-open: any failure leaves firmware null and never
            // blocks the connect — the diagnostic just shows "unknown".
            string? firmware = null;
            if (TryParseIpEndpoint(req.Endpoint, out var fwIp))
            {
                try
                {
                    var found = await p1Discovery.DiscoverAsync(TimeSpan.FromMilliseconds(400), ctx.RequestAborted);
                    firmware = found.FirstOrDefault(d => d.Ip.Equals(fwIp.Address))?.FirmwareString;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "api.connect firmware probe failed — diagnostic firmware will be 'unknown'");
                }
            }

            try
            {
                var state = await r.ConnectAsync(req.Endpoint, req.SampleRate, ctx.RequestAborted, p1BoardKind, firmware);
                return Results.Ok(state);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        app.MapPost("/api/connect/p2", async (
            ConnectRequest req,
            DspPipelineService dsp,
            WdspWisdomInitializer wisdom,
            Zeus.Protocol2.Discovery.IRadioDiscovery p2Discovery,
            HttpContext ctx) =>
        {
            log.LogInformation("api.connect.p2 endpoint={Ep} rate={Rate} force={Force}", req.Endpoint, req.SampleRate, req.Force);

            if (wisdom.Phase != WisdomPhase.Ready)
                return Results.Json(
                    new { error = "DSP is preparing FFTW plans — try again in a moment." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            if (!TryParseIpEndpoint(req.Endpoint, out var ipEndpoint))
                return Results.BadRequest(new { error = $"Invalid endpoint '{req.Endpoint}'." });

            // HARDWARE-SAFETY GUARD (relay chatter / PSU brown-out).
            // Before opening the relay-bearing high-priority stream, unicast-probe
            // the radio and refuse to connect if it reports Busy — i.e. another
            // controller (a co-located saturn-go / p2app stack, or another
            // Zeus/Thetis client) is already driving it. Two masters publishing
            // DIFFERENT band/antenna/ALEX-relay selections make the FPGA flip the
            // BPF/LPF/T-R relay matrix every packet; the relay inrush can brown
            // out a shared PSU and reboot the host (observed 2026-06-23 on a
            // co-located CM5 Saturn all-in-one). A single Zeus master is fine —
            // the danger requires a second, disagreeing controller. The operator
            // takes over deliberately via Reclaim, which stops the other owner
            // and re-connects with force=true. The probe fails OPEN: a radio that
            // doesn't answer the probe is NOT blocked, so this never strands a
            // legitimate connect. The probe must NOT weaken the co-located
            // ephemeral-port bind — it only reads discovery, it doesn't touch the
            // connect socket.
            // Probe result is reused after the busy-gate to capture the
            // firmware version for the diagnostics snapshot — null on a forced
            // connect, which deliberately skips the probe.
            Zeus.Protocol2.Discovery.DiscoveredRadio? probe = null;
            if (!req.Force)
            {
                try
                {
                    probe = await p2Discovery.ProbeAsync(
                        ipEndpoint.Address, TimeSpan.FromMilliseconds(700), ctx.RequestAborted);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Probe failure (socket setup, etc.) must not block a connect —
                    // log and fall through, same fail-open posture as no-reply.
                    log.LogWarning(ex, "api.connect.p2 busy-probe failed — allowing connect");
                }

                if (probe?.Details.Busy == true)
                {
                    log.LogWarning(
                        "api.connect.p2 REFUSED — radio {Ip} reports BUSY (another controller owns it); refusing second-master connect",
                        ipEndpoint.Address);
                    return Results.Json(
                        new
                        {
                            error =
                                "This radio is already in use by another controller. Connecting Zeus as a " +
                                "second master would make the band/antenna/T-R relays chatter and can brown " +
                                "out the radio. Stop the other controller (e.g. saturn-go / another client), " +
                                "or use Reclaim to take exclusive control, then connect again.",
                            busy = true,
                            reclaimable = true,
                        },
                        statusCode: StatusCodes.Status409Conflict);
                }
            }

            var rateKhz = req.SampleRate switch
            {
                48_000 => 48,
                96_000 => 96,
                192_000 => 192,
                384_000 => 384,
                768_000 => 768,      // P2 only (ANAN G2)
                1_536_000 => 1536,   // P2 only (ANAN G2)
                _ => 192,
            };

            // Plumb the discovered board byte through so RadioService can
            // surface the real board kind instead of defaulting to OrionMkII
            // for every P2 connection (issue #171 — Brick2 is Hermes/0x01 on P2).
            var boardKind = req.BoardId is byte b ? MapBoardByteP2(b) : HpsdrBoardKind.Unknown;

            // Firmware version for the "Report a problem" diagnostic snapshot.
            var firmware = probe?.FirmwareString;

            try
            {
                rateKhz = await dsp.ConnectP2Async(ipEndpoint, rateKhz, numAdc: 2, ctx.RequestAborted, boardKind, firmware);
                return Results.Ok(new { protocol = "P2", endpoint = req.Endpoint, sampleRateKhz = rateKhz });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "api.connect.p2 failed");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/disconnect/p2", async (DspPipelineService dsp, HttpContext ctx) =>
        {
            log.LogInformation("api.disconnect.p2");
            await dsp.DisconnectP2Async(ctx.RequestAborted);
            return Results.Ok(new { status = "disconnected" });
        });

        app.MapPost("/api/disconnect", async (RadioService r, HttpContext ctx) =>
        {
            log.LogInformation("api.disconnect");
            return await r.DisconnectAsync(ctx.RequestAborted);
        });

        app.MapPost("/api/vfo", (VfoSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.vfo receiver={Receiver} hz={Hz}", req.Receiver, req.Hz);
            return r.SetReceiverVfo(req.Receiver, req.Hz);
        });

        app.MapPost("/api/vfo/swap", (RadioService r) =>
        {
            log.LogInformation("api.vfo.swap");
            return r.SwapVfos();
        });

        app.MapPost("/api/rx2", (Rx2SetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.rx2 enabled={Enabled} vfoBHz={VfoBHz} audioMode={AudioMode} afGainDb={AfGainDb}",
                req.Enabled, req.VfoBHz, req.AudioMode, req.AfGainDb);
            return r.SetRx2(req);
        });

        // Configure any receiver by index for full multi-DDC (RX1=0, RX2=1,
        // RX3+=2..). Index 0/1 delegate to the RX1/RX2 setters; index >= 2 drives
        // an extra hardware DDC. Only supplied fields change.
        app.MapPost("/api/receivers/{index:int}", (int index, ReceiverSetRequest req, RadioService r, KiwiSdrService kiwi) =>
        {
            if (index < 0 || index >= Zeus.Contracts.WireContract.MaxReceivers)
                return Results.BadRequest(new { error = $"receiver index out of range (0..{Zeus.Contracts.WireContract.MaxReceivers - 1})" });
            // The reserved Kiwi slot is a remote receiver, not a hardware DDC:
            // route tuning/mode/filter to the Kiwi service (enable/disable + URL
            // live on /api/kiwi). Returns the same StateDto so the projected Kiwi
            // entry round-trips back to the client.
            if (index == Zeus.Contracts.WireContract.KiwiReceiverIndex)
            {
                log.LogInformation("api.receivers.kiwi vfoHz={VfoHz} mode={Mode} low={Low} high={High}",
                    req.VfoHz, req.Mode, req.FilterLowHz, req.FilterHighHz);
                // CTUN on → freeze the waterfall centre and roam the dial within
                // the displayed span; off → re-centre on the dial.
                kiwi.SetTuning(req.VfoHz, req.Mode, req.FilterLowHz, req.FilterHighHz, r.Snapshot().CtunEnabled);
                return Results.Ok(r.Snapshot());
            }
            log.LogInformation(
                "api.receivers index={Index} enabled={Enabled} vfoHz={VfoHz} adc={Adc} mode={Mode}",
                index, req.Enabled, req.VfoHz, req.AdcSource, req.Mode);
            return Results.Ok(r.SetReceiver(
                index,
                enabled: req.Enabled,
                vfoHz: req.VfoHz,
                adcSource: req.AdcSource,
                mode: req.Mode,
                filterLowHz: req.FilterLowHz,
                filterHighHz: req.FilterHighHz,
                afGainDb: req.AfGainDb,
                filterPresetName: req.FilterPresetName));
        });

        // Per-RX audio mute (RX1=0, RX2=1, RX3+=2..). Mirrors Thetis chkMUT /
        // chkRX2Mute — silences only this receiver's contribution to the mix.
        app.MapPost("/api/receivers/{index:int}/mute", (int index, MuteSetRequest req, RadioService r, KiwiSdrService kiwi) =>
        {
            if (index < 0 || index >= Zeus.Contracts.WireContract.MaxReceivers)
                return Results.BadRequest(new { error = $"receiver index out of range (0..{Zeus.Contracts.WireContract.MaxReceivers - 1})" });
            log.LogInformation("api.receivers.mute index={Index} muted={Muted}", index, req.Muted);
            if (index == Zeus.Contracts.WireContract.KiwiReceiverIndex)
            {
                kiwi.SetMuted(req.Muted);
                return Results.Ok(r.Snapshot());
            }
            return Results.Ok(r.SetReceiverMuted(index, req.Muted));
        });

        // KiwiSDR slice receiver config. GET returns current status (never the
        // stored password — only HasPassword); POST patches enable/url/password
        // and (re)connects. The Kiwi appears as the "Kiwi" receiver beside the
        // hardware RXs once enabled with a URL.
        app.MapGet("/api/kiwi", (KiwiSdrService kiwi) => Results.Ok(kiwi.GetConfig()));
        app.MapPost("/api/kiwi", async (KiwiSetRequest req, KiwiSdrService kiwi) =>
        {
            log.LogInformation("api.kiwi enabled={Enabled} url={Url} pwSet={PwSet}",
                req.Enabled, req.Url, req.Password is not null);
            return Results.Ok(await kiwi.SetConfigAsync(req.Enabled, req.Url, req.Password, default));
        });

        // Public KiwiSDR directory for the Settings → KIWI map picker. Proxied
        // server-side (the source is plain HTTP — mixed-content/CORS blocked in
        // the browser); cached a couple of minutes in KiwiDirectoryService.
        app.MapGet("/api/kiwi/directory", async (KiwiDirectoryService dir, HttpContext ctx) =>
            Results.Ok(await dir.GetAsync(ctx.RequestAborted)));

        // VFO lock (Thetis chkVFOLock): blocks operator dial tuning; CAT/TCI
        // still tune. Pure software guard, no hardware effect.
        app.MapPost("/api/radio/vfo-lock", (VfoLockSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.radio.vfo-lock locked={Locked}", req.Locked);
            return Results.Ok(r.SetVfoLock(req.Locked));
        });

        // RIT (Receiver Incremental Tuning). Both fields optional; Hz clamped to
        // ±99999. RX1 only.
        app.MapPost("/api/rx/rit", (RitSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.rx.rit enabled={Enabled} hz={Hz}", req.Enabled, req.Hz);
            return Results.Ok(r.SetRit(req.Enabled, req.Hz));
        });

        // XIT (Transmit Incremental Tuning). Both fields optional; Hz clamped to
        // ±99999. Moves only the transmitted carrier.
        app.MapPost("/api/tx/xit", (XitSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.tx.xit enabled={Enabled} hz={Hz}", req.Enabled, req.Hz);
            return Results.Ok(r.SetXit(req.Enabled, req.Hz));
        });

        // Diversity combiner (Thetis DiversityForm). Every field optional.
        // P2/ANAN-class only (needs two phase-synchronous ADCs).
        app.MapPost("/api/rx/diversity", (DiversitySetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.rx.diversity enabled={Enabled} gain={Gain} phaseDeg={PhaseDeg} sourceRx={SourceRx}",
                req.Enabled, req.Gain, req.PhaseDeg, req.SourceRx);
            return Results.Ok(r.SetDiversity(req.Enabled, req.Gain, req.PhaseDeg, req.SourceRx));
        });

        // ATU tune request (Thetis ATUTune). Holds the Apollo/Alex auto-tune bit
        // (Protocol-1 C0=0x12 C2[4]) for DurationMs then auto-clears.
        // Bench-verification pending — see RadioService.RequestAtuTune.
        app.MapPost("/api/radio/atu/tune", (AtuTuneRequest req, RadioService r) =>
        {
            log.LogInformation("api.radio.atu.tune durationMs={DurationMs}", req.DurationMs);
            return Results.Ok(r.RequestAtuTune(req.DurationMs));
        });

        app.MapPost("/api/tx/vfo", (TxVfoSetRequest req, RadioService r) =>
        {
            if (!Enum.IsDefined(req.TxVfo))
                return Results.BadRequest(new { error = $"unknown txVfo {req.TxVfo}" });
            log.LogInformation("api.tx.vfo txVfo={TxVfo}", req.TxVfo);
            return Results.Ok(r.SetTxVfo(req.TxVfo));
        });

        // Select the transmit target by receiver index (0=RX1, 1=RX2, >=2 extra
        // DDC). Generalises /api/tx/vfo beyond the A/B pair; an out-of-range or
        // not-exposed index clamps to RX1 server-side.
        app.MapPost("/api/tx/receiver", (TxReceiverSetRequest req, RadioService r) =>
        {
            if (req.Index < 0 || req.Index >= Zeus.Contracts.WireContract.MaxReceivers)
                return Results.BadRequest(new { error = $"tx receiver index out of range (0..{Zeus.Contracts.WireContract.MaxReceivers - 1})" });
            log.LogInformation("api.tx.receiver index={Index}", req.Index);
            return Results.Ok(r.SetTxReceiver(req.Index));
        });

        // Set the radio's hardware NCO (LO) frequency directly. Does not
        // move VfoHz — used by the panadapter pure-pan gesture when a drag
        // would carry the viewport outside the IQ capture window.
        // See docs/prd/panfall_behavior.md.
        app.MapPost("/api/radio/lo", (RadioLoSetRequest req, RadioService r) =>
        {
            if (req.Hz < 0 || req.Hz > 60_000_000)
            {
                log.LogInformation("api.radio.lo rejected hz={Hz}", req.Hz);
                return Results.BadRequest(new { error = "hz out of range [0, 60000000]" });
            }
            log.LogInformation("api.radio.lo hz={Hz}", req.Hz);
            return Results.Ok(r.SetRadioLo(req.Hz));
        });

        // Per-receiver DDC-centre pan. Index 0 is RX1's hardware NCO (delegates to
        // /api/radio/lo's setter); index >= 1 recentres a secondary DDC (the
        // client keep-in-view autopan's analogue of SetRadioLo). No-op for P1
        // secondaries (shared NCO) / CTUN-off / disabled — see RequestSecondaryLo.
        app.MapPost("/api/receivers/{index:int}/lo", (int index, RadioLoSetRequest req, RadioService r, DspPipelineService pipe, KiwiSdrService kiwi) =>
        {
            if (req.Hz < 0 || req.Hz > 60_000_000)
            {
                log.LogInformation("api.receivers.lo rejected index={Index} hz={Hz}", index, req.Hz);
                return Results.BadRequest(new { error = "hz out of range [0, 60000000]" });
            }
            log.LogInformation("api.receivers.lo index={Index} hz={Hz}", index, req.Hz);
            // Kiwi slice: pan its waterfall centre (no hardware DDC behind it).
            if (index == Zeus.Contracts.WireContract.KiwiReceiverIndex)
            {
                kiwi.SetCenter(req.Hz);
                return Results.Ok(r.Snapshot());
            }
            if (index <= 0) return Results.Ok(r.SetRadioLo(req.Hz));
            pipe.RequestSecondaryLo(index, req.Hz);
            return Results.Ok(r.Snapshot());
        });

        // Toggle CTUN (click-tune / centred tuning). When enabled, panadapter
        // clicks move only the dial and leave the hardware NCO frozen so the
        // operator can tune off-centre; TX retunes to the dial on key-down.
        // See docs/prd/panfall_behavior.md and StateDto.CtunEnabled.
        app.MapPost("/api/radio/ctun", (CtunSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.radio.ctun enabled={Enabled}", req.Enabled);
            return r.SetCtunEnabled(req.Enabled);
        });

        app.MapPost("/api/mode", (ModeSetRequest req, RadioService r) =>
        {
            if (req.Receiver is not (0 or 1))
                return Results.BadRequest(new { error = $"unknown receiver {req.Receiver}" });
            var receiver = req.Receiver == 1 ? TxVfo.B : TxVfo.A;
            log.LogInformation("api.mode mode={Mode} receiver={Receiver}", req.Mode, receiver);
            return Results.Ok(r.SetMode(req.Mode, receiver));
        });

        app.MapPost("/api/bandwidth", (BandwidthSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.bandwidth low={L} high={H}", req.Low, req.High);
            return r.SetFilter(req.Low, req.High);
        });

        // FreeDV digital-voice telemetry + config. The mode itself is selected
        // via /api/mode (RxMode.FreeDv); these surface the live modem state
        // (sync/SNR/submode) polled by the FreeDV panel and apply submode /
        // squelch / TX-text changes. No-op-safe when the codec2 native library
        // is missing (NativeAvailable=false).
        app.MapGet("/api/freedv/status", (FreeDvService fd) => Results.Ok(fd.Status()));

        // FreeDV Reporter stations — live roster mirrored from qso.freedv.org by
        // FreeDvReporterService (persistent read-only Socket.IO link). Returns the
        // current snapshot (connection state + stations sorted by frequency);
        // empty until the first bulk_update arrives. The Stations panel polls this
        // and offers click-to-tune.
        app.MapGet("/api/freedv/stations",
            (FreeDvReporterService svc) => Results.Ok(svc.GetSnapshot()));

        // FreeDV Reporter "report mode" — strictly opt-in (default OFF). When
        // enabled with a callsign + Maidenhead grid, Zeus connects to the reporter
        // in "report" role and broadcasts the operator's callsign / grid / freq /
        // TX activity to the public qso.freedv.org map. GET returns the current
        // (normalized) settings; POST saves them and forces a reconnect so the new
        // role / identity takes effect.
        app.MapGet("/api/freedv/reporter/settings",
            (FreeDvReporterService svc) => Results.Ok(svc.GetSettings()));
        app.MapPost("/api/freedv/reporter/settings",
            (FreeDvReporterSettings req, FreeDvReporterService svc) =>
            {
                var saved = svc.SaveSettings(req);
                log.LogInformation(
                    "api.freedv.reporter.settings report={Enabled} call={Call} grid={Grid}",
                    saved.ReportEnabled, saved.Callsign, saved.GridSquare);
                return Results.Ok(saved);
            });

        // QSY request — ask the station identified by {sid} to move to the
        // operator's current VFO frequency. Requires report mode active and a
        // known sid; 400 otherwise.
        app.MapPost("/api/freedv/stations/{sid}/qsy",
            (string sid, FreeDvReporterService svc) =>
                svc.RequestQsy(sid)
                    ? Results.Ok(new { ok = true })
                    : Results.BadRequest(new { error = "Not reporting, or station unknown." }));
        app.MapPut("/api/freedv/config", (FreeDvConfigRequest req, FreeDvService fd) =>
        {
            log.LogInformation(
                "api.freedv.config submode={Submode} squelch={Sq} thresh={Th}",
                req.Submode, req.SquelchEnabled, req.SnrSquelchThreshDb);
            return Results.Ok(fd.ApplyConfig(req));
        });

        // FreeDV codec2 library install. codec2 can't be built on an operator's
        // machine, so when the bundled binary is missing this fetches the prebuilt
        // lib Zeus committed for the running platform from the repo and stages it,
        // reloading the modem live (see FreeDvNativeInstaller). GET reports install
        // progress + whether codec2 is already present; POST starts a background
        // download (idempotent — no-op if already installed/running). The panel
        // polls GET until phase is "done" / "failed".
        static object FreeDvInstallDto(FreeDvNativeInstaller installer)
        {
            var s = installer.Current;
            return new
            {
                phase = s.Phase.ToString().ToLowerInvariant(),
                percent = s.Percent,
                message = s.Message,
                installed = installer.Installed,
            };
        }
        app.MapGet("/api/freedv/install", (FreeDvNativeInstaller installer) =>
            Results.Ok(FreeDvInstallDto(installer)));
        app.MapPost("/api/freedv/install", (FreeDvNativeInstaller installer) =>
        {
            installer.Start();
            return Results.Ok(FreeDvInstallDto(installer));
        });

        // TX bandpass filter — signed Hz pair (LSB negative, DSB symmetric). Per-mode
        // family memory is managed in RadioService, identical shape to the RX filter.
        // Operator-editable via Settings → TX Filter panel.
        app.MapPost("/api/tx-filter", (TxFilterSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.tx-filter low={L} high={H}", req.LowHz, req.HighHz);
            return r.SetTxFilter(req.LowHz, req.HighHz);
        });

        // SSB bandpass "rectangularity" — issue #871. RX and TX are independent
        // selectors; each pushes the chosen WDSP fir.c window code (0 = soft /
        // Blackman-Harris 4-term, 1 = sharp / BH 7-term) into the live engine and
        // persists to DspSettingsStore.
        app.MapPost("/api/rx/filter-window", (BandpassWindowSetRequest req, RadioService r) =>
        {
            if (!Enum.IsDefined(req.Window))
                return Results.BadRequest(new { error = $"unknown BandpassWindow {req.Window}" });
            log.LogInformation("api.rx.filterWindow window={Window}", req.Window);
            return Results.Ok(r.SetRxBandpassWindow(req.Window));
        });

        app.MapPost("/api/tx/filter-window", (BandpassWindowSetRequest req, RadioService r) =>
        {
            if (!Enum.IsDefined(req.Window))
                return Results.BadRequest(new { error = $"unknown BandpassWindow {req.Window}" });
            log.LogInformation("api.tx.filterWindow window={Window}", req.Window);
            return Results.Ok(r.SetTxBandpassWindow(req.Window));
        });

        // Filter preset endpoints (PRD §5.2). These are the preferred filter surface;
        // /api/bandwidth remains for backward compat. POST /api/filter also accepts
        // an optional PresetName to track which chip is active.
        app.MapPost("/api/filter", (FilterSetRequest req, RadioService r) =>
        {
            if (req.Receiver is not (0 or 1))
                return Results.BadRequest(new { error = $"unknown receiver {req.Receiver}" });
            var receiver = req.Receiver == 1 ? TxVfo.B : TxVfo.A;
            log.LogInformation(
                "api.filter low={L} high={H} preset={P} receiver={Receiver}",
                req.LowHz,
                req.HighHz,
                req.PresetName,
                receiver);
            return Results.Ok(r.SetFilter(req.LowHz, req.HighHz, req.PresetName, receiver));
        });

        app.MapGet("/api/filter/presets", (string? mode, RadioService r) =>
        {
            if (mode is null || !Enum.TryParse<RxMode>(mode, ignoreCase: true, out var rxMode))
                return Results.BadRequest(new { error = $"Unknown mode '{mode}'. Expected one of: {string.Join(", ", Enum.GetNames<RxMode>())}" });
            return Results.Ok(r.GetFilterPresets(rxMode));
        });

        app.MapPost("/api/filter/presets", (FilterPresetWriteRequest req, RadioService r) =>
        {
            log.LogInformation("api.filter.presets mode={M} slot={S} low={L} high={H}", req.Mode, req.SlotName, req.LowHz, req.HighHz);
            if (req.SlotName is not ("VAR1" or "VAR2"))
                return Results.Conflict(new { error = "Fixed presets cannot be edited. Only VAR1 and VAR2 slots are writable." });
            if (!Enum.IsDefined(req.Mode))
                return Results.BadRequest(new { error = $"Unknown mode '{req.Mode}'." });
            r.SetFilterPresetOverride(req.Mode, req.SlotName, req.LowHz, req.HighHz);
            return Results.Ok(r.GetFilterPresets(req.Mode));
        });

        // Advanced-ribbon pane visibility. Persisted via FilterPresetStore so the
        // operator's close-the-ribbon choice survives a Zeus.Server restart.
        app.MapPost("/api/filter/advanced-pane", (FilterAdvancedPaneRequest req, RadioService r) =>
        {
            log.LogInformation("api.filter.advancedPane open={Open}", req.Open);
            return r.SetFilterAdvancedPaneOpen(req.Open);
        });

        // Get favorite filter slots for a mode.
        app.MapGet("/api/filter/favorites", (string? mode, RadioService r) =>
        {
            if (mode is null || !Enum.TryParse<RxMode>(mode, ignoreCase: true, out var rxMode))
                return Results.BadRequest(new { error = $"Unknown mode '{mode}'. Expected one of: {string.Join(", ", Enum.GetNames<RxMode>())}" });
            var slotNames = r.GetFavoriteFilterSlots(rxMode);
            return Results.Ok(new FilterFavoriteSlotsResponse(slotNames));
        });

        // Set favorite filter slots for a mode (up to 3).
        app.MapPost("/api/filter/favorites", (FilterFavoriteSlotsRequest req, RadioService r) =>
        {
            log.LogInformation("api.filter.favorites mode={M} slots={S}", req.Mode, string.Join(",", req.SlotNames));
            if (!Enum.IsDefined(req.Mode))
                return Results.BadRequest(new { error = $"Unknown mode '{req.Mode}'." });
            if (req.SlotNames.Length > 3)
                return Results.BadRequest(new { error = "Maximum 3 favorite slots allowed." });
            return Results.Ok(r.SetFavoriteFilterSlots(req.Mode, req.SlotNames));
        });

        app.MapPost("/api/sampleRate", (SampleRateSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.sampleRate rate={Rate}", req.Rate);
            if (!TryValidateSampleRate(req.Rate, out var err))
                return Results.BadRequest(new { error = err });
            return Results.Ok(r.SetSampleRate(MapHpsdrSampleRate(req.Rate)));
        });

        app.MapPost("/api/preamp", (PreampSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.preamp on={On}", req.On);
            return r.SetPreamp(req.On);
        });

        app.MapPost("/api/agcGain", (AgcGainSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.agcGain topDb={TopDb:F1}", req.TopDb);
            return r.SetAgcTop(req.TopDb);
        });

        // (Removed /api/agc/threshold + /disengage with the AGC knee — AGC-T is
        // the single manual AGC control now.)

        app.MapPost("/api/rx/agc", (AgcSetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.rx.agc mode={Mode} slope={Slope} decayMs={Decay} hangMs={Hang} hangThr={Thr} fixedDb={Fixed}",
                req.Agc.Mode, req.Agc.Slope, req.Agc.DecayMs, req.Agc.HangMs,
                req.Agc.HangThreshold, req.Agc.FixedGainDb);
            if (!Enum.IsDefined(req.Agc.Mode))
                return Results.BadRequest(new { error = $"unknown AgcMode {req.Agc.Mode}" });
            return Results.Ok(r.SetAgc(req.Agc));
        });

        app.MapPost("/api/rx/squelch", (SquelchSetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.rx.squelch enabled={Enabled} level={Level} adaptive={Adaptive} fixedSensitivity={FixedSensitivity}",
                req.Squelch.Enabled, req.Squelch.Level, req.Squelch.Adaptive, req.Squelch.FixedSensitivity);
            if (req.Squelch.Level < 0 || req.Squelch.Level > 100)
                return Results.BadRequest(new { error = $"Squelch Level {req.Squelch.Level} out of range 0..100" });
            if (req.Squelch.FixedSensitivity < SquelchConfig.MinFixedSensitivity ||
                req.Squelch.FixedSensitivity > SquelchConfig.MaxFixedSensitivity)
                return Results.BadRequest(new { error = $"Squelch FixedSensitivity {req.Squelch.FixedSensitivity} out of range 0..100" });
            return Results.Ok(r.SetSquelch(req.Squelch));
        });

        app.MapPost("/api/tx/leveling", (TxLevelingSetRequest req, RadioService r) =>
        {
            var cfg = req.TxLeveling;
            log.LogInformation(
                "api.tx.leveling alcMaxGainDb={Alc:F1} alcDecayMs={AlcDecay} levelerEnabled={Lvlr} levelerDecayMs={LvlrDecay} compEnabled={Comp} compGainDb={CompGain:F1}",
                cfg.AlcMaxGainDb, cfg.AlcDecayMs, cfg.LevelerEnabled, cfg.LevelerDecayMs,
                cfg.CompressorEnabled, cfg.CompressorGainDb);
            // Range validation (Thetis parity §6.1-6.3). RadioService also clamps,
            // but a 400 lets a misbehaving client know its value was rejected.
            if (double.IsNaN(cfg.AlcMaxGainDb) || cfg.AlcMaxGainDb < 0.0 || cfg.AlcMaxGainDb > 120.0)
                return Results.BadRequest(new { error = "alcMaxGainDb must be 0..120 dB" });
            if (cfg.AlcDecayMs < 1 || cfg.AlcDecayMs > 50)
                return Results.BadRequest(new { error = "alcDecayMs must be 1..50" });
            if (cfg.LevelerDecayMs < 1 || cfg.LevelerDecayMs > 5000)
                return Results.BadRequest(new { error = "levelerDecayMs must be 1..5000" });
            if (double.IsNaN(cfg.CompressorGainDb) || cfg.CompressorGainDb < 0.0 || cfg.CompressorGainDb > 20.0)
                return Results.BadRequest(new { error = "compressorGainDb must be 0..20 dB" });
            return Results.Ok(r.SetTxLeveling(cfg));
        });

        // TX fidelity policy and station-profile overrides. Built-in Studio/eSSB/DX
        // defaults live in the frontend; these routes persist only active target
        // selection and operator edits so diagnostics never duplicate profile data.
        app.MapGet("/api/tx/fidelity-policy", (TxFidelityPolicyStore store) =>
            Results.Ok(store.Get()));

        app.MapPut("/api/tx/fidelity-policy", (TxFidelityPolicyDto req, TxFidelityPolicyStore store) =>
        {
            if (!TryValidateTxFidelityPolicy(req, out var err))
                return Results.BadRequest(new { error = err });

            var saved = store.Set(req);
            log.LogInformation(
                "api.tx.fidelityPolicy profile={ProfileId} density={Density}",
                saved.ProfileId, saved.TargetSpectralDensity);
            return Results.Ok(saved);
        });

        // /api/tx/station-profiles* RETIRED — the fixed 3-up TX station-profile
        // system is folded into the unified TX Audio Profiles (the three are now
        // seeded as named, editable profiles). See /api/tx-audio-profiles*.

        app.MapPost("/api/rx/afGain", (RxAfGainSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.rx.afGain db={Db:F1}", req.Db);
            return r.SetRxAfGain(req.Db);
        });

        app.MapPost("/api/attenuator", (AttenuatorSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.attenuator db={Db}", req.Db);
            if (!TryValidateAttenDb(req.Db, out var err))
                return Results.BadRequest(new { error = err });
            return Results.Ok(r.SetAttenuator(new HpsdrAtten(req.Db)));
        });

        app.MapPost("/api/auto-att", (AutoAttSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.auto-att enabled={Enabled}", req.Enabled);
            return r.SetAutoAtt(req.Enabled);
        });

        app.MapGet("/api/rx/adc-protection", (RadioService r) =>
        {
            return Results.Ok(r.GetAdcProtectionStatus());
        });

        app.MapPut("/api/rx/adc-protection", (AdcProtectionSetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.rx.adcProtection enabled={Enabled} attackMs={AttackMs} releaseMs={ReleaseMs} maxOffset={MaxOffset} magLimit={MagLimit}",
                req.Enabled, req.AttackMs, req.ReleaseMs, req.MaxOffsetDb, req.MagnitudeSoftLimit);
            return Results.Ok(r.SetAdcProtection(req));
        });

        app.MapPost("/api/auto-agc", (AutoAgcSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.auto-agc enabled={Enabled}", req.Enabled);
            return r.SetAutoAgc(req.Enabled);
        });

        app.MapPost("/api/tx/mox", (MoxSetRequest req, TxService tx) =>
        {
            log.LogInformation("api.tx.mox on={On}", req.On);
            if (!tx.TrySetMox(req.On, out var err)) return Results.Conflict(new { error = err });
            return Results.Ok(new { moxOn = tx.IsMoxOn });
        });

        // CW keyer (zeus-drf). Body: { text, wpm? }. Returns 202 immediately;
        // playback happens on the engine's worker. WPM null = engine default
        // (currently 20); the engine clamps to 5..50. Empty text is allowed —
        // produces no symbols and resolves to Idle without keying.
        app.MapPost("/api/cw/send", async (CwSendRequest req, CwEngine cw) =>
        {
            await cw.SendAsync(req.Text ?? string.Empty, req.Wpm, default).ConfigureAwait(false);
            return Results.Accepted();
        });

        // Hard abort. Drops the queue and signals the in-flight playback to
        // cancel. MOX falls on the next playback tick (≤ ChunkSamples / SR ≈
        // 10 ms). Returns 200 unconditionally — abort is best-effort.
        app.MapPost("/api/cw/abort", (CwEngine cw) =>
        {
            cw.Abort("api.cw.abort");
            return Results.Ok();
        });

        // Persisted CW operator settings (WPM, Farnsworth, 6 macros,
        // sidetone gain/pitch). PATCH-shaped PUT: every field nullable so
        // the UI can save one slider or one macro without round-tripping
        // the whole record. Returns the post-merge snapshot so the client
        // can reconcile its store with what the server actually stored
        // (e.g. clamped values).
        app.MapGet("/api/cw/settings", (CwSettingsStore store) =>
            Results.Ok(store.Get()));

        app.MapPut("/api/cw/settings", (CwSettingsSetRequest req, CwSettingsStore store, CwSidetoneSource sidetone, RadioService radio) =>
        {
            // Save first so the persisted view is the source of truth even
            // if the live generator update races somehow. Then push the
            // (post-clamp) values to the live generator so a slider drag
            // updates pitch/gain without a restart.
            var snapshot = store.Save(req);
            sidetone.SetPitchHz(snapshot.SidetoneHz);
            sidetone.SetGainDb(snapshot.SidetoneGainDb);
            // Forward keyer speed (WPM) + mode + sidetone to the radio's
            // on-board iambic keyer so a paddle keys at the panel speed:
            // P1 → C&C 0x0B; P2 → TxSpecific internal-keyer arm (#1032). No-op
            // when no radio is connected (cached + re-pushed on the next
            // connect). See zeus-bks.
            radio.SetCwKeyerConfig(snapshot.Wpm, snapshot.KeyerMode, snapshot.SidetoneHz, snapshot.SidetoneGainDb);
            return Results.Ok(snapshot);
        });

        // Mic-gain: N dB in [-40, +10], scales WDSP TXA panel-gain-1 the same
        // way Thetis does (console.cs:28805 setAudioMicGain → Audio.MicPreamp =
        // 10^(db/20) → cmaster.CMSetTXAPanelGain1). The negative range is the
        // important half: browser getUserMedia mics typically peak around
        // -10..-15 dBFS, which over-drives WDSP TXA + ALC and prints as
        // splatter on the air; without an attenuator the operator has nowhere
        // to back off. Range matches Thetis's MicGainMin/Max defaults
        // (console.cs:19151 = -40, :19163 = +10). RadioService persists the dB
        // value via RadioStateStore; the dB → linear (10^(db/20)) conversion
        // happens at the engine seam in DspPipelineService.
        app.MapPost("/api/mic-gain", (MicGainSetRequest req, RadioService r) =>
        {
            var snap = r.SetTxMicGain(req.Db);
            return Results.Ok(new { micGainDb = snap.MicGainDb });
        });

        // Leveler max-gain ceiling in dB. Operator band is 0..20 dB (Thetis parity,
        // setup.designer.cs): 0 disables the headroom entirely (unity-cap Leveler);
        // Thetis's stock default is 15 (radio.cs:2979). Anything outside is a
        // 400 so a misbehaving client can't hand WDSP a value that'd saturate on
        // the first voiced sample. RadioService persists via RadioStateStore so
        // the operator's ceiling survives backend restart; the frontend no
        // longer needs to re-POST on WS reconnect.
        app.MapPost("/api/tx/leveler-max-gain", (LevelerMaxGainSetRequest req, RadioService r) =>
        {
            if (req.Gain < 0.0 || req.Gain > 20.0 || double.IsNaN(req.Gain))
                return Results.BadRequest(new { error = "gain must be 0..20 dB" });
            log.LogInformation("api.tx.levelerMaxGain dB={Db:F1}", req.Gain);
            var snap = r.SetTxLevelerMaxGain(req.Gain);
            return Results.Ok(new { levelerMaxGainDb = snap.LevelerMaxGainDb });
        });

        // TUN: internal-tune carrier. Flips SetTXAPostGenRun on WDSP; server-side is
        // where the PRD's drive clamp to min(drive, 25) lives, and where we gate
        // mutual exclusion with MOX so the HL2 sees exactly one of them active.
        app.MapPost("/api/tx/tun", (TunSetRequest req, TxService tx) =>
        {
            if (!tx.TrySetTun(req.On, out var err))
                return Results.Conflict(new { error = err });
            return Results.Ok(new { tunOn = tx.IsTunOn });
        });

        app.MapPost("/api/tx/drive", (DriveSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.tx.drive percent={Pct}", req.Percent);
            if (req.Percent < 0 || req.Percent > 100)
                return Results.BadRequest(new { error = "percent must be 0..100" });
            r.SetDrive(req.Percent);
            return Results.Ok(new { drivePercent = req.Percent });
        });

        // Waterfall render-state diagnostic (#629). The desktop (WebView2) app
        // has no reachable DevTools, so the frontend POSTs the waterfall's
        // runtime state here a few seconds after connect — letting us confirm
        // headlessly, from the SERVER log, that the history textures seeded
        // (texWidth > 0). Read-only: logs at Info, no side effects.
        app.MapPost("/api/diag/wf", (System.Text.Json.JsonElement body) =>
        {
            log.LogInformation("diag.wf {Report}", body.ToString());
            return Results.Ok();
        });

        // TX pre-key (MOX) delay (issue #630). Withholds modulated RF for N ms
        // after a UI MOX/TUNE key-down so an external amp's T/R relay settles
        // before RF appears. A standalone TX-sequencing setting — deliberately
        // NOT routed through /api/tx/ps/advanced. The server clamps below the PS
        // MOX hold-off, so the echoed value may be lower than requested.
        app.MapPost("/api/tx/prekey-delay", (TxPreKeyDelaySetRequest req, RadioService r) =>
        {
            log.LogInformation("api.tx.prekeyDelay ms={Ms}", req.DelayMs);
            if (req.DelayMs < 0 || req.DelayMs > 500)
                return Results.BadRequest(new { error = "delayMs must be 0..500" });
            var state = r.SetTxMoxPreKeyDelayMs(req.DelayMs);
            return Results.Ok(new { txMoxPreKeyDelayMs = state.TxMoxPreKeyDelayMs });
        });

        // TUN drive %. Symmetric with /api/tx/drive; the same PA-gain math applies,
        // so equal slider positions emit equal watts. Backend selects between the
        // two sources based on whether TUN is keyed (TxService.TrySetTun →
        // RadioService.NotifyTunActive).
        app.MapPost("/api/tx/tune-drive", (TuneDriveSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.tx.tune-drive percent={Pct}", req.Percent);
            if (req.Percent < 0 || req.Percent > 100)
                return Results.BadRequest(new { error = "percent must be 0..100" });
            r.SetTuneDrive(req.Percent);
            return Results.Ok(new { tunePercent = req.Percent });
        });

        // Two-tone test generator (TXA PostGen mode=1). Protocol-agnostic — works
        // on both P1 and P2 because it only touches WDSP TXA, not the wire format.
        app.MapPost("/api/tx/twotone", (TwoToneSetRequest req, RadioService r, TxService tx) =>
        {
            log.LogInformation(
                "api.tx.twotone enabled={On} f1={F1} f2={F2} mag={Mag}",
                req.Enabled, req.Freq1, req.Freq2, req.Mag);
            if (req.Mag is double m && (m < 0.0 || m > 1.0 || double.IsNaN(m)))
                return Results.BadRequest(new { error = "mag must be 0..1" });
            if (req.Freq1 is double f1 && (f1 < 50.0 || f1 > 5000.0 || double.IsNaN(f1)))
                return Results.BadRequest(new { error = "freq1 must be 50..5000 Hz" });
            if (req.Freq2 is double f2 && (f2 < 50.0 || f2 > 5000.0 || double.IsNaN(f2)))
                return Results.BadRequest(new { error = "freq2 must be 50..5000 Hz" });
            // TrySetTwoTone owns both the engine state (RadioService.SetTwoTone) and
            // the MOX side-effect — Thetis parity, setup.cs:11162-11165. Returns the
            // post-mutate snapshot via Snapshot(); on a connect-interlock failure
            // the request is rejected with 400.
            if (!tx.TrySetTwoTone(req, out var err))
                return Results.BadRequest(new { error = err });
            return Results.Ok(r.Snapshot());
        });

        // PureSignal master arm + cal-mode. RadioService.SetPs sets the
        // StateDto bit; DspPipelineService then sequences the active P1 or P2
        // feedback wire path before arming the WDSP engine.
        app.MapPost("/api/tx/ps", (PsControlSetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.tx.ps enabled={On} auto={Auto} single={Single}",
                req.Enabled, req.Auto, req.Single);
            return Results.Ok(r.SetPs(req));
        });

        app.MapPost("/api/tx/ps/advanced", (PsAdvancedSetRequest req, RadioService r) =>
        {
            if (req.HwPeak is double p && (p <= 0.0 || p > 2.0 || double.IsNaN(p)))
                return Results.BadRequest(new { error = "hwPeak must be in (0, 2]" });
            if (req.MoxDelaySec is double mox && (mox < 0.0 || mox > 10.0 || double.IsNaN(mox)))
                return Results.BadRequest(new { error = "moxDelaySec must be 0..10" });
            if (req.LoopDelaySec is double loop && (loop < 0.0 || loop > 100.0 || double.IsNaN(loop)))
                return Results.BadRequest(new { error = "loopDelaySec must be 0..100" });
            if (req.AmpDelayNs is double amp && (amp < 0.0 || amp > 25e6 || double.IsNaN(amp)))
                return Results.BadRequest(new { error = "ampDelayNs must be 0..25e6" });
            log.LogInformation("api.tx.ps.advanced");
            return Results.Ok(r.SetPsAdvanced(req));
        });

        // PS feedback antenna selector. Internal coupler vs External (Bypass).
        // On G2/MkII this flips ALEX_RX_ANTENNA_BYPASS in alex0 during xmit + PS
        // armed. WDSP cal/iqc are unaffected — same DDC0/DDC1 paired feed either
        // way; only the radio routes a different physical signal into DDC0.
        app.MapPost("/api/tx/ps/feedback-source",
            (PsFeedbackSourceSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.tx.ps.feedbackSource source={Source}", req.Source);
            return Results.Ok(r.SetPsFeedbackSource(req));
        });

        // Manual PS TX feedback attenuation — routes through DspPipelineService
        // (it owns both the P1 and P2 clients) to push the wire byte, then
        // persists + surfaces it via RadioService. Operator alternative to
        // AutoAttenuate for a fixed external-tap chain.
        app.MapPost("/api/tx/ps/feedback-attenuation",
            (PsFeedbackAttenuationSetRequest req, DspPipelineService pipe, RadioService r) =>
        {
            log.LogInformation("api.tx.ps.feedbackAttenuation db={Db}", req.Db);
            pipe.SetPsFeedbackAttenuationDb(req.Db);
            return Results.Ok(r.Snapshot());
        });

        // PS-Monitor — operator-facing toggle that swaps the TX panadapter source
        // from the predistorted-IQ analyzer to the PS-feedback (post-PA) analyzer.
        // Pure UI/source-routing flag; no WDSP setter, no wire-format change.
        // Default off; resets each session same as the PS master arm. See issue #121.
        app.MapPost("/api/tx/ps/monitor",
            (PsMonitorSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.tx.ps.monitor enabled={Enabled}", req.Enabled);
            return Results.Ok(r.SetPsMonitor(req));
        });

        app.MapPost("/api/tx/ps/reset", (DspPipelineService pipe) =>
        {
            log.LogInformation("api.tx.ps.reset");
            pipe.CurrentEngine?.ResetPs();
            return Results.Ok(new { reset = true });
        });

        // TX Monitor — preview-path toggle (issue #106 follow-up). Engages a
        // parallel demod of the post-CFIR TX IQ so the operator hears the
        // chain output at the actual TX bandwidth, with or without keying.
        // RX audio is suppressed in the broadcast while monitor is on. The
        // engine call lives in DspPipelineService.UpdateState so it lands
        // alongside the rest of the TX-side seam plumbing on the next tick.
        app.MapPost("/api/tx/monitor",
            (TxMonitorSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.tx.monitor enabled={Enabled}", req.Enabled);
            return Results.Ok(r.SetTxMonitor(req));
        });

        app.MapPost("/api/tx/ps/save", (PsSaveRequest req, DspPipelineService pipe) =>
        {
            if (string.IsNullOrWhiteSpace(req.Filename))
                return Results.BadRequest(new { error = "filename required" });
            log.LogInformation("api.tx.ps.save filename={Filename}", req.Filename);
            pipe.CurrentEngine?.SavePsCorrection(req.Filename);
            return Results.Ok(new { saved = req.Filename });
        });

        app.MapPost("/api/tx/ps/restore", (PsRestoreRequest req, DspPipelineService pipe) =>
        {
            if (string.IsNullOrWhiteSpace(req.Filename))
                return Results.BadRequest(new { error = "filename required" });
            log.LogInformation("api.tx.ps.restore filename={Filename}", req.Filename);
            pipe.CurrentEngine?.RestorePsCorrection(req.Filename);
            return Results.Ok(new { restored = req.Filename });
        });

        app.MapPost("/api/rx/nr", (NrSetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.rx.nr nr={Nr} anf={Anf} snb={Snb} notches={Notches} nb={Nb} thr={Thr:F2}",
                req.Nr.NrMode, req.Nr.AnfEnabled, req.Nr.SnbEnabled,
                req.Nr.NbpNotchesEnabled, req.Nr.NbMode, req.Nr.NbThreshold);
            if (!Enum.IsDefined(req.Nr.NrMode))
                return Results.BadRequest(new { error = $"unknown NrMode {req.Nr.NrMode}" });
            if (!Enum.IsDefined(req.Nr.NbMode))
                return Results.BadRequest(new { error = $"unknown NbMode {req.Nr.NbMode}" });
            return Results.Ok(r.SetNr(req.Nr));
        });

        // Per-popover PATCH endpoints for the right-click NR settings panels (issue
        // #79). Each merges nullable fields onto the persisted NrConfig so the
        // operator can edit one knob without resending the whole NR block. Skipping
        // fields (or sending null) is a no-op for that field.
        app.MapPost("/api/rx/nr2/post2", (Nr2Post2ConfigSetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.rx.nr2.post2 run={Run} factor={Factor} nlevel={Nlevel} rate={Rate} taper={Taper}",
                req.Post2Run, req.Post2Factor, req.Post2Nlevel, req.Post2Rate, req.Post2Taper);
            return Results.Ok(r.SetNr2Post2(req));
        });

        app.MapPost("/api/rx/nr2/core", (Nr2CoreConfigSetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.rx.nr2.core gainMethod={Gm} npeMethod={Npm} aeRun={Ae} trainT1={T1} trainT2={T2}",
                req.GainMethod, req.NpeMethod, req.AeRun, req.TrainT1, req.TrainT2);
            try
            {
                return Results.Ok(r.SetNr2Core(req));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/rx/nr4", (Nr4ConfigSetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.rx.nr4 reduction={Red} smoothing={Smo} whitening={Whi} noiseRescale={Nr} postThr={Pft} scaling={Sc} pos={Pos}",
                req.ReductionAmount, req.SmoothingFactor, req.WhiteningFactor,
                req.NoiseRescale, req.PostFilterThreshold, req.NoiseScalingType, req.Position);
            return Results.Ok(r.SetNr4(req));
        });

        // ---- NR3 (RNNoise) model management (issue #79 follow-up) ----
        // Zeus ships no model; NR3 stays hidden in the UI until the operator
        // installs an RNNoise weights file here — either a multipart upload or a
        // server-side fetch from a URL. Native availability + the installed
        // model name also ride StateDto, so the GET is a convenience for the
        // install panel.
        app.MapGet("/api/rx/nr3/model", (RadioService r) =>
        {
            var s = r.Snapshot();
            return Results.Ok(new { available = s.WdspNr3RnnrAvailable, modelName = s.Nr3ModelName });
        });

        app.MapPost("/api/rx/nr3/model", async (HttpRequest http, RadioService r) =>
        {
            if (!http.HasFormContentType || http.Form.Files.Count == 0)
                return Results.BadRequest(new { error = "expected multipart/form-data with a 'file' field" });
            var file = http.Form.Files["file"] ?? http.Form.Files[0];
            if (file.Length == 0)
                return Results.BadRequest(new { error = "uploaded model file is empty" });
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            try
            {
                var state = r.InstallNr3Model(ms.ToArray(), file.FileName);
                log.LogInformation("api.rx.nr3.model.install name=\"{Name}\" bytes={Bytes}", file.FileName, ms.Length);
                return Results.Ok(state);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Problem(ex.Message); }
        });

        app.MapPost("/api/rx/nr3/model/download", async (Nr3ModelDownloadRequest req, RadioService r, IHttpClientFactory httpFactory) =>
        {
            if (string.IsNullOrWhiteSpace(req.Url) ||
                !Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return Results.BadRequest(new { error = "url must be an absolute http(s) URL" });
            try
            {
                var client = httpFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(60);
                // Cap the buffered response so a wrong (huge) URL can't OOM the
                // host; the model store enforces its own 64 MiB ceiling too.
                client.MaxResponseContentBufferSize = 80L * 1024 * 1024;
                var bytes = await client.GetByteArrayAsync(uri);
                var name = Path.GetFileName(uri.LocalPath);
                if (string.IsNullOrWhiteSpace(name)) name = "model.rnnn";
                var state = r.InstallNr3Model(bytes, name);
                log.LogInformation("api.rx.nr3.model.download url=\"{Url}\" bytes={Bytes}", req.Url, bytes.Length);
                return Results.Ok(state);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (HttpRequestException ex) { return Results.BadRequest(new { error = $"download failed: {ex.Message}" }); }
            catch (TaskCanceledException) { return Results.BadRequest(new { error = "download timed out" }); }
        });

        app.MapDelete("/api/rx/nr3/model", (RadioService r) =>
        {
            log.LogInformation("api.rx.nr3.model.remove");
            return Results.Ok(r.RemoveNr3Model());
        });

        // Manual notch filters (MNF) — the client posts the full notch list on
        // every change (and on connect). GET returns the current set so a fresh
        // client (or a reconnect) can hydrate. Notches kill EMF/birdies in the
        // RX audio via WDSP's notch database.
        app.MapGet("/api/rx/notches", (RadioService r) => Results.Ok(r.Notches));
        app.MapPost("/api/rx/notches", (NotchListRequest req, RadioService r) =>
        {
            var notches = req?.Notches ?? Array.Empty<NotchDto>();
            log.LogInformation("api.rx.notches count={Count}", notches.Count);
            r.SetNotches(notches);
            return Results.Ok(r.Notches);
        });

        // CFC (Continuous Frequency Compressor) — issue #123. POSTs the full 10-band
        // CFC profile + master flags. Defaults to OFF so existing operators see no
        // behavior change. Validation is done by RadioService.SetCfc — bad shapes
        // throw ArgumentException which the framework returns as 400.
        app.MapPost("/api/tx/cfc", (CfcSetRequest req, RadioService r) =>
        {
            if (req?.Config is null)
                return Results.BadRequest(new { error = "Config required" });
            if (req.Config.Bands is null || req.Config.Bands.Length != 10)
                return Results.BadRequest(new { error = $"Bands must have exactly 10 entries; got {req.Config.Bands?.Length ?? 0}" });
            log.LogInformation(
                "api.tx.cfc enabled={Enabled} peq={Peq} preComp={Pre:F1}dB prePeq={PrePeq:F1}dB",
                req.Config.Enabled, req.Config.PostEqEnabled, req.Config.PreCompDb, req.Config.PrePeqDb);
            return Results.Ok(r.SetCfc(req));
        });

        app.MapGet("/api/tx/cfc/presets", (CfcPresetStore store) =>
            Results.Ok(new { presets = store.List() }));

        app.MapPut("/api/tx/cfc/presets/{name}", (string name, CfcSetRequest req, CfcPresetStore store) =>
        {
            if (!TryValidateCfcPresetName(name, out var cleanName, out var nameError))
                return Results.BadRequest(new { error = nameError });
            if (req?.Config is not { } config)
                return Results.BadRequest(new { error = "Config required" });
            if (!TryValidateCfcPresetConfig(config, out var configError))
                return Results.BadRequest(new { error = configError });

            var saved = store.Save(cleanName, config);
            log.LogInformation("api.tx.cfc.presets.save name={Name}", saved.Name);
            return Results.Ok(saved);
        });

        app.MapPost("/api/rx/zoom", (ZoomSetRequest req, RadioService r, KiwiSdrService kiwi) =>
        {
            log.LogInformation("api.rx.zoom level={Level}", req.Level);
            if (req.Level < SyntheticDspEngine.MinZoomLevel || req.Level > SyntheticDspEngine.MaxZoomLevel)
                return Results.BadRequest(new { error = $"zoom level must be in [{SyntheticDspEngine.MinZoomLevel},{SyntheticDspEngine.MaxZoomLevel}]; got {req.Level}" });
            // Zoom is a global setting; mirror it onto the Kiwi slice so its
            // waterfall span follows the slider too (it re-tunes the remote
            // KiwiSDR's SET zoom and re-emits frames at the new Hz/pixel).
            kiwi.SetZoom(req.Level);
            return Results.Ok(r.SetZoom(req.Level));
        });

        // Workspace UI zoom — scales the panel-grid cell pitch (see
        // StateDto.WorkspaceZoomPct). Distinct from /api/rx/zoom (spectral). The
        // server clamps Pct into range rather than 400-ing, so a slider step can
        // never get stuck on an out-of-range value; the echoed state carries the
        // accepted percent back for the optimistic-send reconcile.
        app.MapPost("/api/ui/workspace-zoom", (WorkspaceZoomSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.ui.workspaceZoom pct={Pct}", req.Pct);
            return Results.Ok(r.SetWorkspaceZoom(req.Pct));
        });

        // Band memory: last-used (hz, mode) per HF band. GET returns the full map so
        // the BandButtons UI can restore on load with one round-trip. PUT upserts one
        // entry — the web debounces writes so tuning doesn't hammer LiteDB.
        app.MapGet("/api/bands/memory", (BandMemoryStore store) => Results.Ok(store.GetAll()));

        app.MapPut("/api/bands/memory/{band}", (string band, BandMemorySetRequest req, BandMemoryStore store) =>
        {
            if (string.IsNullOrWhiteSpace(band))
                return Results.BadRequest(new { error = "band name required" });
            if (req.Hz <= 0)
                return Results.BadRequest(new { error = "hz must be positive" });
            store.Upsert(band, req.Hz, req.Mode);
            return Results.Ok(new BandMemoryDto(band, req.Hz, req.Mode));
        });

        // Regional band plan (issue #65). Shipped JSON under BandPlans/ defines
        // baseline regions (IARU R1/R2/R3) and country overrides (EI, G, US FCC
        // General/Extra). Operator can edit per-region segments (PUT) and reset
        // back to shipped defaults (DELETE). Active region is persisted in
        // BandPrefsStore; switches fire BandPlanChanged (0x1B) so other tabs
        // refetch.
        app.MapGet("/api/bands/regions", (BandPlanStore store) =>
            Results.Ok(store.Regions));

        app.MapGet("/api/bands/plan", (string? region, BandPlanService svc) =>
        {
            var regionId = region ?? svc.CurrentRegion.Id;
            var plan = svc.ResolvePlan(regionId);
            return Results.Ok(new BandPlanDto(regionId, plan));
        });

        app.MapGet("/api/bands/current", (BandPlanService svc) =>
            Results.Ok(new
            {
                regionId = svc.CurrentRegion.Id,
                region = svc.CurrentRegion,
                segments = svc.CurrentPlan,
                txGuardIgnore = svc.TxGuardIgnore,
            }));

        app.MapPost("/api/bands/current", (BandPlanCurrentSetRequest req, BandPlanService svc) =>
        {
            svc.SetRegion(req.RegionId);
            return Results.Ok(new { regionId = svc.CurrentRegion.Id });
        });

        app.MapPut("/api/bands/plan", (BandPlanSaveRequest req, BandPlanService svc) =>
        {
            try
            {
                svc.SavePlan(req.RegionId, req.Segments);
                return Results.Ok(new { regionId = req.RegionId, saved = req.Segments.Count });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapDelete("/api/bands/plan/{regionId}", (string regionId, BandPlanService svc) =>
        {
            svc.ResetPlan(regionId);
            return Results.Ok(new { regionId, reset = true });
        });

        app.MapPost("/api/bands/guard", (BandGuardSetRequest req, BandPlanService svc) =>
        {
            svc.SetTxGuardIgnore(req.Ignore);
            return Results.Ok(new { txGuardIgnore = req.Ignore });
        });

        // PA settings — per-band gain/OC masks + globals. Single PUT replaces the
        // whole snapshot because the UI edits rows as a table; incremental PATCHing
        // would deadlock with the RadioService recompute subscription fired on Save.
        // The GET uses the effective board's defaults to fill missing rows so the
        // panel opens with model-appropriate seeds on first load. Optional
        // ?board= override lets the radio-selector preview defaults for a board
        // other than the effective one without persisting the preference — the
        // operator's saved per-band calibration still wins over the preview.
        app.MapGet("/api/pa-settings", (string? board, PaSettingsStore store, RadioService radio) =>
        {
            var preview = ParseBoardKind(board);
            var effective = preview ?? radio.EffectiveBoardKind;
            return Results.Ok(store.GetAll(effective));
        });

        // Pure board defaults — "Reset to defaults" button in the PA panel. Skips
        // the pa_bands collection entirely and returns piHPSDR/Thetis seed values
        // for the requested board (or the effective board if none specified).
        app.MapGet("/api/pa-settings/defaults", (string? board, PaSettingsStore store, RadioService radio) =>
        {
            var preview = ParseBoardKind(board);
            var target = preview ?? radio.EffectiveBoardKind;
            return Results.Ok(store.GetDefaults(target));
        });

        app.MapPut("/api/pa-settings", (PaSettingsSetRequest req, PaSettingsStore store, RadioService radio) =>
        {
            if (req.Global is null || req.Bands is null)
                return Results.BadRequest(new { error = "global and bands required" });
            if (req.Global.PaMaxPowerWatts < 0)
                return Results.BadRequest(new { error = "paMaxPowerWatts must be >= 0" });
            store.Save(new PaSettingsDto(req.Global, req.Bands));
            return Results.Ok(store.GetAll(radio.EffectiveBoardKind));
        });

        // Panadapter background settings — Mode + Fit are JSON; image bytes are
        // kept on a separate endpoint so the lightweight GET that the frontend
        // hits on every load doesn't drag the picture across the wire. The image
        // itself rides as raw bytes (multipart on PUT, application/<mime> on GET).
        // Persisted in zeus-prefs.db so the setting follows the operator across
        // browsers / devices instead of living in per-origin localStorage.
        app.MapGet("/api/display-settings", (DisplaySettingsStore store) => Results.Ok(store.Get()));

        app.MapPut("/api/display-settings", (DisplaySettingsSetRequest req, DisplaySettingsStore store, DspPipelineService dsp) =>
        {
            if (string.IsNullOrWhiteSpace(req.Mode) || string.IsNullOrWhiteSpace(req.Fit))
                return Results.BadRequest(new { error = "mode and fit required" });
            store.SaveMode(req.Mode, req.Fit, req.RxTraceColor,
                req.DbMin, req.DbMax, req.TxDbMin, req.TxDbMax,
                req.WfDbMin, req.WfDbMax, req.WfTxDbMin, req.WfTxDbMax,
                req.TxDisplayCalOffsetDb, req.TxDisplayFftSize,
                req.TxDisplayWindow, req.TxDisplayAvgTauMs);
            var saved = store.Get();
            // Push the (validated, merged) TX display config to the running
            // engine so the change is live without a reconnect. Display-only.
            dsp.ApplyTxDisplaySettings(saved);
            return Results.Ok(saved);
        });

        // Signal Intelligence weak-signal display policy. The frontend owns the
        // live CFAR/noise-floor math; the backend persists the active profile
        // and tuning so multiple clients and diagnostics agree on the policy.
        app.MapGet("/api/dsp/display-intelligence", (DisplayIntelligenceSettingsStore store) =>
            Results.Ok(store.Get()));

        app.MapPut("/api/dsp/display-intelligence", (
            DisplayIntelligenceSettingsDto req,
            DisplayIntelligenceSettingsStore store) =>
        {
            if (!TryValidateDisplayIntelligenceSettings(req, out var err))
                return Results.BadRequest(new { error = err });
            return Results.Ok(store.Save(req));
        });

        app.MapGet("/api/display-settings/image", (DisplaySettingsStore store) =>
        {
            var img = store.GetImage();
            if (img is null) return Results.NotFound();
            return Results.File(img.Value.Bytes, img.Value.Mime);
        });

        // Multipart upload — single field "file", any image/* mime type. Capped
        // at 8 MB so a stray giant TIFF can't fill the prefs DB.
        app.MapPut("/api/display-settings/image", async (HttpContext ctx, DisplaySettingsStore store) =>
        {
            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required" });
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files["file"] ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "file field required" });
            const long MaxBytes = 8 * 1024 * 1024;
            if (file.Length > MaxBytes)
                return Results.BadRequest(new { error = $"file too large (max {MaxBytes} bytes)" });
            var mime = string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType;
            if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "image/* content-type required" });
            using var ms = new MemoryStream(capacity: (int)file.Length);
            await file.CopyToAsync(ms);
            store.SaveImage(ms.ToArray(), mime);
            return Results.Ok(store.Get());
        });

        app.MapDelete("/api/display-settings/image", (DisplaySettingsStore store) =>
        {
            store.DeleteImage();
            return Results.Ok(store.Get());
        });

        // Classic-layout bottom-row pin state — Logbook + TX Stage Meters.
        // GET returns current state; PUT replaces both flags atomically.
        // Persisted in zeus-prefs.db so the layout choice follows the
        // operator across browsers / devices.
        app.MapGet("/api/bottom-pin", (BottomPinStore store) => Results.Ok(store.Get()));

        app.MapPut("/api/bottom-pin", (BottomPinSetRequest req, BottomPinStore store) =>
        {
            store.Save(req.Logbook, req.TxMeters);
            return Results.Ok(store.Get());
        });

        // Vertical split between the panadapter and the waterfall in the
        // Hero panel. PanPercent (10..90) is the panadapter share; the
        // waterfall fills the remainder. Persisted in zeus-prefs.db so the
        // choice follows the operator across browsers / devices, same
        // pattern as /api/bottom-pin. Frontend reads on mount and PUTs
        // debounced on drag-end.
        app.MapGet("/api/pan-wf-split", (PanWfSplitStore store) => Results.Ok(store.Get()));

        app.MapPut("/api/pan-wf-split", (PanWfSplitSetRequest req, PanWfSplitStore store) =>
        {
            var saved = store.Save(req.PanPercent);
            return Results.Ok(saved);
        });

        // Toolbar Mode/Band/Step favorite-slot pins + the live tuning step
        // (StepHz). POST patches only the fields supplied — null fields leave
        // the stored value untouched — so a step-change doesn't reset the
        // favorite pins. Persisted in zeus-prefs.db so the tuning step and
        // favorites survive a backend restart, fixing the Photino desktop
        // per-launch-random-port localStorage reset.
        app.MapGet("/api/toolbar-settings", (ToolbarSettingsStore store) => Results.Ok(store.Get()));

        app.MapPost("/api/toolbar-settings", (ToolbarSettingsSetRequest req, ToolbarSettingsStore store) =>
        {
            store.Save(req.Mode, req.Band, req.Step, req.StepHz);
            return Results.Ok(store.Get());
        });

        // Inline NR settings accordion disclosure state (NR1 / NR2 / NR4).
        // PUT writes all three flags atomically. Persisted in zeus-prefs.db
        // so the chevron-open preference follows the operator across
        // browsers / devices, same pattern as /api/bottom-pin.
        app.MapGet("/api/nr-ui-prefs", (NrUiPrefsStore store) => Results.Ok(store.Get()));

        app.MapPut("/api/nr-ui-prefs", (NrUiPrefsSetRequest req, NrUiPrefsStore store) =>
        {
            store.Set(req.Nr1Expanded, req.Nr2Expanded, req.Nr4Expanded);
            return Results.Ok(store.Get());
        });

        // Operator UI theme ("dark" | "light") + per-CSS-variable colour
        // overrides. PUT replaces both atomically — overrides is a full snapshot,
        // not a partial patch, because the picker tracks all rows together.
        // Persisted in zeus-prefs.db so the look-and-feel follows the operator
        // across browsers / devices, same pattern as /api/nr-ui-prefs.
        app.MapGet("/api/theme-settings", (ThemeSettingsStore store) => Results.Ok(store.Get()));

        app.MapPut("/api/theme-settings", (ThemeSettingsSetRequest req, ThemeSettingsStore store) =>
        {
            store.Set(req.Theme, req.Overrides);
            return Results.Ok(store.Get());
        });

        // Radio selection — operator preference seeding, with discovery as the
        // tiebreaker. Preferred=="Auto" clears only the preferred board while
        // preserving sibling hardware options stored on the same row.
        // Effective = Connected when connected (which
        // may itself be overridden if OverrideDetection is true), Preferred when
        // not connected, Unknown otherwise.
        app.MapGet("/api/radio/selection", (PreferredRadioStore prefs, RadioService radio) =>
        {
            var preferred = prefs.Get();
            var overrideDetection = prefs.GetOverrideDetection();
            return Results.Ok(new RadioSelectionDto(
                Preferred: preferred?.ToString() ?? "Auto",
                Connected: radio.ConnectedBoardKind.ToString(),
                Effective: radio.EffectiveBoardKind.ToString(),
                OverrideDetection: overrideDetection));
        });

        app.MapPut("/api/radio/selection", (RadioSelectionSetRequest req, PreferredRadioStore prefs, RadioService radio) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Preferred))
                return Results.BadRequest(new { error = "preferred required" });

            HpsdrBoardKind? chosen;
            if (string.Equals(req.Preferred, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                chosen = null;
            }
            else if (Enum.TryParse<HpsdrBoardKind>(req.Preferred, ignoreCase: true, out var kind)
                     && kind != HpsdrBoardKind.Unknown)
            {
                chosen = kind;
            }
            else
            {
                return Results.BadRequest(new { error = $"unknown board '{req.Preferred}'" });
            }

            prefs.Set(chosen, req.OverrideDetection);
            var overrideDetection = prefs.GetOverrideDetection();
            return Results.Ok(new RadioSelectionDto(
                Preferred: chosen?.ToString() ?? "Auto",
                Connected: radio.ConnectedBoardKind.ToString(),
                Effective: radio.EffectiveBoardKind.ToString(),
                OverrideDetection: overrideDetection));
        });

        // Board capability fingerprint for the effective board — what the
        // web UI gates feature panels on (volts/amps meter, audio-amp
        // controls, RX2 attenuator mode, Path Illustrator visibility, etc.).
        // Read once at connect; static facts that depend only on the board
        // class. Cross-references docs/references/protocol-1/thetis-board-matrix.md.
        app.MapGet("/api/radio/capabilities", (RadioService radio) =>
        {
            return Results.Ok(BoardCapabilitiesTable.For(radio.EffectiveBoardKind, radio.EffectiveOrionMkIIVariant));
        });

        // Live hardware diagnostic snapshot. Read-only by design: it exposes
        // discovery/state, Thetis-derived static capabilities, and decoded
        // P1/P2 wire telemetry so new hardware fields can be mapped before
        // they become operator-configurable controls.
        app.MapGet("/api/radio/diagnostics", (DiagnosticsProviderRegistry diagnostics) =>
        {
            return Results.Ok(DiagnosticsSnapshot(diagnostics, "hardware"));
        });

        app.MapPost("/api/radio/diagnostics/map/reset", (HardwareDiagnosticsService diag, DiagnosticsProviderRegistry diagnostics) =>
        {
            diag.ResetMapping();
            return Results.Ok(DiagnosticsSnapshot(diagnostics, "hardware"));
        });

        app.MapPost("/api/radio/diagnostics/map/marker", (HardwareDiagnosticsMarkerRequest req, HardwareDiagnosticsService diag, DiagnosticsProviderRegistry diagnostics) =>
        {
            diag.AddMappingMarker(req.Label, req.Notes);
            return Results.Ok(DiagnosticsSnapshot(diagnostics, "hardware"));
        });

        app.MapPost("/api/radio/diagnostics/dsp-scene", (FrontendDspSceneDiagnosticsRequest req, FrontendDspSceneDiagnosticsService scene, DiagnosticsProviderRegistry diagnostics) =>
        {
            scene.Update(req);
            return Results.Ok(DiagnosticsSnapshot(diagnostics, "frontend-dsp-scene"));
        });

        app.MapGet("/api/radio/diagnostics/dsp-scene", (DiagnosticsProviderRegistry diagnostics) =>
        {
            return Results.Ok(DiagnosticsSnapshot(diagnostics, "frontend-dsp-scene"));
        });

        app.MapPost("/api/radio/diagnostics/audio-playback", (FrontendAudioPlaybackDiagnosticsRequest req, FrontendAudioPlaybackDiagnosticsService playback, DiagnosticsProviderRegistry diagnostics) =>
        {
            playback.Update(req);
            return Results.Ok(DiagnosticsSnapshot(diagnostics, "frontend-audio-playback"));
        });

        app.MapGet("/api/radio/diagnostics/audio-playback", (DiagnosticsProviderRegistry diagnostics) =>
        {
            return Results.Ok(DiagnosticsSnapshot(diagnostics, "frontend-audio-playback"));
        });

        app.MapGet("/api/dsp/nr-condition", (FrontendDspSceneDiagnosticsService scene, DspPipelineService dsp, RadioService radio) =>
        {
            var state = radio.Snapshot();
            return Results.Ok(scene.SmartNrCondition(
                dsp.SnapshotNrRuntime(),
                BuildSmartNrRxChainRuntime(state, radio.GetAdcProtectionStatus())));
        });

        app.MapGet("/api/dsp/live-diagnostics", (DiagnosticsProviderRegistry diagnostics) =>
        {
            return Results.Ok(DiagnosticsSnapshot(diagnostics, "dsp-live"));
        });

        app.MapGet("/api/dsp/external-engine-candidates", () =>
        {
            return Results.Ok(DspExternalEngineCandidateCatalog.All());
        });

        app.MapGet("/api/dsp/benchmark-plan", () =>
        {
            return Results.Ok(DspBenchmarkPlanCatalog.Build());
        });

        app.MapGet("/api/dsp/benchmark-metric-catalog", () =>
        {
            return Results.Ok(DspBenchmarkPlanCatalog.BuildMetricCatalog());
        });

        app.MapGet("/api/dsp/benchmark-capture-manifest", (FrontendDspSceneDiagnosticsService scene, DspPipelineService dsp, RadioService radio) =>
        {
            var state = radio.Snapshot();
            var condition = scene.SmartNrCondition(
                dsp.SnapshotNrRuntime(),
                BuildSmartNrRxChainRuntime(state, radio.GetAdcProtectionStatus()));
            var live = DspLiveDiagnosticsService.Build(condition, dsp.SnapshotLiveRuntimeEvidence(), state);
            return Results.Ok(DspBenchmarkCaptureManifestService.Build(live, DspBenchmarkPlanCatalog.Build()));
        });

        app.MapGet("/api/dsp/modernization-snapshot", (DiagnosticsProviderRegistry diagnostics) =>
        {
            return Results.Ok(DiagnosticsSnapshot(diagnostics, "dsp-modernization"));
        });

        app.MapGet("/api/tx/external-ptt", (ExternalPttService externalPtt) =>
        {
            return Results.Ok(externalPtt.Snapshot());
        });

        app.MapGet("/api/cw/hardware-keying", (HardwareDiagnosticsService diag, ExternalPttService externalPtt) =>
        {
            return Results.Ok(diag.KeyingSnapshot(externalPtt.Snapshot()));
        });

        // Hardware-PTT-IN → MOX enable gate (per-install, default OFF). GET
        // returns the live lamp level + persisted enable flag + hang time; PUT
        // flips the gate. A persisted ON flag only ARMS the gate — MOX still
        // requires a physical footswitch edge (edge-triggered, never on
        // connect/restart). The lamp tracks the footswitch regardless of the
        // gate. Ungated per board — every board exposes a PTT-IN line.
        app.MapGet("/api/radio/ptt-status", (ExternalPttService externalPtt) =>
        {
            return Results.Ok(externalPtt.Snapshot());
        });

        app.MapPut("/api/radio/ptt-status", (PttEnableSetRequest req, PttSettingsStore store, ExternalPttService externalPtt) =>
        {
            store.Set(req.Enabled);
            return Results.Ok(externalPtt.Snapshot());
        });

        // ANAN G2 / G2-Ultra hardware front-panel bridge settings (per-install).
        // GET returns the stored enable/device/baud plus live bridge status
        // (connected + active device/baud + panel type). PUT updates the stored
        // settings and reconnects the bridge — no server restart. DevicePath ""
        // clears the override (auto-detect); Baud 0 = auto. On the G2 Pi the
        // defaults auto-detect the panel; on a Windows/macOS host point it at the
        // COM port here. Ungated — the panel is a host serial device.
        app.MapGet("/api/radio/front-panel", (Zeus.Server.FrontPanel.G2FrontPanelService svc) =>
        {
            return Results.Ok(svc.Snapshot());
        });

        app.MapPut("/api/radio/front-panel",
            (G2PanelSettingsSetRequest req,
             Zeus.Server.FrontPanel.G2PanelSettingsStore store,
             Zeus.Server.FrontPanel.G2FrontPanelService svc) =>
        {
            if (req.Baud is int b && b is not (0 or 9600 or 115200))
                return Results.BadRequest(new { error = $"unsupported baud {b} (use 0=auto, 9600, or 115200)" });
            var cur = store.Get();
            store.Set(
                enabled: req.Enabled ?? cur.Enabled,
                devicePath: req.DevicePath ?? cur.DevicePath,
                baud: req.Baud ?? cur.Baud);
            return Results.Ok(svc.Snapshot());
        });

        // Global (per-radio) TX-audio source (external-audio-jacks re-port). GET
        // surfaces the per-board capability gates + the RESOLVED (board-clamped)
        // source so the single-select picker shows only the jacks the connected
        // board offers and hydrates from what the server is actually pushing.
        // Always 200 — a board with neither codec nor HL2 mic reports both gates
        // false and the panel shows nothing.
        app.MapGet("/api/radio/audio", (RadioService radio, AudioSettingsStore store) =>
        {
            var caps = BoardCapabilitiesTable.For(radio.EffectiveBoardKind, radio.EffectiveOrionMkIIVariant);
            var resolved = RadioService.ClampAudioSource(store.Get(), caps);
            return Results.Ok(new AudioFrontEndDto(
                HasOnboardCodec: caps.HasOnboardCodec,
                HermesLite2MicFrontEnd: caps.HermesLite2MicFrontEnd,
                HasRadioLineIn: caps.HasRadioLineIn,
                HasBalancedXlr: caps.HasBalancedXlr,
                HasMicBias: caps.HasMicBias,
                Source: resolved.Source,
                MicBoost: resolved.MicBoost,
                MicBias: resolved.MicBias,
                LineInGain: resolved.LineInGain));
        });

        // PUT the whole global TX-audio source. Capability-gated: 409 when the
        // connected board has no audio front-end at all (neither codec nor HL2
        // mic), so a non-audio board can never be handed audio bytes. The
        // requested Source is CLAMPED against the board's capabilities (an
        // unsupported jack → Host) before persisting, so the store never holds a
        // source the wire can't emit on this board. LineInGain is clamped 0..31.
        // The save fires AudioSettingsStore.Changed -> RadioService.PushAudioFrontEnd,
        // which re-clamps + pushes server-authoritatively to the live client
        // (P1 SetAudioFrontEnd / P2 TxSpecific 50/51) and mirrors the resolved
        // source into StateDto — never via the frontend, so no clobber-on-connect.
        app.MapPut("/api/radio/audio", (AudioFrontEndSetRequest req, RadioService radio, AudioSettingsStore store) =>
        {
            if (req is null)
                return Results.BadRequest(new { error = "body required" });

            var caps = BoardCapabilitiesTable.For(radio.EffectiveBoardKind, radio.EffectiveOrionMkIIVariant);
            bool audioCapable = caps.HasOnboardCodec || caps.HermesLite2MicFrontEnd;
            if (!audioCapable)
                return Results.Conflict(new { error = $"board {radio.EffectiveBoardKind} has no audio front-end" });

            var requested = new AudioSourceSelection(
                Source: req.Source,
                MicBoost: req.MicBoost,
                MicBias: req.MicBias,
                LineInGain: (byte)Math.Clamp(req.LineInGain, 0, 31));
            // Clamp to the board before persisting — a board that lacks the
            // requested jack stores Host, never the illegal source.
            store.Set(RadioService.ClampAudioSource(requested, caps));

            var resolved = RadioService.ClampAudioSource(store.Get(), caps);
            return Results.Ok(new AudioFrontEndDto(
                caps.HasOnboardCodec,
                caps.HermesLite2MicFrontEnd,
                caps.HasRadioLineIn,
                caps.HasBalancedXlr,
                caps.HasMicBias,
                resolved.Source,
                resolved.MicBoost,
                resolved.MicBias,
                resolved.LineInGain));
        });

        // Radio-side speaker output (codec-equipped radios, P1 + P2). GET reports
        // the persisted opt-in plus whether it's currently effective for the
        // connected board (any codec radio; HL2 has no stream codec and is
        // excluded). The frontend refetches this on connect to hydrate the toggle
        // without touching the StateDto wire format. Issue #1122.
        app.MapGet("/api/radio/speaker-output", (RadioSpeakerSettingsStore store, RadioSpeakerAudioSink sink) =>
            Results.Ok(new RadioSpeakerOutputDto(
                Enabled: store.Enabled,
                Available: sink.AvailableForConnectedBoard())));

        // PUT the opt-in. Persisted globally; the store's Changed event clears the
        // RX-audio ring when turned off so a later RX never replays a stale tail.
        // The sink self-gates per frame (board + MOX + mono/48k), so the toggle is
        // safe to flip at any time, including mid-session.
        app.MapPut("/api/radio/speaker-output", (RadioSpeakerOutputSetRequest req, RadioSpeakerSettingsStore store, RadioSpeakerAudioSink sink) =>
        {
            if (req is null)
                return Results.BadRequest(new { error = "body required" });

            store.Set(req.Enabled);
            return Results.Ok(new RadioSpeakerOutputDto(
                Enabled: store.Enabled,
                Available: sink.AvailableForConnectedBoard()));
        });

        app.MapGet("/api/radio/power-calibration", (HardwareDiagnosticsService diag) =>
        {
            return Results.Ok(diag.PowerCalibrationSnapshot());
        });

        app.MapGet("/api/radio/supply-alarms", (HardwareDiagnosticsService diag) =>
        {
            return Results.Ok(diag.SupplyAlarmsSnapshot());
        });

        app.MapGet("/api/radio/pa-thermal", (TxMetersService txMeters) =>
        {
            return Results.Ok(txMeters.PaThermalSnapshot());
        });

        app.MapGet("/api/radio/g2-sensors", (HardwareDiagnosticsService diag) =>
        {
            return Results.Ok(diag.G2SensorMappingSnapshot());
        });

        app.MapGet("/api/radio/network-profile", (HardwareDiagnosticsService diag) =>
        {
            return Results.Ok(diag.NetworkProfileSnapshot());
        });

        app.MapGet("/api/radio/user-io/labels", (HardwareDiagnosticsService diag) =>
        {
            return Results.Ok(diag.UserIoLabelsSnapshot());
        });

        app.MapGet("/api/radio/user-io/actions", (HardwareDiagnosticsService diag) =>
        {
            return Results.Ok(diag.UserIoActionsSnapshot());
        });

        app.MapGet("/api/radio/dig-in", (HardwareDiagnosticsService diag) =>
        {
            return Results.Ok(diag.DigInSnapshot());
        });

        // Operator-selected variant for the 0x0A wire-byte alias family
        // (issue #218). Routes calibration / PA gain / rated-watts dispatch
        // when the connected board is OrionMkII. Default G2 preserves
        // pre-#218 behaviour; operators with a non-G2 board select the
        // variant once and the dispatch picks up the right bridge constants.
        app.MapGet("/api/radio/variant", (PreferredRadioStore prefs) =>
        {
            return Results.Ok(new { Variant = prefs.GetOrionMkIIVariant().ToString() });
        });

        app.MapPut("/api/radio/variant", (RadioVariantSetRequest req, PreferredRadioStore prefs) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Variant))
                return Results.BadRequest(new { error = "variant required" });

            if (!Enum.TryParse<OrionMkIIVariant>(req.Variant, ignoreCase: true, out var variant))
                return Results.BadRequest(new { error = $"unknown variant '{req.Variant}'" });

            prefs.SetOrionMkIIVariant(variant);
            return Results.Ok(new { Variant = variant.ToString() });
        });

        // HL2-specific optional toggles (issue #279). Currently a single
        // field — Band Volts PWM enable — but the response is an object so
        // future mi0bot HL2 toggles slot in without breaking the contract.
        // GET always returns 200 with the persisted value regardless of the
        // connected board; the UI gates visibility on
        // BoardCapabilities.HasHl2OptionalToggles (HL2 only) so non-HL2
        // operators never see the controls. PUT writes the persisted value
        // AND pushes through to any live Protocol-1 client so the bit lands
        // on the wire immediately. Honoured on HL2 only on the wire.
        app.MapGet("/api/radio/hl2-options", (RadioService radio) =>
        {
            return Results.Ok(new Hl2OptionsDto(BandVolts: radio.GetHl2BandVolts()));
        });

        app.MapPut("/api/radio/hl2-options", (Hl2OptionsSetRequest req, RadioService radio) =>
        {
            if (req is null)
                return Results.BadRequest(new { error = "body required" });

            var effective = radio.SetHl2BandVolts(req.BandVolts);
            return Results.Ok(new Hl2OptionsDto(BandVolts: effective));
        });

        // HL2 user GPIO (external-port parity audit — re-port of external-ports
        // plan Phase 5). The 4-bit user_dig_out mask → 0x0a/wire-0x14 frame
        // C3[3:0] → MCP23008 on the HL2 IO connector. GET always returns 200; the
        // Supported flag is the board's HasHl2UserGpio capability (HL2 only) and
        // the frontend gates the User-GPIO card on it. PUT 409s on a board without
        // the capability so a non-HL2 board can never be handed a GPIO mask. The
        // save is server-authoritative: SetHl2GpioMask persists + fires
        // Changed → RadioService.PushHl2Gpio, which re-pushes to the live client —
        // never via the frontend, so no clobber-on-connect.
        app.MapGet("/api/radio/hl2-gpio", (RadioService radio) =>
        {
            var caps = BoardCapabilitiesTable.For(radio.EffectiveBoardKind, radio.EffectiveOrionMkIIVariant);
            return Results.Ok(new Hl2GpioDto(
                Supported: caps.HasHl2UserGpio,
                Bits: caps.HasHl2UserGpio ? radio.GetHl2GpioMask() : 0));
        });

        app.MapPut("/api/radio/hl2-gpio", (Hl2GpioSetRequest req, RadioService radio) =>
        {
            if (req is null)
                return Results.BadRequest(new { error = "body required" });

            var caps = BoardCapabilitiesTable.For(radio.EffectiveBoardKind, radio.EffectiveOrionMkIIVariant);
            if (!caps.HasHl2UserGpio)
                return Results.Conflict(new { error = $"board {radio.EffectiveBoardKind} has no user GPIO" });

            radio.SetHl2GpioMask((byte)(req.Bits & 0x0F));
            return Results.Ok(new Hl2GpioDto(Supported: true, Bits: radio.GetHl2GpioMask()));
        });

        // External antenna ports (external-ports plan — antenna slice, #804).
        // GET returns the per-band TX/RX antenna + RX-aux selection plus the
        // board-capability gates the frontend renders the right selectors from.
        // Antenna state is server-authoritative and NEVER enters StateDto.
        app.MapGet("/api/radio/antenna", (RadioService radio, AntennaSettingsStore store) =>
        {
            var caps = BoardCapabilitiesTable.For(radio.EffectiveBoardKind, radio.EffectiveOrionMkIIVariant);
            var bands = store.GetAll()
                .Select(b => new AntennaBandDto(b.Band, b.TxAnt.ToString(), b.RxAnt.ToString(), b.RxAux.ToString()))
                .ToArray();
            return Results.Ok(new AntennaSettingsDto(
                HasTxAntennaRelays: caps.HasTxAntennaRelays,
                HasRxAntennaRelays: caps.HasRxAntennaRelays,
                Bands: bands,
                AvailableRxAux: AvailableRxAux(caps.RxAuxInputs)));
        });

        // PUT one band's antenna selection. Capability-gated: 400 on a malformed
        // body / unknown band / unparseable antenna; 409 when the request asks
        // for a relay the connected board does not have (a non-ANT1 TX on a board
        // without TX relays, a non-ANT1 RX on a board without RX relays, or an
        // aux the board does not expose). ANT1 / None are always accepted (the
        // hardwired default on every board). The save fires Changed →
        // RadioService.RecomputePaAndPush, which pushes server-authoritatively to
        // the live client (P1 SetAntennaRx / P2 SetAntennas) — never via the
        // frontend, so no clobber-on-connect. The wire layer defers a mid-key
        // relay change to the unkey edge; PS owns the K36/BYPASS relay while armed
        // regardless of an aux=BYPASS pick (PS-K36 firewall).
        app.MapPut("/api/radio/antenna", (AntennaSetRequest req, RadioService radio, AntennaSettingsStore store) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Band))
                return Results.BadRequest(new { error = "band required" });
            if (!BandUtils.HfBands.Contains(req.Band))
                return Results.BadRequest(new { error = $"unknown band '{req.Band}'" });
            if (!Enum.TryParse<HpsdrAntenna>(req.TxAnt, ignoreCase: true, out var txAnt))
                return Results.BadRequest(new { error = $"unknown txAnt '{req.TxAnt}'" });
            if (!Enum.TryParse<HpsdrAntenna>(req.RxAnt, ignoreCase: true, out var rxAnt))
                return Results.BadRequest(new { error = $"unknown rxAnt '{req.RxAnt}'" });
            var rxAuxStr = string.IsNullOrWhiteSpace(req.RxAux) ? "None" : req.RxAux;
            if (!Enum.TryParse<RxAuxInputSel>(rxAuxStr, ignoreCase: true, out var rxAux))
                return Results.BadRequest(new { error = $"unknown rxAux '{req.RxAux}'" });

            var caps = BoardCapabilitiesTable.For(radio.EffectiveBoardKind, radio.EffectiveOrionMkIIVariant);
            if (txAnt != HpsdrAntenna.Ant1 && !caps.HasTxAntennaRelays)
                return Results.Conflict(new { error = $"board {radio.EffectiveBoardKind} has no TX antenna relays; only Ant1 is valid" });
            if (rxAnt != HpsdrAntenna.Ant1 && !caps.HasRxAntennaRelays)
                return Results.Conflict(new { error = $"board {radio.EffectiveBoardKind} has no RX antenna relays; only Ant1 is valid" });
            if (rxAux != RxAuxInputSel.None && !RxAuxSupported(rxAux, caps.RxAuxInputs))
                return Results.Conflict(new { error = $"board {radio.EffectiveBoardKind} does not expose RX-aux input {rxAux}" });

            store.SetBand(req.Band, txAnt, rxAnt, rxAux);

            var bands = store.GetAll()
                .Select(b => new AntennaBandDto(b.Band, b.TxAnt.ToString(), b.RxAnt.ToString(), b.RxAux.ToString()))
                .ToArray();
            return Results.Ok(new AntennaSettingsDto(
                caps.HasTxAntennaRelays, caps.HasRxAntennaRelays, bands,
                AvailableRxAux(caps.RxAuxInputs)));
        });

        // ANAN-G2 / Saturn-class ADC options. Dither/random write Protocol-2
        // CmdRx bytes 5/6 when the connected/effective board advertises
        // SupportsG2AdcOptions; non-G2 boards still persist the preference but
        // report Supported=false and receive zeroed wire bits.
        app.MapGet("/api/radio/g2-options", (RadioService radio) =>
        {
            return Results.Ok(radio.GetG2Options());
        });

        app.MapPut("/api/radio/g2-options", (G2OptionsSetRequest req, RadioService radio) =>
        {
            if (req is null)
                return Results.BadRequest(new { error = "body required" });
            if (req.Rx1AttenuatorDb is < 0 or > 31)
                return Results.BadRequest(new { error = "rx1AttenuatorDb must be in 0..31 dB." });

            return Results.Ok(radio.SetG2Options(req));
        });

        // Per-radio frequency calibration (issue #325). GET returns the
        // persisted correction factor + its ppm representation. POST
        // /calibrate runs the one-button auto-cal procedure (snapshot
        // state, tune WWV 10 MHz, find peak, apply factor, restore).
        // POST /reset clears the factor back to 1.0.
        app.MapGet("/api/radio/frequency-calibration", (RadioService radio) =>
        {
            double factor = radio.GetFrequencyCorrectionFactor();
            double ppm = (factor - 1.0) * 1e6;
            double offsetAt10MHz = ppm * 10.0; // Hz offset at 10 MHz
            return Results.Ok(new
            {
                factor,
                ppm,
                offsetHzAt10MHz = offsetAt10MHz,
            });
        });

        app.MapPost("/api/radio/frequency-calibration/calibrate", async (
            FrequencyCalibrationService cal, HttpContext ctx) =>
        {
            log.LogInformation("api.freqcal.calibrate begin");
            var result = await cal.CalibrateAsync(ct: ctx.RequestAborted).ConfigureAwait(false);
            log.LogInformation("api.freqcal.calibrate result={Outcome} offset={Off} factor={Factor}",
                result.Outcome, result.OffsetHz, result.AppliedFactor);
            return Results.Ok(result);
        });

        app.MapPost("/api/radio/frequency-calibration/reset", (FrequencyCalibrationService cal) =>
        {
            log.LogInformation("api.freqcal.reset");
            cal.Reset();
            return Results.Ok(new { factor = 1.0, ppm = 0.0, offsetHzAt10MHz = 0.0 });
        });

        // UI layout: flexlayout-react panel arrangement, persisted per operator profile.
        // GET returns 404 when no layout has been saved yet (frontend falls back to
        // DEFAULT_LAYOUT). PUT replaces; DELETE resets to default on next load.
        app.MapGet("/api/ui/layout", (LayoutStore store) =>
        {
            var layout = store.Get();
            return layout is null ? Results.NotFound() : Results.Ok(layout);
        });

        app.MapPut("/api/ui/layout", (UiLayoutSetRequest req, LayoutStore store) =>
        {
            if (string.IsNullOrWhiteSpace(req.LayoutJson))
                return Results.BadRequest(new { error = "layoutJson required" });
            store.Upsert(req.LayoutJson);
            return Results.Ok(store.Get());
        });

        app.MapDelete("/api/ui/layout", (LayoutStore store) =>
        {
            store.Delete();
            return Results.NoContent();
        });

        // Beacon endpoint: navigator.sendBeacon posts a Blob with Content-Type
        // application/json; minimal response so the browser's 204-check passes.
        app.MapPost("/api/ui/layout-beacon", async (LayoutStore store, HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);
            try
            {
                // Accept either the legacy single-layout shape or the v2
                // named-layout shape — beforeunload handlers in the field can
                // still be sending the old format while the page is reloading
                // into the new client.
                var named = System.Text.Json.JsonSerializer.Deserialize<SaveNamedLayoutRequest>(
                    body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (named?.LayoutJson is { } njson && !string.IsNullOrWhiteSpace(njson)
                    && !string.IsNullOrWhiteSpace(named.LayoutId))
                {
                    store.UpsertNamed(
                        named.RadioKey ?? "default",
                        named.LayoutId,
                        named.Name ?? named.LayoutId,
                        njson,
                        named.Icon,
                        named.Description);
                }
                else
                {
                    var req = System.Text.Json.JsonSerializer.Deserialize<UiLayoutSetRequest>(
                        body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (req?.LayoutJson is { } json && !string.IsNullOrWhiteSpace(json))
                        store.Upsert(json);
                }
            }
            catch { /* sendBeacon is fire-and-forget; swallow parse errors */ }
            return Results.Ok();
        });

        // Multi-layout API (issue #241) — named layouts keyed per radio.
        // `radio` query param is the BoardKind string ("HermesLite2", etc.) or
        // "default" while no radio is connected.
        app.MapGet("/api/ui/layouts", (string? radio, LayoutStore store) =>
            Results.Ok(store.GetForRadio(radio ?? "default")));

        app.MapPut("/api/ui/layouts", (SaveNamedLayoutRequest req, LayoutStore store) =>
        {
            if (string.IsNullOrWhiteSpace(req.LayoutJson))
                return Results.BadRequest(new { error = "layoutJson required" });
            if (string.IsNullOrWhiteSpace(req.LayoutId))
                return Results.BadRequest(new { error = "layoutId required" });
            return Results.Ok(store.UpsertNamed(
                req.RadioKey ?? "default",
                req.LayoutId,
                req.Name ?? req.LayoutId,
                req.LayoutJson,
                req.Icon,
                req.Description));
        });

        app.MapPost("/api/ui/layouts/active", (SetActiveLayoutRequest req, LayoutStore store) =>
        {
            if (string.IsNullOrWhiteSpace(req.LayoutId))
                return Results.BadRequest(new { error = "layoutId required" });
            return Results.Ok(store.SetActive(req.RadioKey ?? "default", req.LayoutId));
        });

        app.MapDelete("/api/ui/layouts", (string? radio, string? id, LayoutStore store) =>
        {
            if (string.IsNullOrWhiteSpace(id))
                return Results.BadRequest(new { error = "id required" });
            return Results.Ok(store.DeleteNamed(radio ?? "default", id));
        });

        // Saved-layouts library — reusable layout presets per radio, separate
        // from the working tabs above. The operator snapshots a workspace into a
        // preset (PUT), restores/seeds from it client-side, and manages the pool.
        app.MapGet("/api/ui/saved-layouts", (string? radio, LayoutStore store) =>
            Results.Ok(store.GetSavedLayouts(radio ?? "default")));

        app.MapPut("/api/ui/saved-layouts", (SaveSavedLayoutRequest req, LayoutStore store) =>
        {
            if (string.IsNullOrWhiteSpace(req.SavedId))
                return Results.BadRequest(new { error = "savedId required" });
            if (string.IsNullOrWhiteSpace(req.LayoutJson))
                return Results.BadRequest(new { error = "layoutJson required" });
            return Results.Ok(store.UpsertSavedLayout(
                req.RadioKey ?? "default",
                req.SavedId,
                req.Name,
                req.LayoutJson,
                req.Icon,
                req.Description));
        });

        app.MapDelete("/api/ui/saved-layouts", (string? radio, string? id, LayoutStore store) =>
        {
            if (string.IsNullOrWhiteSpace(id))
                return Results.BadRequest(new { error = "id required" });
            return Results.Ok(store.DeleteSavedLayout(radio ?? "default", id));
        });

        // Detached workspace windows (extra Photino frames popped off the dock)
        // that were open at the last desktop shutdown. The desktop shell reads
        // this once on startup and reopens each; web/headless clients never call
        // it (the restore path is gated on the Photino bridge), so it returns an
        // empty list there.
        app.MapGet("/api/ui/workspace-windows", (OpenWorkspaceWindowsStore store) =>
            Results.Ok(store.GetAll()));

        // Prefs-database (profile) selector. All Zeus prefs/settings/layouts live
        // in a single LiteDB resolved by PrefsDbPath.Get() at startup; the active
        // choice is a pointer file (NOT inside any DB). Switching applies on the
        // next launch, so /api/prefs/active-database flags restartRequired and the
        // frontend follows up with POST /api/app/restart.
        app.MapGet("/api/prefs/databases", () =>
            Results.Ok(new PrefsDatabasesDto(PrefsDbPath.ActiveRelativePath(), PrefsDbPath.ListProfiles())));

        app.MapPost("/api/prefs/active-database", (SetActiveDatabaseRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.RelativePath))
                return Results.BadRequest(new { error = "relativePath required" });
            try
            {
                PrefsDbPath.SetActive(req.RelativePath);
                return Results.Ok(new { restartRequired = true });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/prefs/databases", (CreateDatabaseRequest req) =>
        {
            try
            {
                PrefsDbPath.CreateProfile(req.Name);
                return Results.Ok(new PrefsDatabasesDto(PrefsDbPath.ActiveRelativePath(), PrefsDbPath.ListProfiles()));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/prefs/databases/import", (ImportDatabaseRequest req) =>
        {
            try
            {
                PrefsDbPath.ImportProfile(req.SourcePath, req.Name);
                return Results.Ok(new PrefsDatabasesDto(PrefsDbPath.ActiveRelativePath(), PrefsDbPath.ListProfiles()));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // File-picker import: the webview can't hand the server a filesystem
        // path, so the chosen .db is uploaded as multipart and written into the
        // profiles dir here. Antiforgery is disabled — this is a loopback /
        // LAN-token API, not a cookie-auth form post.
        app.MapPost("/api/prefs/databases/upload", async (HttpRequest req) =>
        {
            try
            {
                if (!req.HasFormContentType)
                    return Results.BadRequest(new { error = "Expected a multipart file upload." });
                var form = await req.ReadFormAsync();
                var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
                if (file is null || file.Length == 0)
                    return Results.BadRequest(new { error = "No file uploaded." });

                var nameField = form.TryGetValue("name", out var n) ? n.ToString() : null;
                var profileName = string.IsNullOrWhiteSpace(nameField)
                    ? Path.GetFileNameWithoutExtension(file.FileName)
                    : nameField;

                await using var stream = file.OpenReadStream();
                PrefsDbPath.ImportProfileFromStream(stream, profileName);
                return Results.Ok(new PrefsDatabasesDto(PrefsDbPath.ActiveRelativePath(), PrefsDbPath.ListProfiles()));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).DisableAntiforgery();

        // Download an existing profile's .db so the operator can back it up or
        // move it to another machine. Opens the file shared so the active
        // database can be exported while it's in use.
        app.MapGet("/api/prefs/databases/export", (string? relativePath) =>
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return Results.BadRequest(new { error = "relativePath required" });
            try
            {
                var bytes = PrefsDbPath.ReadProfileBytes(relativePath, out var fileName);
                return Results.File(bytes, "application/octet-stream", fileName);
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound(new { error = "Database not found." });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/app/restart", (AppRestartService restart) =>
        {
            restart.RequestRestart();
            return Results.Ok(new { restarting = true });
        });

        app.MapPost("/api/app/quit", (AppRestartService restart) =>
        {
            restart.RequestQuit();
            return Results.Ok(new { quitting = true });
        });

        // ---- Reset & Uninstall Zeus (About panel) ----------------------------
        // Preview the exact paths the wipe will remove (+ warnings) and mint a
        // one-shot confirm token. removeBinary asks for a full uninstall (app
        // binary too); BinaryRemovalSupported reports whether that's achievable
        // on this install (false → data-only fallback).
        app.MapGet("/api/app/uninstall/preview",
            (bool? removeBinary, Zeus.Server.Uninstall.UninstallService svc) =>
                Results.Ok(svc.Preview(removeBinary ?? false)));

        // One-click backup: prefs DB + all profiles + logbook + ADIF, streamed as
        // a single zip the operator saves to ~/Downloads (which the wipe never
        // touches) BEFORE uninstalling.
        app.MapGet("/api/app/backup",
            async (Zeus.Server.Uninstall.UninstallService svc, HttpContext ctx) =>
            {
                var (bytes, fileName) = await svc.BuildBackupAsync(ctx.RequestAborted);
                return Results.File(bytes, "application/zip", fileName);
            });

        // Execute. Requires the one-shot token from the preview. Builds + validates
        // the manifest fresh (fail-closed); on success launches the detached wipe
        // and exits.
        app.MapPost("/api/app/uninstall",
            (UninstallRequest req, Zeus.Server.Uninstall.UninstallService svc) =>
            {
                var result = svc.Execute(req.Token, req.RemoveBinary);
                if (!result.Started)
                    return Results.BadRequest(new { error = result.Error, abortReasons = result.AbortReasons });
                return Results.Ok(new { uninstalling = true });
            }).DisableAntiforgery();

        app.MapGet("/api/qrz/status", (QrzService qrz) => qrz.GetStatus());

        app.MapPost("/api/qrz/login", async (QrzLoginRequest req, QrzService qrz, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "username and password required" });
            log.LogInformation("api.qrz.login user={User}", req.Username);
            try
            {
                var status = await qrz.LoginAsync(req.Username, req.Password, ctx.RequestAborted);
                if (!status.Connected && status.Error != null)
                    return Results.Json(status, statusCode: StatusCodes.Status401Unauthorized);
                return Results.Ok(status);
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(new { error = $"QRZ unreachable: {ex.Message}" }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/api/qrz/lookup", async (QrzLookupRequest req, QrzService qrz, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(req.Callsign))
                return Results.BadRequest(new { error = "callsign required" });
            try
            {
                var station = await qrz.LookupAsync(req.Callsign.Trim().ToUpperInvariant(), ctx.RequestAborted);
                if (station == null) return Results.NotFound(new { error = $"no QRZ record for {req.Callsign}" });
                return Results.Ok(station);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
            }
            catch (QrzSubscriptionRequiredException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status402PaymentRequired);
            }
        });

        app.MapPost("/api/qrz/logout", async (QrzService qrz, HttpContext ctx) =>
        {
            await qrz.LogoutAsync(ctx.RequestAborted);
            return Results.Ok(qrz.GetStatus());
        });

        app.MapPost("/api/qrz/apikey", async (QrzSetApiKeyRequest req, QrzService qrz, HttpContext ctx) =>
        {
            await qrz.SetApiKeyAsync(req.ApiKey, ctx.RequestAborted);
            return Results.Ok(qrz.GetStatus());
        });

        // ── ZeusChat — operator-to-operator chat over the Cloudflare relay ──
        app.MapGet("/api/chat/status", (ChatService chat) => chat.GetStatus());

        // Web-client heartbeat: presence is published only while the operator is
        // showing the Chat panel (so closing/hiding it drops them off the roster).
        app.MapPost("/api/chat/visible", (ChatVisibleRequest req, ChatService chat) =>
            Results.Ok(chat.ReportPanelVisible(req.Visible)));

        app.MapPost("/api/chat/enable", (ChatEnableRequest req, ChatService chat) =>
        {
            log.LogInformation("api.chat.enable enabled={Enabled}", req.Enabled);
            return Results.Ok(chat.SetEnabled(req.Enabled));
        });

        app.MapPost("/api/chat/send", async (ChatSendRequest req, ChatService chat, HttpContext ctx) =>
        {
            // Text is optional when an image is attached (image-only message).
            if (string.IsNullOrWhiteSpace(req.Text) && req.Attachment is null)
                return Results.BadRequest(new { error = "message text required" });
            try
            {
                await chat.SendMessageAsync(req.Text, req.Room, req.Attachment, ctx.RequestAborted);
                return Results.Ok(new { ok = true });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        app.MapGet("/api/chat/messages", (ChatService chat, int? limit) =>
            Results.Ok(new { messages = chat.GetMessages(limit ?? 200) }));

        app.MapGet("/api/chat/roster", (ChatService chat) =>
            Results.Ok(new { operators = chat.GetRoster() }));

        // Friend graph (consent gate for seeing another operator's frequency).
        app.MapGet("/api/chat/friends", (ChatService chat) => Results.Ok(chat.GetFriends()));

        // request / accept / deny / remove all take { callsign } and share the
        // same 400 (empty) / 409 (not connected) error mapping as /send.
        static async Task<IResult> FriendAction(
            Func<string, CancellationToken, Task> action, ChatFriendRequest req, HttpContext ctx)
        {
            if (string.IsNullOrWhiteSpace(req.Callsign))
                return Results.BadRequest(new { error = "callsign required" });
            try
            {
                await action(req.Callsign, ctx.RequestAborted);
                return Results.Ok(new { ok = true });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
        }

        app.MapPost("/api/chat/friends/request", (ChatFriendRequest req, ChatService chat, HttpContext ctx) =>
            FriendAction(chat.SendFriendRequestAsync, req, ctx));
        app.MapPost("/api/chat/friends/accept", (ChatFriendRequest req, ChatService chat, HttpContext ctx) =>
            FriendAction(chat.AcceptFriendAsync, req, ctx));
        app.MapPost("/api/chat/friends/deny", (ChatFriendRequest req, ChatService chat, HttpContext ctx) =>
            FriendAction(chat.DenyFriendAsync, req, ctx));
        app.MapPost("/api/chat/friends/remove", (ChatFriendRequest req, ChatService chat, HttpContext ctx) =>
            FriendAction(chat.RemoveFriendAsync, req, ctx));

        // Rooms, DMs, history, moderation, eye toggle — all share the 400/409 map.
        static async Task<IResult> Run(Func<Task> action)
        {
            try { await action(); return Results.Ok(new { ok = true }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
        }

        app.MapGet("/api/chat/rooms", (ChatService chat) => Results.Ok(new { rooms = chat.GetRooms() }));

        app.MapPost("/api/chat/dm", (ChatDmRequest req, ChatService chat, HttpContext ctx) =>
            Run(() => chat.SendDmAsync(req.To, req.Text, req.Attachment, ctx.RequestAborted)));

        app.MapPost("/api/chat/history", (ChatRoomRequest req, ChatService chat, HttpContext ctx) =>
            Run(() => chat.RequestHistoryAsync(req.Room, ctx.RequestAborted)));

        app.MapPost("/api/chat/freq-visibility", (ChatFreqVisibilityRequest req, ChatService chat, HttpContext ctx) =>
            Run(() => chat.SetFreqVisibilityAsync(req.Public, ctx.RequestAborted)));

        // Admin/moderation (relay enforces that the caller is actually an admin).
        app.MapPost("/api/chat/admin/room/create", (ChatRoomCreateRequest req, ChatService chat, HttpContext ctx) =>
            Run(() => chat.CreateRoomAsync(req.Name, ctx.RequestAborted)));
        app.MapPost("/api/chat/admin/room/delete", (ChatRoomRequest req, ChatService chat, HttpContext ctx) =>
            Run(() => chat.DeleteRoomAsync(req.Room, ctx.RequestAborted)));
        app.MapPost("/api/chat/admin/room/add", (ChatRoomMemberRequest req, ChatService chat, HttpContext ctx) =>
            Run(() => chat.AddMemberAsync(req.Room, req.Callsign, ctx.RequestAborted)));
        app.MapPost("/api/chat/admin/room/remove", (ChatRoomMemberRequest req, ChatService chat, HttpContext ctx) =>
            Run(() => chat.RemoveMemberAsync(req.Room, req.Callsign, ctx.RequestAborted)));
        app.MapPost("/api/chat/admin/ban", (ChatFriendRequest req, ChatService chat, HttpContext ctx) =>
            Run(() => chat.BanAsync(req.Callsign, ctx.RequestAborted)));
        app.MapPost("/api/chat/admin/unban", (ChatFriendRequest req, ChatService chat, HttpContext ctx) =>
            Run(() => chat.UnbanAsync(req.Callsign, ctx.RequestAborted)));
        app.MapPost("/api/chat/admin/clear", (ChatClearRequest req, ChatService chat, HttpContext ctx) =>
            Run(() => chat.ClearRoomAsync(req.Room, ctx.RequestAborted)));
        app.MapPost("/api/chat/admin/broadcast", (ChatBroadcastRequest req, ChatService chat, HttpContext ctx) =>
            Run(() => chat.BroadcastAsync(req.Text, ctx.RequestAborted)));
        app.MapPost("/api/chat/admin/bans", (ChatService chat, HttpContext ctx) =>
            Run(() => chat.ListBansAsync(ctx.RequestAborted)));
        app.MapPost("/api/chat/admin/see-all", (ChatSeeAllRequest req, ChatService chat, HttpContext ctx) =>
            Run(() => chat.SetSeeAllFreqAsync(req.On, ctx.RequestAborted)));

        // Point-to-point propagation (DE → DX). Proxies the HamClock sidecar's
        // ITU-R P.533-14 engine; always returns 200 with an {available:false}
        // payload when the engine is offline so the QRZ card can degrade quietly.
        app.MapGet("/api/propagation", async (
            PropagationService prop, HttpContext ctx,
            double deLat, double deLon, double dxLat, double dxLon,
            string? mode, double? power, string? antenna, double? freq) =>
        {
            var result = await prop.PredictAsync(
                deLat, deLon, dxLat, dxLon, mode, power, antenna, freq, ctx.RequestAborted);
            return Results.Ok(result);
        });

        // Comprehensive solar / space-weather snapshot (proxies HamClock's N0NBH
        // feed). Always 200 with {available:false} when the sidecar is offline.
        app.MapGet("/api/spacewx", async (SpaceWeatherService sw, HttpContext ctx) =>
            Results.Ok(await sw.GetAsync(ctx.RequestAborted)));

        app.MapGet("/api/log/entries", async (LogService logService, HttpContext ctx, int skip = 0, int take = 100) =>
        {
            var response = await logService.GetLogEntriesAsync(skip, take, ctx.RequestAborted);
            return Results.Ok(response);
        });

        app.MapGet("/api/log/worked", async (LogService logService, HttpContext ctx, string callsign, int recent = 5) =>
        {
            if (string.IsNullOrWhiteSpace(callsign))
                return Results.BadRequest(new { error = "callsign required" });

            var response = await logService.GetWorkedCallsignSummaryAsync(callsign, recent, ctx.RequestAborted);
            return Results.Ok(response);
        });

        app.MapPost("/api/log/entry", async (
            CreateLogEntryRequest req,
            LogService logService,
            QrzService qrz,
            WsjtxUdpBroadcaster wsjtx,
            Wsjtx.N1mmBroadcaster n1mm,
            CloudLog.CloudLogService cloudLog,
            HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(req.Callsign))
                return Results.BadRequest(new { error = "callsign required" });

            // Operator name enrichment: when the caller didn't supply a name
            // (FT8 auto-log, a spot logged without a QRZ card open, an import
            // lacking NAME), fill it from QRZ. The QRZ feed splits the operator
            // into <fname>+<name>, so compose the full name rather than the
            // surname alone. Best-effort — a lookup miss or no subscription
            // must never block logging the QSO.
            if (string.IsNullOrWhiteSpace(req.Name))
            {
                var enriched = await TryQrzFullNameAsync(qrz, req.Callsign, ctx.RequestAborted);
                if (!string.IsNullOrWhiteSpace(enriched))
                    req = req with { Name = enriched };
            }

            var entry = await logService.CreateLogEntryAsync(req, ctx.RequestAborted);
            // Push the just-logged QSO to every opted-in external logger. Each
            // target is OFF by default and no-ops when disabled; this is the only
            // logging path that egresses (ADIF bulk import never does).
            // Fire-and-forget so a slow/blocked target (e.g. a free-text Host whose
            // DNS is dead) can never delay the operator's log confirmation. Each
            // client swallows its own failures internally; use a detached token so
            // the post-response request abort doesn't cancel them.
            _ = wsjtx.BroadcastLoggedQsoAsync(entry, CancellationToken.None);   // Class A: WSJT-X type-12 UDP
            _ = n1mm.BroadcastLoggedQsoAsync(entry, CancellationToken.None);    // Class B: N1MM contactinfo UDP
            _ = cloudLog.PublishAsync(entry, CancellationToken.None);           // Class C: Wavelog/Cloudlog + Club Log HTTP
            return Results.Ok(entry);
        });

        app.MapGet("/api/log/export/adif", async (LogService logService, HttpContext ctx) =>
        {
            var adif = await logService.ExportToAdifAsync(null, ctx.RequestAborted);
            var fileName = $"zeus-log-{DateTime.UtcNow:yyyyMMdd-HHmmss}.adi";
            return Results.File(
                System.Text.Encoding.UTF8.GetBytes(adif),
                "text/plain",
                fileName);
        });

        // Write the logbook (or a selected subset) to an ADIF file in a
        // directory on the machine running the backend, returning the path so
        // the Logbook panel can confirm where it landed (a silent browser
        // download gave the operator no feedback). Null directory => Downloads.
        app.MapPost("/api/log/export/adif/file", async (AdifExportToFileRequest req, LogService logService, HttpContext ctx) =>
        {
            try
            {
                var result = await logService.ExportToAdifFileAsync(req.Directory, req.LogEntryIds, ctx.RequestAborted);
                return Results.Ok(result);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Results.BadRequest(new { error = $"Could not write ADIF file: {ex.Message}" });
            }
        });

        app.MapPost("/api/log/import/adif", async (LogService logService, HttpContext ctx) =>
        {
            using var reader = new StreamReader(
                ctx.Request.Body,
                System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            var adif = await reader.ReadToEndAsync(ctx.RequestAborted);
            if (string.IsNullOrWhiteSpace(adif))
                return Results.BadRequest(new { error = "ADIF file is empty" });

            try
            {
                var response = await logService.ImportAdifAsync(adif, ctx.RequestAborted);
                return Results.Ok(response);
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/log/publish/qrz", async (QrzPublishRequest req, QrzService qrz, LogService logService, HttpContext ctx) =>
        {
            if (req.LogEntryIds == null || !req.LogEntryIds.Any())
                return Results.BadRequest(new { error = "no log entry IDs provided" });

            var entries = await logService.GetLogEntriesByIdsAsync(req.LogEntryIds, ctx.RequestAborted);
            var results = new List<QrzPublishResult>();

            foreach (var entry in entries)
            {
                var result = await qrz.PublishLogEntryAsync(entry, ctx.RequestAborted);
                results.Add(result);

                // Update log entry with QRZ log ID if successful
                if (result.Success && !string.IsNullOrEmpty(result.QrzLogId))
                {
                    await logService.UpdateQrzUploadStatusAsync(entry.Id, result.QrzLogId, ctx.RequestAborted);
                }
            }

            var successCount = results.Count(r => r.Success);
            var failedCount = results.Count - successCount;

            return Results.Ok(new QrzPublishResponse(
                TotalCount: results.Count,
                SuccessCount: successCount,
                FailedCount: failedCount,
                Results: results));
        });

        // -- Logging v2: HTTP cloud-log uploaders (Wavelog/Cloudlog + Club Log) --
        // Per-QSO realtime ADIF push. Egress OFF by default; fired automatically
        // from /api/log/entry. These endpoints manage config/secrets and a manual
        // batch re-publish. Secrets are write-only — status reports presence only.
        app.MapGet("/api/log/cloud/config", async (CloudLog.CloudLogService cloud, HttpContext ctx) =>
            Results.Ok(await cloud.GetStatusAsync(ctx.RequestAborted)));

        app.MapPost("/api/log/cloud/config", (CloudLog.CloudLogConfig req, CloudLog.CloudLogService cloud) =>
        {
            log.LogInformation(
                "api.log.cloud.config wavelog={Wl} clublog={Cl}",
                req.Wavelog.Enabled, req.ClubLog.Enabled);
            return Results.Ok(cloud.SetConfig(req));
        });

        app.MapPost("/api/log/cloud/credentials", async (
            CloudLog.CloudLogCredentialsRequest req, CloudLog.CloudLogService cloud, HttpContext ctx) =>
        {
            // Each non-null field is applied; an empty string clears that secret.
            if (req.WavelogApiKey is not null)
                await cloud.SetWavelogApiKeyAsync(req.WavelogApiKey, ctx.RequestAborted);
            if (req.ClubLogPassword is not null || req.ClubLogApiKey is not null)
                await cloud.SetClubLogCredentialsAsync(req.ClubLogPassword, req.ClubLogApiKey, ctx.RequestAborted);
            return Results.Ok(await cloud.GetStatusAsync(ctx.RequestAborted));
        });

        app.MapPost("/api/log/publish/cloud", async (
            QrzPublishRequest req, CloudLog.CloudLogService cloud, LogService logService, HttpContext ctx) =>
        {
            if (req.LogEntryIds == null || !req.LogEntryIds.Any())
                return Results.BadRequest(new { error = "no log entry IDs provided" });
            var entries = await logService.GetLogEntriesByIdsAsync(req.LogEntryIds, ctx.RequestAborted);
            var results = new List<CloudLog.CloudLogResult>();
            foreach (var entry in entries)
                results.AddRange(await cloud.PublishOnceAsync(entry, ctx.RequestAborted));
            return Results.Ok(new
            {
                total = results.Count,
                successCount = results.Count(r => r.Success),
                failedCount = results.Count(r => !r.Success),
                results,
            });
        });

        // -- Logging v2: N1MM-format UDP broadcaster (HRD / DXKeeper gateway) -----
        app.MapGet("/api/n1mm/config", (Wsjtx.N1mmBroadcaster n1mm) => Results.Ok(n1mm.GetConfig()));

        app.MapPost("/api/n1mm/config", (Wsjtx.N1mmConfig req, Wsjtx.N1mmBroadcaster n1mm) =>
        {
            log.LogInformation(
                "api.n1mm.config enabled={En} host={Host} port={Port}", req.Enabled, req.Host, req.Port);
            return Results.Ok(n1mm.SetConfig(req));
        });

        app.MapGet("/api/rotator/status", (RotctldService rot) => rot.GetStatus());

        app.MapGet("/api/rotator/config", (RotctldService rot) => rot.GetConfig());

        app.MapPost("/api/rotator/config", async (RotctldConfig req, RotctldService rot, HttpContext ctx) =>
        {
            log.LogInformation("api.rotator.config enabled={En} host={Host} port={Port}", req.Enabled, req.Host, req.Port);
            var status = await rot.SetConfigAsync(req, ctx.RequestAborted);
            return Results.Ok(status);
        });

        app.MapPost("/api/rotator/set", async (RotctldSetAzRequest req, RotctldService rot, HttpContext ctx) =>
        {
            if (!double.IsFinite(req.Azimuth)) return Results.BadRequest(new { error = "azimuth must be finite" });
            var status = await rot.SetAzAsync(req.Azimuth, ctx.RequestAborted);
            if (!status.Connected) return Results.Json(status, statusCode: StatusCodes.Status503ServiceUnavailable);
            return Results.Ok(status);
        });

        app.MapPost("/api/rotator/stop", async (RotctldService rot, HttpContext ctx) =>
        {
            var status = await rot.StopRotatorAsync(ctx.RequestAborted);
            return Results.Ok(status);
        });

        app.MapPost("/api/rotator/test", async (RotctldTestRequest req, RotctldService rot, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(req.Host) || req.Port is <= 0 or >= 65536)
                return Results.BadRequest(new { error = "host and port required" });
            var result = await rot.TestAsync(req.Host.Trim(), req.Port, ctx.RequestAborted);
            return Results.Ok(result);
        });

        // Multi-rotator config (issue #917). Returns / updates the full slot
        // list + the active slot id + the AutoRoute toggle. Slot-aware UI hits
        // these; legacy /api/rotator/config above keeps reflecting only the
        // active slot for the Compass / Dial panels.
        app.MapGet("/api/rotator/multi-config", (RotctldService rot) => rot.GetMultiConfig());

        app.MapPost("/api/rotator/multi-config", async (RotctldMultiConfig req, RotctldService rot, HttpContext ctx) =>
        {
            log.LogInformation(
                "api.rotator.multi-config slots={Count} active={Active} autoRoute={Auto}",
                req.Slots?.Count ?? 0, req.ActiveSlotId, req.AutoRoute);
            var cfg = await rot.SetMultiConfigAsync(req, ctx.RequestAborted);
            return Results.Ok(cfg);
        });

        app.MapPost("/api/rotator/active", async (RotctldSetActiveRequest req, RotctldService rot, HttpContext ctx) =>
        {
            if (req.SlotId <= 0) return Results.BadRequest(new { error = "slotId must be positive" });
            var status = await rot.SetActiveSlotAsync(req.SlotId, ctx.RequestAborted);
            return Results.Ok(status);
        });

        app.MapGet("/api/tci/status", (TciManagementService tci) => tci.GetStatus());

        app.MapPost("/api/tci/config", (TciRuntimeConfig req, TciManagementService tci, HttpContext ctx) =>
        {
            log.LogInformation("api.tci.config enabled={En} bind={Bind} port={Port}", req.Enabled, req.BindAddress, req.Port);
            var status = tci.SetConfig(req);
            return Results.Ok(status);
        });

        app.MapPost("/api/tci/test", (TciTestRequest req, TciManagementService tci, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(req.BindAddress) || req.Port is <= 0 or >= 65536)
                return Results.BadRequest(new { error = "bindAddress and port required" });
            var result = tci.TestPort(req.BindAddress.Trim(), req.Port);
            return Results.Ok(result);
        });

        app.MapGet("/api/cat/status", (CatManagementService cat) => cat.GetStatus());

        app.MapPost("/api/cat/config", (CatRuntimeConfig req, CatManagementService cat, HttpContext ctx) =>
        {
            log.LogInformation("api.cat.config enabled={En} bind={Bind} port={Port}", req.Enabled, req.BindAddress, req.Port);
            var status = cat.SetConfig(req);
            return Results.Ok(status);
        });

        app.MapPost("/api/cat/test", (CatTestRequest req, CatManagementService cat, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(req.BindAddress) || req.Port is <= 0 or >= 65536)
                return Results.BadRequest(new { error = "bindAddress and port required" });
            var result = cat.TestPort(req.BindAddress.Trim(), req.Port);
            return Results.Ok(result);
        });

        // Serial CAT ports (Thetis CAT1–4). GET returns per-port config + live
        // open/error status plus the host's enumerable serial devices (UI
        // suggestions; pty/com0com pairs aren't enumerable so the field is
        // free-form). PUT replaces all four port configs and hot-reconnects (no
        // restart). POST /test probe-opens one port to verify it's usable.
        app.MapGet("/api/cat/serial/status",
            (Zeus.Server.Cat.CatSerialService svc) => Results.Ok(svc.Snapshot()));

        app.MapPut("/api/cat/serial/config",
            (CatSerialConfig req, CatSerialConfigStore store, Zeus.Server.Cat.CatSerialService svc) =>
        {
            var ports = req?.Ports ?? Array.Empty<CatSerialPortConfig>();
            log.LogInformation("api.cat.serial.config ports=[{Ports}]",
                string.Join(", ", ports.Select((p, i) => $"#{i + 1} {(p.Enabled ? $"{p.PortName}@{p.BaudRate}" : "off")}")));
            store.Set(ports);
            return Results.Ok(svc.Snapshot());
        });

        app.MapPost("/api/cat/serial/test",
            (CatSerialTestRequest req, Zeus.Server.Cat.CatSerialService svc) =>
        {
            if (string.IsNullOrWhiteSpace(req?.PortName))
                return Results.BadRequest(new { error = "portName required" });
            return Results.Ok(svc.TestPort(req));
        });

        // WSJT-X logged-QSO UDP broadcaster (outbound QSO-record push to
        // JTAlert / Log4OM / GridTracker / N1MM). Default OFF / loopback; config
        // applies live (no restart — there is no listener, only a UDP sender).
        app.MapGet("/api/wsjtx/status", (WsjtxManagementService wsjtx) => wsjtx.GetStatus());

        app.MapPost("/api/wsjtx/config", (WsjtxRuntimeConfig req, WsjtxManagementService wsjtx, HttpContext ctx) =>
        {
            log.LogInformation(
                "api.wsjtx.config enabled={En} transport={Tr} host={Host} port={Port} group={Grp} ttl={Ttl} type5={T5} live={Live}",
                req.Enabled, req.Transport, req.Host, req.Port, req.MulticastGroup, req.MulticastTtl,
                req.SendQsoLogged, req.SendLiveDecodes);
            var status = wsjtx.SetConfig(req);
            return Results.Ok(status);
        });

        // Digital-mode spotting uploaders (FT8/FT4 -> PSK Reporter, WSPR ->
        // WSPRnet). NEW network egress, both DISABLED by default; config applies
        // live (the uploaders only send UDP/HTTP — there is no listener).
        app.MapGet("/api/spotting/status", (SpottingManagementService spotting) => spotting.GetStatus());

        app.MapPost("/api/spotting/config", (SpottingRuntimeConfig req, SpottingManagementService spotting) =>
        {
            log.LogInformation(
                "api.spotting.config psk={Psk} wsprnet={Wspr}",
                req.PskReporterEnabled, req.WsprnetEnabled);
            var status = spotting.SetConfig(req);
            return Results.Ok(status);
        });

        app.Map("/ws", async (HttpContext ctx, StreamingHub hub) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await hub.AttachClientAsync(ws, ctx.RequestAborted);
        });

        // -- Remote-access QR (Server menu: scan → open remote client) --------
        // Renders any URL to an SVG QR. The Server menu requests it for the
        // operator's /go/<callsign> address (RemoteQr.AddressFor). See
        // docs/designs/remote-access-webrtc.md.
        app.MapGet("/api/remote/qr.svg", (string? data) =>
        {
            if (string.IsNullOrWhiteSpace(data) || data.Length > 1024)
                return Results.BadRequest(new { error = "data required (max 1024 chars)" });
            return Results.Content(
                Zeus.Server.Hosting.Remote.RemoteQr.Svg(data), "image/svg+xml");
        });

        // -- Remote-access session password (ADR-0008) ------------------------
        // The operator sets a password here; remote access cannot be enabled
        // without one. Verified end-to-end via SPAKE2+ — the server stores only
        // the verifier, never the password.
        app.MapGet("/api/remote/password/status",
            (Zeus.Server.Hosting.Remote.RemotePasswordStore store) =>
                Results.Ok(new { hasPassword = store.HasPassword() }));

        app.MapPost("/api/remote/password",
            (Zeus.Server.Hosting.Remote.RemotePasswordRequest req,
             Zeus.Server.Hosting.Remote.RemotePasswordStore store) =>
            {
                if (string.IsNullOrWhiteSpace(req?.Password) || req.Password.Length < 8)
                    return Results.BadRequest(new { error = "password must be at least 8 characters" });
                store.Set(req.Password);
                log.LogInformation("api.remote.password set");
                return Results.Ok(new { hasPassword = true });
            });

        app.MapDelete("/api/remote/password",
            (Zeus.Server.Hosting.Remote.RemotePasswordStore store) =>
            {
                store.Clear();
                log.LogInformation("api.remote.password cleared");
                return Results.Ok(new { hasPassword = false });
            });

        // WebRTC signaling: answer the browser's offer with a password-gated session
        // (Phase 1). 403 when no password is set — there is no unauthenticated path.
        app.MapPost("/api/remote/connect",
            async (Zeus.Server.Hosting.Remote.RemoteConnectRequest req,
                   Zeus.Server.Hosting.Remote.RemoteWebRtcService rtc,
                   CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(req?.Sdp))
                    return Results.BadRequest(new { error = "sdp required" });
                try
                {
                    var answer = await rtc.ConnectAsync(req.Sdp, ct);
                    return Results.Ok(new { sdp = answer, type = "answer" });
                }
                catch (Zeus.Server.Hosting.Remote.RemoteAccessDisabledException)
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
            });

        // -- WebRTC data-plane spike (Phase 0) --------------------------------
        // Dev-only: inert unless ZEUS_RTC_SPIKE=1. Answers a browser SDP offer
        // and echoes binary DataChannel messages so we can measure real
        // browser↔Zeus.Server round-trip latency. See zeus-web/public/rtc-spike.html
        // and docs/designs/remote-access-webrtc.md. NOT the production transport.
        if (Environment.GetEnvironmentVariable("ZEUS_RTC_SPIKE") == "1")
        {
            log.LogWarning("rtc.spike endpoint ENABLED (/api/rtc/spike/offer) — dev only");
            app.MapPost("/api/rtc/spike/offer",
                async (Zeus.Server.Hosting.Remote.RtcSpikeOffer req,
                       Zeus.Server.Hosting.Remote.WebRtcSpikeService rtc,
                       CancellationToken ct) =>
                {
                    if (string.IsNullOrWhiteSpace(req?.Sdp))
                        return Results.BadRequest(new { error = "sdp required" });
                    var answerSdp = await rtc.CreateEchoAnswerAsync(req.Sdp, ct);
                    return Results.Ok(new { sdp = answerSdp, type = "answer" });
                });
        }

        // -- HamClock embed (optional Node sidecar; see HamClockService) -----
        // Inert until the operator installs it from Settings → HamClock. The
        // <iframe> in HamClockWindow points at the sidecar's own port (status
        // reports it), so HamClock serves its own /api proxy + /assets at the
        // root of that port with no path rewriting.
        app.MapGet("/api/hamclock/status", (HamClockService hc) => Results.Ok(hc.Snapshot()));

        app.MapPost("/api/hamclock/install", (HamClockService hc) =>
        {
            bool started = hc.BeginInstall();
            return started
                ? Results.Accepted("/api/hamclock/status", hc.Snapshot())
                : Results.Conflict(new { error = "HamClock is already installing or starting." });
        });

        app.MapPost("/api/hamclock/start", async (HamClockService hc) =>
        {
            try
            {
                var port = await hc.StartAsync();
                return Results.Ok(new { port, status = hc.Snapshot() });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message, status = hc.Snapshot() });
            }
        });

        app.MapPost("/api/hamclock/stop", (HamClockService hc) =>
        {
            hc.Stop();
            return Results.Ok(hc.Snapshot());
        });

        // LAN Browser proxy. Fetches a private-LAN device web page on the radio
        // host's behalf and returns it (HTML rewritten so sub-resources route
        // back through this same endpoint). Scoped to RFC1918 / IPv6-ULA targets
        // by LanProxyService; loopback + public + cloud-metadata are refused.
        // Tunnels transparently for remote operators (it's a same-origin /api GET).
        app.MapGet("/api/lan/proxy", async (string? url, string? inline, LanProxyService proxy, HttpContext ctx, CancellationToken ct) =>
        {
            // inline=1 → self-contained HTML for an iframe srcdoc (the remote path,
            // where the operator's browser can't load sub-resources from the LAN).
            bool wantInline = inline is "1" or "true";
            var result = await proxy.FetchAsync(url, wantInline, ct);
            ctx.Response.StatusCode = result.Status;
            ctx.Response.ContentType = result.ContentType ?? "application/octet-stream";
            await ctx.Response.Body.WriteAsync(result.Body, ct);
        });

        // HamClock DX-push config + push (Logging v2). Outbound "set the worked
        // station on the map". Egress OFF by default; server-side forward avoids
        // browser CORS / HTTPS-mixed-content blocking the classic-HamClock host.
        app.MapGet("/api/hamclock/push-config", (HamClockPushManagementService push) =>
            Results.Ok(push.GetConfig()));

        app.MapPost("/api/hamclock/push-config", (HamClockPushConfig req, HamClockPushManagementService push) =>
        {
            log.LogInformation(
                "api.hamclock.push-config enabled={En} trigger={Tr} target={Tg} host={Host} port={Port}",
                req.Enabled, req.Trigger, req.Target, req.ExternalHost, req.ExternalPort);
            return Results.Ok(push.SetConfig(req));
        });

        app.MapPost("/api/hamclock/dx", async (
            HamClockDxRequest req, HamClockPushManagementService push, HamClockService hc) =>
        {
            var cfg = push.GetConfig();
            if (!cfg.Enabled)
                return Results.Ok(new { pushed = false, skipped = "disabled" });
            // Fire the GET inline but tightly time-bounded inside PushDxAsync; it
            // never throws and returns false on any skip/failure.
            bool pushed = await hc.PushDxAsync(req.Grid, cfg.Target, cfg.ExternalHost, cfg.ExternalPort);
            return Results.Ok(new { pushed });
        });

        return app;
    }

    // ---------- helpers (formerly local functions in Program.cs) -------------

    /// <summary>
    /// Best-effort QRZ lookup that returns the operator's full display name
    /// ("First Last") for a callsign, or null. The QRZ feed splits the operator
    /// into &lt;fname&gt; (first) and &lt;name&gt; (last), so compose both —
    /// the surname alone is what made the logbook Name column read blank for
    /// records where QRZ only populates the first name. Never throws: a missing
    /// subscription, a not-logged-in state, or a lookup miss simply yields null
    /// so the QSO still logs.
    /// </summary>
    private static async Task<string?> TryQrzFullNameAsync(QrzService qrz, string callsign, CancellationToken ct)
    {
        try
        {
            // Bound the lookup so logging a QSO never stalls on a slow or
            // gate-contended QRZ feed (FT8 bursts, no subscription, network
            // blip). The QSO logs with no name rather than hanging.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var station = await qrz.LookupAsync(callsign, timeout.Token).ConfigureAwait(false);
            if (station == null) return null;
            var full = string.Join(' ', new[] { station.FirstName, station.Name }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim()));
            return string.IsNullOrWhiteSpace(full) ? null : full;
        }
        catch
        {
            // Not logged in / no subscription / network blip — logging a QSO
            // must never depend on QRZ being reachable.
            return null;
        }
    }

    private static IResult GetNativeAudioDevices(IServiceProvider sp)
    {
        var sink = sp.GetService<NativeAudioSink>();
        var mic = sp.GetService<NativeMicCapture>();
        if (sink is null && mic is null)
        {
            return Results.Ok(new NativeAudioDevicesResponse(
                Supported: false,
                InputDeviceId: null,
                OutputDeviceId: null,
                ActiveInputDeviceId: null,
                ActiveOutputDeviceId: null,
                Inputs: [],
                Outputs: [],
                Error: null));
        }

        try
        {
            var snapshot = MiniAudioDevices.Enumerate();
            return Results.Ok(BuildNativeAudioDevicesResponse(
                sink,
                mic,
                snapshot,
                supported: true,
                error: null));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return Results.Ok(BuildNativeAudioDevicesResponse(
                sink,
                mic,
                MiniAudioDeviceSnapshot.Empty,
                supported: false,
                error: ex.Message));
        }
    }

    private static async Task<IResult> SetNativeAudioDevices(
        NativeAudioDevicesSetRequest body,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var sink = sp.GetService<NativeAudioSink>();
        var mic = sp.GetService<NativeMicCapture>();
        if (sink is null && mic is null)
            return Results.NotFound(new { error = "native audio not active in this host mode" });

        string? inputDeviceId = NormalizeDeviceId(body?.InputDeviceId);
        string? outputDeviceId = NormalizeDeviceId(body?.OutputDeviceId);

        MiniAudioDeviceSnapshot snapshot;
        try
        {
            snapshot = MiniAudioDevices.Enumerate();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return Results.BadRequest(new { error = $"native audio device enumeration unavailable: {ex.Message}" });
        }

        if (inputDeviceId is not null && snapshot.Inputs.All(d => d.Id != inputDeviceId))
            return Results.BadRequest(new { error = "inputDeviceId is not in the current native input device list" });
        if (outputDeviceId is not null && snapshot.Outputs.All(d => d.Id != outputDeviceId))
            return Results.BadRequest(new { error = "outputDeviceId is not in the current native output device list" });

        if (mic is not null)
            await mic.SetInputDeviceAsync(inputDeviceId, ct);
        if (sink is not null)
            await sink.SetOutputDeviceAsync(outputDeviceId, ct);

        return Results.Ok(BuildNativeAudioDevicesResponse(
            sink,
            mic,
            snapshot,
            supported: true,
            error: null));
    }

    private static NativeAudioDevicesResponse BuildNativeAudioDevicesResponse(
        NativeAudioSink? sink,
        NativeMicCapture? mic,
        MiniAudioDeviceSnapshot snapshot,
        bool supported,
        string? error)
    {
        return new NativeAudioDevicesResponse(
            Supported: supported,
            InputDeviceId: mic?.ConfiguredInputDeviceId,
            OutputDeviceId: sink?.ConfiguredOutputDeviceId,
            ActiveInputDeviceId: mic?.ActiveInputDeviceId,
            ActiveOutputDeviceId: sink?.ActiveOutputDeviceId,
            Inputs: snapshot.Inputs.Select(ToNativeAudioDeviceDto).ToArray(),
            Outputs: snapshot.Outputs.Select(ToNativeAudioDeviceDto).ToArray(),
            Error: error);
    }

    private static NativeAudioDeviceDto ToNativeAudioDeviceDto(MiniAudioDeviceInfo device) =>
        new(device.Id, device.Name, device.IsDefault);

    private static string? NormalizeDeviceId(string? deviceId)
    {
        var trimmed = deviceId?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    // The aux-input strings the connected board's Alex / filter board exposes
    // (external-ports plan — antenna slice, #804). Empty on boards with no aux
    // inputs (HL2). Names match the RxAuxInputSel single-choice enum the PUT
    // request parses.
    static string[] AvailableRxAux(RxAuxInputs caps)
    {
        var list = new List<string>(4);
        if (caps.HasFlag(RxAuxInputs.Ext1))   list.Add(nameof(RxAuxInputSel.Ext1));
        if (caps.HasFlag(RxAuxInputs.Ext2))   list.Add(nameof(RxAuxInputSel.Ext2));
        if (caps.HasFlag(RxAuxInputs.Xvtr))   list.Add(nameof(RxAuxInputSel.Xvtr));
        if (caps.HasFlag(RxAuxInputs.Bypass)) list.Add(nameof(RxAuxInputSel.Bypass));
        return list.ToArray();
    }

    static bool RxAuxSupported(RxAuxInputSel sel, RxAuxInputs caps) => sel switch
    {
        RxAuxInputSel.Ext1   => caps.HasFlag(RxAuxInputs.Ext1),
        RxAuxInputSel.Ext2   => caps.HasFlag(RxAuxInputs.Ext2),
        RxAuxInputSel.Xvtr   => caps.HasFlag(RxAuxInputs.Xvtr),
        RxAuxInputSel.Bypass => caps.HasFlag(RxAuxInputs.Bypass),
        _                    => true, // None always allowed
    };

    static bool TryParseIpEndpoint(string raw, out IPEndPoint ep)
    {
        ep = null!;
        var idx = raw.LastIndexOf(':');
        string host = idx > 0 ? raw[..idx] : raw;
        int port = 1024;
        if (idx > 0 && int.TryParse(raw[(idx + 1)..], out var p)) port = p;
        if (!IPAddress.TryParse(host, out var ip)) return false;
        ep = new IPEndPoint(ip, port);
        return true;
    }

    // Connect-time projection of the discovered board byte → kind. Protocol 1
    // and Protocol 2 use DIFFERENT wire numbering for the dual-ADC family
    // (Angelia/Orion/OrionMkII), so they MUST use separate maps that mirror
    // Zeus.Protocol1/Protocol2 Discovery.ReplyParser respectively. Reusing one
    // table for both was the connect-time half of issue #780 (a P2 ANAN-200D,
    // byte 0x04, was projected through the P1 table as Angelia/100D).
    // Do NOT merge these two.
    static HpsdrBoardKind MapBoardByteP1(byte raw) => raw switch
    {
        0x00 => HpsdrBoardKind.Metis,
        0x01 => HpsdrBoardKind.Hermes,
        0x02 => HpsdrBoardKind.HermesII,
        0x04 => HpsdrBoardKind.Angelia,      // ANAN-100D (P1 wire)
        0x05 => HpsdrBoardKind.Orion,        // ANAN-200D (P1 wire)
        0x06 => HpsdrBoardKind.HermesLite2,
        0x0A => HpsdrBoardKind.OrionMkII,
        0x14 => HpsdrBoardKind.HermesC10,
        _    => HpsdrBoardKind.Unknown,
    };

    static HpsdrBoardKind MapBoardByteP2(byte raw) => raw switch
    {
        0x00 => HpsdrBoardKind.Metis,        // Atlas
        0x01 => HpsdrBoardKind.Hermes,
        0x02 => HpsdrBoardKind.HermesII,
        0x03 => HpsdrBoardKind.Angelia,      // ANAN-100D (P2 wire)
        0x04 => HpsdrBoardKind.Orion,        // ANAN-200D (P2 wire — issue #780)
        0x05 => HpsdrBoardKind.OrionMkII,    // ANAN-7000DLE / 8000DLE (P2 wire)
        0x06 => HpsdrBoardKind.HermesLite2,
        0x0A => HpsdrBoardKind.OrionMkII,    // Saturn / ANAN-G2
        0x14 => HpsdrBoardKind.HermesC10,    // ANAN-G2E
        _    => HpsdrBoardKind.Unknown,
    };

    static HpsdrBoardKind? ParseBoardKind(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (string.Equals(raw, "Auto", StringComparison.OrdinalIgnoreCase)) return null;
        return Enum.TryParse<HpsdrBoardKind>(raw, ignoreCase: true, out var kind)
            ? kind
            : null;
    }

    static bool TryValidateSampleRate(int rate, out string error)
    {
        // 768/1536 kHz are Protocol-2 only; the P1 connect path
        // (RadioService.ConnectAsync) rejects them, and SetSampleRate clamps the
        // live P1 path, so it's safe to accept them here for the P2 flow.
        if (rate is 48_000 or 96_000 or 192_000 or 384_000 or 768_000 or 1_536_000) { error = ""; return true; }
        error = $"sampleRate must be one of {{48000, 96000, 192000, 384000, 768000, 1536000}}, got {rate}.";
        return false;
    }

    static bool TryValidateAttenDb(int db, out string error)
    {
        if (db >= HpsdrAtten.MinDb && db <= HpsdrAtten.MaxDb) { error = ""; return true; }
        error = $"atten must be in {HpsdrAtten.MinDb}..{HpsdrAtten.MaxDb} dB, got {db}.";
        return false;
    }

    static readonly HashSet<string> TxStationProfileIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "studio-ssb",
        "essb",
        "dx",
    };

    static bool TryValidateTxStationProfileId(string? id, out string error)
    {
        if (!string.IsNullOrWhiteSpace(id) && TxStationProfileIds.Contains(id))
        {
            error = "";
            return true;
        }
        error = "profile id must be one of studio-ssb, essb, dx";
        return false;
    }

    static bool TryValidateTxFidelityPolicy(TxFidelityPolicyDto policy, out string error)
    {
        if (!TryValidateTxStationProfileId(policy.ProfileId, out error))
            return false;
        if (policy.TargetSpectralDensity < 0 || policy.TargetSpectralDensity > 100)
        {
            error = "targetSpectralDensity must be 0..100";
            return false;
        }
        error = "";
        return true;
    }

    static bool TryValidateCfcPresetName(string? name, out string cleanName, out string error)
    {
        cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length == 0)
        {
            error = "Preset name required";
            return false;
        }
        if (cleanName.Length > 80)
        {
            error = "Preset name must be 80 characters or fewer";
            return false;
        }
        if (cleanName.Any(c => char.IsControl(c) || c is '/' or '\\' or '?' or '#'))
        {
            error = "Preset name contains an invalid character";
            return false;
        }

        error = "";
        return true;
    }

    static bool TryValidateCfcPresetConfig(CfcConfig? cfc, out string error)
    {
        if (cfc is null)
        {
            error = "Config required";
            return false;
        }
        if (!IsFinite(cfc.PreCompDb) || !IsFinite(cfc.PrePeqDb))
        {
            error = "CFC preCompDb and prePeqDb must be finite";
            return false;
        }
        if (cfc.Bands is null || cfc.Bands.Length != 10)
        {
            error = $"Bands must have exactly 10 entries; got {cfc.Bands?.Length ?? 0}";
            return false;
        }
        for (var i = 0; i < cfc.Bands.Length; i++)
        {
            var band = cfc.Bands[i];
            if (!IsFinite(band.FreqHz) || !IsFinite(band.CompLevelDb) || !IsFinite(band.PostGainDb))
            {
                error = $"CFC band {i + 1} values must be finite";
                return false;
            }
        }

        error = "";
        return true;
    }

    static bool TryValidateDisplayIntelligenceSettings(DisplayIntelligenceSettingsDto settings, out string error)
    {
        var profileId = settings.ProfileId?.Trim().ToLowerInvariant();
        if (profileId is not ("balanced" or "dx" or "cw" or "digital" or "voice" or "contest" or "custom"))
        {
            error = "profileId must be one of balanced, dx, cw, digital, voice, contest, custom";
            return false;
        }
        if (!IsFinite(settings.PopFloorDb) || settings.PopFloorDb < 0.0 || settings.PopFloorDb > 12.0)
        {
            error = "popFloorDb must be 0..12 dB";
            return false;
        }
        if (!IsFinite(settings.PopSpanDb) || settings.PopSpanDb < 12.0 || settings.PopSpanDb > 60.0)
        {
            error = "popSpanDb must be 12..60 dB";
            return false;
        }
        if (!IsFinite(settings.PopGamma) || settings.PopGamma < 0.3 || settings.PopGamma > 1.2)
        {
            error = "popGamma must be 0.3..1.2";
            return false;
        }
        if (settings.PopRenderIntensity < 0 || settings.PopRenderIntensity > 100)
        {
            error = "popRenderIntensity must be 0..100";
            return false;
        }
        if (settings.WaterfallReliefDepth < 0 || settings.WaterfallReliefDepth > 100)
        {
            error = "waterfallReliefDepth must be 0..100";
            return false;
        }
        if (settings.WaterfallSmoothness < 0 || settings.WaterfallSmoothness > 100)
        {
            error = "waterfallSmoothness must be 0..100";
            return false;
        }
        if (!IsFinite(settings.CoherenceHoldGate) || settings.CoherenceHoldGate < 0.2 || settings.CoherenceHoldGate > 0.8)
        {
            error = "coherenceHoldGate must be 0.2..0.8";
            return false;
        }
        if (!IsFinite(settings.CoherenceBoostDb) || settings.CoherenceBoostDb < 0.0 || settings.CoherenceBoostDb > 8.0)
        {
            error = "coherenceBoostDb must be 0..8 dB";
            return false;
        }
        if (!IsFinite(settings.RidgeBoost) || settings.RidgeBoost < 0.0 || settings.RidgeBoost > 0.8)
        {
            error = "ridgeBoost must be 0..0.8";
            return false;
        }
        if (!IsFinite(settings.RidgeMaxBoostDb) || settings.RidgeMaxBoostDb < 0.0 || settings.RidgeMaxBoostDb > 12.0)
        {
            error = "ridgeMaxBoostDb must be 0..12 dB";
            return false;
        }
        if (settings.VisualAgcStrength < 0 || settings.VisualAgcStrength > 100)
        {
            error = "visualAgcStrength must be 0..100";
            return false;
        }
        if (settings.ImpulseRejectDb < 8 || settings.ImpulseRejectDb > 32)
        {
            error = "impulseRejectDb must be 8..32 dB";
            return false;
        }
        if (settings.SnapRadiusHz < 500 || settings.SnapRadiusHz > 12_000)
        {
            error = "snapRadiusHz must be 500..12000 Hz";
            return false;
        }
        if (!IsFinite(settings.SnapMinSnrDb) || settings.SnapMinSnrDb < 3.0 || settings.SnapMinSnrDb > 16.0)
        {
            error = "snapMinSnrDb must be 3..16 dB";
            return false;
        }
        if (!IsFinite(settings.PeakMinSnrDb) || settings.PeakMinSnrDb < 4.0 || settings.PeakMinSnrDb > 20.0)
        {
            error = "peakMinSnrDb must be 4..20 dB";
            return false;
        }
        error = "";
        return true;
    }

    static bool TryValidateTxStationProfile(TxStationProfileDto profile, out string error)
    {
        if (!TryValidateTxStationProfileId(profile.Id, out error))
            return false;
        if (string.IsNullOrWhiteSpace(profile.Label))
        {
            error = "label required";
            return false;
        }
        if (!string.Equals(profile.AudioSuiteRoute, "native", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(profile.AudioSuiteRoute, "vst", StringComparison.OrdinalIgnoreCase))
        {
            error = "audioSuiteRoute must be 'native' or 'vst'";
            return false;
        }
        if (profile.AudioSuiteProfileName is { Length: > 96 })
        {
            error = "audioSuiteProfileName must be 96 characters or fewer";
            return false;
        }
        if (!IsFinite(profile.MicGainDb) || profile.MicGainDb < -40.0 || profile.MicGainDb > 10.0)
        {
            error = "micGainDb must be -40..10 dB";
            return false;
        }
        if (!IsFinite(profile.LevelerMaxGainDb) || profile.LevelerMaxGainDb < 0.0 || profile.LevelerMaxGainDb > 20.0)
        {
            error = "levelerMaxGainDb must be 0..20 dB";
            return false;
        }
        if (profile.LowCutHz < 20 || profile.LowCutHz > 600)
        {
            error = "lowCutHz must be 20..600 Hz";
            return false;
        }
        if (profile.HighCutHz < 1500 || profile.HighCutHz > 6000 || profile.HighCutHz <= profile.LowCutHz)
        {
            error = "highCutHz must be 1500..6000 Hz and greater than lowCutHz";
            return false;
        }
        if (profile.SpectralDensity < 0 || profile.SpectralDensity > 100)
        {
            error = "spectralDensity must be 0..100";
            return false;
        }

        var tx = profile.TxLeveling;
        if (tx is null)
        {
            error = "txLeveling required";
            return false;
        }
        if (!IsFinite(tx.AlcMaxGainDb) || tx.AlcMaxGainDb < 0.0 || tx.AlcMaxGainDb > 120.0)
        {
            error = "alcMaxGainDb must be 0..120 dB";
            return false;
        }
        if (tx.AlcDecayMs < 1 || tx.AlcDecayMs > 50)
        {
            error = "alcDecayMs must be 1..50";
            return false;
        }
        if (tx.LevelerDecayMs < 1 || tx.LevelerDecayMs > 5000)
        {
            error = "levelerDecayMs must be 1..5000";
            return false;
        }
        if (!IsFinite(tx.CompressorGainDb) || tx.CompressorGainDb < 0.0 || tx.CompressorGainDb > 20.0)
        {
            error = "compressorGainDb must be 0..20 dB";
            return false;
        }

        var cfc = profile.CfcConfig;
        if (cfc is null)
        {
            error = "cfcConfig required";
            return false;
        }
        if (!IsFinite(cfc.PreCompDb) || cfc.PreCompDb < -12.0 || cfc.PreCompDb > 12.0)
        {
            error = "preCompDb must be -12..12 dB";
            return false;
        }
        if (!IsFinite(cfc.PrePeqDb) || cfc.PrePeqDb < -12.0 || cfc.PrePeqDb > 12.0)
        {
            error = "prePeqDb must be -12..12 dB";
            return false;
        }
        if (cfc.Bands is null || cfc.Bands.Length != 10)
        {
            error = $"cfcConfig.bands must have exactly 10 entries; got {cfc.Bands?.Length ?? 0}";
            return false;
        }
        foreach (var band in cfc.Bands)
        {
            if (!IsFinite(band.FreqHz) || band.FreqHz < 20.0 || band.FreqHz > 6000.0)
            {
                error = "cfc band freqHz must be 20..6000 Hz";
                return false;
            }
            if (!IsFinite(band.CompLevelDb) || band.CompLevelDb < 0.0 || band.CompLevelDb > 20.0)
            {
                error = "cfc band compLevelDb must be 0..20 dB";
                return false;
            }
            if (!IsFinite(band.PostGainDb) || band.PostGainDb < -12.0 || band.PostGainDb > 12.0)
            {
                error = "cfc band postGainDb must be -12..12 dB";
                return false;
            }
        }

        error = "";
        return true;
    }

    static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    internal static SmartNrRxChainRuntimeDto BuildSmartNrRxChainRuntime(StateDto state, AdcProtectionStatusDto adc)
    {
        var agc = state.Agc ?? new AgcConfig();
        var squelch = state.Squelch ?? new SquelchConfig();

        return new(
            SchemaVersion: 2,
            Source: "backend-radio-state",
            FilterLowHz: state.FilterLowHz,
            FilterHighHz: state.FilterHighHz,
            FilterWidthHz: Math.Abs(state.FilterHighHz - state.FilterLowHz),
            FilterPresetName: state.FilterPresetName,
            AutoAgcEnabled: state.AutoAgcEnabled,
            AgcMode: agc.Mode.ToString(),
            AgcTopDb: Math.Round(state.AgcTopDb, 1),
            AgcOffsetDb: Math.Round(state.AgcOffsetDb, 1),
            EffectiveAgcTopDb: Math.Round(state.AgcTopDb + state.AgcOffsetDb, 1),
            AutoAttEnabled: state.AutoAttEnabled,
            AdcProtectionEnabled: adc.Config.Enabled,
            AttenDb: adc.AttenDb,
            AttOffsetDb: adc.OffsetDb,
            EffectiveAttenDb: adc.EffectiveDb,
            AdcOverloadWarning: adc.Warning,
            AdcOverloadLevel: adc.OverloadLevel,
            LastOverloadBits: adc.LastOverloadBits,
            Adc0MaxMagnitude: adc.Adc0MaxMagnitude,
            Adc1MaxMagnitude: adc.Adc1MaxMagnitude,
            Adc0MaxMagnitudeAtOverload: adc.Adc0MaxMagnitudeAtOverload,
            Adc1MaxMagnitudeAtOverload: adc.Adc1MaxMagnitudeAtOverload,
            LastAdcTelemetryUtc: adc.LastTelemetryUtc,
            SquelchEnabled: squelch.Enabled,
            SquelchAdaptive: squelch.Adaptive,
            SquelchLevel: squelch.Level,
            PreampOn: state.PreampOn);
    }

    private static object DiagnosticsSnapshot(DiagnosticsProviderRegistry registry, string routeSegment)
    {
        if (registry.TryGetByRoute(routeSegment, out var provider))
            return provider.Snapshot();

        throw new InvalidOperationException(
            $"Diagnostics provider route '{routeSegment}' is not registered.");
    }

    internal static object BuildTxStageDiagnostics(TxStageMeters stage, bool hostTxActive)
    {
        bool hasLevelEvidence =
            IsUsableTxLevel(stage.MicPk)
            || IsUsableTxLevel(stage.EqPk)
            || IsUsableTxLevel(stage.LvlrPk)
            || IsUsableTxLevel(stage.CfcPk)
            || IsUsableTxLevel(stage.CompPk)
            || IsUsableTxLevel(stage.AlcPk)
            || IsUsableTxLevel(stage.OutPk);
        string status = hostTxActive
            ? hasLevelEvidence ? "active" : "waiting-for-stage-meters"
            : "idle";
        var density = BuildTxStageDensityDiagnostics(stage, hostTxActive, hasLevelEvidence);

        return new
        {
            schemaVersion = 1,
            source = "wdsp-txa-meter-ring",
            status,
            hostTxActive,
            micPkDbfs = TxLevelDb(stage.MicPk),
            micAvDbfs = TxLevelDb(stage.MicAv),
            eqPkDbfs = TxLevelDb(stage.EqPk),
            eqAvDbfs = TxLevelDb(stage.EqAv),
            lvlrPkDbfs = TxLevelDb(stage.LvlrPk),
            lvlrAvDbfs = TxLevelDb(stage.LvlrAv),
            lvlrGrDb = TxGainReductionDb(stage.LvlrGr),
            cfcPkDbfs = TxLevelDb(stage.CfcPk),
            cfcAvDbfs = TxLevelDb(stage.CfcAv),
            cfcGrDb = TxGainReductionDb(stage.CfcGr),
            compPkDbfs = TxLevelDb(stage.CompPk),
            compAvDbfs = TxLevelDb(stage.CompAv),
            alcPkDbfs = TxLevelDb(stage.AlcPk),
            alcAvDbfs = TxLevelDb(stage.AlcAv),
            alcGrDb = TxGainReductionDb(stage.AlcGr),
            outPkDbfs = TxLevelDb(stage.OutPk),
            outAvDbfs = TxLevelDb(stage.OutAv),
            outputHeadroomDb = density.OutputHeadroomDb,
            outputCrestFactorDb = density.OutputCrestFactorDb,
            densityStatus = density.Status,
            densityTone = density.Tone,
            densityRecommendation = density.Recommendation,
            diagnosticRecommendation = status switch
            {
                "active" => "WDSP TXA stage meters are live; use Mic/Leveler/CFC/ALC/OUT peaks and gain reduction to tune station audio and spectral density.",
                "waiting-for-stage-meters" => "TX is keyed but WDSP TXA stage meters have not published yet; keep feeding mic/two-tone IQ and verify the TX ingest path.",
                _ => "TXA stage meters are idle; key MOX/TUN or enable the TX monitor path to evaluate mic, leveler, CFC, ALC, and output quality.",
            },
        };
    }

    internal static TxStageDensityDiagnostics BuildTxStageDensityDiagnostics(
        TxStageMeters stage,
        bool hostTxActive,
        bool hasLevelEvidence)
    {
        double? outPk = TxLevelDb(stage.OutPk);
        double? outAv = TxLevelDb(stage.OutAv);
        double alcGr = TxGainReductionDb(stage.AlcGr);
        double cfcGr = TxGainReductionDb(stage.CfcGr);
        double? headroom = outPk is { } pk ? Math.Round(Math.Max(0.0, -pk), 1) : null;
        double? crest = outPk is { } pk2 && outAv is { } av
            ? Math.Round(Math.Max(0.0, pk2 - av), 1)
            : null;

        if (!hostTxActive)
        {
            return new(
                OutputHeadroomDb: headroom,
                OutputCrestFactorDb: crest,
                Status: "idle",
                Tone: "standby",
                Recommendation: "TXA density diagnostics are idle; key MOX/TUN or enable TX monitor before judging on-air spectral density.");
        }

        if (!hasLevelEvidence || outPk is null || outAv is null || headroom is null || crest is null)
        {
            return new(
                OutputHeadroomDb: headroom,
                OutputCrestFactorDb: crest,
                Status: "waiting-for-stage-meters",
                Tone: "verify",
                Recommendation: "TX is keyed but output density cannot be evaluated until WDSP TXA output peak and average meters are live.");
        }

        if (headroom < 1.0 || stage.OutPk > -0.5f)
        {
            return new(
                OutputHeadroomDb: headroom,
                OutputCrestFactorDb: crest,
                Status: "clip-risk",
                Tone: "protect",
                Recommendation: "TX output is within 1 dB of digital full scale; reduce mic gain, drive, CFC, or ALC pressure before increasing density.");
        }

        if (alcGr >= 8.0)
        {
            return new(
                OutputHeadroomDb: headroom,
                OutputCrestFactorDb: crest,
                Status: "alc-heavy",
                Tone: "protect",
                Recommendation: "ALC is carrying heavy gain reduction; back down mic gain or upstream compression so the leveler/CFC shape density before ALC clamps the waveform.");
        }

        if (crest > 14.0 || outAv < -24.0)
        {
            return new(
                OutputHeadroomDb: headroom,
                OutputCrestFactorDb: crest,
                Status: "underfilled",
                Tone: "optimize",
                Recommendation: "TX output has high crest factor or low average level; raise mic/leveler drive or add gentle CFC/compression while preserving headroom.");
        }

        if (crest < 4.0 && cfcGr >= 6.0)
        {
            return new(
                OutputHeadroomDb: headroom,
                OutputCrestFactorDb: crest,
                Status: "over-dense",
                Tone: "protect",
                Recommendation: "TX output is very dense with strong CFC gain reduction; reduce CFC/compression if speech edges sound flat or adjacent-channel splatter rises.");
        }

        return new(
            OutputHeadroomDb: headroom,
            OutputCrestFactorDb: crest,
            Status: "density-optimized",
            Tone: "ready",
            Recommendation: "TX output density and headroom are in the target window; confirm with RF power, PureSignal feedback, and an off-air monitor.");
    }

    private static bool IsUsableTxLevel(float value) =>
        float.IsFinite(value) && value > -300.0f;

    private static double? TxLevelDb(float value) =>
        IsUsableTxLevel(value) ? Math.Round(value, 1) : null;

    private static double TxGainReductionDb(float value)
    {
        if (!float.IsFinite(value)) return 0.0;
        return Math.Round(Math.Max(0.0, value), 1);
    }

    internal static TxAudioPathHealthDto BuildTxAudioPathHealth(
        DateTimeOffset generatedUtc,
        long ringTotalWritten,
        long ringTotalRead,
        int ringCount,
        long ringDropped,
        int ringCapacity,
        double ringRecentMag,
        long totalMicSamples,
        long totalTxBlocks,
        long droppedFrames,
        Protocol2TxIqDiagnostics? p2,
        bool hostTxActive,
        TxMicUplinkDiagnosticsDto? micUplink = null,
        bool requiresMicUplink = false)
    {
        double ringFillPct = ringCapacity <= 0
            ? 0.0
            : Math.Round(Math.Clamp(ringCount * 100.0 / ringCapacity, 0.0, 100.0), 1);
        double ringDropRatioPct = ringTotalWritten <= 0
            ? 0.0
            : Math.Round(ringDropped * 100.0 / ringTotalWritten, 3);
        long p2InputComplexSamples = p2?.InputComplexSamples ?? 0;
        long p2PacketsSent = p2?.PacketsSent ?? 0;
        long? p2QueuedPackets = p2?.QueuedPackets;
        double? p2AgeMs = p2?.LastRateTimestampUtc is { } lastRate
            ? Math.Max(0.0, (generatedUtc - lastRate).TotalMilliseconds)
            : null;
        bool p2DucLive = p2 is { LastPacketsPerSecond: > 0 } && p2AgeMs is <= 2500.0;
        bool p2WaitingForTx = p2 is { SenderRunning: true }
            && !p2DucLive
            && p2InputComplexSamples <= 0
            && p2PacketsSent <= 0;
        bool ingestSeen = totalMicSamples > 0
            || totalTxBlocks > 0
            || ringTotalWritten > 0
            || ringTotalRead > 0;
        bool p2PriorActivity = p2InputComplexSamples > 0 || p2PacketsSent > 0;
        bool ringPressure = ringDropRatioPct > 1.0
            || (ringDropped > 0 && ringFillPct >= 95.0);
        double? micAgeMs = micUplink?.LastFrameAgeMs;
        bool micUplinkMissing = requiresMicUplink && (micUplink is null || micUplink.TotalFrames <= 0);
        bool micUplinkStale = requiresMicUplink && micAgeMs is > 1500.0;
        bool micUplinkInvalid = requiresMicUplink && micUplink is { TotalFrames: <= 0 }
            && (micUplink.InvalidFrames > 0 || micUplink.OversizeMessages > 0);

        string status;
        string recommendation;
        if (micUplinkInvalid)
        {
            status = "tx-mic-uplink-invalid";
            recommendation = "Host MOX is active but only invalid mic uplink frames are reaching the server; verify the browser/native worklet is sending 960 f32 samples per frame before judging TX density.";
        }
        else if (micUplinkMissing)
        {
            status = "tx-mic-uplink-missing";
            recommendation = "Host MOX is active but no mic PCM frames have reached the server; grant mic permission, start native capture, or verify the client websocket before increasing drive.";
        }
        else if (micUplinkStale && !ingestSeen)
        {
            status = "tx-mic-uplink-stale";
            recommendation = "Host MOX is active but the mic uplink is stale and no TX ingest has advanced; verify client capture and websocket health before judging station audio quality.";
        }
        else if (!ingestSeen)
        {
            status = "idle";
            recommendation = "No TX audio or TX IQ has reached the ingest path yet; key MOX/TUN, run two-tone, or enable TX monitor audio before judging station fidelity.";
        }
        else if (hostTxActive && ringPressure)
        {
            status = "tx-ring-pressure";
            recommendation = "Host TX is active and the TX IQ ring is dropping or full; reduce TX processing burst pressure, verify P2 DUC packet flow, and avoid increasing drive or density until egress is clean.";
        }
        else if (hostTxActive && (p2DucLive || p2InputComplexSamples > 0 || p2PacketsSent > 0))
        {
            status = "tx-audio-flowing";
            recommendation = "TX audio is reaching the active DUC path; use TXA stage peaks, ALC/CFC gain reduction, P2 packets, RF power, and PureSignal feedback together for fidelity tuning.";
        }
        else if (hostTxActive)
        {
            status = "tx-waiting-for-p2-iq";
            recommendation = "Host TX is active but P2 DUC input counters are not advancing; keep drive low and verify WDSP TXA output, TX monitor routing, and transport selection.";
        }
        else if (p2 is not null && p2WaitingForTx && ringPressure)
        {
            status = "standby-ring-pressure";
            recommendation = "Host TX is idle and P2 DUC has no input; P1 compatibility-ring drops are standby ingest pressure, not on-air P2 egress evidence. Key MOX/TUN or enable a TX monitor path and watch P2 input/packet counters.";
        }
        else if (!hostTxActive && ringPressure && p2PriorActivity)
        {
            status = "post-tx-ring-pressure";
            recommendation = "Host TX is idle and P2 DUC counters show prior TX activity; ring pressure is last-TX or standby ingest history, not current on-air audio-path proof. Key MOX/TUN or TX monitor when you need a fresh fidelity reading.";
        }
        else if (!hostTxActive && ringPressure)
        {
            status = "idle-ring-pressure";
            recommendation = "TX ingest is producing IQ while host TX is idle and the ring is dropping; treat this as standby path pressure unless it persists after keying with live P2 DUC packets.";
        }
        else
        {
            status = "standby-audio-present";
            recommendation = "TX audio or WDSP TXA activity is visible while host TX is idle; this is useful for monitor-path tuning, but on-air density still requires keyed P2 packets and RF/PureSignal evidence.";
        }

        return new TxAudioPathHealthDto(
            SchemaVersion: 1,
            Status: status,
            HostTxActive: hostTxActive,
            P2Attached: p2 is not null,
            P2DucLive: p2DucLive,
            P2WaitingForTx: p2WaitingForTx,
            P2LastActivityAgeMs: p2AgeMs is { } age ? Math.Round(age, 0) : null,
            P2InputComplexSamples: p2InputComplexSamples,
            P2PacketsSent: p2PacketsSent,
            P2QueuedPackets: p2QueuedPackets,
            RequiresMicUplink: requiresMicUplink,
            MicUplinkStatus: micUplink?.Status ?? "unknown",
            MicUplinkLastFrameAgeMs: micUplink?.LastFrameAgeMs,
            MicUplinkFrames: micUplink?.TotalFrames ?? 0,
            MicUplinkSamples: micUplink?.TotalSamples ?? 0,
            MicUplinkInvalidFrames: micUplink?.InvalidFrames ?? 0,
            MicUplinkOversizeMessages: micUplink?.OversizeMessages ?? 0,
            TotalMicSamples: totalMicSamples,
            TotalTxBlocks: totalTxBlocks,
            DroppedFrames: droppedFrames,
            RingTotalWritten: ringTotalWritten,
            RingTotalRead: ringTotalRead,
            RingCount: ringCount,
            RingCapacity: ringCapacity,
            RingFillPct: ringFillPct,
            RingDropped: ringDropped,
            RingDropRatioPct: ringDropRatioPct,
            RingRecentMag: Math.Round(ringRecentMag, 3),
            DiagnosticRecommendation: recommendation);
    }

    internal static TxEgressHealthDto BuildTxEgressHealth(
        DateTimeOffset generatedUtc,
        long p1RingTotalWritten,
        long p1RingDropped,
        Protocol2TxIqDiagnostics? p2,
        bool hostMoxOn = false,
        bool hostTunOn = false,
        bool hostTwoToneOn = false,
        bool? hardwarePtt = null,
        double? forwardWatts = null,
        RxMode? txMode = null,
        bool g2DutyGuidance = false)
    {
        const double RfDetectedWatts = 0.05;
        bool hostTxActive = hostMoxOn || hostTunOn || hostTwoToneOn;
        bool rfDetected = forwardWatts is > RfDetectedWatts;
        var duty = TxDutyGuidance(txMode, g2DutyGuidance, forwardWatts, hostTxActive);
        double p1RingDropRatioPct = p1RingTotalWritten <= 0
            ? 0.0
            : Math.Round(p1RingDropped * 100.0 / p1RingTotalWritten, 3);
        if (p2 is not { } p2Diag)
        {
            var rfStatus = RfEvidenceStatus(hostTxActive, rfDetected, p2Live: false, hardwarePtt: hardwarePtt);
            var qualityScore = TxEgressQualityScore("p2-unattached", hostTxActive, rfDetected, p2: null, p1RingDropRatioPct);
            var qualityTone = TxEgressQualityTone("p2-unattached", hostTxActive, rfDetected, duty.LimitExceeded);
            var qualityReasons = TxEgressQualityReasons(
                health: "p2-unattached",
                p2Attached: false,
                p2Live: false,
                packetRateStatus: "missing",
                p2: null,
                hostTxActive,
                rfDetected,
                p1RingDropRatioPct,
                duty);
            if (duty.LimitExceeded) qualityScore = Math.Min(qualityScore, 35);
            return new TxEgressHealthDto(
                SchemaVersion: 1,
                GeneratedUtc: generatedUtc,
                ActiveTransport: "P1",
                HealthStatus: "p2-unattached",
                P2Attached: false,
                P2Live: false,
                P2LastActivityAgeMs: null,
                P1RingDropRatioPct: p1RingDropRatioPct,
                HostMoxOn: hostMoxOn,
                HostTunOn: hostTunOn,
                HostTwoToneOn: hostTwoToneOn,
                HostTxActive: hostTxActive,
                HardwarePtt: hardwarePtt,
                ForwardWatts: forwardWatts,
                RfDetected: rfDetected,
                RfEvidenceStatus: rfStatus,
                QualityScore: qualityScore,
                QualityTone: qualityTone,
                P2PacketRateStatus: "missing",
                P2LastPacketsPerSecond: null,
                P2FifoModelSamples: null,
                P2QueuedPackets: null,
                P2TransportFailures: 0,
                QualityReasons: qualityReasons,
                TxDutyProfile: duty.Profile,
                ContinuousDutyRecommendedMaxWatts: duty.RecommendedMaxWatts,
                ContinuousDutyLimitExceeded: duty.LimitExceeded,
                ContinuousDutyManualReference: duty.ManualReference,
                DiagnosticRecommendation: p1RingDropRatioPct > 1.0
                    ? "No Protocol 2 TX client is attached and the P1 TX IQ ring is dropping samples; verify the active radio transport before judging TX audio fidelity."
                    : "No Protocol 2 TX client is attached; P1 ring counters are the only visible TX egress path.");
        }

        double? ageMs = p2Diag.LastRateTimestampUtc is { } lastRate
            ? Math.Max(0.0, (generatedUtc - lastRate).TotalMilliseconds)
            : null;
        bool p2Live = p2Diag.LastPacketsPerSecond > 0 && ageMs is <= 2500.0;
        string packetRateStatus = P2PacketRateStatus(p2Diag.LastPacketsPerSecond, ageMs);
        long failures = p2Diag.QueueWriteFailures + p2Diag.SendFailures;
        string health;
        string recommendation;

        if (!p2Diag.SenderRunning)
        {
            health = "p2-sender-stopped";
            recommendation = "Protocol 2 is attached but the DUC sender task is not running; reconnect the radio before trusting TX egress telemetry.";
        }
        else if (failures > 0)
        {
            health = "p2-send-failures";
            recommendation = "P2 DUC egress has write/send failures; stop judging audio quality until the transport is clean.";
        }
        else if (p2Diag.QueuedPackets > 12)
        {
            health = "p2-queue-backed-up";
            recommendation = "P2 DUC queue is backing up; check host scheduling and packet pacing before increasing TX density.";
        }
        else if (p2Live)
        {
            health = "p2-live";
            if (rfDetected)
            {
                recommendation = "P2 DUC egress and RF forward-power evidence are live. Use P2 queued/sent packets, packet rate, FIFO model, failure counters, and PA watts together for TX density and fidelity decisions.";
            }
            else if (hostTxActive)
            {
                recommendation = "Host TX is keyed and P2 DUC egress is live, but PA forward power is not detected; keep drive low and verify TX inhibit, band/PA routing, and antenna/load before judging fidelity.";
            }
            else
            {
                recommendation = "P2 DUC packets are live while host MOX/TUN/two-tone and PA forward power are idle; treat this as off-air TX monitor or idle packet flow, not on-air spectral-density proof.";
            }
        }
        else if (p2Diag.InputComplexSamples > 0 || p2Diag.PacketsSent > 0)
        {
            if (hostTxActive)
            {
                health = "p2-stale";
                recommendation = "Host TX is active but P2 DUC counters are stale; keep drive low and verify WDSP TXA output, TX monitor routing, and P2 packet pacing before judging fidelity.";
            }
            else
            {
                health = "p2-post-tx-idle";
                recommendation = "P2 DUC counters show prior TX activity, but host TX and RF are idle now; treat this as last-TX history, not an active egress fault, and key MOX/TUN or TX monitor when you want a fresh fidelity reading.";
            }
        }
        else
        {
            health = "p2-waiting-for-tx";
            recommendation = !hostTxActive && p1RingDropRatioPct > 1.0
                ? "P2 DUC sender is attached and waiting for TX IQ; P1 compatibility-ring standby drops are not on-air P2 egress evidence until MOX/TUN is keyed and P2 input/packet counters advance."
                : "P2 DUC sender is attached and waiting for TX IQ; key MOX/TUN or feed mic/TX monitor audio to validate live egress.";
        }

        var p2QualityScore = TxEgressQualityScore(health, hostTxActive, rfDetected, p2Diag, p1RingDropRatioPct);
        var p2QualityTone = TxEgressQualityTone(health, hostTxActive, rfDetected, duty.LimitExceeded);
        var p2QualityReasons = TxEgressQualityReasons(
            health,
            p2Attached: true,
            p2Live,
            packetRateStatus,
            p2Diag,
            hostTxActive,
            rfDetected,
            p1RingDropRatioPct,
            duty);
        if (duty.LimitExceeded) p2QualityScore = Math.Min(p2QualityScore, 35);

        return new TxEgressHealthDto(
            SchemaVersion: 1,
            GeneratedUtc: generatedUtc,
            ActiveTransport: "P2",
            HealthStatus: health,
            P2Attached: true,
            P2Live: p2Live,
            P2LastActivityAgeMs: ageMs is { } age ? Math.Round(age, 0) : null,
            P1RingDropRatioPct: p1RingDropRatioPct,
            HostMoxOn: hostMoxOn,
            HostTunOn: hostTunOn,
            HostTwoToneOn: hostTwoToneOn,
            HostTxActive: hostTxActive,
            HardwarePtt: hardwarePtt,
            ForwardWatts: forwardWatts,
            RfDetected: rfDetected,
            RfEvidenceStatus: RfEvidenceStatus(hostTxActive, rfDetected, p2Live, hardwarePtt),
            QualityScore: p2QualityScore,
            QualityTone: p2QualityTone,
            P2PacketRateStatus: packetRateStatus,
            P2LastPacketsPerSecond: p2Diag.LastPacketsPerSecond,
            P2FifoModelSamples: p2Diag.LastFifoModelSamples,
            P2QueuedPackets: p2Diag.QueuedPackets,
            P2TransportFailures: failures,
            QualityReasons: p2QualityReasons,
            TxDutyProfile: duty.Profile,
            ContinuousDutyRecommendedMaxWatts: duty.RecommendedMaxWatts,
            ContinuousDutyLimitExceeded: duty.LimitExceeded,
            ContinuousDutyManualReference: duty.ManualReference,
            DiagnosticRecommendation: recommendation);
    }

    static bool IsG2DutyGuidance(string? effectiveBoard, string? variant) =>
        string.Equals(effectiveBoard, HpsdrBoardKind.OrionMkII.ToString(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(variant, OrionMkIIVariant.G2.ToString(), StringComparison.OrdinalIgnoreCase);

    static TxDutyGuidanceDto TxDutyGuidance(
        RxMode? mode,
        bool g2DutyGuidance,
        double? forwardWatts,
        bool hostTxActive)
    {
        const double G2ContinuousDutyWatts = 30.0;
        bool continuousDuty = mode is RxMode.AM or RxMode.FM or RxMode.SAM or RxMode.DSB or RxMode.DIGL or RxMode.DIGU;
        if (!g2DutyGuidance)
        {
            return new(
                Profile: continuousDuty ? "continuous-duty" : "pep-intermittent",
                RecommendedMaxWatts: null,
                LimitExceeded: false,
                ManualReference: null);
        }

        if (!continuousDuty)
        {
            return new(
                Profile: "pep-intermittent",
                RecommendedMaxWatts: null,
                LimitExceeded: false,
                ManualReference: "ANAN-G2 manual: 100 W class operation is intended for intermittent PEP modes; Data/AM/FM and other continuous-duty modes should be operated at 30 W or less.");
        }

        bool exceeded = hostTxActive && forwardWatts is > G2ContinuousDutyWatts;
        return new(
            Profile: "continuous-duty",
            RecommendedMaxWatts: G2ContinuousDutyWatts,
            LimitExceeded: exceeded,
            ManualReference: "ANAN-G2 manual: Data/AM/FM and other continuous-duty modes must be operated at 30 W or less; AM carrier output is specified as 1-30 W.");
    }

    static int TxEgressQualityScore(
        string health,
        bool hostTxActive,
        bool rfDetected,
        Protocol2TxIqDiagnostics? p2,
        double p1RingDropRatioPct)
    {
        var score = health switch
        {
            "p2-live" when hostTxActive && rfDetected => 100,
            "p2-live" when hostTxActive => 62,
            "p2-live" => 74,
            "p2-waiting-for-tx" => 68,
            "p2-post-tx-idle" => 72,
            "p2-stale" => 46,
            "p2-queue-backed-up" => 42,
            "p2-send-failures" => 18,
            "p2-sender-stopped" => 12,
            "p2-unattached" => 50,
            _ => 40,
        };

        if (p2 is null && p1RingDropRatioPct > 1.0) score -= 20;
        if (p2 is { QueuedPackets: > 0 and <= 12 } p2Diag)
            score -= Math.Min(10, (int)p2Diag.QueuedPackets);
        return Math.Clamp(score, 0, 100);
    }

    static string TxEgressQualityTone(string health, bool hostTxActive, bool rfDetected, bool dutyLimitExceeded)
    {
        if (dutyLimitExceeded)
            return "protect";
        if (health is "p2-sender-stopped" or "p2-send-failures" or "p2-queue-backed-up")
            return "protect";
        if (health == "p2-live" && hostTxActive && !rfDetected)
            return "protect";
        if (health == "p2-live" && rfDetected)
            return "ready";
        if (health is "p2-waiting-for-tx" or "p2-post-tx-idle")
            return "standby";
        return "verify";
    }

    static string P2PacketRateStatus(int packetsPerSecond, double? ageMs)
    {
        if (packetsPerSecond <= 0 || ageMs is null) return "missing";
        if (ageMs <= 2500.0) return "fresh";
        if (ageMs <= 15_000.0) return "stale";
        return "expired";
    }

    static string[] TxEgressQualityReasons(
        string health,
        bool p2Attached,
        bool p2Live,
        string packetRateStatus,
        Protocol2TxIqDiagnostics? p2,
        bool hostTxActive,
        bool rfDetected,
        double p1RingDropRatioPct,
        TxDutyGuidanceDto duty)
    {
        var reasons = new List<string>();
        if (!p2Attached)
        {
            reasons.Add("p2-unattached");
        }
        else if (p2 is { } p2Diag)
        {
            reasons.Add(p2Live ? "p2-rate-fresh" : $"p2-rate-{packetRateStatus}");
            if (!p2Diag.SenderRunning) reasons.Add("p2-sender-stopped");
            if (p2Diag.QueueWriteFailures > 0) reasons.Add("p2-queue-write-failures");
            if (p2Diag.SendFailures > 0) reasons.Add("p2-send-failures");
            if (p2Diag.QueuedPackets > 12) reasons.Add("p2-queue-backed-up");
            if (p2Diag.InputComplexSamples > 0) reasons.Add("tx-iq-seen");
            if (p2Diag.LastFifoModelSamples > 0) reasons.Add("p2-fifo-modeled");
            if (health == "p2-post-tx-idle") reasons.Add("p2-post-tx-idle");
        }

        reasons.Add(hostTxActive ? "host-tx-active" : "host-tx-idle");
        reasons.Add(rfDetected ? "rf-forward-power-present" : "rf-forward-power-missing");
        if (duty.RecommendedMaxWatts is not null) reasons.Add("continuous-duty-mode");
        if (duty.LimitExceeded) reasons.Add("continuous-duty-limit-exceeded");
        if (p1RingDropRatioPct > 1.0)
        {
            reasons.Add(p2Attached && !hostTxActive
                ? "p1-ring-standby-pressure"
                : "p1-ring-drop-pressure");
        }
        return reasons.ToArray();
    }

    static string RfEvidenceStatus(
        bool hostTxActive,
        bool rfDetected,
        bool p2Live,
        bool? hardwarePtt)
    {
        if (rfDetected) return "rf-active";
        if (hostTxActive) return "host-keyed-no-forward-power";
        if (hardwarePtt == true) return "hardware-ptt-input-no-forward-power";
        if (p2Live) return "transport-live-rf-idle";
        return "rf-idle";
    }

    static HpsdrSampleRate MapHpsdrSampleRate(int hz) => hz switch
    {
        48_000 => HpsdrSampleRate.Rate48k,
        96_000 => HpsdrSampleRate.Rate96k,
        192_000 => HpsdrSampleRate.Rate192k,
        384_000 => HpsdrSampleRate.Rate384k,
        768_000 => HpsdrSampleRate.Rate768k,     // P2 only (RadioService clamps P1)
        1_536_000 => HpsdrSampleRate.Rate1536k,  // P2 only
        _ => throw new ArgumentOutOfRangeException(nameof(hz), hz, "validate before calling"),
    };
}

internal sealed record NativeMuteRequest(bool Muted);
/// <summary>Operator Allow/Deny body for a maintainer remote-support request (remote-diag P3).</summary>
internal sealed record SupportDecisionRequest(string? RequestId);
/// <summary>Operator opt-in body for the L1 Remote Diagnostics master switch (remote-diag P5).</summary>
internal sealed record SupportAvailabilityRequest(bool? Available, bool? AutoShareCrashes);
internal sealed record NativeAudioDevicesSetRequest(string? InputDeviceId, string? OutputDeviceId);
internal sealed record NativeAudioDeviceDto(string Id, string Name, bool IsDefault);
internal sealed record NativeAudioDevicesResponse(
    bool Supported,
    string? InputDeviceId,
    string? OutputDeviceId,
    string? ActiveInputDeviceId,
    string? ActiveOutputDeviceId,
    IReadOnlyList<NativeAudioDeviceDto> Inputs,
    IReadOnlyList<NativeAudioDeviceDto> Outputs,
    string? Error);
internal sealed record PreviewSetRequest(bool Enabled, bool? MeterOnly = null);
internal sealed record ChainOrderSetRequest(List<string> PluginIds);
internal sealed record ChainMembershipSetRequest(bool Active);
internal sealed record ScanVstDirectoryRequest(string Directory, string? Route = null);

// Body for the AU scan endpoints. AUs come from the OS AudioComponent
// registry, so there is no directory — only an optional route selector
// ("auto" | "tx" | "rx" | "both"), mirroring the VST3 scan's Route field.
internal sealed record ScanAuRequest(string? Route = null);
internal sealed record MasterBypassSetRequest(bool Bypassed);
internal sealed record ProcessingModeSetRequest(string Mode);
internal sealed record TxStageDensityDiagnostics(
    double? OutputHeadroomDb,
    double? OutputCrestFactorDb,
    string Status,
    string Tone,
    string Recommendation);
internal sealed record TxMicUplinkDiagnosticsDto(
    int SchemaVersion,
    string Status,
    bool SubscriberAttached,
    int ClientCount,
    int ExpectedFrameSamples,
    int ExpectedFrameBytes,
    long TotalFrames,
    long TotalSamples,
    long TotalBytes,
    long LastFrameBytes,
    long LastFrameSamples,
    double? LastFrameAgeMs,
    DateTimeOffset? LastFrameUtc,
    long InvalidFrames,
    long OversizeMessages,
    long UnknownFrames,
    string DiagnosticRecommendation);
internal sealed record TxAudioPathHealthDto(
    int SchemaVersion,
    string Status,
    bool HostTxActive,
    bool P2Attached,
    bool P2DucLive,
    bool P2WaitingForTx,
    double? P2LastActivityAgeMs,
    long P2InputComplexSamples,
    long P2PacketsSent,
    long? P2QueuedPackets,
    bool RequiresMicUplink,
    string MicUplinkStatus,
    double? MicUplinkLastFrameAgeMs,
    long MicUplinkFrames,
    long MicUplinkSamples,
    long MicUplinkInvalidFrames,
    long MicUplinkOversizeMessages,
    long TotalMicSamples,
    long TotalTxBlocks,
    long DroppedFrames,
    long RingTotalWritten,
    long RingTotalRead,
    int RingCount,
    int RingCapacity,
    double RingFillPct,
    long RingDropped,
    double RingDropRatioPct,
    double RingRecentMag,
    string DiagnosticRecommendation);
internal sealed record TxEgressHealthDto(
    int SchemaVersion,
    DateTimeOffset GeneratedUtc,
    string ActiveTransport,
    string HealthStatus,
    bool P2Attached,
    bool P2Live,
    double? P2LastActivityAgeMs,
    double P1RingDropRatioPct,
    bool HostMoxOn,
    bool HostTunOn,
    bool HostTwoToneOn,
    bool HostTxActive,
    bool? HardwarePtt,
    double? ForwardWatts,
    bool RfDetected,
    string RfEvidenceStatus,
    int QualityScore,
    string QualityTone,
    string P2PacketRateStatus,
    int? P2LastPacketsPerSecond,
    long? P2FifoModelSamples,
    long? P2QueuedPackets,
    long P2TransportFailures,
    string[] QualityReasons,
    string TxDutyProfile,
    double? ContinuousDutyRecommendedMaxWatts,
    bool ContinuousDutyLimitExceeded,
    string? ContinuousDutyManualReference,
    string DiagnosticRecommendation);
internal sealed record TxDutyGuidanceDto(
    string Profile,
    double? RecommendedMaxWatts,
    bool LimitExceeded,
    string? ManualReference);
// Body for POST /api/radios/reclaim. Endpoint is the discovered "ip:port"
// string (port is ignored — protocol command ports are fixed); Protocol is
// "P1" or "P2" so the server sends the correct stop frame.
internal sealed record ReclaimRadioRequest(string? Endpoint, string? Protocol);
internal sealed record HardwareDiagnosticsMarkerRequest(string? Label, string? Notes);
internal sealed record WavRecordStartRequest(string? Source, string? Folder);
internal sealed record WavPlayRequest(string? File, string? Dest);
internal sealed record WavRenameRequest(string? From, string? Name);
internal sealed record WavMoveRequest(string? From, string? Folder);
internal sealed record WavFolderRequest(string? Path);
internal sealed record WavDeleteRequest(string? File);
// Body for POST /api/wav/root. Null/empty Path resets to the platform default.
internal sealed record WavRootRequest(string? Path);
