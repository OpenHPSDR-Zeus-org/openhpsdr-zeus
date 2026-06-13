// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using Microsoft.Extensions.Options;
using Zeus.Contracts;

namespace Zeus.Server.Tci;

/// <summary>
/// TCI (Transceiver Control Interface) server. Accepts ExpertSDR3-compatible
/// WebSocket clients on a dedicated Kestrel listener (default :40001) wired
/// in Program.cs via a port-branched middleware. Spoken by loggers (Log4OM,
/// N1MM+), digital-mode apps (JTDX, WSJT-X), and SDR display tools. Implements
/// TCI v1.8 per the ExpertSDR3 spec.
/// </summary>
// Hosted lifetime here only wires/unwires radio-event subscriptions and closes
// live sessions on shutdown — the HTTP accept loop lives in Kestrel, not here.
// (HttpListener on Windows needs a per-user urlacl for wildcard binds; Kestrel
// binds sockets directly, so clone-and-run works without elevation. See #30.)
public sealed class TciServer : IHostedService, IDisposable
{
    private readonly ILogger<TciServer> _log;
    private readonly TciOptions _options;
    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly DspPipelineService _pipeline;
    private readonly TxMetersService _txMeters;
    private readonly SpotManager _spots;
    private readonly TxAudioIngest _txAudioIngest;
    private readonly CwEngine _cwEngine;
    private readonly CwSettingsStore _cwSettings;
    private readonly ILoggerFactory _loggerFactory;
    // Tracks the previous CW engine state so we only emit cw_macros_empty
    // on the Sending → Idle transition (i.e. when the queue actually
    // drained), not on every Idle-stays-Idle status broadcast.
    private CwEngineState _lastCwState = CwEngineState.Idle;

    private readonly ConcurrentDictionary<Guid, TciSession> _clients = new();
    private bool _subscribed;

    // TX_CHRONO demand-driven pacing — mirrors Thetis cmaster.serviceTCITxProtocol.
    // WSJT-X generates all FT8 audio as fast as TX_CHRONO arrives, so we MUST
    // pace at exactly real-time (1 chrono per 2048 samples @ 48 kHz = 42.6667 ms).
    // Integer-millisecond pacing leaves a 1.6% rate error that — combined with
    // any timer drift — overflows TxAudioIngest._accumulator (2048 cap) every
    // ~1.8 s, dropping a full 42 ms block and breaking FT8 phase continuity.
    // Compute the spacing in stopwatch ticks (sub-ms) and pace off a monotonic
    // Stopwatch so DateTime jumps can't disturb TX timing.
    private const int TciTxBlockSamples = 2048;
    private const int TciTxBlockRate = 48000;
    private const int TciTxBurstCount = 3;
    private static readonly long TciTxChronoSpacingSwTicks =
        (long)Math.Round((double)TciTxBlockSamples * Stopwatch.Frequency / TciTxBlockRate);

    private int _tciTxSamplesInPipeline;
    private long _tciTxLastChronoSwTicks;
    private readonly Stopwatch _txChronoClock = new();
    private readonly object _tciTxStateLock = new();
    private Timer? _txChronoTimer;
    private byte[]? _txChronoFrame;

    internal void NotifyTxAudioQueued(int monoSamplesQueued)
    {
        lock (_tciTxStateLock)
        {
            _tciTxSamplesInPipeline += monoSamplesQueued;
        }
    }

    internal void NotifyWdspConsumed(int monoSamplesConsumed)
    {
        lock (_tciTxStateLock)
        {
            _tciTxSamplesInPipeline = Math.Max(0, _tciTxSamplesInPipeline - monoSamplesConsumed);
        }
    }

    public TciServer(
        IOptions<TciOptions> options,
        RadioService radio,
        TxService tx,
        DspPipelineService pipeline,
        TxMetersService txMeters,
        SpotManager spots,
        TxAudioIngest txAudioIngest,
        CwEngine cwEngine,
        CwSettingsStore cwSettings,
        ILoggerFactory loggerFactory)
    {
        _log = loggerFactory.CreateLogger<TciServer>();
        _options = options.Value;
        _radio = radio;
        _tx = tx;
        _pipeline = pipeline;
        _txMeters = txMeters;
        _spots = spots;
        _txAudioIngest = txAudioIngest;
        _cwEngine = cwEngine;
        _cwSettings = cwSettings;
        _loggerFactory = loggerFactory;
    }

    public int ClientCount => _clients.Count;

