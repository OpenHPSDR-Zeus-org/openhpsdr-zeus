// SPDX-License-Identifier: GPL-2.0-or-later
//
// Route surface for the Zeus host. Extracted from the original
// Zeus.Server/Program.cs top-level statements so that both Zeus.Server
// (service mode) and Zeus.Desktop (Photino in-process mode) share one
// endpoint definition.

using System.Net;
using Zeus.Contracts;
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

        // Capabilities snapshot — host-mode + platform + feature gates.
        // Frontend fetches once on app mount and hides UI for unavailable
        // features (e.g. TX Audio Tools when vstHost.available=false).
        app.MapGet("/api/capabilities",
            (CapabilitiesService caps) => Results.Ok(caps.Snapshot()));

        app.MapGet("/api/state", (RadioService r) => r.Snapshot());

        // TX diagnostic — exposes the producer/consumer counts for the mic-to-IQ ring
        // so we can verify end-to-end wiring without relying on logging. Safe to leave
        // in as it's free to call and reveals nothing that isn't already in DI.
        // TX wiring diagnostic: verifies producer (TxAudioIngest) and consumer
        // (Protocol1Client via ITxIqSource) stats. Useful for "is the mic reaching
        // TXA, and is the EP2 packer actually reading the ring" questions without
        // hunting through logs. Free to call, exposes no secrets.
        app.MapGet("/api/tx/diag", (Zeus.Protocol1.TxIqRing ring, Zeus.Protocol1.ITxIqSource src, TxAudioIngest ingest) =>
        {
            return Results.Ok(new
            {
                iqSourceType = src.GetType().FullName,
                iqSourceIsRing = ReferenceEquals(src, ring),
                ring = new { ring.TotalWritten, ring.TotalRead, ring.Count, ring.Dropped, ring.Capacity, ring.RecentMag },
                ingest = new { ingest.TotalMicSamples, ingest.TotalTxBlocks, ingest.DroppedFrames },
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
                    ["gatewareBuild"] = r.Details.GatewareBuild.ToString(),
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
                    ["protocolSupported"] = r.Details.ProtocolSupported.ToString(),
                    ["numReceivers"] = r.Details.NumReceivers.ToString(),
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

            try
            {
                var state = await r.ConnectAsync(req.Endpoint, req.SampleRate, ctx.RequestAborted);
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
                _ => 192,
            };

            // Plumb the discovered board byte through so RadioService can
            // surface the real board kind instead of defaulting to OrionMkII
            // for every P2 connection (issue #171 — Brick2 is Hermes/0x01 on P2).
            var boardKind = req.BoardId is byte b ? MapBoardByte(b) : HpsdrBoardKind.Unknown;

            try
            {
                await dsp.ConnectP2Async(ipEndpoint, rateKhz, numAdc: 2, ctx.RequestAborted, boardKind);
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

        // Mic-gain: N dB in [-40, +10], scales WDSP TXA panel-gain-1 the same
        // way Thetis does (console.cs:28805 setAudioMicGain → Audio.MicPreamp =
        // 10^(db/20) → cmaster.CMSetTXAPanelGain1). The negative range is the
        // important half: browser getUserMedia mics typically peak around
        // -10..-15 dBFS, which over-drives WDSP TXA + ALC and prints as
        // splatter on the air; without an attenuator the operator has nowhere
        // to back off. Range matches Thetis's MicGainMin/Max defaults
        // (console.cs:19151 = -40, :19163 = +10).
        app.MapPost("/api/mic-gain", (MicGainSetRequest req, DspPipelineService pipe) =>
        {
            int db = Math.Clamp(req.Db, -40, 10);
            double gain = Math.Pow(10.0, db / 20.0);
            pipe.CurrentEngine?.SetTxPanelGain(gain);
            return Results.Ok(new { micGainDb = db });
        });

        // Leveler max-gain ceiling in dB. Operator-safe band is 0..15 dB: 0 disables
        // the headroom entirely (unity-cap Leveler) and 15 matches Thetis's stock
        // ceiling (radio.cs:2979 tx_leveler_max_gain = 15.0). Anything outside is a
        // 400 so a misbehaving client can't hand WDSP a value that'd saturate on
        // the first voiced sample. The server is stateless for this setting —
        // frontend re-POSTs on WS reconnect to re-sync after a server restart.
        app.MapPost("/api/tx/leveler-max-gain", (LevelerMaxGainSetRequest req, DspPipelineService pipe) =>
        {
            if (req.Gain < 0.0 || req.Gain > 15.0 || double.IsNaN(req.Gain))
                return Results.BadRequest(new { error = "gain must be 0..15 dB" });
            log.LogInformation("api.tx.levelerMaxGain dB={Db:F1}", req.Gain);
            pipe.CurrentEngine?.SetTxLevelerMaxGain(req.Gain);
            return Results.Ok(new { levelerMaxGainDb = req.Gain });
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

        // PureSignal master arm + cal-mode. P1 is gated off in the frontend in v1
        // because the Protocol1Client wire-format work for PS isn't done yet, but
        // the server endpoint stays open — RadioService.SetPs sets the StateDto bit
        // and the engine receives SetPsEnabled either way; only the radio-side
        // feedback path is P2-only. See hermes.md / TODO(ps-p1).
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
            store.SaveMode(req.Mode, req.Fit);
            return Results.Ok(store.Get());
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

        // ----------------------------------------------------------------
        //  RF2K-S amplifier (network device, REST + RFB click injection)
        // ----------------------------------------------------------------
        app.MapGet("/api/rf2k/status", (Rf2kService rf) => rf.GetStatus());

        app.MapGet("/api/rf2k/config", (Rf2kService rf) => rf.GetConfig());

        app.MapPost("/api/rf2k/config", async (Rf2kConfig req, Rf2kService rf, HttpContext ctx) =>
        {
            log.LogInformation("api.rf2k.config enabled={En} host={Host} port={Port} vncPort={VncPort}",
                req.Enabled, req.Host, req.Port, req.VncPort);
            var status = await rf.SetConfigAsync(req, ctx.RequestAborted);
            return Results.Ok(status);
        });

        app.MapPost("/api/rf2k/operate", async (Rf2kSetOperateRequest req, Rf2kService rf, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(req.Mode))
                return Results.BadRequest(new { error = "mode required (OPERATE or STANDBY)" });
            var status = await rf.SetOperateModeAsync(req.Mode, ctx.RequestAborted);
            return Results.Ok(status);
        });

        app.MapPost("/api/rf2k/interface", async (Rf2kSetInterfaceRequest req, Rf2kService rf, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(req.Interface))
                return Results.BadRequest(new { error = "interface required (UNIV/CAT/UDP/TCI)" });
            var status = await rf.SetOperationalInterfaceAsync(req.Interface, ctx.RequestAborted);
            return Results.Ok(status);
        });

        app.MapPost("/api/rf2k/antenna", async (Rf2kSetAntennaRequest req, Rf2kService rf, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(req.Type))
                return Results.BadRequest(new { error = "type required (INTERNAL or EXTERNAL)" });
            var status = await rf.SetActiveAntennaAsync(req.Type, req.Number, ctx.RequestAborted);
            return Results.Ok(status);
        });

        app.MapPost("/api/rf2k/reset", async (Rf2kService rf, HttpContext ctx) =>
        {
            var status = await rf.ResetErrorAsync(ctx.RequestAborted);
            return Results.Ok(status);
        });

        app.MapPost("/api/rf2k/test", async (Rf2kTestRequest req, Rf2kService rf, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(req.Host) || req.Port is <= 0 or >= 65536)
                return Results.BadRequest(new { error = "host and port required" });
            var result = await rf.TestAsync(req.Host.Trim(), req.Port, ctx.RequestAborted);
            return Results.Ok(result);
        });

        // VNC click injection — Tune/Bypass and arbitrary calibration click.
        app.MapPost("/api/rf2k/tune", async (Rf2kService rf, HttpContext ctx) =>
        {
            var result = await rf.SendTuneClickAsync(ctx.RequestAborted);
            return Results.Ok(result);
        });

        app.MapPost("/api/rf2k/bypass", async (Rf2kService rf, HttpContext ctx) =>
        {
            var result = await rf.SendBypassClickAsync(ctx.RequestAborted);
            return Results.Ok(result);
        });

        app.MapPost("/api/rf2k/click", async (Rf2kClickRequest req, Rf2kService rf, HttpContext ctx) =>
        {
            var result = await rf.SendTestClickAsync(req.X, req.Y, ctx.RequestAborted);
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

        // ----------------------------------------------------------------
        //  VST plugin host (Wave 6a — backend only; Wave 6b ships UI)
        // ----------------------------------------------------------------
        // All endpoints group under /api/plughost. Async ones use the
        // request-aborted token so a navigating browser stops waiting.
        // 8 slots, 0..7; bad slot → 400. Sidecar lifecycle is owned by
        // VstHostHostedService — REST handlers never poke PluginHostManager
        // directly so persistence and SignalR pushes stay coherent.
        app.MapPost("/api/plughost/master", async (
            VstHostMasterRequest req, VstHostHostedService svc, HttpContext ctx) =>
        {
            try
            {
                await svc.SetMasterEnabledAsync(req.Enabled, ctx.RequestAborted);
                return Results.Ok(svc.GetState());
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "api.plughost.master set failed enabled={En}", req.Enabled);
                return Results.Problem(detail: ex.Message, statusCode: 409);
            }
        });

        app.MapGet("/api/plughost/state",
            (VstHostHostedService svc) => Results.Ok(svc.GetState()));

        app.MapGet("/api/plughost/catalog", async (
            bool? rescan,
            Zeus.PluginHost.Discovery.IPluginScanner scanner,
            VstHostHostedService svc,
            HttpContext ctx) =>
        {
            // Default-paths-only unless rescan=true (then include custom).
            var roots = new List<string>(
                Zeus.PluginHost.Discovery.DefaultPluginPaths.ForCurrentPlatform());
            if (rescan == true)
            {
                foreach (var p in svc.CustomSearchPaths)
                {
                    if (Directory.Exists(p) && !roots.Contains(p)) roots.Add(p);
                }
            }
            var manifests = await scanner.ScanAsync(roots, ctx.RequestAborted);
            var dtos = manifests.Select(m => new
            {
                filePath = m.FilePath,
                displayName = m.DisplayName,
                format = m.Format.ToString(),
                platform = m.Platform.ToString(),
                bitness = m.Bitness.ToString(),
                bundlePath = m.BundlePath,
                warnings = m.ScanWarnings,
            });
            return Results.Ok(new { plugins = dtos });
        });

        app.MapGet("/api/plughost/searchPaths",
            (VstHostHostedService svc) =>
                Results.Ok(new { paths = svc.CustomSearchPaths }));

        app.MapPost("/api/plughost/searchPaths", (
            VstHostSearchPathRequest req, VstHostHostedService svc) =>
        {
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { error = "path is required" });
            if (!Directory.Exists(req.Path))
                return Results.BadRequest(new { error = "directory does not exist" });
            var added = svc.AddCustomSearchPath(req.Path);
            return Results.Ok(new { added, paths = svc.CustomSearchPaths });
        });

        // DELETE doesn't allow inferred body params on minimal API; the path
        // travels as a query string (?path=...) so the URL-encoded path is
        // self-contained.
        app.MapDelete("/api/plughost/searchPaths", (
            string? path, VstHostHostedService svc) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path is required" });
            var removed = svc.RemoveCustomSearchPath(path);
            return Results.Ok(new { removed, paths = svc.CustomSearchPaths });
        });

        app.MapGet("/api/plughost/slots/{idx:int}", (
            int idx, VstHostHostedService svc, Zeus.PluginHost.IPluginHost host) =>
        {
            if (idx < 0 || idx >= host.MaxChainSlots)
                return Results.BadRequest(new { error = "slot index out of range" });
            var slot = host.Slots[idx];
            var path = svc.GetSlotPath(idx);
            var paramDtos = slot.Parameters.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                units = p.Units,
                defaultValue = p.DefaultValue,
                currentValue = p.CurrentValue,
                stepCount = p.StepCount,
                flags = (byte)p.Flags,
            });
            return Results.Ok(new
            {
                index = slot.Index,
                plugin = slot.Plugin is null ? null : new
                {
                    name = slot.Plugin.Name,
                    vendor = slot.Plugin.Vendor,
                    version = slot.Plugin.Version,
                    path = path,
                },
                bypass = slot.Bypass,
                parameters = paramDtos,
            });
        });

        app.MapPost("/api/plughost/slots/{idx:int}/load", async (
            int idx, VstHostSlotLoadRequest req,
            VstHostHostedService svc, Zeus.PluginHost.IPluginHost host,
            HttpContext ctx) =>
        {
            if (idx < 0 || idx >= host.MaxChainSlots)
                return Results.BadRequest(new { error = "slot index out of range" });
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { error = "path is required" });
            // Phases 5a + 6 added Vestige (Linux VST2 .so) and CLAP (.clap)
            // alongside VST3. The sidecar's PluginChain dispatches by file
            // extension at LoadSlot time. Anything that's not one of those
            // three formats falls through to a single descriptive error.
            bool isVst3 = req.Path.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase);
            bool isVst2 = req.Path.EndsWith(".so",  StringComparison.OrdinalIgnoreCase)
                       || req.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            bool isClap = req.Path.EndsWith(".clap", StringComparison.OrdinalIgnoreCase);
            if (!isVst3 && !isVst2 && !isClap)
                return Results.BadRequest(new
                {
                    error = "unsupported plugin format (expected .vst3 / .so / .dll / .clap)",
                });
            try
            {
                var outcome = await svc.LoadSlotAsync(idx, req.Path, ctx.RequestAborted);
                if (!outcome.Ok)
                {
                    return Results.Problem(
                        detail: outcome.Error ?? "load failed",
                        statusCode: 409);
                }
                return Results.Ok(new
                {
                    index = idx,
                    plugin = outcome.Info is null ? null : new
                    {
                        name = outcome.Info.Name,
                        vendor = outcome.Info.Vendor,
                        version = outcome.Info.Version,
                        path = req.Path,
                    },
                });
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "api.plughost.slots.load slot={Slot}", idx);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/plughost/slots/{idx:int}/unload", async (
            int idx, VstHostHostedService svc, Zeus.PluginHost.IPluginHost host,
            HttpContext ctx) =>
        {
            if (idx < 0 || idx >= host.MaxChainSlots)
                return Results.BadRequest(new { error = "slot index out of range" });
            try
            {
                await svc.UnloadSlotAsync(idx, ctx.RequestAborted);
                return Results.Ok(new { index = idx });
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "api.plughost.slots.unload slot={Slot}", idx);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/plughost/slots/{idx:int}/bypass", async (
            int idx, VstHostBypassRequest req,
            VstHostHostedService svc, Zeus.PluginHost.IPluginHost host,
            HttpContext ctx) =>
        {
            if (idx < 0 || idx >= host.MaxChainSlots)
                return Results.BadRequest(new { error = "slot index out of range" });
            try
            {
                await svc.SetSlotBypassAsync(idx, req.Bypass, ctx.RequestAborted);
                return Results.Ok(new { index = idx, bypass = req.Bypass });
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "api.plughost.slots.bypass slot={Slot}", idx);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapGet("/api/plughost/slots/{idx:int}/parameters", async (
            int idx, VstHostHostedService svc, Zeus.PluginHost.IPluginHost host,
            HttpContext ctx) =>
        {
            if (idx < 0 || idx >= host.MaxChainSlots)
                return Results.BadRequest(new { error = "slot index out of range" });
            var list = await svc.ListSlotParametersAsync(idx, ctx.RequestAborted);
            var dtos = list.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                units = p.Units,
                defaultValue = p.DefaultValue,
                currentValue = p.CurrentValue,
                stepCount = p.StepCount,
                flags = (byte)p.Flags,
            });
            return Results.Ok(new { parameters = dtos });
        });

        app.MapPost("/api/plughost/slots/{idx:int}/parameters/{paramId:long}", async (
            int idx, long paramId, VstHostParameterRequest req,
            VstHostHostedService svc, Zeus.PluginHost.IPluginHost host,
            HttpContext ctx) =>
        {
            if (idx < 0 || idx >= host.MaxChainSlots)
                return Results.BadRequest(new { error = "slot index out of range" });
            if (paramId < 0 || paramId > uint.MaxValue)
                return Results.BadRequest(new { error = "paramId out of range" });
            if (req.Value < 0.0 || req.Value > 1.0)
                return Results.BadRequest(new { error = "value must be in [0,1]" });
            try
            {
                await svc.SetSlotParameterAsync(idx, (uint)paramId, req.Value,
                    ctx.RequestAborted);
                return Results.Ok(new { index = idx, paramId, value = req.Value });
            }
            catch (Exception ex)
            {
                log.LogWarning(ex,
                    "api.plughost.slots.parameter slot={Slot} param={Id}", idx, paramId);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/plughost/slots/{idx:int}/editor/show", async (
            int idx, VstHostHostedService svc, Zeus.PluginHost.IPluginHost host,
            HttpContext ctx) =>
        {
            if (idx < 0 || idx >= host.MaxChainSlots)
                return Results.BadRequest(new { error = "slot index out of range" });
            var outcome = await svc.ShowSlotEditorAsync(idx, ctx.RequestAborted);
            if (!outcome.Ok)
            {
                return Results.Problem(
                    detail: outcome.Error ?? "editor not available",
                    statusCode: 409);
            }
            return Results.Ok(new { index = idx, width = outcome.Width, height = outcome.Height });
        });

        app.MapPost("/api/plughost/slots/{idx:int}/editor/hide", async (
            int idx, VstHostHostedService svc, Zeus.PluginHost.IPluginHost host,
            HttpContext ctx) =>
        {
            if (idx < 0 || idx >= host.MaxChainSlots)
                return Results.BadRequest(new { error = "slot index out of range" });
            var closed = await svc.HideSlotEditorAsync(idx, ctx.RequestAborted);
            return Results.Ok(new { index = idx, closed });
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
        if (rate is 48_000 or 96_000 or 192_000 or 384_000) { error = ""; return true; }
        error = $"sampleRate must be one of {{48000, 96000, 192000, 384000}}, got {rate}.";
        return false;
    }

    static bool TryValidateAttenDb(int db, out string error)
    {
        if (db >= HpsdrAtten.MinDb && db <= HpsdrAtten.MaxDb) { error = ""; return true; }
        error = $"atten must be in {HpsdrAtten.MinDb}..{HpsdrAtten.MaxDb} dB, got {db}.";
        return false;
    }

    static HpsdrSampleRate MapHpsdrSampleRate(int hz) => hz switch
    {
        48_000 => HpsdrSampleRate.Rate48k,
        96_000 => HpsdrSampleRate.Rate96k,
        192_000 => HpsdrSampleRate.Rate192k,
        384_000 => HpsdrSampleRate.Rate384k,
        _ => throw new ArgumentOutOfRangeException(nameof(hz), hz, "validate before calling"),
    };
}
