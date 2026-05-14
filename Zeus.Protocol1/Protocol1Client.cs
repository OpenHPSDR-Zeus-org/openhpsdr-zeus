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
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

namespace Zeus.Protocol1;

public sealed class Protocol1Client : IProtocol1Client
{
    private const int DefaultFrameChannelCapacity = 64;
    private const int RxSocketTimeoutMs = 100;
    private const int ConsecutiveTimeoutsBeforeGiveUp = 10;
    // HL2's TX DAC runs at a fixed 48 kHz regardless of the RX sample rate;
    // each EP2 packet carries 126 IQ pairs so the target TX packet rate is
    // 381 pkt/s. Earlier attempts at using a PeriodicTimer fell to whatever
    // the OS rounded the period to (observed 500 pkt/s at requested 2.625 ms
    // on macOS, 333 pkt/s at the prior integer-ms tick of 3 ms) — both rates
    // mismatch the HL2's clock and cost dB of TX power. TX now fires in
    // response to each received RX packet, divided by the RX/TX rate ratio
    // so the HL2's own clock paces the transmitter. pihpsdr old_protocol.c
    // uses the same pattern.
    private readonly SemaphoreSlim _txSignal = new(0, int.MaxValue);

    private readonly ILogger<Protocol1Client> _log;
    private readonly Channel<IqFrame> _channel;

    // Mutation state written from any thread, read from the TX thread.
    // 64-bit fields are written atomically on 64-bit .NET (Interlocked.Exchange used for safety).
    private long _vfoAHz = 7_100_000;
    private int _rate = (int)HpsdrSampleRate.Rate48k;
    private int _preamp;       // 0 / 1
    private int _attenDb;      // 0..31 dB (HpsdrAtten value)
    private int _antenna = (int)HpsdrAntenna.Ant1;
    // HL2 Band Volts PWM enable. Wire encoding is C3 bit 3 of the Config
    // frame — same bit that legacy HPSDR boards used for ADC DITHER, which
    // HL2's AD9866 doesn't need (see hermes-lite2-protocol.md line 39 and
    // mi0bot's HL2 fork, which exposes this in the UI as "Band Volts").
    private int _enableHl2BandVolts;
    private int _boardKind = (int)HpsdrBoardKind.HermesLite2;
    private int _hasN2adr;      // 0 / 1
    private int _mox;           // 0 / 1
    private int _drivePct;      // 0..100 UI percent; mapped to 0..255 on snapshot
    // When >= 0, RadioService has pushed a fully-computed drive byte (post PA
    // calibration) and we send that instead of the percent mapping. Legacy
    // callers that only call SetDrive(percent) keep working untouched.
    private int _driveByteOverride = -1;
    private int _ocTxMask;      // user OC pin mask for TX (low 7 bits)
    private int _ocRxMask;      // user OC pin mask for RX (low 7 bits)
    // PureSignal master arm. When set on HL2 the C0=0x14 (Attenuator) frame
    // also writes puresignal_run into C2 bit 6, the predistortion register
    // is added to the rotation, and (when MOX is on) two receivers are
    // requested in the Config frame so the gateware emits paired DDC0/DDC1
    // IQ. Issue #172. mi0bot networkproto1.c:1102, console.cs:8483-8503.
    private int _psEnabled;
    private int _psPredistortionValue;     // 0..15 (low nibble of C2)
    private int _psPredistortionSubindex;  // 0..255 (whole C1 byte)
    // HL2 TX-side step attenuator (AD9866 TX PGA) target in dB. Sentinel
    // int.MinValue = "untouched" so the C4 byte falls through to the
    // existing RX-side encoding in WriteAttenuatorPayload — first PS arm
    // is bit-exact identical to today. PsAutoAttenuateService writes here
    // each time mi0bot's timer2code SetNewValues state would fire ATTOnTX.
    // mi0bot console.cs:2084 (UI range -28..+31), networkproto1.c:1086-1088
    // (wire encoding).
    private int _hl2TxAttnDb = int.MinValue;
    private long _droppedFrames;
    private long _totalFrames;

    private Socket? _socket;
    private IPEndPoint? _remote;
    private Thread? _rxThread;
    private Task? _txTask;
    private CancellationTokenSource? _loopCts;
    private bool _disposed;

    // TX IQ source: WDSP-TXA-driven ring in the live path (task #7/#8), or
    // the built-in test-tone when caller wants a bring-up carrier. Default is
    // the tone so legacy callers (tests, tools/zeus-dump) keep working.
    private readonly ITxIqSource _txIqSource;