    public Task StartAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("tci.disabled (set Tci:Enabled=true to enable)");
            return Task.CompletedTask;
        }

        _radio.StateChanged += OnRadioStateChanged;
        _radio.Connected += OnRadioConnected;
        _radio.Disconnected += OnRadioDisconnected;
        _radio.PreampChanged += OnPreampChanged;
        _radio.MoxChanged += OnMoxChanged;
        _pipeline.RxMeterUpdated += OnRxMeterUpdated;
        _pipeline.RxIqAvailable += OnRxIqAvailable;
        _pipeline.RxAudioAvailable += OnRxAudioAvailable;
        _txMeters.TxMetersUpdated += OnTxMetersUpdated;
        _cwEngine.Status += OnCwEngineStatus;
        _subscribed = true;

        _log.LogInformation("tci.listening bind={Bind} port={Port}", _options.BindAddress, _options.Port);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_subscribed)
        {
            _radio.StateChanged -= OnRadioStateChanged;
            _radio.Connected -= OnRadioConnected;
            _radio.Disconnected -= OnRadioDisconnected;
            _radio.PreampChanged -= OnPreampChanged;
            _radio.MoxChanged -= OnMoxChanged;
            _pipeline.RxMeterUpdated -= OnRxMeterUpdated;
            _pipeline.RxIqAvailable -= OnRxIqAvailable;
            _pipeline.RxAudioAvailable -= OnRxAudioAvailable;
            _txMeters.TxMetersUpdated -= OnTxMetersUpdated;
            _cwEngine.Status -= OnCwEngineStatus;
            _subscribed = false;
        }

        StopTciTxService();

        _log.LogInformation("tci.stopping active={Count}", _clients.Count);

        foreach (var session in _clients.Values)
        {
            session.Dispose();
        }
        _clients.Clear();

        await Task.CompletedTask;
    }

    /// <summary>
    /// Handle an incoming HTTP request on the TCI listener port. Upgrades to a
    /// WebSocket, registers a session, and runs it to completion. Invoked from
    /// the port-branch middleware in Program.cs.
    /// </summary>
    public async Task AcceptAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        WebSocket ws;
        try
        {
            ws = await context.WebSockets.AcceptWebSocketAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "tci websocket upgrade failed");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        var id = Guid.NewGuid();
        var sessionLog = _loggerFactory.CreateLogger<TciSession>();
        var session = new TciSession(id, ws, sessionLog, _radio, _tx, _pipeline, _spots, _options, _cwEngine, _cwSettings, _txAudioIngest, this);

        _clients[id] = session;
        _log.LogInformation("tci.client.connected id={Id} total={Count}", id, _clients.Count);

        try
        {
            await session.RunAsync(context.RequestAborted);
        }
        finally
        {
            _clients.TryRemove(id, out _);
            session.Dispose();
            _log.LogInformation("tci.client.disconnected id={Id} total={Count}", id, _clients.Count);
        }
    }

    // --- Event handlers: broadcast state changes to all connected clients ---

    private int _lastBroadcastAttenDb = int.MinValue;

    private void OnRadioStateChanged(StateDto state)
    {
        // Broadcast VFO, mode, filter changes to all clients
        // Use rate limiting for VFO (can fire rapidly during tuning)
        BroadcastRateLimited("vfo:0,0", TciProtocol.Command("vfo", 0, 0, state.VfoHz));
        BroadcastRateLimited("vfo:0,1", TciProtocol.Command("vfo", 0, 1, state.VfoHz));
        BroadcastRateLimited("dds:0", TciProtocol.Command("dds", 0, state.VfoHz));

        // Mode and filter are less frequent — send immediately
        string tciMode = TciProtocol.ModeToTci(state.Mode);
        Broadcast(TciProtocol.Command("modulation", 0, tciMode));
        Broadcast(TciProtocol.Command("rx_filter_band", 0, state.FilterLowHz, state.FilterHighHz));

        // TX frequency event (derived from VFO)
        Broadcast(TciProtocol.Command("tx_frequency", state.VfoHz));

        // IF limits on sample rate change
        int halfRate = state.SampleRate / 2;
        Broadcast(TciProtocol.Command("if_limits", -halfRate, halfRate));

        // Step attenuator change → rx_step_att_ex (spec §5.4 S→C push)
        if (state.AttenDb != _lastBroadcastAttenDb)
        {
            _lastBroadcastAttenDb = state.AttenDb;
            Broadcast(TciProtocol.Command("rx_step_att_ex", 0, state.AttenDb));
        }
    }

    private void OnPreampChanged(bool on)
    {
        // rx_preamp_att_ex:<rx>,<int>  — combined preamp/attenuator push.
        // Spec format is loose; emit a single int (1=preamp on, 0=off).
        Broadcast(TciProtocol.Command("rx_preamp_att_ex", 0, on ? 1 : 0));
    }

    private void OnRadioConnected(Protocol1.IProtocol1Client client)
    {
        Broadcast(TciProtocol.Command("start"));
    }

    private void OnRadioDisconnected()
    {
        Broadcast(TciProtocol.Command("stop"));
    }

    private void OnRxMeterUpdated(int channelId, double dbm)
    {
        // TCI rx_smeter event: rx_smeter:<rx>,<chan>,<dbm>
        // Rate-limited to avoid flooding during rapid meter updates
        BroadcastRateLimited($"rx_smeter:0,{channelId}", TciProtocol.Command("rx_smeter", 0, channelId, (int)Math.Round(dbm)));

        // Spec §5.6 rx_channel_sensors:<rx>,...,<dBm> — combined-frame
        // telemetry, sent only to clients that opted in via rx_sensors_enable.
        if (_clients.IsEmpty) return;
        int dbmRounded = (int)Math.Round(dbm);
        var frame = TciProtocol.Command("rx_channel_sensors", 0, channelId, dbmRounded);
        foreach (var session in _clients.Values)
        {
            if (session.WantsRxSensors) session.Send(frame);
        }
    }

    private void OnRxIqAvailable(int receiver, int sampleRateHz, ReadOnlyMemory<double> interleavedIQ)
    {
        // The pooled IQ buffer is only valid for the duration of this call.
        // Build the binary frame synchronously (which copies samples into a
        // freshly allocated byte[]) so the enqueued payload outlives the buffer.
        if (_clients.IsEmpty) return;

        // Cheap pre-flight: skip the allocate+copy if no client wants this RX.
        bool anyWants = false;
        foreach (var session in _clients.Values)
        {
            if (session.WantsIqStream(receiver)) { anyWants = true; break; }
        }
        if (!anyWants) return;

        var payload = TciStreamPayload.BuildIqFromDoubles(receiver, sampleRateHz, interleavedIQ.Span);
        foreach (var session in _clients.Values)
        {
            if (session.WantsIqStream(receiver))
                session.SendBinary(payload);
        }
    }

    private void OnRxAudioAvailable(int receiver, int sampleRateHz, ReadOnlyMemory<float> samples)
    {
        if (_clients.IsEmpty) return;

        bool anyWants = false;
        foreach (var session in _clients.Values)
        {
            if (session.WantsAudioStream(receiver)) { anyWants = true; break; }
        }

        if (!anyWants) return;

        var payload = TciStreamPayload.BuildAudioFromFloats(receiver, sampleRateHz, samples.Span);
        foreach (var session in _clients.Values)
        {
            if (session.WantsAudioStream(receiver))
                session.SendBinary(payload);
        }
    }

    private void OnTxMetersUpdated(float fwdWatts, float refWatts, float swr, float alcPk, float alcGr)
    {
        // Standalone TX meter events broadcast to all clients
        // tx_power:<watts> — forward power
        var fwdStr = fwdWatts.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        Broadcast(TciProtocol.Command("tx_power", fwdStr));
        // tx_forward_power:<watts> — alias many clients listen for
        Broadcast(TciProtocol.Command("tx_forward_power", fwdStr));
        // tx_swr:<ratio> — SWR ratio (e.g. 1.5 for 1.5:1)
        var swrStr = swr.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        Broadcast(TciProtocol.Command("tx_swr", swrStr));
        Broadcast(TciProtocol.Command("swr", swrStr));
        // tx_alc:<percent> — ALC gain reduction as percentage (0-100)
        int alcPct = alcGr > 0 ? Math.Min(100, (int)Math.Round(alcGr * 10)) : 0;
        Broadcast(TciProtocol.Command("tx_alc", alcPct));

        // Spec §5.6 tx_sensors:<channel>,<mic_db>,<fwd_pwr>,<rev_pwr>,<swr>
        // Combined-frame TX telemetry, sent only to clients that opted in via
        // tx_sensors_enable. mic_db isn't carried in TxMetersUpdated; emit 0.
        if (_clients.IsEmpty) return;
        var refStr = refWatts.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        var frame = TciProtocol.Command(
            "tx_sensors",
            0, 0,
            fwdStr,
            refStr,
            swrStr);
        foreach (var session in _clients.Values)
        {
            if (session.WantsTxSensors) session.Send(frame);
        }
    }

    private void OnCwEngineStatus(CwEngineStatus status)
    {
        // Per the ExpertSDR3 TCI 2.0 spec, cw_macros_empty notifies clients
        // that the keyer / macro queue has drained — used by contest loggers
        // to know when a CQ macro finished so they can start a listening
        // window. The engine raises Idle whenever the queue hits zero (in
        // PR zeus-drf), so we just edge-detect Sending → Idle and forward.
        var prev = _lastCwState;
        _lastCwState = status.State;
        if (prev != CwEngineState.Idle && status.State == CwEngineState.Idle && status.QueueDepth == 0)
        {
            Broadcast(TciProtocol.Command("cw_macros_empty"));
        }
    }

    /// <summary>
    /// Broadcast a command to all connected clients immediately.
    /// </summary>
    private void Broadcast(string commandLine)
    {
        if (_clients.IsEmpty) return;
        foreach (var session in _clients.Values)
        {
            session.Send(commandLine);
        }
    }

    /// <summary>
    /// Broadcast a rate-limited command (VFO/DDS) to all connected clients.
    /// </summary>
    private void BroadcastRateLimited(string key, string commandLine)
    {
        if (_clients.IsEmpty) return;
        foreach (var session in _clients.Values)
        {
            session.SendRateLimited(key, commandLine);
        }
    }

    // --- TX_CHRONO emitter ---
    //
    // Per spec §3.4, the server sends TX_CHRONO sync frames during TX so
    // the client knows to upload another TX audio block. Without this, a
    // well-behaved client (e.g. ExpertSDR3, hams using JTDX-via-TCI) waits
    // for chrono before sending the next mic buffer.
    //
    // The timer fires only while MOX is on AND there's at least one session
    // with TX source = TCI; otherwise the timer is parked. Frame body is
    // empty (length=0); the receiver field carries 0 since Zeus is single-
    // transceiver.

    private void OnMoxChanged(bool moxOn)
    {
        if (moxOn) StartTciTxService();
        else StopTciTxService();
    }

    private void StartTciTxService()
    {
        if (_txChronoTimer is not null) return;
        _txChronoClock.Restart();
        lock (_tciTxStateLock)
        {
            _tciTxSamplesInPipeline = 0;
            // Seed one spacing in the past so the first timer fire sends one
            // chrono. Any larger initial debt would dump the burst-cap on the
            // very first tick, which WSJT-X can't usefully consume before MOX
            // is fully up.
            _tciTxLastChronoSwTicks = _txChronoClock.ElapsedTicks - TciTxChronoSpacingSwTicks;
        }
        _txChronoFrame ??= TciStreamPayload.BuildTxChrono(receiver: 0, sampleRate: 48000);
        // Service the queue at ~half the spacing so a single late OS tick can
        // still be caught up by the next one without burning the burst budget.
        // The actual chrono cadence is enforced by the stopwatch math inside
        // ServiceTciTxProtocol, not by this period.
        const int ServiceIntervalMs = 20;
        _txChronoTimer = new Timer(_ => ServiceTciTxProtocol(), null, ServiceIntervalMs, ServiceIntervalMs);
        _txAudioIngest.SetWdspConsumedCallback(NotifyWdspConsumed);
        _log.LogDebug("tci.tx.chrono started (demand-driven, spacingTicks={Ticks})", TciTxChronoSpacingSwTicks);
    }

    private void StopTciTxService()
    {
        var t = Interlocked.Exchange(ref _txChronoTimer, null);
        if (t is null) return;
        t.Dispose();
        _txAudioIngest.SetWdspConsumedCallback(null);
        lock (_tciTxStateLock)
        {
            _tciTxSamplesInPipeline = 0;
        }
        _log.LogDebug("tci.tx.chrono stopped");
    }

    private void ServiceTciTxProtocol()
    {
        if (!_tx.IsMoxOn || _clients.IsEmpty) return;

        var frame = _txChronoFrame;
        if (frame is null) return;

        int sent = 0;
        while (sent < TciTxBurstCount)
        {
            long nowTicks = _txChronoClock.ElapsedTicks;
            lock (_tciTxStateLock)
            {
                // Rate-limit: don't send faster than real-time audio consumption.
                // One TX_CHRONO = 2048 mono samples @ 48 kHz = 42.6667 ms.
                long elapsed = nowTicks - _tciTxLastChronoSwTicks;
                if (elapsed < TciTxChronoSpacingSwTicks)
                    break;

                // Don't request more if pipeline already has enough buffered.
                // Target: ~100 ms of audio ahead (4800 samples).
                if (_tciTxSamplesInPipeline > 4800)
                    break;

                // Advance by one spacing in ticks (NOT to nowTicks): the OS
                // timer fires unevenly, and resetting to nowTicks bakes the
                // drift in permanently — causing FT8 audio rate to slip ~2%
                // above real-time and overflow the TxAudioIngest accumulator
                // every ~1.8 s. Fixed-increment advance lets a late tick
                // catch up via the burst budget below.
                _tciTxLastChronoSwTicks += TciTxChronoSpacingSwTicks;
            }

            foreach (var session in _clients.Values)
            {
                if (session.TxSourceIsTci) session.SendBinary(frame);
            }
            sent++;
        }
    }

    public void Dispose()
    {
        StopTciTxService();
        foreach (var session in _clients.Values)
        {
            session.Dispose();
        }
        _clients.Clear();
    }
}
