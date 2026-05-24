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

using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Zeus.Contracts;
using Zeus.Protocol1;

namespace Zeus.Server.Tci;

/// <summary>
/// Per-client TCI session. Manages WebSocket send/receive loops, command
/// parsing, event broadcasting, and rate limiting.
///
/// Outbound architecture mirrors Thetis TCIServer: three priority queues
/// (Urgent / Binary / Control) drained by a single send loop in priority
/// order. Queues are unbounded — backpressure is provided implicitly by
/// the underlying socket send window; on a write exception, the session
/// is torn down.
/// </summary>
public sealed class TciSession : IDisposable
{
    private const int MaxInboundTextBytes = 8 * 1024;
    private const int MaxInboundBinaryBytes = 2 * 1024 * 1024; // 2 MB for future binary frames

    private readonly Guid _id;
    private readonly WebSocket _ws;
    private readonly ILogger _log;
    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly DspPipelineService _pipeline;
    private readonly SpotManager _spots;
    private readonly TciOptions _options;
    private readonly TciRateLimiter _rateLimiter;
    private readonly TxAudioIngest? _txAudioIngest;
    private readonly TciTxAudioReceiver? _txAudioReceiver;

    // TCI 2.0 TRX 3rd arg: when "tci" we route inbound binary TX audio frames
    // into the WDSP TX path; otherwise the radio's local mic source is used
    // and we drop incoming TX audio. Default is "false" (no TCI source).
    private bool _txSourceIsTci;
    // Negotiated audio_stream channel count and sample rate. Defaults match
    // the handshake-advertised values (stereo, 48 kHz). Per spec §5.8 the
    // client may set audio_stream_channels=1 for mono uploads.
    private int _audioStreamChannels = 2;
    // Per-session digital-mode pitch offsets (DIGL_OFFSET / DIGU_OFFSET).
    // Stored only — Zeus has no backend for these yet, but we acknowledge
    // and echo so digital-mode clients (WSJT-X / JTDX) don't see "unknown
    // command" log spam.
    private int _diglOffsetHz;
    private int _diguOffsetHz;

    private readonly ConcurrentQueue<TciOutboundFrame> _urgentQueue = new();
    private readonly ConcurrentQueue<TciOutboundFrame> _binaryQueue = new();
    private readonly ConcurrentQueue<TciOutboundFrame> _controlQueue = new();
    private readonly SemaphoreSlim _outboundSignal = new(0);

    // Track current drive level so we can echo it back on query
    private int _lastDrivePercent = 50;

    // Per-session binary stream subscriptions. Producers (TciServer publish
    // path) check WantsIqStream/WantsAudioStream(rx) before building/dispatching frames.
    private readonly object _streamLock = new();
    private readonly HashSet<int> _iqStreamEnabled = new();
    private int _iqSampleRate = 48000;
    private readonly HashSet<int> _audioStreamEnabled = new();
    private int _audioSampleRate = 48000;

    public Guid Id => _id;

    /// <summary>True if this session has subscribed to IQ for the given receiver.</summary>
    public bool WantsIqStream(int receiver)
    {
        lock (_streamLock) return _iqStreamEnabled.Contains(receiver);
    }

    /// <summary>True if this session has subscribed to IQ for any receiver.</summary>
    public bool WantsAnyIqStream()
    {
        lock (_streamLock) return _iqStreamEnabled.Count > 0;
    }

    /// <summary>Last client-requested IQ sample rate, clamped to [48000, 384000].</summary>
    public int IqSampleRate
    {
        get { lock (_streamLock) return _iqSampleRate; }
    }

    /// <summary>True if this session has subscribed to RX audio for the given receiver.</summary>
    public bool WantsAudioStream(int receiver)
    {
        lock (_streamLock) return _audioStreamEnabled.Contains(receiver);
    }

    /// <summary>True if this session has subscribed to RX audio for any receiver.</summary>
    public bool WantsAnyAudioStream()
    {
        lock (_streamLock) return _audioStreamEnabled.Count > 0;
    }

    /// <summary>Last client-requested audio sample rate, clamped to [8000, 48000].</summary>
    public int AudioSampleRate
    {
        get { lock (_streamLock) return _audioSampleRate; }
    }

    public TciSession(
        Guid id,
        WebSocket ws,
        ILogger log,
        RadioService radio,
        TxService tx,
        DspPipelineService pipeline,
        SpotManager spots,
        TciOptions options,
        TxAudioIngest? txAudioIngest = null,
        TciServer? tciServer = null)
    {
        _id = id;
        _ws = ws;
        _log = log;
        _radio = radio;
        _tx = tx;
        _pipeline = pipeline;
        _spots = spots;
        _options = options;
        _rateLimiter = new TciRateLimiter(options.RateLimitMs, Send);
        _txAudioIngest = txAudioIngest;
        _txAudioReceiver = txAudioIngest is not null
            ? new TciTxAudioReceiver(txAudioIngest.OnMicPcmBytesFromTci, log,
                onMonoSamplesQueued: tciServer is not null ? tciServer.NotifyTxAudioQueued : null)
            : null;
    }

    /// <summary>True if the operator has selected the TCI WebSocket as the TX
    /// audio source via <c>TRX:0,true,tci;</c>. Used by <see cref="TciServer"/>
    /// to gate TX_CHRONO frame emission to this session.</summary>
    public bool TxSourceIsTci { get { lock (_streamLock) return _txSourceIsTci; } }

    /// <summary>Drop any TX audio buffered in the receiver. Call on MOX
    /// falling edge so the next keyed-up TX doesn't replay a stale tail.</summary>
    public void ResetTxAudio() => _txAudioReceiver?.Reset();

    /// <summary>
    /// Main session loop: send handshake, then run parallel send/receive loops.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            // Send handshake immediately after WS upgrade
            await SendHandshakeAsync(linkedCts.Token);