    public Protocol1Client(ILogger<Protocol1Client>? logger = null, ITxIqSource? iqSource = null)
    {
        _log = logger ?? NullLogger<Protocol1Client>.Instance;
        // Wrap the operator's IQ source in a tap so we can mirror every
        // transmitted sample into the PS-feedback TX-side ring without
        // changing the WriteUsbFrame signature. The tap is cheap (one
        // s16→float and a ring-index bump per sample) and a no-op when
        // PS isn't armed (the consumer side checks PsEnabled before
        // touching the ring contents).
        _txIqSource = new PsTapIqSource(iqSource ?? new TestToneGenerator(), this);
        _channel = Channel.CreateBounded<IqFrame>(new BoundedChannelOptions(DefaultFrameChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    public ChannelReader<IqFrame> IqFrames => _channel.Reader;
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);
    public long TotalFrames => Interlocked.Read(ref _totalFrames);

    public event Action<TelemetryReading>? TelemetryReceived;
    public event Action<AdcOverloadStatus>? AdcOverloadObserved;

    // ---- PureSignal feedback (HL2-only, P1) -------------------------
    // 1024-sample paired blocks fed to WDSP `psccF`. Mirrors P2's
    // Protocol2Client.PsFeedbackFrames channel so DspPipelineService can
    // pump either protocol with the same code. Issue #172.
    //
    // TX side = the operator's TX IQ as we wrote it to the wire (snapshotted
    // from _txIqSource on every TX packet). RX side = DDC1 samples decoded
    // from the EP6 RX stream when PsEnabled && Mox && nddc==2 (HL2 paired
    // layout, mi0bot networkproto1.c:990,1005).
    private const int PsFeedbackBlockSize = 1024;
    private readonly Channel<PsFeedbackFrame> _psFeedbackFrames = Channel.CreateUnbounded<PsFeedbackFrame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly float[] _psTxI = new float[PsFeedbackBlockSize];
    private readonly float[] _psTxQ = new float[PsFeedbackBlockSize];
    private readonly float[] _psRxI = new float[PsFeedbackBlockSize];
    private readonly float[] _psRxQ = new float[PsFeedbackBlockSize];
    private int _psBlockFill;
    private ulong _psBlockStartSeq;
    // Diagnostic counter — tells the operator (via 1-Hz log line) whether the
    // gateware is actually emitting paired DDC0/DDC1 frames after PS arm.
    // See lessons_puresignal_convergence_g2_mkii.md for the same idiom on P2.
    private long _psPairedPacketCount;
    private long _psBlocksEmitted;
    public long PsPairedPacketCount => Interlocked.Read(ref _psPairedPacketCount);

    public ChannelReader<PsFeedbackFrame> PsFeedbackFrames => _psFeedbackFrames.Reader;

    // ---- Synchronous RX sink (iter5: collapse pumps onto RxLoop thread) -----
    // Optional sink attached via AttachRxSink. When non-null, RxLoop calls
    // sink.OnIqFrame / sink.OnPsFeedbackFrame DIRECTLY instead of writing to
    // the channels — this eliminates the Channel<T> -> WaitToReadAsync ->
    // ThreadPool wake-up amplification we measured in iter4. We keep the
    // channel-write fallback for the no-sink case so tests / in-process
    // probes (e.g. Zeus.Protocol1.Tests, tools/zeus-dump) continue to work.
    //
    // Read via Volatile.Read at the top of every packet so a runtime swap
    // (rare; Interlocked.Exchange) is visible without a lock.
    private IRxPacketSink? _rxSink;

    /// <inheritdoc />
    public void AttachRxSink(IRxPacketSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        Interlocked.Exchange(ref _rxSink, sink);
    }

    /// <inheritdoc />
    public void DetachRxSink() => Interlocked.Exchange(ref _rxSink, null);

    // Small ring of the most-recent TX-IQ samples we wrote to the wire.
    // When PS+MOX is on, the RX loop pulls a "TX side" out of this ring
    // for each DDC1 feedback sample so the calcc state machine receives
    // paired (tx_ref, rx_obs) blocks. The ring holds two PS feedback blocks
    // worth of samples (2048) so the natural latency between writing an IQ
    // sample and seeing its post-PA reflection on DDC1 (HL2 + external
    // coupler total round-trip ≈ 1-2 ms or ~50-200 samples at 48 kHz)
    // is comfortably absorbed without overwriting before the read.
    //
    // Aligning ring read offset with ring write offset is approximate —
    // mi0bot/pihpsdr both rely on calcc's own coarse alignment search to
    // recover the exact group-delay, and the operator-tunable "Amp delay"
    // setting (PsAmpDelayNs) is exactly there to nudge it. So we read
    // from the most-recent write minus a fixed lookback (currently 0 —
    // calcc handles the rest).
    private const int PsTxRingSize = 2048;
    private readonly float[] _psTxRingI = new float[PsTxRingSize];
    private readonly float[] _psTxRingQ = new float[PsTxRingSize];
    private int _psTxRingWrite;

    /// <summary>
    /// Capture one TX-IQ sample-pair just after it has been written into the
    /// EP2 payload. Called from <see cref="ControlFrame.WriteUsbFrame"/>
    /// indirectly via the <see cref="ITxIqSource"/> seam — see
    /// <see cref="ApplyPsTap"/>.
    /// </summary>
    internal void RecordPsTxSample(short i, short q)
    {
        // s16 → float in [-1, +1].
        const float scale = 1f / 32768f;
        int idx = _psTxRingWrite;
        _psTxRingI[idx] = i * scale;
        _psTxRingQ[idx] = q * scale;
        idx = (idx + 1) & (PsTxRingSize - 1); // PsTxRingSize is power of 2.
        // Volatile write so the RX-thread reader observes ordered writes.
        Volatile.Write(ref _psTxRingWrite, idx);
    }

    /// <summary>
    /// Decode an HL2 4-DDC PS-armed EP6 packet — mi0bot's canonical layout
    /// (Thetis console.cs:8186-8265, networkproto1.c:WriteMainLoop_HL2,
    /// cmaster.cs:8511-8550 FOUR_DDC routing). Stream assignment:
    ///   DDC0 = RX1 audio (operator's listening freq) → publish to IqFrame
    ///          channel so the panadapter and audio chain stay alive even
    ///          while PS is keying.
    ///   DDC1 = RX2 / unused on HL2 → discarded.
    ///   DDC2 = PS RX feedback (post-PA tap, NCO=TX freq) → pscc "rx" arg.
    ///   DDC3 = PS TX reference (TX-DAC loopback, NCO=TX freq) → pscc "tx".
    /// Pair DDC2 + DDC3 samples 1:1, accumulate 1024 paired complex samples,
    /// then emit a PsFeedbackFrame for the DspPipelineService pump.
    /// </summary>
    private void HandlePs4DdcPacket(ReadOnlySpan<byte> packet)
    {
        int needed = 2 * PacketParser.Hl2Ps4DdcSamplesPerPacket;
        var ddc0 = ArrayPool<double>.Shared.Rent(needed);
        var ddc1 = ArrayPool<double>.Shared.Rent(needed);
        var ddc2 = ArrayPool<double>.Shared.Rent(needed);
        var ddc3 = ArrayPool<double>.Shared.Rent(needed);
        bool publishedToIqChannel = false;
        try
        {
            if (!PacketParser.TryParseHl2Ps4DdcPacket(
                    packet, ddc0, ddc1, ddc2, ddc3,
                    out uint seq, out int samples,
                    out TelemetryReading telemetry0,
                    out TelemetryReading telemetry1,
                    out byte overloadBits))
                return;

            Interlocked.Increment(ref _psPairedPacketCount);
            ObserveSequence(seq);
            Interlocked.Increment(ref _totalFrames);

            // Fan out telemetry + overload exactly like the standard 1-DDC
            // path (ReceiveLoopAsync). Without this, FWD/REF/PA-temp and
            // ADC-overload signals freeze for the duration of any PS+TUN
            // window — operator sees 0.0 W in the meter while the radio is
            // visibly transmitting.
            if (telemetry0.C0Address != 0)
            {
                try { TelemetryReceived?.Invoke(telemetry0); }
                catch (Exception ex) { _log.LogWarning(ex, "TelemetryReceived handler threw"); }
            }
            if (telemetry1.C0Address != 0)
            {
                try { TelemetryReceived?.Invoke(telemetry1); }
                catch (Exception ex) { _log.LogWarning(ex, "TelemetryReceived handler threw"); }
            }
            try { AdcOverloadObserved?.Invoke(AdcOverloadStatus.FromBits(overloadBits)); }
            catch (Exception ex) { _log.LogWarning(ex, "AdcOverloadObserved handler threw"); }

            // DDC0 → IqFrame channel — keeps panadapter / audio alive during PS+TX.
            // Use a fresh rented buffer the channel can own; the ddc0 rental is
            // freed in the finally block.
            var rented = ArrayPool<double>.Shared.Rent(2 * samples);
            new ReadOnlySpan<double>(ddc0, 0, 2 * samples)
                .CopyTo(rented.AsSpan(0, 2 * samples));
            int rateHz = (HpsdrSampleRate)Volatile.Read(ref _rate) switch
            {
                HpsdrSampleRate.Rate48k => 48_000,
                HpsdrSampleRate.Rate96k => 96_000,
                HpsdrSampleRate.Rate192k => 192_000,
                HpsdrSampleRate.Rate384k => 384_000,
                _ => 48_000,
            };
            var memory = new ReadOnlyMemory<double>(rented, 0, 2 * samples);
            var frame = new IqFrame(memory, samples, rateHz, seq, NowNs());
            // iter5: if a synchronous sink is attached, hand the frame off
            // directly on the RX thread (no Channel hop). Sink takes ownership
            // of `rented` on success; on throw, we return the buffer ourselves
            // so a buggy consumer can't leak the pool.
            var sinkSnap = Volatile.Read(ref _rxSink);
            if (sinkSnap != null)
            {
                try
                {
                    sinkSnap.OnIqFrame(in frame);
                    publishedToIqChannel = true; // sink now owns `rented`
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "p1.rx.sink_threw kind=iq");
                    ArrayPool<double>.Shared.Return(rented);
                }
            }
            else if (_channel.Writer.TryWrite(frame))
            {
                publishedToIqChannel = true; // channel now owns `rented`
            }
            else
            {
                ArrayPool<double>.Shared.Return(rented);
            }

            // DDC2 → pscc RX, DDC3 → pscc TX. Mirror mi0bot cmaster.cs:8537-8538
            // (FOUR_DDC routing for HL2 with tot=5: psrx=2, pstx=3).
            for (int s = 0; s < samples; s++)
            {
                if (_psBlockFill == 0) _psBlockStartSeq = seq;
                _psRxI[_psBlockFill] = (float)ddc2[2 * s];
                _psRxQ[_psBlockFill] = (float)ddc2[2 * s + 1];
                _psTxI[_psBlockFill] = (float)ddc3[2 * s];
                _psTxQ[_psBlockFill] = (float)ddc3[2 * s + 1];
                _psBlockFill++;

                if (_psBlockFill >= PsFeedbackBlockSize)
                {
                    var txI = new float[PsFeedbackBlockSize];
                    var txQ = new float[PsFeedbackBlockSize];
                    var rxI = new float[PsFeedbackBlockSize];
                    var rxQ = new float[PsFeedbackBlockSize];
                    Array.Copy(_psTxI, txI, PsFeedbackBlockSize);
                    Array.Copy(_psTxQ, txQ, PsFeedbackBlockSize);
                    Array.Copy(_psRxI, rxI, PsFeedbackBlockSize);
                    Array.Copy(_psRxQ, rxQ, PsFeedbackBlockSize);
                    var psFrame = new PsFeedbackFrame(txI, txQ, rxI, rxQ, _psBlockStartSeq);
                    // iter5: prefer the synchronous sink when attached. PS-feedback
                    // buffers are plain float[] (not pooled), so a sink-throws path
                    // just drops the block — no ArrayPool fallout.
                    var psSinkSnap = Volatile.Read(ref _rxSink);
                    if (psSinkSnap != null)
                    {
                        try { psSinkSnap.OnPsFeedbackFrame(in psFrame); }
                        catch (Exception ex) { _log.LogError(ex, "p1.rx.sink_threw kind=psfb"); }
                    }
                    else
                    {
                        _psFeedbackFrames.Writer.TryWrite(psFrame);
                    }
                    _psBlockFill = 0;

                    // Heartbeat: every Nth block, log block-peak magnitudes so
                    // we can see whether DDC2 / DDC3 are actually carrying signal.
                    // PS at 192k emits ~187 blocks/s; log every ~190 = ~1 Hz.
                    if (++_psBlocksEmitted % 190 == 0)
                    {
                        float rxPk = 0f, txPk = 0f, rxAbs = 0f, txAbs = 0f;
                        for (int j = 0; j < PsFeedbackBlockSize; j++)
                        {
                            float ari = Math.Abs(rxI[j]);
                            float arq = Math.Abs(rxQ[j]);
                            float ati = Math.Abs(txI[j]);
                            float atq = Math.Abs(txQ[j]);
                            if (ari > rxPk) rxPk = ari;
                            if (arq > rxPk) rxPk = arq;
                            if (ati > txPk) txPk = ati;
                            if (atq > txPk) txPk = atq;
                            rxAbs += ari + arq;
                            txAbs += ati + atq;
                        }
                        _log.LogInformation(
                            "p1.ps.fb DDC2(rx) peak={RxPk:F4} mean={RxMn:F4} | DDC3(tx) peak={TxPk:F4} mean={TxMn:F4} | blocks={N}",
                            rxPk, rxAbs / (2 * PsFeedbackBlockSize),
                            txPk, txAbs / (2 * PsFeedbackBlockSize),
                            _psBlocksEmitted);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(ddc0);
            ArrayPool<double>.Shared.Return(ddc1);
            ArrayPool<double>.Shared.Return(ddc2);
            ArrayPool<double>.Shared.Return(ddc3);
            _ = publishedToIqChannel; // suppress unused warning
        }
    }

    public bool EnableHl2BandVolts
    {
        get => Volatile.Read(ref _enableHl2BandVolts) != 0;
        set => Interlocked.Exchange(ref _enableHl2BandVolts, value ? 1 : 0);
    }

    public Task ConnectAsync(IPEndPoint radioEndpoint, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_socket is not null) throw new InvalidOperationException("Already connected.");

        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 256 * 1024,
            SendBufferSize = 64 * 1024,
            ReceiveTimeout = RxSocketTimeoutMs,
        };
        sock.Bind(new IPEndPoint(IPAddress.Any, 0));

        _socket = sock;
        _remote = radioEndpoint;
        _log.LogInformation("Protocol1 bound local={Local} remote={Remote}", sock.LocalEndPoint, radioEndpoint);
        return Task.CompletedTask;
    }

    public Task StartAsync(StreamConfig config, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_socket is null || _remote is null) throw new InvalidOperationException("Call ConnectAsync first.");
        if (_loopCts is not null) throw new InvalidOperationException("Already started.");

        Interlocked.Exchange(ref _rate, (int)config.Rate);
        Interlocked.Exchange(ref _preamp, config.PreampOn ? 1 : 0);
        Interlocked.Exchange(ref _attenDb, config.Atten.ClampedDb);
        Interlocked.Exchange(ref _droppedFrames, 0);
        Interlocked.Exchange(ref _totalFrames, 0);

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Send Metis start. We send 3× on macOS to work around first-UDP-drop
        // (doc 02 §3).
        SendStartStop(start: true);

        _rxThread = new Thread(RxLoop)
        {
            IsBackground = true,
            Name = "Zeus.Protocol1.Rx",
        };
        _rxThread.Start();

        _txTask = Task.Run(() => TxLoopAsync(_loopCts.Token), _loopCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_loopCts is null) return;

        try
        {
            _loopCts.Cancel();
        }
        catch (ObjectDisposedException) { }

        SendStartStop(start: false);

        if (_txTask is not null)
        {
            try { await _txTask.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { _log.LogWarning("TX loop did not exit within 2s."); }
        }

        _rxThread?.Join(TimeSpan.FromSeconds(2));

        _loopCts.Dispose();
        _loopCts = null;
        _rxThread = null;
        _txTask = null;

        // Drain stale RX packets for ~100 ms per doc 02 §3.
        await DrainSocketAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
    }

    public Task DisconnectAsync(CancellationToken ct)
    {
        if (_socket is not null)
        {
            try { _socket.Close(); } catch { /* best-effort */ }
            _socket.Dispose();
            _socket = null;
        }
        _remote = null;
        return Task.CompletedTask;
    }

    public void SetVfoAHz(long hz) => Interlocked.Exchange(ref _vfoAHz, hz);
    public void SetSampleRate(HpsdrSampleRate rate) => Interlocked.Exchange(ref _rate, (int)rate);
    public void SetPreamp(bool on) => Interlocked.Exchange(ref _preamp, on ? 1 : 0);
    public void SetAttenuator(HpsdrAtten atten) => Interlocked.Exchange(ref _attenDb, atten.ClampedDb);
    public void SetAntennaRx(HpsdrAntenna ant) => Interlocked.Exchange(ref _antenna, (int)ant);
    public void SetBoardKind(HpsdrBoardKind board) => Interlocked.Exchange(ref _boardKind, (int)board);

    public HpsdrBoardKind BoardKind => (HpsdrBoardKind)Volatile.Read(ref _boardKind);
    public void SetHasN2adr(bool hasN2adr) => Interlocked.Exchange(ref _hasN2adr, hasN2adr ? 1 : 0);
    public void SetMox(bool on) => Interlocked.Exchange(ref _mox, on ? 1 : 0);
    public void SetDrive(int percent) =>
        Interlocked.Exchange(ref _drivePct, Math.Clamp(percent, 0, 100));

    public void SetDriveByte(byte value) =>
        Interlocked.Exchange(ref _driveByteOverride, value);

    public void SetOcMasks(byte txMask, byte rxMask)
    {
        Interlocked.Exchange(ref _ocTxMask, txMask & 0x7F);
        Interlocked.Exchange(ref _ocRxMask, rxMask & 0x7F);
    }

    /// <summary>
    /// Arm or disarm PureSignal on the wire. HL2-only effect: the C0=0x14
    /// (Attenuator) frame OR's puresignal_run into C2 bit 6, and the
    /// Predistortion register is added to the round-robin so calcc's
    /// subindex/value are kept in sync. The packet decoder switches to
    /// the 2-DDC paired layout only while PsEnabled is true AND MOX is
    /// asserted (matching mi0bot networkproto1.c:990, 1005). Reverts to
    /// 1-DDC standard layout otherwise.
    ///
    /// On non-HL2 boards this is a no-op on the wire — Protocol 2 has its
    /// own PS path via Protocol2Client.SetPsFeedbackEnabled. Storing the
    /// flag locally keeps the StateDto / engine in sync regardless of
    /// board so the round-tripping pumps don't get out of sync.
    /// </summary>
    public void SetPsEnabled(bool on)
    {
        Interlocked.Exchange(ref _psEnabled, on ? 1 : 0);
    }

    public bool PsEnabled => Volatile.Read(ref _psEnabled) != 0;

    /// <summary>
    /// Set the HL2 predistortion register payload (0x2b). value is the 4-bit
    /// PS-value (clamped to 0..15), subindex is the 8-bit subindex written
    /// to C1. Driven by WDSP's calcc state machine via the engine's
    /// SetPsControl pump; see DspPipelineService.
    /// </summary>
    public void SetPsPredistortion(byte value, byte subindex)
    {
        Interlocked.Exchange(ref _psPredistortionValue, value & 0x0F);
        Interlocked.Exchange(ref _psPredistortionSubindex, subindex);
    }

    public void SetHl2TxStepAttenuationDb(int db)
    {
        // Range matches mi0bot console.cs:2084 (udTXStepAttData.Minimum=-28,
        // Maximum=+31). ControlFrame.WriteAttenuatorPayload then maps to the
        // 6-bit wire byte via (31 - db) | 0x40 per networkproto1.c:1086-1088.
        int clamped = Math.Clamp(db, -28, 31);
        Interlocked.Exchange(ref _hl2TxAttnDb, clamped);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        try { DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
    }

    internal ControlFrame.CcState SnapshotState()
    {
        int over = Volatile.Read(ref _driveByteOverride);
        byte drive = over >= 0
            ? (byte)over
            // UI percent → raw 0..255 HPSDR drive byte. Used only when
            // RadioService hasn't pushed a calibrated byte (tests / legacy).
            : (byte)(Volatile.Read(ref _drivePct) * 255 / 100);

        bool psOn = Volatile.Read(ref _psEnabled) != 0;
        bool moxOn = Volatile.Read(ref _mox) != 0;
        bool isHl2 = (HpsdrBoardKind)Volatile.Read(ref _boardKind) == HpsdrBoardKind.HermesLite2;
        // Number of receivers requested in the Config payload (`(N-1) << 3`
        // in C4 bits [5:3]). mi0bot's HL2 path (Thetis console.cs:8186-8265)
        // uses **4 DDCs** during PS+MOX:
        //   DDC0 → RX1 audio (operator's listening freq, stays alive!)
        //   DDC1 → RX2 (or unused)
        //   DDC2 → PS RX feedback (post-PA tap, NCO=TX freq) — pscc "rx"
        //   DDC3 → PS TX reference (TX-DAC loopback, NCO=TX freq) — pscc "tx"
        // Outside PS+MOX we stay at single-DDC so the existing 1-DDC EP6
        // packet shape and parser are bit-exact unchanged.
        byte numRxMinus1 = (byte)(psOn && isHl2 && moxOn ? 3 : 0);

        return new(
            VfoAHz: Interlocked.Read(ref _vfoAHz),
            Rate: (HpsdrSampleRate)Volatile.Read(ref _rate),
            PreampOn: Volatile.Read(ref _preamp) != 0,
            Atten: new HpsdrAtten(Volatile.Read(ref _attenDb)),
            RxAntenna: (HpsdrAntenna)Volatile.Read(ref _antenna),
            Mox: Volatile.Read(ref _mox) != 0,
            EnableHl2BandVolts: Volatile.Read(ref _enableHl2BandVolts) != 0,
            Board: (HpsdrBoardKind)Volatile.Read(ref _boardKind),
            HasN2adr: Volatile.Read(ref _hasN2adr) != 0,
            DriveLevel: drive,
            UserOcTxMask: (byte)Volatile.Read(ref _ocTxMask),
            UserOcRxMask: (byte)Volatile.Read(ref _ocRxMask),
            PsEnabled: psOn,
            PsPredistortionValue: (byte)Volatile.Read(ref _psPredistortionValue),
            PsPredistortionSubindex: (byte)Volatile.Read(ref _psPredistortionSubindex),
            NumReceiversMinusOne: numRxMinus1,
            // mi0bot networkproto1.c:1086-1088 — when MOX is on and the
            // operator/auto-att has set ATTOnTX, swap C4 source from
            // rx_step_attn to tx_step_attn. Sentinel int.MinValue means
            // untouched, fall through to the RX-side encoding above.
            Hl2TxAttnDb: Volatile.Read(ref _hl2TxAttnDb));
    }

    private void RxLoop()
    {
        var sock = _socket!;
        var ct = _loopCts!.Token;
        var buffer = new byte[PacketParser.PacketLength];
        // perf3: reuse one SocketAddress across receives. The pre-.NET-8
        // `ReceiveFrom(..., ref EndPoint)` overload allocates a fresh
        // IPEndPoint via EndPoint.Create() on every call (per .NET runtime
        // source — SocketAddress -> IPEndPoint conversion). At 381 RX
        // pkt/s that's the largest single allocator on the receive path
        // (~16% of total alloc-rate per perf3 baseline). The remote
        // address is written into `sockAddr` but never read by RxLoop —
        // HL2 is the only peer the bound socket sees. .NET 8+ exposes a
        // ReceiveFrom overload that fills a reusable SocketAddress in
        // place, eliminating the per-call allocation entirely.
        var sockAddr = new SocketAddress(sock.AddressFamily);
        int consecutiveTimeouts = 0;
        // TX-pacing counter — every Nth successfully-parsed RX packet signals
        // TxLoopAsync to emit one EP2 packet. N = rxRate / 48 kHz because the
        // HL2's TX DAC clock runs at a fixed 48 kHz regardless of the RX rate.
        int rxPktCounter = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = sock.ReceiveFrom(buffer, SocketFlags.None, sockAddr);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    if (++consecutiveTimeouts >= ConsecutiveTimeoutsBeforeGiveUp)
                    {
                        _log.LogWarning("RX: {N} consecutive socket timeouts — radio gone", consecutiveTimeouts);
                        return;
                    }
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                consecutiveTimeouts = 0;

                if (n != PacketParser.PacketLength) continue;

                // PS-armed 4-DDC layout (HL2 only). HL2 emits the 26-byte-
                // per-slot 4-DDC packet shape only when the last Config
                // frame carried NumReceiversMinusOne=3 — and SnapshotState
                // only requests that during MOX+PS+HL2. Outside that window
                // the operator gets normal single-RX 8-byte packets, so the
                // parser must follow the same gate (mi0bot Thetis
                // console.cs:8186-8265 — the !_mox branch keeps single-DDC
                // even with PS armed). Brief mismatch on MOX edges (1-3 ms
                // while the new Config frame propagates) is tolerated;
                // pscc resets cleanly on any garbage block via its
                // MOX-delay state. The 4-DDC handler publishes DDC0 to
                // the IqFrame channel (RX1 audio + panadapter stay alive)
                // and DDC2/DDC3 to the PsFeedbackFrame channel.
                if (Volatile.Read(ref _psEnabled) != 0
                    && Volatile.Read(ref _mox) != 0
                    && (HpsdrBoardKind)Volatile.Read(ref _boardKind) == HpsdrBoardKind.HermesLite2)
                {
                    HandlePs4DdcPacket(buffer.AsSpan(0, n));
                    // Pace the TX loop off the same RX clock so MOX TX
                    // continues to fire while PS is armed.
                    var psRateHz = (HpsdrSampleRate)Volatile.Read(ref _rate) switch
                    {
                        HpsdrSampleRate.Rate48k => 48_000,
                        HpsdrSampleRate.Rate96k => 96_000,
                        HpsdrSampleRate.Rate192k => 192_000,
                        HpsdrSampleRate.Rate384k => 384_000,
                        _ => 48_000,
                    };
                    // 4-DDC packets are 38 paired samples/packet, so the
                    // RX pkt rate is rateHz/38 (vs rateHz/126 for N=1).
                    // Target TX pkt rate stays at 48k/126 ≈ 381. Rounded
                    // division avoids the integer-truncation overshoot we
                    // had earlier.
                    double rxPktsPerSec = psRateHz / (double)PacketParser.Hl2Ps4DdcSamplesPerPacket;
                    int psTxDivider = Math.Max(1, (int)Math.Round(rxPktsPerSec / 381.0));
                    if ((++rxPktCounter % psTxDivider) == 0)
                    {
                        try { _txSignal.Release(); } catch (SemaphoreFullException) { }
                    }
                    continue;
                }

                var rented = ArrayPool<double>.Shared.Rent(2 * PacketParser.ComplexSamplesPerPacket);
                bool ok = PacketParser.TryParsePacket(
                    buffer.AsSpan(0, n),
                    rented,
                    out uint seq,
                    out int samples,
                    out TelemetryReading telemetry0,
                    out TelemetryReading telemetry1,
                    out byte overloadBits);

                if (!ok)
                {
                    ArrayPool<double>.Shared.Return(rented);
                    continue;
                }

                ObserveSequence(seq);
                Interlocked.Increment(ref _totalFrames);

                // Fire per-frame: each USB frame's C&C is processed independently,
                // so pairs like (addr=1, addr=2) both contribute updates. The former
                // "last wins" logic masked the FWD reading whenever the HL2 paired
                // its FWD frame with a REF frame.
                // Synchronous fan-out; handlers must not block the RX thread.
                if (telemetry0.C0Address != 0)
                {
                    try { TelemetryReceived?.Invoke(telemetry0); }
                    catch (Exception ex) { _log.LogWarning(ex, "TelemetryReceived handler threw"); }
                }
                if (telemetry1.C0Address != 0)
                {
                    try { TelemetryReceived?.Invoke(telemetry1); }
                    catch (Exception ex) { _log.LogWarning(ex, "TelemetryReceived handler threw"); }
                }

                // Overload status fires every packet — the auto-ATT control loop
                // needs cleared-frame signals as well as set ones to decay the offset.
                try { AdcOverloadObserved?.Invoke(AdcOverloadStatus.FromBits(overloadBits)); }
                catch (Exception ex) { _log.LogWarning(ex, "AdcOverloadObserved handler threw"); }

                var rateHz = (HpsdrSampleRate)Volatile.Read(ref _rate) switch
                {
                    HpsdrSampleRate.Rate48k => 48_000,
                    HpsdrSampleRate.Rate96k => 96_000,
                    HpsdrSampleRate.Rate192k => 192_000,
                    HpsdrSampleRate.Rate384k => 384_000,
                    _ => 48_000,
                };

                // Pace the TX loop off the HL2's own clock. HL2 emits RX
                // packets at (rateHz / 126) pkt/s; we want TX at (48_000/126)
                // = 381 pkt/s, so signal every Nth RX packet where
                // N = rateHz / 48_000. At 48k RX that's 1:1 (piHPSDR-style),
                // at 192k it's 1 TX per 4 RX.
                int txDivider = Math.Max(1, rateHz / 48_000);
                if ((++rxPktCounter % txDivider) == 0)
                {
                    try { _txSignal.Release(); } catch (SemaphoreFullException) { /* over-backpressured; TxLoopAsync will catch up */ }
                }

                var memory = new ReadOnlyMemory<double>(rented, 0, 2 * samples);
                var frame = new IqFrame(memory, samples, rateHz, seq, NowNs());
                // iter5: prefer the synchronous sink when attached — the sink
                // takes ownership of `rented` on a successful (non-throwing)
                // return and must arrange the ArrayPool return when done. On
                // throw, we return the buffer here so a broken consumer
                // can't leak the pool.
                var sinkSnap = Volatile.Read(ref _rxSink);
                if (sinkSnap != null)
                {
                    try { sinkSnap.OnIqFrame(in frame); }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "p1.rx.sink_threw kind=iq");
                        ArrayPool<double>.Shared.Return(rented);
                    }
                }
                else
                {
                    // DropOldest: full-channel writes never block; oldest frame is discarded.
                    // Its rented buffer is not returned to ArrayPool — we accept that the pool
                    // will re-allocate rather than complicate ownership for MVP.
                    _channel.Writer.TryWrite(frame);
                }
            }
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    private uint _lastSeenSequence;
    private bool _seenAnySequence;

    private void ObserveSequence(uint seq)
    {
        if (_seenAnySequence && seq > _lastSeenSequence)
        {
            long gap = (long)seq - (long)_lastSeenSequence - 1;
            if (gap > 0) Interlocked.Add(ref _droppedFrames, gap);
        }
        _seenAnySequence = true;
        _lastSeenSequence = seq;
    }

    // 4-phase rotation across the registers we currently own. Every phase
    // pairs the frequency register (ensuring sub-3ms QSY latency) with one
    // of Config / DriveFilter / Attenuator in turn. Attenuator needs a slot
    // or HL2 firmware never sees gain changes.
    //
    // When MOX is on we swap in a TX-flavored table: with duplex=1 always
    // (ControlFrame.cs Config C4[2]), HL2 needs TxFreq (0x02) continuously
    // or its TX mixer sits at power-on default (likely 0) and the PA sees
    // no drive. RxFreq stays in the rotation so demod during duplex TX
    // follows QSY, and TxFreq shows up in 2 of every 4 packets so a QSY
    // while keyed takes effect within a couple of ms. The RX VFO is reused for
    // TxFreq when Split/RIT are off, which matches what we do here since Zeus
    // has no separate TX VFO yet.
    internal static (ControlFrame.CcRegister first, ControlFrame.CcRegister second) PhaseRegisters(int phase, bool mox)
        => PhaseRegisters(phase, mox, psArmed: false);

    /// <summary>
    /// Round-robin register selector. When <paramref name="psArmed"/> is true
    /// the rotation is widened to 8 phases and includes the new HL2-PS
    /// registers — RxFreq2 (DDC1 NCO) and AdcRouting (per-RX→ADC selector,
    /// HL2-doc 0x0e). Without RxFreq2 the second DDC sits at 0 Hz; without
    /// AdcRouting it samples ADC0 instead of the dedicated PA-coupler ADC1
    /// — pscc would then see no useful feedback and binfo[6] would NaN out
    /// (Issue #172, observed before this fix). Mirrors mi0bot
    /// networkproto1.c:WriteMainLoop_HL2 case 2/3/4.
    /// </summary>
    internal static (ControlFrame.CcRegister first, ControlFrame.CcRegister second) PhaseRegisters(
        int phase, bool mox, bool psArmed)
    {
        if (psArmed)
        {
            int q = phase & 0xF;
            if (mox)
            {
                // PS+MOX (HL2 4-DDC). Every 16-frame window emits each of
                // the seven PS-critical registers (Config, TxFreq, RxFreq,
                // RxFreq2, RxFreq3, RxFreq4, AdcRouting, Attenuator,
                // DriveFilter) at least twice. RxFreq3/RxFreq4 carry the
                // pscc TX/RX NCO frequencies — without them DDC2 and DDC3
                // sit at 0 Hz and pscc gets DC. Predistortion is omitted;
                // mi0bot doesn't emit it for HL2.
                return q switch
                {
                    0  => (ControlFrame.CcRegister.TxFreq,     ControlFrame.CcRegister.RxFreq3),
                    1  => (ControlFrame.CcRegister.TxFreq,     ControlFrame.CcRegister.RxFreq4),
                    2  => (ControlFrame.CcRegister.Attenuator, ControlFrame.CcRegister.TxFreq),
                    3  => (ControlFrame.CcRegister.TxFreq,     ControlFrame.CcRegister.DriveFilter),
                    4  => (ControlFrame.CcRegister.RxFreq,     ControlFrame.CcRegister.RxFreq3),
                    5  => (ControlFrame.CcRegister.RxFreq2,    ControlFrame.CcRegister.RxFreq4),
                    6  => (ControlFrame.CcRegister.AdcRouting, ControlFrame.CcRegister.TxFreq),
                    7  => (ControlFrame.CcRegister.Config,     ControlFrame.CcRegister.TxFreq),
                    8  => (ControlFrame.CcRegister.TxFreq,     ControlFrame.CcRegister.RxFreq3),
                    9  => (ControlFrame.CcRegister.RxFreq,     ControlFrame.CcRegister.RxFreq4),
                    10 => (ControlFrame.CcRegister.Attenuator, ControlFrame.CcRegister.RxFreq3),
                    11 => (ControlFrame.CcRegister.TxFreq,     ControlFrame.CcRegister.DriveFilter),
                    12 => (ControlFrame.CcRegister.RxFreq2,    ControlFrame.CcRegister.RxFreq3),
                    13 => (ControlFrame.CcRegister.RxFreq4,    ControlFrame.CcRegister.TxFreq),
                    14 => (ControlFrame.CcRegister.AdcRouting, ControlFrame.CcRegister.RxFreq3),
                    _  => (ControlFrame.CcRegister.Config,     ControlFrame.CcRegister.RxFreq4),
                };
            }
            // PS armed but RX-only: cache RxFreq3 / RxFreq4 / AdcRouting so
            // the radio has them ready for the next MOX edge. Number-of-
            // receivers in Config is 0 here so DDC2/DDC3 aren't streaming;
            // these writes are harmless.
            return q switch
            {
                0  => (ControlFrame.CcRegister.Config,     ControlFrame.CcRegister.RxFreq),
                1  => (ControlFrame.CcRegister.RxFreq,     ControlFrame.CcRegister.DriveFilter),
                2  => (ControlFrame.CcRegister.Attenuator, ControlFrame.CcRegister.RxFreq),
                3  => (ControlFrame.CcRegister.RxFreq,     ControlFrame.CcRegister.RxFreq2),
                4  => (ControlFrame.CcRegister.RxFreq,     ControlFrame.CcRegister.RxFreq3),
                5  => (ControlFrame.CcRegister.RxFreq,     ControlFrame.CcRegister.RxFreq4),
                6  => (ControlFrame.CcRegister.AdcRouting, ControlFrame.CcRegister.RxFreq),
                7  => (ControlFrame.CcRegister.Attenuator, ControlFrame.CcRegister.RxFreq),
                8  => (ControlFrame.CcRegister.RxFreq,     ControlFrame.CcRegister.Config),
                9  => (ControlFrame.CcRegister.RxFreq,     ControlFrame.CcRegister.DriveFilter),
                10 => (ControlFrame.CcRegister.RxFreq3,    ControlFrame.CcRegister.RxFreq4),
                11 => (ControlFrame.CcRegister.RxFreq,     ControlFrame.CcRegister.Attenuator),
                12 => (ControlFrame.CcRegister.RxFreq,     ControlFrame.CcRegister.RxFreq3),
                13 => (ControlFrame.CcRegister.RxFreq,     ControlFrame.CcRegister.RxFreq4),
                14 => (ControlFrame.CcRegister.RxFreq,     ControlFrame.CcRegister.AdcRouting),
                _  => (ControlFrame.CcRegister.RxFreq,     ControlFrame.CcRegister.Config),
            };
        }

        int p = phase & 0x3;
        if (mox)
        {
            return p switch
            {
                0 => (ControlFrame.CcRegister.TxFreq,     ControlFrame.CcRegister.RxFreq),
                1 => (ControlFrame.CcRegister.TxFreq,     ControlFrame.CcRegister.DriveFilter),
                2 => (ControlFrame.CcRegister.Attenuator, ControlFrame.CcRegister.TxFreq),
                _ => (ControlFrame.CcRegister.TxFreq,     ControlFrame.CcRegister.Config),
            };
        }
        return p switch
        {
            0 => (ControlFrame.CcRegister.Config,     ControlFrame.CcRegister.RxFreq),
            1 => (ControlFrame.CcRegister.RxFreq,     ControlFrame.CcRegister.DriveFilter),
            2 => (ControlFrame.CcRegister.Attenuator, ControlFrame.CcRegister.RxFreq),
            _ => (ControlFrame.CcRegister.RxFreq,     ControlFrame.CcRegister.Config),
        };
    }

    private async Task TxLoopAsync(CancellationToken ct)
    {
        var sock = _socket!;
        var remote = _remote!;
        var buf = new byte[ControlFrame.PacketLength];
        uint sendSeq = 0;
        int phase = 0;
        // Diagnostic: count packets per wall-second so we can verify the TX
        // rate actually lands near 381 pkt/s (HL2 48 kHz DAC / 126 pairs per
        // packet). RxLoop releases _txSignal once per HL2-paced tick.
        var rateWindowStart = DateTime.UtcNow;
        int rateWindowPkts = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _txSignal.WaitAsync(ct).ConfigureAwait(false);
                var state = SnapshotState();
                // PS-armed rotation widens to 8 phases to fit the
                // Predistortion (0x2b) register without crowding TxFreq.
                // The phase counter wraps modulo whichever rotation is in
                // effect, recomputed every tick so a mid-stream PS toggle
                // doesn't lose its slot.
                bool psArmed = state.PsEnabled && state.Board == HpsdrBoardKind.HermesLite2;
                var (first, second) = PhaseRegisters(phase, state.Mox, psArmed);
                phase = (phase + 1) & (psArmed ? 0xF : 0x3);
                ControlFrame.BuildDataPacket(buf, sendSeq++, first, second, in state, _txIqSource);
                rateWindowPkts++;
                var nowUtc = DateTime.UtcNow;
                var elapsed = nowUtc - rateWindowStart;
                if (elapsed >= TimeSpan.FromSeconds(1))
                {
                    _log.LogInformation(
                        "p1.tx.rate pkts={Pkts} in {Ms:F0}ms = {Rate:F0} pkt/s (target 381) | wire: peak={Peak}/32767 mean={Mean} firstI={I} firstQ={Q} drv={Drv}",
                        rateWindowPkts, elapsed.TotalMilliseconds, rateWindowPkts / elapsed.TotalSeconds,
                        ControlFrame.LastPeakAbs, ControlFrame.LastMeanAbs,
                        ControlFrame.LastFirstI, ControlFrame.LastFirstQ, ControlFrame.LastDriveByte);
                    rateWindowStart = nowUtc;
                    rateWindowPkts = 0;
                }
                try
                {
                    await sock.SendToAsync(buf, SocketFlags.None, remote, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException ex)
                {
                    _log.LogWarning(ex, "TX SendTo failed; stopping TX loop.");
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
    }

    private void SendStartStop(bool start)
    {
        if (_socket is null || _remote is null) return;
        Span<byte> buf = stackalloc byte[64];
        ControlFrame.BuildStartStop(buf, start);
        byte[] heap = buf.ToArray();
        // Send 3× on macOS (first-UDP-drop workaround). Harmless elsewhere.
        int sends = OperatingSystem.IsMacOS() ? 3 : 1;
        for (int i = 0; i < sends; i++)
        {
            try { _socket.SendTo(heap, _remote); }
            catch (SocketException ex) { _log.LogWarning(ex, "Start/stop send {I}/{N} failed", i + 1, sends); }
            if (sends > 1 && i < sends - 1) Thread.Sleep(30);
        }
    }

    private async Task DrainSocketAsync(TimeSpan drainFor)
    {
        if (_socket is null) return;
        var deadline = DateTime.UtcNow + drainFor;
        var scratch = new byte[PacketParser.PacketLength];
        var remote = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var result = await _socket.ReceiveFromAsync(scratch, SocketFlags.None, remote).WaitAsync(drainFor).ConfigureAwait(false);
                _ = result;
            }
            catch { break; }
        }
    }

    private static long NowNs() =>
        (long)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency));

    /// <summary>
    /// Pass-through IQ source that mirrors every sample into the owning
    /// client's PS-TX ring. Cheap regardless of PS arm state — the ring
    /// reader checks PsEnabled before consuming. Issue #172.
    /// </summary>
    private sealed class PsTapIqSource : ITxIqSource
    {
        private readonly ITxIqSource _inner;
        private readonly Protocol1Client _owner;
        public PsTapIqSource(ITxIqSource inner, Protocol1Client owner)
        {
            _inner = inner;
            _owner = owner;
        }
        public (short i, short q) Next(double amplitude)
        {
            var (i, q) = _inner.Next(amplitude);
            _owner.RecordPsTxSample(i, q);
            return (i, q);
        }
    }
}
