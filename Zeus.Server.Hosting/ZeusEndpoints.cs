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
using Zeus.Server.Tci;

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

        app.MapGet("/api/version", () =>
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var attr = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute;
            var version = attr?.InformationalVersion ?? "unknown";
            return Results.Ok(new { version });
        });

        // Capabilities snapshot — host-mode + platform metadata. Frontend
        // fetches once on app mount; future feature gates will reattach as
        // the new plugin system fills the FeatureMatrix. The HttpContext-
        // aware Snapshot overload lets desktop + ShareOverLan report
        // host="server" to LAN clients while loopback Photino keeps
        // host="desktop" — see CapabilitiesService.Snapshot(HttpContext).
        app.MapGet("/api/capabilities",
            (HttpContext ctx, CapabilitiesService caps) => Results.Ok(caps.Snapshot(ctx)));

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
                : Results.Ok(new { supported = true, muted = sink.IsMuted });
        });
        app.MapPost("/api/audio/native/mute", (NativeMuteRequest body, IServiceProvider sp) =>
        {
            var sink = sp.GetService<NativeAudioSink>();
            if (sink is null) return Results.NotFound(new { error = "native audio not active in this host mode" });
            sink.SetMuted(body.Muted);
            return Results.Ok(new { supported = true, muted = sink.IsMuted });
        });

        // Audio Suite audition toggle — when on, the audio plugin chain's
        // output (the operator's mic through EQ / Comp / Exciter / Bass /
        // Reverb / future plugins) is mixed into the same RX playback path
        // so the operator can hear the chain's effect on their voice without
        // keying the radio. Pairs with the live pre-MOX meter tap; both
        // require the same NativeMicCapture → AudioPluginBridge.ProcessLivePreview
        // path to be running. Browser mode reports supported=false (audition
        // is desktop-only in v1).
        app.MapGet("/api/audio-suite/audition", (IAuditionAudioSink audition) =>
        {
            bool supported = audition is not NoOpAuditionAudioSink;
            return Results.Ok(new { supported, enabled = audition.IsEnabled });
        });
        app.MapPut("/api/audio-suite/audition", (AuditionSetRequest body, IAuditionAudioSink audition) =>
        {
            if (audition is NoOpAuditionAudioSink)
                return Results.NotFound(new { error = "audition not available in this host mode" });
            audition.SetEnabled(body.Enabled);
            return Results.Ok(new { supported = true, enabled = audition.IsEnabled });
        });

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

        // Audio Suite profiles — named snapshots of the chain config
        // (active plugin order + parked set + master bypass). Lets the
        // operator save a whole rack layout and recall it in one click.
        // Chain-level only in v1: per-plugin knob positions are NOT
        // captured (a running plugin reads its settings only at init, so
        // there's no live-reload path to apply them yet).
        app.MapGet("/api/audio-suite/profiles", (AudioProfileService profiles) =>
        {
            var list = profiles.List().Select(p => new
            {
                name = p.Name,
                order = p.Order,
                parked = p.Parked,
                masterBypass = p.MasterBypass,
                createdUtc = p.CreatedUtc,
                updatedUtc = p.UpdatedUtc,
            });
            return Results.Ok(new { profiles = list });
        });
        // PUT saves (or overwrites) the named profile from the CURRENT
        // live chain config.
        app.MapPut("/api/audio-suite/profiles/{name}", async (string name, AudioProfileService profiles) =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "profile name is required" });
            var entry = await profiles.SaveCurrentAsync(name.Trim());
            return Results.Ok(new
            {
                name = entry.Name,
                order = entry.Order,
                parked = entry.Parked,
                masterBypass = entry.MasterBypass,
                vstStates = entry.PluginStates.Count,
                createdUtc = entry.CreatedUtc,
                updatedUtc = entry.UpdatedUtc,
            });
        });
        // POST applies the named profile to the live chain.
        app.MapPost("/api/audio-suite/profiles/{name}/apply", (string name, AudioProfileService profiles, ChainOrderService chainOrder) =>
        {
            if (profiles.Apply(name))
                return Results.Ok(new { pluginIds = chainOrder.CurrentOrder });
            return Results.NotFound(new { error = $"no audio profile named '{name}'" });
        });
        app.MapDelete("/api/audio-suite/profiles/{name}", (string name, AudioProfileService profiles) =>
        {
            return profiles.Delete(name)
                ? Results.Ok(new { deleted = name })
                : Results.NotFound(new { error = $"no audio profile named '{name}'" });
        });

        // Scan a directory for VST3 plugins and register each as an
        // installed Zeus plugin so it flows into the Audio Suite chain.
        // Each .vst3 becomes a generated plugin package (stub assembly +
        // synthesized manifest); already-registered VSTs are skipped.
        // Returns what was registered / skipped / failed.
        app.MapPost("/api/audio-suite/scan-vst-directory",
            async (ScanVstDirectoryRequest body, VstDirectoryScanService scanner,
                   ChainOrderService chainOrder, CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Directory))
                return Results.BadRequest(new { error = "directory is required" });
            try
            {
                var result = await scanner.ScanAsync(body.Directory, ct);
                // Scanned VSTs always land in Available, never the active chain: a
                // scan must not change what's processing audio. Without this, a
                // freshly-registered id that was previously active would rejoin the
                // live chain on its own.
                chainOrder.ParkAll(result.Registered.Select(r => r.Id).ToList());
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

        // Audio Suite chain-level IN/OUT signal meters. Linear peak of
        // the block entering / leaving the TX insert chain, plus dBFS.
        // Reads 0 until the chain processes audio — which only happens
        // during MOX/TX or desktop-mode audition (mic preview). The
        // Audio Suite window polls this ~15 Hz while open, mirroring the
        // per-plugin /meters polling (no new WS frame / wire-format
        // change).
        app.MapGet("/api/audio-suite/chain/meters", (AudioPluginBridge bridge) =>
        {
            var (inPk, outPk) = bridge.ChainMeters;
            static double ToDb(float lin) => lin <= 1e-6f ? -120.0 : 20.0 * Math.Log10(lin);
            return Results.Ok(new
            {
                inputPeak = inPk,
                outputPeak = outPk,
                inputDb = ToDb(inPk),
                outputDb = ToDb(outPk),
            });
        });

        // Audio Suite VST editor (plug-in GUI). Opens the plugin's REAL
        // native editor window on the host desktop via the in-process VST
        // bridge — the same way a standalone VST host shows a plugin
        // window (Windows-only at present). POST opens, DELETE closes,
        // GET reports whether the window is currently up.
        static IResult MapEditorResult(EditorActionResult r, bool open) => r switch
        {
            EditorActionResult.Ok        => Results.Ok(new { open }),
            EditorActionResult.NotFound  => Results.NotFound(new { error = "No such plugin in the TX chain." }),
            EditorActionResult.NotAVst   => Results.BadRequest(new { error = "This plugin has no native VST editor." }),
            EditorActionResult.NotLoaded => Results.Json(
                new { error = "This VST didn't load, so it has no editor to show. It may not be a valid VST3 audio-effect (check the server log), or native VST hosting is off (headless/server mode keeps it gated — run the desktop app, or set ZEUS_ENABLE_VST_LOAD=1)." },
                statusCode: 409),
            EditorActionResult.Failed    => Results.Json(
                new { error = "Failed to open the plugin editor (unsupported on this platform?)." },
                statusCode: 500),
            _                            => Results.StatusCode(500),
        };

        // Editor routing is mode-aware (host consolidation): when the
        // out-of-process engine is active (VST processing mode) the editor is
        // hosted crash-isolated in the engine process — the same instance that
        // is processing audio — so we drive it via open_editor/close_editor.
        // Otherwise we fall back to the in-process zeus-vst-bridge editor.
        app.MapGet("/api/audio-suite/plugins/{id}/editor",
            (string id, AudioPluginBridge bridge, AudioProcessingModeService mode) =>
            Results.Ok(new { open = mode.EngineActive ? mode.IsEditorOpen(id) : bridge.IsEditorOpen(id) }));

        app.MapPost("/api/audio-suite/plugins/{id}/editor",
            (string id, AudioPluginBridge bridge, AudioProcessingModeService mode) =>
            MapEditorResult(mode.EngineActive ? mode.OpenEditor(id) : bridge.OpenEditor(id), open: true));

        app.MapDelete("/api/audio-suite/plugins/{id}/editor",
            (string id, AudioPluginBridge bridge, AudioProcessingModeService mode) =>
            MapEditorResult(mode.EngineActive ? mode.CloseEditor(id) : bridge.CloseEditor(id), open: false));

        // WAV recorder / player. Records RX or processed-TX audio to float32
        // WAVs in the Downloads folder, and plays recordings back to the local
        // monitor (no keying). Over-the-air playback is a later layer.
        app.MapGet("/api/wav/status", (Zeus.Server.Wav.WavRecorderService wav) =>
            Results.Ok(wav.GetStatus()));
        app.MapGet("/api/wav/list", (Zeus.Server.Wav.WavRecorderService wav) =>
            Results.Ok(new { dir = wav.RecordingsDir, recordings = wav.ListRecordings() }));
        app.MapPost("/api/wav/record/start",
            (WavRecordStartRequest body, Zeus.Server.Wav.WavRecorderService wav) =>
        {
            var source = string.Equals(body?.Source, "tx", StringComparison.OrdinalIgnoreCase)
                ? Zeus.Server.Wav.WavRecordSource.Tx
                : Zeus.Server.Wav.WavRecordSource.Rx;
            try { return Results.Ok(new { file = wav.StartRecording(source) }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost("/api/wav/record/stop", (Zeus.Server.Wav.WavRecorderService wav) =>
        {
            var r = wav.StopRecording();
            return r is { } x
                ? Results.Ok(new { file = Path.GetFileName(x.Path), samples = x.Samples })
                : Results.Ok(new { file = (string?)null, samples = 0L });
        });
        app.MapPost("/api/wav/play",
            (WavPlayRequest body, Zeus.Server.Wav.WavRecorderService wav) =>
        {
            if (string.IsNullOrWhiteSpace(body?.File))
                return Results.BadRequest(new { error = "file is required" });
            try { wav.Play(body.File); return Results.Ok(wav.GetStatus()); }
            catch (FileNotFoundException) { return Results.NotFound(new { error = "recording not found" }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost("/api/wav/stop", (Zeus.Server.Wav.WavRecorderService wav) =>
        {
            wav.StopPlayback();
            return Results.Ok(wav.GetStatus());
        });
        app.MapDelete("/api/wav/{file}", (string file, Zeus.Server.Wav.WavRecorderService wav) =>
        {
            try { wav.DeleteRecording(file); return Results.Ok(new { deleted = file }); }
            catch (FileNotFoundException) { return Results.NotFound(new { error = "recording not found" }); }
        });

        app.MapGet("/api/state", (RadioService r) => r.Snapshot());

        // TX diagnostic — exposes the producer/consumer counts for the mic-to-IQ ring
        // so we can verify end-to-end wiring without relying on logging. Safe to leave
        // in as it's free to call and reveals nothing that isn't already in DI.
        // TX wiring diagnostic: verifies producer (TxAudioIngest) and consumer
        // (Protocol1Client via ITxIqSource) stats. Useful for "is the mic reaching
        // TXA, and is the EP2 packer actually reading the ring" questions without
        // hunting through logs. Free to call, exposes no secrets.
        app.MapGet("/api/tx/diag", (Zeus.Protocol1.TxIqRing ring, Zeus.Protocol1.ITxIqSource src, TxAudioIngest ingest, AudioPluginBridge? pluginBridge, Zeus.Plugins.Host.Audio.VstEngineController? vstEngine) =>
        {
            return Results.Ok(new
            {
                iqSourceType = src.GetType().FullName,
                iqSourceIsRing = ReferenceEquals(src, ring),
                ring = new { ring.TotalWritten, ring.TotalRead, ring.Count, ring.Dropped, ring.Capacity, ring.RecentMag },
                ingest = new { ingest.TotalMicSamples, ingest.TotalTxBlocks, ingest.DroppedFrames },
                txPlugins = pluginBridge is null ? null : new
                {
                    masterBypassed = pluginBridge.IsMasterBypassed,
                    bypassedForRemoteTx = pluginBridge.IsBypassedForRemoteTxSource,
                },
                vstEngine = vstEngine is null ? null : new
                {
                    active = vstEngine.IsActive,
                    degradedBlocks = vstEngine.DegradedBlocks,
                },
            });
        });

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

        app.MapPost("/api/connect", async (ConnectRequest req, RadioService r, WdspWisdomInitializer wisdom, HttpContext ctx) =>
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
            var p1BoardKind = req.BoardId is byte bid ? MapBoardByte(bid) : HpsdrBoardKind.Unknown;

            try
            {
                var state = await r.ConnectAsync(req.Endpoint, req.SampleRate, ctx.RequestAborted, p1BoardKind);
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

        app.MapPost("/api/connect/p2", async (ConnectRequest req, DspPipelineService dsp, WdspWisdomInitializer wisdom, HttpContext ctx) =>
        {
            log.LogInformation("api.connect.p2 endpoint={Ep} rate={Rate}", req.Endpoint, req.SampleRate);

            if (wisdom.Phase != WisdomPhase.Ready)
                log.LogWarning("api.connect.p2 proceeding before wisdom ready; WDSP may fall back to synthetic");

            if (!TryParseIpEndpoint(req.Endpoint, out var ipEndpoint))
                return Results.BadRequest(new { error = $"Invalid endpoint '{req.Endpoint}'." });

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
            var boardKind = req.BoardId is byte b ? MapBoardByte(b) : HpsdrBoardKind.Unknown;

            try
            {
                rateKhz = await dsp.ConnectP2Async(ipEndpoint, rateKhz, numAdc: 2, ctx.RequestAborted, boardKind);
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
            log.LogInformation("api.vfo hz={Hz}", req.Hz);
            return r.SetVfo(req.Hz);
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
            log.LogInformation("api.mode mode={Mode}", req.Mode);
            return r.SetMode(req.Mode);
        });

        app.MapPost("/api/bandwidth", (BandwidthSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.bandwidth low={L} high={H}", req.Low, req.High);
            return r.SetFilter(req.Low, req.High);
        });

        // TX bandpass filter — signed Hz pair (LSB negative, DSB symmetric). Per-mode
        // family memory is managed in RadioService, identical shape to the RX filter.
        // Operator-editable via Settings → TX Filter panel.
        app.MapPost("/api/tx-filter", (TxFilterSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.tx-filter low={L} high={H}", req.LowHz, req.HighHz);
            return r.SetTxFilter(req.LowHz, req.HighHz);
        });

        // Filter preset endpoints (PRD §5.2). These are the preferred filter surface;
        // /api/bandwidth remains for backward compat. POST /api/filter also accepts
        // an optional PresetName to track which chip is active.
        app.MapPost("/api/filter", (FilterSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.filter low={L} high={H} preset={P}", req.LowHz, req.HighHz, req.PresetName);
            return r.SetFilter(req.LowHz, req.HighHz, req.PresetName);
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
                "api.rx.squelch enabled={Enabled} level={Level}",
                req.Squelch.Enabled, req.Squelch.Level);
            if (req.Squelch.Level < 0 || req.Squelch.Level > 100)
                return Results.BadRequest(new { error = $"Squelch Level {req.Squelch.Level} out of range 0..100" });
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

        app.MapGet("/api/tx/station-profiles", (TxStationProfileStore store) =>
            Results.Ok(new TxStationProfilesResponse(store.GetAll())));

        app.MapPut("/api/tx/station-profiles/{id}", (string id, TxStationProfileDto req, TxStationProfileStore store) =>
        {
            var routeId = id.Trim().ToLowerInvariant();
            if (!TryValidateTxStationProfileId(routeId, out var idErr))
                return Results.BadRequest(new { error = idErr });
            if (!string.Equals(req.Id, routeId, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "profile id must match route id" });

            var profile = req with { Id = routeId };
            if (!TryValidateTxStationProfile(profile, out var err))
                return Results.BadRequest(new { error = err });

            var saved = store.Upsert(profile);
            log.LogInformation(
                "api.tx.stationProfile id={Id} mic={Mic:F1} leveler={Leveler:F1} low={Low} high={High} density={Density}",
                saved.Id, saved.MicGainDb, saved.LevelerMaxGainDb, saved.LowCutHz, saved.HighCutHz, saved.SpectralDensity);
            return Results.Ok(saved);
        });

        app.MapDelete("/api/tx/station-profiles/{id}", (string id, TxStationProfileStore store) =>
        {
            var routeId = id.Trim().ToLowerInvariant();
            if (!TryValidateTxStationProfileId(routeId, out var idErr))
                return Results.BadRequest(new { error = idErr });
            var removed = store.Delete(routeId);
            log.LogInformation("api.tx.stationProfile.reset id={Id} removed={Removed}", routeId, removed);
            return Results.Ok(new { id = routeId, removed });
        });

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
            // Forward keyer speed (WPM) + mode to the radio's on-board iambic
            // keyer (C&C 0x0B) so a paddle keys at the panel speed. No-op when
            // no radio is connected (the value is cached + re-pushed on the
            // next connect). See zeus-bks.
            radio.SetCwKeyerConfig(snapshot.Wpm, snapshot.KeyerMode);
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

        // TX Monitor — audition-path toggle (issue #106 follow-up). Engages a
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

        app.MapPost("/api/rx/zoom", (ZoomSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.rx.zoom level={Level}", req.Level);
            if (req.Level < SyntheticDspEngine.MinZoomLevel || req.Level > SyntheticDspEngine.MaxZoomLevel)
                return Results.BadRequest(new { error = $"zoom level must be in [{SyntheticDspEngine.MinZoomLevel},{SyntheticDspEngine.MaxZoomLevel}]; got {req.Level}" });
            return Results.Ok(r.SetZoom(req.Level));
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

        app.MapPut("/api/display-settings", (DisplaySettingsSetRequest req, DisplaySettingsStore store) =>
        {
            if (string.IsNullOrWhiteSpace(req.Mode) || string.IsNullOrWhiteSpace(req.Fit))
                return Results.BadRequest(new { error = "mode and fit required" });
            store.SaveMode(req.Mode, req.Fit, req.RxTraceColor,
                req.DbMin, req.DbMax, req.TxDbMin, req.TxDbMax,
                req.WfDbMin, req.WfDbMax, req.WfTxDbMin, req.WfTxDbMax);
            return Results.Ok(store.Get());
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
        // tiebreaker. Preferred=="Auto" removes the override (stored as absence,
        // not a sentinel enum value). Effective = Connected when connected (which
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
        app.MapGet("/api/radio/diagnostics", (HardwareDiagnosticsService diag) =>
        {
            return Results.Ok(diag.Snapshot());
        });

        app.MapPost("/api/radio/diagnostics/map/reset", (HardwareDiagnosticsService diag) =>
        {
            diag.ResetMapping();
            return Results.Ok(diag.Snapshot());
        });

        app.MapPost("/api/radio/diagnostics/map/marker", (HardwareDiagnosticsMarkerRequest req, HardwareDiagnosticsService diag) =>
        {
            diag.AddMappingMarker(req.Label, req.Notes);
            return Results.Ok(diag.Snapshot());
        });

        app.MapPost("/api/radio/diagnostics/dsp-scene", (FrontendDspSceneDiagnosticsRequest req, FrontendDspSceneDiagnosticsService scene) =>
        {
            scene.Update(req);
            return Results.Ok(scene.Snapshot());
        });

        app.MapGet("/api/dsp/nr-condition", (FrontendDspSceneDiagnosticsService scene, DspPipelineService dsp) =>
        {
            return Results.Ok(scene.SmartNrCondition(dsp.SnapshotNrRuntime()));
        });

        app.MapGet("/api/tx/external-ptt", (ExternalPttService externalPtt) =>
        {
            return Results.Ok(externalPtt.Snapshot());
        });

        app.MapGet("/api/cw/hardware-keying", (HardwareDiagnosticsService diag, ExternalPttService externalPtt) =>
        {
            return Results.Ok(diag.KeyingSnapshot(externalPtt.Snapshot()));
        });

        app.MapGet("/api/radio/power-calibration", (HardwareDiagnosticsService diag) =>
        {
            return Results.Ok(diag.PowerCalibrationSnapshot());
        });

        app.MapGet("/api/radio/supply-alarms", (HardwareDiagnosticsService diag) =>
        {
            return Results.Ok(diag.SupplyAlarmsSnapshot());
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

        app.MapGet("/api/log/entries", async (LogService logService, HttpContext ctx, int skip = 0, int take = 100) =>
        {
            var response = await logService.GetLogEntriesAsync(skip, take, ctx.RequestAborted);
            return Results.Ok(response);
        });

        app.MapPost("/api/log/entry", async (CreateLogEntryRequest req, LogService logService, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(req.Callsign))
                return Results.BadRequest(new { error = "callsign required" });
            var entry = await logService.CreateLogEntryAsync(req, ctx.RequestAborted);
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

        return app;
    }

    // ---------- helpers (formerly local functions in Program.cs) -------------

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

    // Mirrors the byte→enum maps in Zeus.Protocol1.Discovery.ReplyParser and
    // Zeus.Protocol2.Discovery.ReplyParser. Kept inline (not factored to a
    // shared helper) because those parsers are deliberately self-contained
    // per protocol; this is the connect-time projection of the same table.
    static HpsdrBoardKind MapBoardByte(byte raw) => raw switch
    {
        0x00 => HpsdrBoardKind.Metis,
        0x01 => HpsdrBoardKind.Hermes,
        0x02 => HpsdrBoardKind.HermesII,
        0x04 => HpsdrBoardKind.Angelia,
        0x05 => HpsdrBoardKind.Orion,
        0x06 => HpsdrBoardKind.HermesLite2,
        0x0A => HpsdrBoardKind.OrionMkII,
        0x14 => HpsdrBoardKind.HermesC10,
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
internal sealed record AuditionSetRequest(bool Enabled);
internal sealed record ChainOrderSetRequest(List<string> PluginIds);
internal sealed record ChainMembershipSetRequest(bool Active);
internal sealed record ScanVstDirectoryRequest(string Directory);
internal sealed record MasterBypassSetRequest(bool Bypassed);
internal sealed record ProcessingModeSetRequest(string Mode);
internal sealed record HardwareDiagnosticsMarkerRequest(string? Label, string? Notes);
internal sealed record WavRecordStartRequest(string? Source);
internal sealed record WavPlayRequest(string? File);