            var sendTask = SendLoopAsync(linkedCts.Token);
            var recvTask = ReceiveLoopAsync(linkedCts.Token);
            await Task.WhenAny(sendTask, recvTask);
            linkedCts.Cancel();
            try { await Task.WhenAll(sendTask, recvTask); } catch { /* drained */ }
        }
        finally
        {
            _rateLimiter.Dispose();
        }
    }

    /// <summary>
    /// Enqueue a TCI text command at Control priority (commands, query echoes,
    /// state-change events). Bypasses the rate limiter.
    /// </summary>
    public void Send(string commandLine)
    {
        Enqueue(new TciOutboundFrame(commandLine), TciOutboundPriority.Control);
    }

    /// <summary>
    /// Enqueue a TCI text command at Urgent priority (ping/pong, close, errors).
    /// </summary>
    public void SendUrgent(string commandLine)
    {
        Enqueue(new TciOutboundFrame(commandLine), TciOutboundPriority.Urgent);
    }

    /// <summary>
    /// Enqueue a binary frame (IQ / RX-audio / TX-chrono stream payload) at
    /// Binary priority. Frame bytes are sent verbatim as a WebSocket binary
    /// message — the caller is responsible for the TCI 64-byte stream header.
    /// </summary>
    public void SendBinary(byte[] payload)
    {
        Enqueue(new TciOutboundFrame(payload), TciOutboundPriority.Binary);
    }

    /// <summary>
    /// Enqueue a rate-limited event (VFO/DDS changes during tuning).
    /// </summary>
    public void SendRateLimited(string key, string commandLine)
    {
        _rateLimiter.Enqueue(key, commandLine);
    }

    private void Enqueue(TciOutboundFrame frame, TciOutboundPriority priority)
    {
        switch (priority)
        {
            case TciOutboundPriority.Urgent:
                _urgentQueue.Enqueue(frame);
                break;
            case TciOutboundPriority.Binary:
                _binaryQueue.Enqueue(frame);
                break;
            default:
                _controlQueue.Enqueue(frame);
                break;
        }
        _outboundSignal.Release();
    }

    private async Task SendHandshakeAsync(CancellationToken ct)
    {
        var state = _radio.Snapshot();
        var commands = TciHandshake.BuildHandshake(
            state,
            state.SampleRate,
            _tx.IsMoxOn,
            _tx.IsTunOn,
            _lastDrivePercent);

        // One TCI command per WebSocket text frame — Thetis TCIServer.sendTextFrame
        // convention. Some clients only parse the first command in a frame.
        foreach (var cmd in commands)
        {
            var bytes = Encoding.ASCII.GetBytes(cmd);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        _log.LogInformation("tci.handshake sent client={Id} commands={Count}", _id, commands.Count);
    }

    /// <summary>
    /// Single send loop draining Urgent → Binary → Control queues in priority
    /// order. On any send failure the loop exits, the linked CTS cancels the
    /// receive loop, and the session tears down (matches Thetis abortSocketTransport).
    /// </summary>
    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                await _outboundSignal.WaitAsync(ct);
                if (TryDequeueNext(out var frame))
                {
                    await SendFrameAsync(frame, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _log.LogDebug(ex, "tci send loop ended client={Id}", _id);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "tci send loop write failed client={Id}", _id);
        }
    }

    private bool TryDequeueNext(out TciOutboundFrame frame)
    {
        if (_urgentQueue.TryDequeue(out frame)) return true;
        if (_binaryQueue.TryDequeue(out frame)) return true;
        if (_controlQueue.TryDequeue(out frame)) return true;
        frame = default;
        return false;
    }

    private async Task SendFrameAsync(TciOutboundFrame frame, CancellationToken ct)
    {
        if (_ws.State != WebSocketState.Open) return;
        if (frame.IsBinary)
        {
            await _ws.SendAsync(frame.Bytes!, WebSocketMessageType.Binary, true, ct);
        }
        else
        {
            var bytes = Encoding.ASCII.GetBytes(frame.Text!);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[8 * 1024];
        byte[]? textAccum = null;
        int textAccumLen = 0;
        byte[]? binAccum = null;
        int binAccumLen = 0;

        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(buf, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                    return;
                }

                int chunkLen = result.Count;

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // Single-fragment fast path.
                    if (result.EndOfMessage && binAccum is null)
                    {
                        HandleBinaryFrame(new ReadOnlySpan<byte>(buf, 0, chunkLen));
                        continue;
                    }
                    if (binAccum is null)
                    {
                        binAccum = ArrayPool<byte>.Shared.Rent(Math.Max(chunkLen, 16 * 1024));
                        binAccumLen = 0;
                    }
                    if (binAccumLen + chunkLen > MaxInboundBinaryBytes)
                    {
                        _log.LogWarning("tci oversize binary frame client={Id} len={Len}", _id, binAccumLen + chunkLen);
                        ArrayPool<byte>.Shared.Return(binAccum);
                        binAccum = null;
                        binAccumLen = 0;
                        continue;
                    }
                    if (binAccumLen + chunkLen > binAccum.Length)
                    {
                        int newSize = Math.Min(MaxInboundBinaryBytes, binAccum.Length * 2);
                        while (newSize < binAccumLen + chunkLen) newSize = Math.Min(MaxInboundBinaryBytes, newSize * 2);
                        var grown = ArrayPool<byte>.Shared.Rent(newSize);
                        Buffer.BlockCopy(binAccum, 0, grown, 0, binAccumLen);
                        ArrayPool<byte>.Shared.Return(binAccum);
                        binAccum = grown;
                    }
                    Buffer.BlockCopy(buf, 0, binAccum, binAccumLen, chunkLen);
                    binAccumLen += chunkLen;

                    if (result.EndOfMessage)
                    {
                        HandleBinaryFrame(new ReadOnlySpan<byte>(binAccum, 0, binAccumLen));
                        ArrayPool<byte>.Shared.Return(binAccum);
                        binAccum = null;
                        binAccumLen = 0;
                    }
                    continue;
                }

                // Text frame: accumulate and parse
                if (result.EndOfMessage && textAccum is null)
                {
                    // Fast path: single-fragment text message
                    string line = Encoding.ASCII.GetString(buf, 0, chunkLen);
                    HandleCommand(line);
                    continue;
                }

                // Multi-fragment message: accumulate
                if (textAccum is null)
                {
                    textAccum = ArrayPool<byte>.Shared.Rent(Math.Max(chunkLen, 4096));
                    textAccumLen = 0;
                }
                if (textAccumLen + chunkLen > MaxInboundTextBytes)
                {
                    _log.LogWarning("tci oversize text frame client={Id} len={Len}", _id, textAccumLen + chunkLen);
                    ArrayPool<byte>.Shared.Return(textAccum);
                    textAccum = null;
                    textAccumLen = 0;
                    continue;
                }
                if (textAccumLen + chunkLen > textAccum.Length)
                {
                    int newSize = Math.Min(MaxInboundTextBytes, textAccum.Length * 2);
                    while (newSize < textAccumLen + chunkLen) newSize = Math.Min(MaxInboundTextBytes, newSize * 2);
                    var grown = ArrayPool<byte>.Shared.Rent(newSize);
                    Buffer.BlockCopy(textAccum, 0, grown, 0, textAccumLen);
                    ArrayPool<byte>.Shared.Return(textAccum);
                    textAccum = grown;
                }
                Buffer.BlockCopy(buf, 0, textAccum, textAccumLen, chunkLen);
                textAccumLen += chunkLen;

                if (result.EndOfMessage)
                {
                    string line = Encoding.ASCII.GetString(textAccum, 0, textAccumLen);
                    HandleCommand(line);
                    ArrayPool<byte>.Shared.Return(textAccum);
                    textAccum = null;
                    textAccumLen = 0;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _log.LogDebug(ex, "tci recv loop ended client={Id}", _id);
        }
        finally
        {
            if (textAccum is not null) ArrayPool<byte>.Shared.Return(textAccum);
            if (binAccum is not null) ArrayPool<byte>.Shared.Return(binAccum);
        }
    }

    /// <summary>
    /// Dispatch a fully-reassembled inbound binary frame. Currently we only
    /// route TX audio (StreamType=2) — TCI 2.0 also defines IQ/RX-audio/
    /// LineOut as outbound-only, and TX_CHRONO (=3) is sent by the server
    /// rather than received. Spec §3.4.
    /// </summary>
    private void HandleBinaryFrame(ReadOnlySpan<byte> frame)
    {
        if (!TciStreamPayload.TryParseHeader(frame, out var header))
        {
            _log.LogDebug("tci binary frame too short or malformed len={Len}", frame.Length);
            return;
        }

        if (header.StreamType != TciStreamType.TxAudioStream)
        {
            _log.LogDebug("tci inbound binary type={Type} ignored (TX audio only)", header.StreamType);
            return;
        }

        if (_txAudioReceiver is null)
        {
            return;
        }

        bool sourceIsTci;
        int channels;
        lock (_streamLock)
        {
            sourceIsTci = _txSourceIsTci;
            channels = _audioStreamChannels;
        }
        if (!sourceIsTci)
        {
            _log.LogDebug("tci.tx.audio dropped (TRX source != tci)");
            return;
        }
        if (!_tx.IsMoxOn)
        {
            _log.LogDebug("tci.tx.audio dropped (MOX off)");
            return;
        }

        var samplePayload = frame.Slice(TciStreamPayload.HeaderSize);
        _txAudioReceiver.AcceptTxAudio(
            samplePayload,
            header.SampleType,
            header.Length,
            channels,
            (int)header.SampleRate);
    }

    /// <summary>
    /// Parse and dispatch an inbound TCI command. May contain multiple
    /// semicolon-terminated commands in one line.
    /// </summary>
    private void HandleCommand(string line)
    {
        // TCI clients may batch multiple commands in one WebSocket frame,
        // separated by semicolons. Split and handle each.
        var commands = line.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var cmd in commands)
        {
            if (string.IsNullOrWhiteSpace(cmd)) continue;
            var parsed = TciProtocol.Parse(cmd);
            if (parsed is null)
            {
                _log.LogDebug("tci malformed command: {Cmd}", cmd);
                continue;
            }

            var (command, args) = parsed.Value;
            DispatchCommand(command, args);
        }
    }

    private void DispatchCommand(string command, string[] args)
    {
        try
        {
            switch (command.ToLowerInvariant())
            {
                // --- Frequency / Oscillator ---
                case "vfo":
                    HandleVfo(args);
                    break;
                case "dds":
                    HandleDds(args);
                    break;
                case "if":
                    HandleIf(args);
                    break;

                // --- Mode / Filter ---
                case "modulation":
                    HandleModulation(args);
                    break;
                case "rx_filter_band":
                    HandleRxFilterBand(args);
                    break;

                // --- PTT / TX ---
                case "trx":
                    HandleTrx(args);
                    break;
                case "tune":
                    HandleTune(args);
                    break;
                case "tx_enable":
                    HandleTxEnable(args);
                    break;
                case "drive":
                    HandleDrive(args);
                    break;
                case "tune_drive":
                    HandleTuneDrive(args);
                    break;

                // --- Audio ---
                case "mute":
                    HandleMute(args);
                    break;
                case "rx_mute":
                    HandleRxMute(args);
                    break;
                case "volume":
                    HandleVolume(args);
                    break;
                case "mon_enable":
                    HandleMonEnable(args);
                    break;
                case "mon_volume":
                    HandleMonVolume(args);
                    break;

                // --- AGC ---
                case "agc_gain":
                    HandleAgcGain(args);
                    break;

                // --- CW keyer / macros (ack-only; no CW engine yet) ---
                case "cw_macros_speed":
                case "cw_macros":
                case "cw_msg":
                case "keyer":
                    _log.LogDebug("tci cw command accepted but unimplemented (no CW engine): {Cmd}", command);
                    break;

                // --- Split / RIT / XIT / Lock (stubs) ---
                case "split_enable":
                    HandleSplitEnable(args);
                    break;
                case "rit_enable":
                    HandleRitEnable(args);
                    break;
                case "rit_offset":
                    HandleRitOffset(args);
                    break;
                case "xit_enable":
                    HandleXitEnable(args);
                    break;
                case "xit_offset":
                    HandleXitOffset(args);
                    break;
                case "lock":
                    HandleLock(args);
                    break;

                // --- Lifecycle ---
                case "start":
                    HandleStart(args);
                    break;
                case "stop":
                    HandleStop(args);
                    break;

                // --- Spots ---
                case "spot":
                    HandleSpot(args);
                    break;
                case "spot_delete":
                    HandleSpotDelete(args);
                    break;
                case "spot_clear":
                    HandleSpotClear(args);
                    break;

                // --- Noise reduction / blanking ---
                // Spec-conformant `rx_*` names (TCI 2.0 §4.2) route to the same
                // backends as the legacy non-prefixed forms (`nr_enable`,
                // `nb_enable`, …) accepted by older Zeus clients. The two
                // forms are interchangeable on the wire.
                case "nr_enable":
                case "rx_nr_enable":
                    HandleNrEnable(args);
                    break;
                case "nb_enable":
                case "rx_nb_enable":
                    HandleNbEnable(args);
                    break;
                case "rx_nb2_enable":
                    HandleNb2Enable(args);
                    break;
                case "anf_enable":
                case "rx_anf_enable":
                    HandleAnfEnable(args);
                    break;
                case "anc_enable":
                case "rx_anc_enable":
                    HandleAncEnable(args);
                    break;

                // --- Preamp / attenuator ---
                case "preamp":
                    HandlePreamp(args);
                    break;
                case "attenuator":
                    HandleAttenuator(args);
                    break;

                // --- Binary streams ---
                case "iq_start":
                    HandleIqStart(args);
                    break;
                case "iq_stop":
                    HandleIqStop(args);
                    break;
                case "iq_samplerate":
                    HandleIqSampleRate(args);
                    break;
                case "audio_start":
                    HandleAudioStart(args);
                    break;
                case "audio_stop":
                    HandleAudioStop(args);
                    break;
                case "audio_samplerate":
                    HandleAudioSampleRate(args);
                    break;
                case "audio_stream_sample_type":
                case "audio_stream_channels":
                case "audio_stream_samples":
                case "tx_stream_audio_buffering":
                    HandleStreamConfigEcho(command, args);
                    break;

                // --- S-meter polling (TCI spec §6) ---
                case "rx_smeter":
                case "smeter":
                case "s_meter":
                    HandleRxSmeterQuery(args);
                    break;

                // --- Post-handshake state-sync GETs (spec §3.3) ---
                // Stubs only — these have no backend and exist so the v2.5.1
                // client's state-sync burst doesn't see "unknown command" log
                // spam. Real implementations live above (rx_nb_enable,
                // rx_anf_enable, rx_anc_enable, rx_nr_enable).
                case "sql_enable":
                    HandleStubBoolPerRx(command, args, false);
                    break;
                case "sql_level":
                    HandleStubIntPerRx(command, args, 0);
                    break;
                case "rx_bin_enable":
                    HandleStubBoolPerRx(command, args, false);
                    break;
                case "rx_volume":
                    HandleRxVolume(args);
                    break;
                case "agc_auto_ex":
                    HandleStubBoolPerRx(command, args, true);
                    break;
                case "tx_profile_ex":
                    HandleTxProfileEx(args);
                    break;
                case "tx_profiles_ex":
                    HandleTxProfilesEx(args);
                    break;

                // --- VFO lock / swap / RX2 enable / TX filter band ---
                case "vfo_lock":
                    HandleVfoLock(args);
                    break;
                case "vfo_swap_ex":
                    HandleVfoSwapEx(args);
                    break;
                case "rx_enable":
                    HandleRxEnable(args);
                    break;
                case "rx_channel_enable":
                    HandleRxChannelEnable(args);
                    break;
                case "tx_filter_band_ex":
                    HandleTxFilterBandEx(args);
                    break;

                // --- Digital-mode pitch offsets (TCI 1.9 §4.2) ---
                // No backend wiring — Zeus uses the rx_filter_band passband
                // directly. Stored per-session and echoed so JTDX / WSJT-X
                // don't see "unknown command" log spam.
                case "digl_offset":
                    HandleDigOffset(args, isUpper: false);
                    break;
                case "digu_offset":
                    HandleDigOffset(args, isUpper: true);
                    break;

                // --- NR with level (spec §5.4 rx_nr_enable_ex:rx,bool,level) ---
                // The plain 2-arg form (rx_nr_enable) routes to HandleNrEnable
                // above; only the _ex variant (with optional level) lands here.
                case "rx_nr_enable_ex":
                    HandleRxNrEnableEx(args);
                    break;

                // --- Antenna (no RadioService API yet; ack + log) ---
                case "rx_antenna":
                    HandleRxAntenna(args);
                    break;

                // --- Sensor stream gating (spec §5.6) ---
                case "tx_sensors_enable":
                    HandleTxSensorsEnable(args);
                    break;
                case "rx_sensors_enable":
                    HandleRxSensorsEnable(args);
                    break;

                // --- CAT pass-through (spec §5.9: PS0/PS1/ZZTX0) ---
                case "run_cat_ex":
                    HandleRunCatEx(args);
                    break;

                // --- Unknown ---
                default:
                    _log.LogDebug("tci unknown command: {Cmd}", command);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "tci command handler exception: {Cmd}", command);
        }
    }

    // --- Command Handlers ---

    private void HandleVfo(string[] args)
    {
        // vfo:<rx>,<chan>,<hz> or vfo:<rx>,<chan> (query)
        if (args.Length < 2) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;
        if (!TciProtocol.TryParseInt(args[1], out int chan)) return;

        if (args.Length == 2)
        {
            // Query: echo current VFO
            var state = _radio.Snapshot();
            Send(TciProtocol.Command("vfo", rx, chan, state.VfoHz));
        }
        else if (args.Length >= 3 && TciProtocol.TryParseLong(args[2], out long hz))
        {
            // Spec §8.5: vfo:trx,vfo,0 is invalid — never set a VFO to 0 Hz.
            // Reject silently rather than driving the radio to an out-of-range freq.
            if (hz <= 0) return;
            // TCI is a CAT-like external source — bypass the frozen-NCO
            // auto-recenter heuristic so the hardware tracks the commanded
            // frequency absolutely. Mirrors Thetis CATChangesCenterFreq
            // default. Issue #461.
            _radio.SetVfo(hz, fromExternal: true);
            // Don't echo back immediately — the StateChanged event will broadcast it
        }
    }

    private void HandleDds(string[] args)
    {
        // dds:<rx>,<hz> or dds:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            // Query
            var state = _radio.Snapshot();
            Send(TciProtocol.Command("dds", rx, state.VfoHz));
        }
        else if (args.Length >= 2 && TciProtocol.TryParseLong(args[1], out long hz))
        {
            // Set DDS (same as VFO for single-RX). External source — see HandleVfo.
            _radio.SetVfo(hz, fromExternal: true);
        }
    }

    private void HandleIf(string[] args)
    {
        // if:<rx>,<chan>,<offset_hz> or if:<rx>,<chan> (query)
        // We don't support IF offset yet — always zero
        if (args.Length < 2) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;
        if (!TciProtocol.TryParseInt(args[1], out int chan)) return;

        if (args.Length == 2)
        {
            Send(TciProtocol.Command("if", rx, chan, 0));
        }
        // Ignore set commands — IF offset not implemented
    }

    private void HandleModulation(string[] args)
    {
        // modulation:<rx>,<MODE> or modulation:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            var state = _radio.Snapshot();
            string tciMode = TciProtocol.ModeToTci(state.Mode);
            Send(TciProtocol.Command("modulation", rx, tciMode));
        }
        else if (args.Length >= 2)
        {
            var mode = TciProtocol.TciToMode(args[1]);
            if (mode.HasValue)
            {
                _radio.SetMode(mode.Value);
            }
        }
    }

    private void HandleRxFilterBand(string[] args)
    {
        // rx_filter_band:<rx>,<lo_hz>,<hi_hz> or rx_filter_band:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            var state = _radio.Snapshot();
            Send(TciProtocol.Command("rx_filter_band", rx, state.FilterLowHz, state.FilterHighHz));
        }
        else if (args.Length >= 3 &&
                 TciProtocol.TryParseInt(args[1], out int lo) &&
                 TciProtocol.TryParseInt(args[2], out int hi))
        {
            _radio.SetFilter(lo, hi);
        }
    }

    private void HandleTrx(string[] args)
    {
        // trx:<rx>,<bool>[,signal_source] or trx:<rx> (query). Spec 2.0 §4.2:
        // signal_source ∈ {tci, mic1, mic2, micPC, ecoder2}. When = "tci",
        // the server takes mic samples from inbound TCI binary frames; any
        // other value (or absent) keeps the radio's locally-selected mic.
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("trx", rx, _tx.IsMoxOn));
            return;
        }

        if (!TciProtocol.TryParseBool(args[1], out bool on)) return;

        // Latch the source flag BEFORE flipping MOX so the first inbound TX
        // audio frame after key-up is decoded against the correct gate.
        if (args.Length >= 3)
        {
            string src = args[2].Trim();
            bool wantsTci = string.Equals(src, "tci", StringComparison.OrdinalIgnoreCase);
            lock (_streamLock) _txSourceIsTci = wantsTci;
            if (!wantsTci)
                _log.LogDebug("tci.trx source={Src} (TX audio routed to local mic)", src);
        }
        else
        {
            // Per spec, no 3rd arg = use the radio's local mic. Clear any
            // previously-set TCI routing so a key-up via plain `trx:0,true;`
            // doesn't accidentally inherit a stale tci-source flag.
            lock (_streamLock) _txSourceIsTci = false;
        }

        // On MOX falling edge, drop any TX audio buffered from this session
        // so the next keyed-up TX starts from silence. (TxAudioIngest also
        // clears the IQ ring on its own MOX falling edge — this just keeps
        // the receiver-side accumulator in step.)
        if (!on) ResetTxAudio();

        // Echo post-call truth rather than the request: if the radio is
        // mid-disconnect / mid-reconfigure, or already in the requested
        // state, TrySetMox is a no-op and lying to MSHV/WSJT-X causes the
        // client to think MOX is on when it isn't. MSHV tolerates redundant
        // echoes — it does not tolerate desynchronised state.
        _tx.TrySetMox(on, MoxSource.Tci, out _);
        Send(TciProtocol.Command("trx", rx, _tx.IsMoxOn));
    }

    private void HandleTune(string[] args)
    {
        // tune:<rx>,<bool> or tune:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("tune", rx, _tx.IsTunOn));
        }
        else if (args.Length >= 2 && TciProtocol.TryParseBool(args[1], out bool on))
        {
            // Echo post-call truth — see HandleTrx for rationale.
            _tx.TrySetTun(on, out _);
            Send(TciProtocol.Command("tune", rx, _tx.IsTunOn));
        }
    }

    private void HandleTxEnable(string[] args)
    {
        // tx_enable:<rx>,<bool> or tx_enable:<rx> (query). Per the
        // ExpertSDR2 convention (MSHV 2.76, JTDX-TCI, …) this is a
        // courtesy echo, not an authoritative gate: clients send it after
        // the handshake to announce TX intent and wait for the server to
        // echo the same value back before they will issue trx:0,true;.
        // Real MOX/TUN permission stays in _tx.TrySetMox / _tx.TrySetTun.
        // Keep the query value in sync with TciHandshake.cs:107.
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("tx_enable", rx, _tx.IsMoxOn || _tx.IsTunOn));
        }
        else if (args.Length >= 2 && TciProtocol.TryParseBool(args[1], out bool on))
        {
            // Echo the requested value verbatim. tx_enable is a courtesy
            // handshake — the client sends `tx_enable:0,true;` to announce
            // intent and waits for the matching echo before it will send
            // `trx:0,true;` (PTT-on). At this point MOX/TUN are still off
            // (PTT hasn't arrived yet), so echoing post-call truth would
            // bounce back `false`, which MSHV reads as "TX denied" — and
            // it never issues trx. Don't apply the HandleTrx "post-call
            // truth" nit here: there's no set call whose result could
            // diverge from the request; the handler IS the echo.
            Send(TciProtocol.Command("tx_enable", rx, on));
        }
    }

    private void HandleDrive(string[] args)
    {
        // drive:<rx>,<0-100> or drive:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("drive", rx, _lastDrivePercent));
        }
        else if (args.Length >= 2 && TciProtocol.TryParseInt(args[1], out int pct))
        {
            int clamped = Math.Clamp(pct, 0, _options.LimitPowerLevels ? 50 : 100);
            _lastDrivePercent = clamped;
            _radio.SetDrive(clamped);
        }
    }

    private void HandleTuneDrive(string[] args)
    {
        // tune_drive:<rx>,<0-100> or tune_drive:<rx> (query)
        // For now, same as drive
        HandleDrive(args);
    }

    private void HandleMute(string[] args)
    {
        // mute:<bool> or mute (query)
        if (args.Length == 0)
        {
            Send(TciProtocol.Command("mute", false)); // no master mute yet
        }
        // Ignore set — not implemented
    }

    private void HandleRxMute(string[] args)
    {
        // rx_mute:<rx>,<bool> or rx_mute:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("rx_mute", rx, false));
        }
        // Ignore set — not implemented
    }

    private void HandleVolume(string[] args)
    {
        // volume:<db>  (SET)  or  volume  (GET)
        // Legacy / Thetis-flavoured master volume — not in the SunSDR TCI
        // catalog, but real clients emit it. Wire to RxAfGainDb so this and
        // rx_volume share a single source of truth (RadioService clamps to
        // [-50, +20] dB to mirror Thetis ptbAF).
        if (args.Length == 0)
        {
            var state = _radio.Snapshot();
            Send(TciProtocol.Command("volume", (int)Math.Round(state.RxAfGainDb)));
            return;
        }
        if (TciProtocol.TryParseDouble(args[0], out double db))
            _radio.SetRxAfGain(db);
    }

    private void HandleMonEnable(string[] args)
    {
        // mon_enable:<bool> or mon_enable (query)
        if (args.Length == 0)
        {
            Send(TciProtocol.Command("mon_enable", false));
        }
        // Ignore set — sidetone not implemented
    }

    private void HandleMonVolume(string[] args)
    {
        // mon_volume:<db> or mon_volume (query)
        if (args.Length == 0)
        {
            Send(TciProtocol.Command("mon_volume", -20));
        }
        // Ignore set — sidetone not implemented
    }

    private void HandleAgcGain(string[] args)
    {
        // agc_gain:<rx>,<db> or agc_gain:<rx> (query)
        // ExpertSDR3 TCI spec: AGC gain is synonymous with AGC top (max gain)
        // Range: -20 to 120 dB per Thetis convention
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            // Query: echo current AGC top
            var state = _radio.Snapshot();
            Send(TciProtocol.Command("agc_gain", rx, (int)state.AgcTopDb));
        }
        else if (args.Length >= 2 && TciProtocol.TryParseDouble(args[1], out double db))
        {
            // Set AGC top (gain)
            _radio.SetAgcTop(db);
            // StateChanged event will broadcast the update to all clients
        }
    }

    private void HandleSplitEnable(string[] args)
    {
        // split_enable:<rx>,<bool> or split_enable:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("split_enable", rx, false));
        }
        // Ignore set — split not implemented
    }

    private void HandleRitEnable(string[] args)
    {
        // rit_enable:<rx>,<bool> or rit_enable:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("rit_enable", rx, false));
        }
        // Ignore set — RIT not implemented
    }

    private void HandleRitOffset(string[] args)
    {
        // rit_offset:<rx>,<hz> or rit_offset:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("rit_offset", rx, 0));
        }
        // Ignore set — RIT not implemented
    }

    private void HandleXitEnable(string[] args)
    {
        // xit_enable:<rx>,<bool> or xit_enable:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("xit_enable", rx, false));
        }
        // Ignore set — XIT not implemented
    }

    private void HandleXitOffset(string[] args)
    {
        // xit_offset:<rx>,<hz> or xit_offset:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("xit_offset", rx, 0));
        }
        // Ignore set — XIT not implemented
    }

    private void HandleLock(string[] args)
    {
        // lock:<rx>,<bool> or lock:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("lock", rx, false));
        }
        // Ignore set — lock not implemented
    }

    private void HandleStart(string[] args)
    {
        // start — power on (no-op if already connected)
        // We can't auto-connect without knowing the endpoint; log and ignore
        _log.LogDebug("tci 'start' command received (no-op — connect via REST API)");
    }

    private void HandleStop(string[] args)
    {
        // stop — power off
        _ = _radio.DisconnectAsync();
    }

    private void HandleSpot(string[] args)
    {
        // spot:<callsign>,<mode>,<freq_hz>,<argb>[,<comment>]
        if (args.Length < 4) return;
        string callsign = args[0];
        string mode = args[1];
        if (!TciProtocol.TryParseLong(args[2], out long freqHz)) return;
        if (!TciProtocol.TryParseInt(args[3], out int argbSigned)) return;
        uint argb = unchecked((uint)argbSigned);
        string? comment = args.Length > 4 ? args[4] : null;

        _spots.AddSpot(callsign, mode, freqHz, argb, comment);
        _log.LogDebug("tci spot added: {Call} {Mode} {Freq} Hz", callsign, mode, freqHz);
    }

    private void HandleSpotDelete(string[] args)
    {
        // spot_delete:<callsign>
        if (args.Length < 1) return;
        _spots.RemoveSpot(args[0]);
    }

    private void HandleSpotClear(string[] args)
    {
        // spot_clear
        _spots.ClearAll();
    }

    private void HandleIqStart(string[] args)
    {
        // iq_start:<rx>,<bool>  — start (true) or stop (false) per-receiver IQ stream
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;
        bool enable = true;
        if (args.Length >= 2 && TciProtocol.TryParseBool(args[1], out bool parsed))
            enable = parsed;
        SetIqStream(rx, enable);
    }

    private void HandleIqStop(string[] args)
    {
        // iq_stop:<rx>  — alias of iq_start:<rx>,false
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;
        SetIqStream(rx, false);
    }

    private void HandleIqSampleRate(string[] args)
    {
        // iq_samplerate:<rate>  or  iq_samplerate (query)
        // Range matches Thetis: [48000, 384000]. Stored on the session; the
        // actual rate of published frames is the radio's native rate, echoed
        // back to the client when streaming starts.
        if (args.Length == 0)
        {
            Send(TciProtocol.Command("iq_samplerate", IqSampleRate));
            return;
        }
        if (TciProtocol.TryParseInt(args[0], out int rate))
        {
            rate = Math.Clamp(rate, 48000, 384000);
            lock (_streamLock) _iqSampleRate = rate;
            Send(TciProtocol.Command("iq_samplerate", rate));
        }
    }

    private void SetIqStream(int rx, bool enable)
    {
        lock (_streamLock)
        {
            if (enable) _iqStreamEnabled.Add(rx);
            else _iqStreamEnabled.Remove(rx);
        }
        Send(TciProtocol.Command("iq_start", rx, enable));
    }

    private void HandleAudioStart(string[] args)
    {
        // audio_start:<rx>,<bool>  — start (true) or stop (false) per-receiver audio stream
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;
        bool enable = true;
        if (args.Length >= 2 && TciProtocol.TryParseBool(args[1], out bool parsed))
            enable = parsed;
        SetAudioStream(rx, enable);
    }

    private void HandleAudioStop(string[] args)
    {
        // audio_stop:<rx>  — alias of audio_start:<rx>,false
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;
        SetAudioStream(rx, false);
    }

    private void HandleAudioSampleRate(string[] args)
    {
        // audio_samplerate:<rate>  or  audio_samplerate (query)
        // Range: [8000, 48000]. Zeus emits audio at 48 kHz; the requested rate
        // is stored and echoed. Down-sampling is not yet implemented.
        if (args.Length == 0)
        {
            Send(TciProtocol.Command("audio_samplerate", AudioSampleRate));
            return;
        }
        if (TciProtocol.TryParseInt(args[0], out int rate))
        {
            rate = Math.Clamp(rate, 8000, 48000);
            lock (_streamLock) _audioSampleRate = rate;
            Send(TciProtocol.Command("audio_samplerate", rate));
        }
    }

    private void SetAudioStream(int rx, bool enable)
    {
        lock (_streamLock)
        {
            if (enable) _audioStreamEnabled.Add(rx);
            else _audioStreamEnabled.Remove(rx);
        }
        Send(TciProtocol.Command("audio_start", rx, enable));
    }

    private void HandleStreamConfigEcho(string command, string[] args)
    {
        // audio_stream_sample_type, audio_stream_channels, audio_stream_samples,
        // tx_stream_audio_buffering — server echoes whatever the client sets
        // (TCI spec §5.8 subscription burst). Zeus honours audio_stream_channels
        // for inbound TX audio decode; the others are echo-only so the client
        // sees its requested config reflected.
        if (args.Length == 0)
        {
            // Query — re-emit the handshake-advertised value
            string value = command switch
            {
                "audio_stream_sample_type" => "float32",
                "audio_stream_channels" => _audioStreamChannels.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "audio_stream_samples" => "2048",
                "tx_stream_audio_buffering" => "50",
                _ => "0",
            };
            Send($"{command}:{value};");
            return;
        }
        // SET form. For audio_stream_channels we latch the value so the TX
        // audio receiver knows how to interpret inbound binary frames
        // (mono vs stereo mixdown).
        if (string.Equals(command, "audio_stream_channels", StringComparison.OrdinalIgnoreCase)
            && TciProtocol.TryParseInt(args[0], out int ch)
            && (ch == 1 || ch == 2))
        {
            lock (_streamLock) _audioStreamChannels = ch;
        }
        Send($"{command}:{args[0]};");
    }

    private void HandleRxSmeterQuery(string[] args)
    {
        // rx_smeter:<rx>,<chan>  — GET form. The server already pushes
        // rx_smeter values via TciServer.OnSMeter at the rate-limited cadence;
        // respond to the query with a placeholder. Clients fall back to
        // deriving S-meter from IQ FFT if no response within 500 ms (spec §6).
        int rx = 0, chan = 0;
        if (args.Length >= 1) TciProtocol.TryParseInt(args[0], out rx);
        if (args.Length >= 2) TciProtocol.TryParseInt(args[1], out chan);
        Send(TciProtocol.Command("rx_smeter", rx, chan, -120));
    }

    private void HandleStubBoolPerRx(string command, string[] args, bool defaultValue)
    {
        // <command>:<rx>            — GET, returns default
        // <command>:<rx>,<bool>     — SET, ack-only (no backing state yet)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;
        if (args.Length == 1)
        {
            Send(TciProtocol.Command(command, rx, defaultValue));
            return;
        }
        if (TciProtocol.TryParseBool(args[1], out bool value))
            Send(TciProtocol.Command(command, rx, value));
    }

    private void HandleStubIntPerRx(string command, string[] args, int defaultValue)
    {
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;
        if (args.Length == 1)
        {
            Send(TciProtocol.Command(command, rx, defaultValue));
            return;
        }
        if (TciProtocol.TryParseInt(args[1], out int value))
            Send(TciProtocol.Command(command, rx, value));
    }

    private void HandleRxVolume(string[] args)
    {
        // SunSDR TCI spec §5.4 — per-RX volume in dB.
        //   rx_volume:<trx>,<rx>          GET → echo current dB
        //   rx_volume:<trx>,<rx>,<dB>     SET → route through SetRxAfGain
        // Zeus has a single shared AF bus today, so all (trx,rx) combos
        // mirror RxAfGainDb. RadioService clamps to [-50, +20] dB.
        if (args.Length < 2) return;
        if (!TciProtocol.TryParseInt(args[0], out int trx)) return;
        if (!TciProtocol.TryParseInt(args[1], out int rx)) return;
        if (args.Length == 2)
        {
            var state = _radio.Snapshot();
            Send(TciProtocol.Command("rx_volume", trx, rx, (int)Math.Round(state.RxAfGainDb)));
            return;
        }
        if (TciProtocol.TryParseDouble(args[2], out double db))
            _radio.SetRxAfGain(db);
    }

    private void HandleTxProfileEx(string[] args)
    {
        // tx_profile_ex             — GET active profile name
        // tx_profile_ex:<name>      — SET active profile (ack-only)
        if (args.Length == 0)
        {
            Send(TciProtocol.Command("tx_profile_ex", "Default"));
            return;
        }
        Send(TciProtocol.Command("tx_profile_ex", args[0]));
    }

    private void HandleTxProfilesEx(string[] args)
    {
        // tx_profiles_ex             — GET list of all configured profiles
        // Zeus doesn't have a profile system; return a single-entry list.
        Send(TciProtocol.Command("tx_profiles_ex", "Default"));
    }

    private void HandleRunCatEx(string[] args)
    {
        // run_cat_ex:<KENWOOD_CMD>  — Kenwood CAT pass-through (spec §5.9).
        // Zeus has no general CAT engine, but the three commands clients
        // actually rely on are wired here:
        //   PS1   — power on  → ConnectAsync (no-op if no last-known endpoint)
        //   PS0   — power off → DisconnectAsync
        //   ZZTX0 — force-unkey → TxService.TrySetMox(false)
        // The client does not expect a wire reply.
        string cmd = args.Length > 0 ? args[0].Trim().ToUpperInvariant() : "";
        switch (cmd)
        {
            case "PS0":
                _log.LogInformation("tci.run_cat_ex PS0 → disconnect");
                _ = _radio.DisconnectAsync();
                break;
            case "PS1":
                // Re-connect requires an endpoint and sample rate. Zeus's
                // power-up flow runs through discovery + the web UI; without
                // a stored last-known endpoint, log and skip rather than
                // guess.
                _log.LogInformation("tci.run_cat_ex PS1 → ignored (no auto-connect path; use Zeus discovery)");
                break;
            case "ZZTX0":
                _log.LogInformation("tci.run_cat_ex ZZTX0 → force-unkey");
                // Source-tagged: a remote CAT command can drop MOX it itself
                // claimed via trx:true, but cannot truncate a Cwx- or
                // Hardware-driven transmission. The operator's UI button is
                // still the master override.
                _tx.TrySetMox(false, MoxSource.Tci, out _);
                break;
            default:
                _log.LogDebug("tci.run_cat_ex unhandled cmd={Cmd}", cmd);
                break;
        }
    }

    private void HandleVfoLock(string[] args)
    {
        // vfo_lock:<trx>,<vfo>          GET → echo
        // vfo_lock:<trx>,<vfo>,<bool>   SET → ack-only (Zeus has no per-VFO lock yet)
        if (args.Length < 2) return;
        if (!TciProtocol.TryParseInt(args[0], out int trx)) return;
        if (!TciProtocol.TryParseInt(args[1], out int vfo)) return;
        if (args.Length == 2)
        {
            Send(TciProtocol.Command("vfo_lock", trx, vfo, false));
            return;
        }
        if (TciProtocol.TryParseBool(args[2], out bool locked))
            Send(TciProtocol.Command("vfo_lock", trx, vfo, locked));
    }

    private void HandleVfoSwapEx(string[] args)
    {
        // vfo_swap_ex:<trx>  — swap VFO A/B contents (C→S only).
        // Zeus has no split / dual-VFO state to swap yet; log and ack.
        _log.LogDebug("tci.vfo_swap_ex received (split not implemented)");
    }

    private void HandleRxEnable(string[] args)
    {
        // rx_enable:<trx>,<bool>  — toggle secondary receiver (RX2).
        // Zeus is single-RX; echo whatever the client sent so its UI doesn't
        // assume the SET silently succeeded.
        if (args.Length < 2) return;
        if (!TciProtocol.TryParseInt(args[0], out int trx)) return;
        if (TciProtocol.TryParseBool(args[1], out bool enabled))
            Send(TciProtocol.Command("rx_enable", trx, enabled));
    }

    private void HandleRxChannelEnable(string[] args)
    {
        // rx_channel_enable:<rx>,<chan>,<bool>  — TCI 2.0 §4.2 spec form for
        // toggling channel B (VFO B) on a receiver. 2-arg form is a GET.
        // Zeus has no second VFO yet; echo so the client UI stays consistent.
        if (args.Length < 2) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;
        if (!TciProtocol.TryParseInt(args[1], out int chan)) return;
        if (args.Length == 2)
        {
            // Channel A is always on; channel B is always off (single-VFO).
            Send(TciProtocol.Command("rx_channel_enable", rx, chan, chan == 0));
            return;
        }
        if (TciProtocol.TryParseBool(args[2], out bool enabled))
            Send(TciProtocol.Command("rx_channel_enable", rx, chan, enabled));
    }

    private void HandleDigOffset(string[] args, bool isUpper)
    {
        // digl_offset / digu_offset:<hz>  (SET)
        // digl_offset / digu_offset       (GET)
        // Range 0..4000 Hz per spec §4.2. Stored per session — backend
        // application would treat this as the digital-mode passband centre
        // shift, which Zeus today bakes into rx_filter_band directly.
        string name = isUpper ? "digu_offset" : "digl_offset";
        if (args.Length == 0)
        {
            int current = isUpper ? _diguOffsetHz : _diglOffsetHz;
            Send(TciProtocol.Command(name, current));
            return;
        }
        if (!TciProtocol.TryParseInt(args[0], out int hz)) return;
        hz = Math.Clamp(hz, 0, 4000);
        if (isUpper) _diguOffsetHz = hz;
        else _diglOffsetHz = hz;
        Send(TciProtocol.Command(name, hz));
    }

    private void HandleTxFilterBandEx(string[] args)
    {
        // tx_filter_band_ex:<lo_hz>,<hi_hz>  — TX bandpass filter (spec §5.3).
        // Note: NO rx index, unlike rx_filter_band. Zeus tracks
        // TxFilterLowHz / TxFilterHighHz on StateDto but doesn't yet expose
        // a setter; ack with the client-supplied values so the client UI
        // updates and treat as informational.
        if (args.Length == 0)
        {
            var state = _radio.Snapshot();
            Send(TciProtocol.Command("tx_filter_band_ex", state.TxFilterLowHz, state.TxFilterHighHz));
            return;
        }
        if (args.Length >= 2
            && TciProtocol.TryParseInt(args[0], out int lo)
            && TciProtocol.TryParseInt(args[1], out int hi))
        {
            Send(TciProtocol.Command("tx_filter_band_ex", lo, hi));
        }
    }

    private void HandleRxNrEnableEx(string[] args)
    {
        // rx_nr_enable / rx_nr_enable_ex:<rx>,<bool>[,<level>]  — spec §5.4.
        // level 1..4 maps to NrMode: 1=Anr (NR), 2=Emnr (NR2), 3=Sbnr (Spec NR),
        // 4=Anr (Zeus has no NR4; closest available). bool=false → NrMode.Off
        // regardless of level.
        if (args.Length < 2) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;
        if (!TciProtocol.TryParseBool(args[1], out bool enable)) return;

        NrMode mode = NrMode.Off;
        if (enable)
        {
            int level = 1;
            if (args.Length >= 3 && TciProtocol.TryParseInt(args[2], out int parsedLevel))
                level = parsedLevel;
            mode = level switch
            {
                2 => NrMode.Emnr,
                3 => NrMode.Sbnr,
                _ => NrMode.Anr,
            };
        }
        var current = _radio.Snapshot().Nr ?? new NrConfig();
        _radio.SetNr(current with { NrMode = mode });
    }

    private void HandleRxAntenna(string[] args)
    {
        // rx_antenna:<rx>,<n>  — n=0..2 for ANT1..3, n=3 for "default" (spec §5.4).
        // RadioService has no antenna-selection API yet (HL2 has no
        // switchable antenna; ANAN-class boards do, but Zeus hasn't wired
        // it). Log and ack so clients don't see "unknown command".
        if (args.Length < 2) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;
        if (TciProtocol.TryParseInt(args[1], out int n))
        {
            _log.LogDebug("tci.rx_antenna rx={Rx} n={N} (no antenna API yet; ack-only)", rx, n);
            Send(TciProtocol.Command("rx_antenna", rx, n));
        }
    }

    // --- Sensor stream gating (spec §5.6) ---
    // Clients opt in to combined-frame telemetry pushes (tx_sensors,
    // rx_channel_sensors). Off by default; the existing rx_smeter / tx_power
    // / tx_swr broadcasts continue regardless.
    private bool _wantsTxSensors;
    private bool _wantsRxSensors;
    public bool WantsTxSensors { get { lock (_streamLock) return _wantsTxSensors; } }
    public bool WantsRxSensors { get { lock (_streamLock) return _wantsRxSensors; } }

    /// <summary>Per-session desired sensor-push interval in ms (clamped 30..1000
    /// per spec §4.4). 0 = use server default cadence. Currently informational —
    /// Zeus pushes at the rx_smeter / TxMeters event rate regardless.</summary>
    public int RxSensorsIntervalMs { get { lock (_streamLock) return _rxSensorsIntervalMs; } }
    public int TxSensorsIntervalMs { get { lock (_streamLock) return _txSensorsIntervalMs; } }
    private int _rxSensorsIntervalMs;
    private int _txSensorsIntervalMs;

    private void HandleTxSensorsEnable(string[] args)
    {
        // Spec §4.4: tx_sensors_enable:<bool>[,<interval_ms>]
        // No rx index. Interval is optional (range 30..1000 ms).
        if (args.Length == 0)
        {
            Send(TciProtocol.Command("tx_sensors_enable", WantsTxSensors));
            return;
        }
        if (!TciProtocol.TryParseBool(args[0], out bool enable)) return;
        int interval = 0;
        if (args.Length >= 2 && TciProtocol.TryParseInt(args[1], out int parsedInterval))
            interval = Math.Clamp(parsedInterval, 30, 1000);
        lock (_streamLock)
        {
            _wantsTxSensors = enable;
            _txSensorsIntervalMs = interval;
        }
        // Echo back in the same shape the client sent (with interval if it
        // was supplied) so the client sees its request reflected.
        Send(args.Length >= 2
            ? TciProtocol.Command("tx_sensors_enable", enable, interval)
            : TciProtocol.Command("tx_sensors_enable", enable));
    }

    private void HandleRxSensorsEnable(string[] args)
    {
        // Spec §4.4: rx_sensors_enable:<bool>[,<interval_ms>]
        // No rx index — TCI 2.0 corrected an earlier shape that took rx,bool;
        // the client of record (ExpertSDR3 1.0.7+, JTDX, Log4OM) sends the
        // bool-only form. Range matches tx_sensors_enable.
        if (args.Length == 0)
        {
            Send(TciProtocol.Command("rx_sensors_enable", WantsRxSensors));
            return;
        }
        if (!TciProtocol.TryParseBool(args[0], out bool enable)) return;
        int interval = 0;
        if (args.Length >= 2 && TciProtocol.TryParseInt(args[1], out int parsedInterval))
            interval = Math.Clamp(parsedInterval, 30, 1000);
        lock (_streamLock)
        {
            _wantsRxSensors = enable;
            _rxSensorsIntervalMs = interval;
        }
        Send(args.Length >= 2
            ? TciProtocol.Command("rx_sensors_enable", enable, interval)
            : TciProtocol.Command("rx_sensors_enable", enable));
    }

    private void HandleNrEnable(string[] args)
    {
        // nr_enable:<rx>,<bool>  — enable/disable noise reduction (NR1/ANR)
        // Maps bool true → NrMode.Anr (NR1), false → NrMode.Off.
        // Use the full NrConfig API if you need NR2/NR4 — this is the TCI primitive.
        if (args.Length < 2) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;
        if (!TciProtocol.TryParseBool(args[1], out bool enable)) return;

        var current = _radio.Snapshot().Nr ?? new NrConfig();
        var updated = current with { NrMode = enable ? NrMode.Anr : NrMode.Off };
        _radio.SetNr(updated);
    }

    private void HandleNbEnable(string[] args)
    {
        // nb_enable / rx_nb_enable:<rx>,<bool>  — enable/disable noise blanker (NB1).
        // Maps bool true → NbMode.Nb1, false → NbMode.Off. GET form is 1 arg
        // and replies with the live state. The two command names are aliases
        // (TCI 2.0 spec uses the rx_-prefixed form; older Zeus accepted the
        // bare form).
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;
        if (args.Length == 1)
        {
            var snap = _radio.Snapshot().Nr ?? new NrConfig();
            Send(TciProtocol.Command("rx_nb_enable", rx, snap.NbMode == NbMode.Nb1));
            return;
        }
        if (!TciProtocol.TryParseBool(args[1], out bool enable)) return;

        var current = _radio.Snapshot().Nr ?? new NrConfig();
        // Setting NB1 off should leave NB2 alone if it was active. We only
        // touch the NB slot when the requested change concerns NB1.
        NbMode next = enable ? NbMode.Nb1
                             : (current.NbMode == NbMode.Nb1 ? NbMode.Off : current.NbMode);
        _radio.SetNr(current with { NbMode = next });
    }

    private void HandleNb2Enable(string[] args)
    {
        // rx_nb2_enable:<rx>,<bool>  — enable/disable NB2. Mutually exclusive
        // with NB1 in WDSP, so flipping NB2 on switches the slot from NB1.
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;
        if (args.Length == 1)
        {
            var snap = _radio.Snapshot().Nr ?? new NrConfig();
            Send(TciProtocol.Command("rx_nb2_enable", rx, snap.NbMode == NbMode.Nb2));
            return;
        }
        if (!TciProtocol.TryParseBool(args[1], out bool enable)) return;

        var current = _radio.Snapshot().Nr ?? new NrConfig();
        NbMode next = enable ? NbMode.Nb2
                             : (current.NbMode == NbMode.Nb2 ? NbMode.Off : current.NbMode);
        _radio.SetNr(current with { NbMode = next });
    }

    private void HandleAnfEnable(string[] args)
    {
        // anf_enable:<rx>,<bool>  — enable/disable automatic notch filter
        if (args.Length < 2) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;
        if (!TciProtocol.TryParseBool(args[1], out bool enable)) return;

        var current = _radio.Snapshot().Nr ?? new NrConfig();
        _radio.SetNr(current with { AnfEnabled = enable });
    }

    private void HandleAncEnable(string[] args)
    {
        // anc_enable:<rx>,<bool>  — enable/disable spectral noise blanker (SNB/ANC)
        // Maps to SnbEnabled in NrConfig.
        if (args.Length < 2) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;
        if (!TciProtocol.TryParseBool(args[1], out bool enable)) return;

        var current = _radio.Snapshot().Nr ?? new NrConfig();
        _radio.SetNr(current with { SnbEnabled = enable });
    }

    private void HandlePreamp(string[] args)
    {
        // preamp:<rx>,<bool>  or  preamp:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("preamp", rx, _radio.PreampOn));
        }
        else if (args.Length >= 2 && TciProtocol.TryParseBool(args[1], out bool on))
        {
            _radio.SetPreamp(on);
        }
    }

    private void HandleAttenuator(string[] args)
    {
        // attenuator:<rx>,<db>  or  attenuator:<rx> (query)
        // Range: 0..31 dB (HPSDR hardware limit).
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("attenuator", rx, _radio.EffectiveAttenDb));
        }
        else if (args.Length >= 2 && TciProtocol.TryParseInt(args[1], out int db))
        {
            _radio.SetAttenuator(new HpsdrAtten(db));
        }
    }

    public void Dispose()
    {
        _rateLimiter.Dispose();
        _outboundSignal.Dispose();
    }
}

internal enum TciOutboundPriority
{
    Urgent,
    Binary,
    Control,
}

internal readonly struct TciOutboundFrame
{
    public readonly string? Text;
    public readonly byte[]? Bytes;

    public bool IsBinary => Bytes is not null;

    public TciOutboundFrame(string text)
    {
        Text = text;
        Bytes = null;
    }

    public TciOutboundFrame(byte[] bytes)
    {
        Text = null;
        Bytes = bytes;
    }
}
