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

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;

namespace Zeus.Protocol2;

/// <summary>
/// Protocol 2 (OpenHPSDR "new protocol" / Thetis "ETH") streaming client.
/// Mirrors Zeus.Protocol1.Protocol1Client's lifecycle surface where it
/// overlaps; Protocol-1-only methods (HL2 dither, N2ADR filter board) are
/// absent here. Wire format verified against Thetis ChannelMaster network.c.
/// </summary>
public sealed class Protocol2Client : IDisposable, IAsyncDisposable
{
    private const int BufLen = 1444;
    private const int DiscoverySamplesPerPacket = 238;

    // Hi-priority status packet (radio → host on UDP 1025). Thetis treats it
    // as a 60-byte payload (network.c:683-756 reads up through byte 55), but
    // some firmwares pad to a longer length. Gate on "at least byte 19 valid"
    // — that's the highest offset the FWD/REV/exciter decode touches — so we
    // do not silently drop a short packet that still carries the meter ADCs.
    // 4-byte BE u32 P2 sequence header that prefixes every UDP packet, plus
    // the 20-byte hi-pri field range we actually decode (PTT/PLL @ +0,
    // exciter @ +2, FWD @ +10, REV @ +18). Real radios send 60-byte packets;
    // the guard is the minimum we need to safely read every field.
    private const int HiPriSeqHeaderBytes = 4;
    private const int HiPriStatusMinBytes = HiPriSeqHeaderBytes + 20;

    // On ANAN G2 MkII (Orion-II / Saturn) the first two DDC slots are wired
    // to the PureSignal / diversity feedback path. User-visible receivers
    // start at DDC2. pihpsdr's `new_protocol_receive_specific` and
    // `new_protocol_high_priority` both do `ddc = 2 + i` for these boards;
    // we follow the same convention. Radio then sends DDC2 IQ from port
    // 1035 + 2 = 1037.
    private const int G2RxDdc = 2;

    // Hermes-class radios (Brick2 is the live consumer) use a single ADC
    // and have no PureSignal feedback DDCs reserved at the front of the pool.
    // The user-visible RX maps to DDC0 directly. Radio sends DDC0 IQ from
    // port 1035. Reference: deskhpsdr src/new_protocol.c:1692-1698 — Hermes
    // sets `receiver[i].ddc = i` (not `i + 2` as for Orion/Angelia/MkII).
    private const int HermesRxDdc = 0;

    // 2^32 / 122_880_000 — converts Hz to a 32-bit phase-increment word
    // when the general packet is in "send phase word" mode (bit 3 of
    // CmdGeneral[37], which pihpsdr and Thetis both set).
    private const double HzToPhase = 34.952533333333333;

    private readonly ILogger<Protocol2Client> _log;
    private readonly Channel<IqFrame> _iqFrames = Channel.CreateUnbounded<IqFrame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private Socket? _sock;
    private IPEndPoint? _radioEndpoint;
    private CancellationTokenSource? _rxCts;
    private Task? _rxTask;
    private Task? _keepaliveTask;
    private int _sampleRateKhz = 48;
    private uint _rxFreqHz = 14_200_000;
    private byte _numAdc = 2;
    // Connected board kind, plumbed from RadioService's ConnectedBoardKind via
    // DspPipelineService at connect time. Used by HandleDdcPacket for the
    // Hermes-on-P2 48 kHz amplitude correction (Brick2 firmware delivers IQ
    // ~29 dB hotter at 48 kHz than at higher rates — deskhpsdr
    // src/new_protocol.c:2516-2530). Unknown == no correction applied.
    private HpsdrBoardKind _boardKind = HpsdrBoardKind.Unknown;
    // Mercury preamp defaults OFF — on a G2 the ADC has enough dynamic range
    // that the preamp is a crutch, not a default. Operator enables it when
    // needed via the UI. Attenuator 0 dB so the front-end isn't knocked down.
    private bool _preampOn;
    private byte _rxStepAttnDb;
    // TX step attenuator (0..31 dB) — Thetis network.c:1238-1242 writes the
    // same value to bytes 57/58/59 of CmdTx (one per ADC tap). The PS
    // auto-attenuate loop adjusts this when info[4] feedback level lands
    // outside the 128..181 ideal window so calcc has a chance to converge.
    // Default 0 matches the radio's power-on state and pihpsdr's untouched
    // baseline.
    private byte _txStepAttnDb;
    // PA settings — pushed from RadioService when PaSettingsStore changes or
    // the VFO crosses a band edge. _paEnabled is the global toggle that lands
    // in CmdGeneral[58]; _driveByte is the pre-calibrated drive level for
    // CmdHighPriority[345]; _ocTxMask/_ocRxMask drive CmdHighPriority[1401].
    // The piHPSDR-style global "OCtune" override was removed in #124 for
    // hardware-safety: OC during TUN follows OcTx, identical to TX.
    private bool _paEnabled = true;
    private byte _driveByte;
    private byte _ocTxMask;
    private byte _ocRxMask;
    private bool _moxOn;
    private bool _tuneActive;
    private long _totalFrames;
    private long _droppedFrames;
    private uint _lastDdc0Seq;
    private bool _haveFirstDdc0;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    // Per-stream sequence counters. The G2 firmware tracks seq per destination
    // port; sharing one counter across CmdGeneral/CmdRx/CmdTx/CmdHighPriority
    // makes the first HighPriority packet land at seq=2+, which the radio
    // treats as "stream started mid-flight" and silently drops — leaving the
    // DDC locked to whatever the previous client's last tune was.
    private uint _seqCmdGeneral;
    private uint _seqCmdRx;
    private uint _seqCmdTx;
    private uint _seqCmdHp;
    private uint _seqCmdTxIq;

    // TX-DUC IQ accumulator. WDSP TXA emits 1024/2048-sample blocks; the P2
    // wire format wants 240 complex samples per 1444-byte packet on port 1029.
    // We buffer into this 240-pair scratch and enqueue whenever it fills.
    private const int TxIqSamplesPerPacket = 240;
    // DAC rate = 192 kHz. 240 samples = 1.25 ms of audio per packet. A steady
    // 1 packet / 1.25 ms stream keeps the radio's TX FIFO level instead of
    // bursting (which shows up on the air as a pulsed / AM-modulated carrier
    // with multi-tone-looking sidebands).
    private const double TxDacSampleRate = 192_000.0;
    // Mirrors pihpsdr new_protocol.c:1972 — target FIFO fill of ~1250 samples
    // (6.5 ms of audio buffered in the radio). Past that we pace; below it
    // we send as fast as packets are ready so the radio never underruns.
    private const double TxFifoTargetSamples = 1250.0;
    private readonly float[] _txIqScratch = new float[TxIqSamplesPerPacket * 2];
    private int _txIqScratchCount;
    private readonly object _txIqGate = new();
    private readonly Channel<byte[]> _txIqQueue = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });
    private Task? _txIqSenderTask;

    // ---- PureSignal feedback (DDC0 + DDC1 paired on UDP 1035) ----
    // PS feedback decoder: when armed, packets on port 1035 carry interleaved
    // (DDC0=PS_RX_FEEDBACK=post-PA coupler IQ, DDC1=PS_TX_FEEDBACK=TX-DAC
    // loopback IQ) sample pairs (pihpsdr new_protocol.c:1615-1616, 2463-2510;
    // transmitter.c:2015-2030, 2066). DDC0 feeds pscc's "rx" arg; DDC1 feeds
    // pscc's "tx" arg. The accumulator collects 1024 complex pairs across
    // packets before emitting a frame. When PS is disarmed the radio reverts
    // to single-DDC packets and the standard RX demuxer takes over.
    // Volatile because RxLoop reads it across threads.
    private volatile bool _psFeedbackEnabled;
    // PS feedback source — false=Internal coupler (default), true=External
    // (Bypass). When externally bypassing, alex0 gains ALEX_RX_ANTENNA_BYPASS
    // (bit 11) during xmit + PS armed. RxSpecific/TxSpecific are byte-
    // identical between sources — only this one alex0 bit differs.
    // Reference: pihpsdr new_protocol.c:1284-1296 alex0 bypass selection.
    private volatile bool _psFeedbackExternal;
    private const int PsFeedbackBlockSize = 1024;
    private readonly Channel<PsFeedbackFrame> _psFeedbackFrames = Channel.CreateUnbounded<PsFeedbackFrame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly float[] _psTxI = new float[PsFeedbackBlockSize];
    private readonly float[] _psTxQ = new float[PsFeedbackBlockSize];
    private readonly float[] _psRxI = new float[PsFeedbackBlockSize];
    private readonly float[] _psRxQ = new float[PsFeedbackBlockSize];
    private int _psBlockFill;
    private ulong _psBlockStartSeq;

    public Protocol2Client(ILogger<Protocol2Client> log)
    {
        _log = log;
    }

    public ChannelReader<IqFrame> IqFrames => _iqFrames.Reader;
    public long TotalFrames => Interlocked.Read(ref _totalFrames);
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    // ---- Synchronous RX sink (iter5: collapse pumps onto RxLoop thread) -----
    // Optional sink attached via AttachRxSink. When non-null, RxLoop calls
    // sink.OnIqFrame / sink.OnPsFeedbackFrame DIRECTLY instead of writing to
    // the channels — this eliminates the Channel<T> -> WaitToReadAsync ->
    // ThreadPool wake-up amplification we measured in iter4. The channel-write
    // fallback remains when no sink is attached, so tests and in-process
    // probes continue to work.
    //
    // Mirrors the P1 surface; the IqFrame / PsFeedbackFrame types are
    // per-protocol because the protocol projects do not reference each other.
    private IRxPacketSink? _rxSink;

    /// <summary>
    /// Attach a synchronous RX sink. While non-null, the RX loop calls the
    /// sink directly INSTEAD of writing to <see cref="IqFrames"/> /
    /// <see cref="PsFeedbackFrames"/>. See <see cref="IRxPacketSink"/> for
    /// the threading contract.
    /// </summary>
    public void AttachRxSink(IRxPacketSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        Interlocked.Exchange(ref _rxSink, sink);
    }

    /// <summary>Detach the current RX sink, reverting to the channel-write path.</summary>
    public void DetachRxSink() => Interlocked.Exchange(ref _rxSink, null);

    /// <summary>
    /// Raised from the RX loop on every successfully received hi-priority
    /// status packet (UDP 1025, 60 B). Carries the FWD/REF/exciter ADC
    /// readings that drive the operator's TX power meter, plus the PTT-in
    /// and PLL-lock status bits. Mirrors P1's
    /// <c>IProtocol1Client.TelemetryReceived</c> surface.
    /// Fire-and-forget — handlers run synchronously on the RX thread and must
    /// not block. Issue #174 (G2 / P2 TX power meter shows zero).
    /// </summary>
    public event Action<P2TelemetryReading>? TelemetryReceived;

    /// <summary>
    /// Monotonic count of hi-priority status (UDP 1025) packets parsed since
    /// Start. Diagnostic — lets the operator confirm the radio is actually
    /// publishing PA telemetry, separately from whether the watts math
    /// looks right. Read by the 1 Hz log line in
    /// <see cref="RxLoop(System.Threading.CancellationToken)"/>.
    /// </summary>
    public long HiPriPacketCount => Interlocked.Read(ref _hiPriPackets);
    private long _hiPriPackets;

    public Task ConnectAsync(IPEndPoint radioEndpoint, CancellationToken ct)
    {
        if (_sock is not null)
            throw new InvalidOperationException("Already connected.");

        _radioEndpoint = new IPEndPoint(radioEndpoint.Address, 1024);
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Matched port convention — PC binds 1025, radio sends back with source
        // ports 1025/1026/1027/1035.. which we demux by fromaddr.
        sock.Bind(new IPEndPoint(IPAddress.Any, 1025));
        sock.ReceiveBufferSize = 1 << 20;
        _sock = sock;
        _log.LogInformation("p2.connect radio={Radio} localPort=1025", radioEndpoint.Address);
        return Task.CompletedTask;
    }

    public Task StartAsync(int sampleRateKhz, CancellationToken ct)
    {
        if (_sock is null || _radioEndpoint is null)
            throw new InvalidOperationException("Call ConnectAsync first.");
        if (_rxTask is not null)
            throw new InvalidOperationException("Already started.");

        _sampleRateKhz = sampleRateKhz;

        // macOS-only: pre-prime the kernel ARP table for the radio's IP
        // before we fire off any real packets. The Brick2 firmware answers
        // ARP requests incorrectly (DL1BZ in Zeus issue #171: deskhpsdr hit
        // the same problem and shipped this workaround at
        // src/new_protocol.c:453-474). Without an entry the first SendTo
        // races the ARP resolution and the radio's streams never start.
        // Linux/Windows hosts haven't shown the same symptom; gate strictly
        // on macOS so we don't introduce side-effects on platforms where the
        // OS handles ARP correctly out of the box.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            PrimeMacOSUdpRoute(_radioEndpoint!.Address, _log);
        }

        // Startup sequence matches Thetis SendStart() and Priapus/NextGenSDR:
        // CmdGeneral → CmdRx → CmdTx → CmdHighPriority(run=1). Skipping CmdTx
        // leaves the G2 MkII in a half-configured state where its BPF board
        // latches a random band instead of honouring CmdHighPriority filter
        // bits on subsequent tunes.
        SendCmdGeneral();
        Thread.Sleep(50);
        SendCmdRx();
        Thread.Sleep(50);
        SendCmdTx();
        Thread.Sleep(50);
        SendCmdHighPriority(run: true);

        _rxCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _rxTask = Task.Run(() => RxLoop(_rxCts.Token));
        _keepaliveTask = Task.Run(() => KeepaliveLoop(_rxCts.Token));
        // Paced TX IQ sender — drains the queue FlushTxIqLocked fills and
        // holds the radio's DUC FIFO at a steady level.
        _txIqSenderTask = Task.Run(() => TxIqSenderLoop(_rxCts.Token));
        _log.LogInformation("p2.start rate={Rate}kHz freq={Freq}Hz", _sampleRateKhz, _rxFreqHz);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_rxTask is null) return;

        SendCmdHighPriority(run: false);
        _rxCts?.Cancel();
        try { await _rxTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _rxTask = null;
        if (_keepaliveTask is not null)
        {
            try { await _keepaliveTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _keepaliveTask = null;
        }
        _txIqQueue.Writer.TryComplete();
        if (_txIqSenderTask is not null)
        {
            try { await _txIqSenderTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _txIqSenderTask = null;
        }
        _rxCts?.Dispose();
        _rxCts = null;
        _iqFrames.Writer.TryComplete();
        _log.LogInformation("p2.stop totalFrames={Total} dropped={Drop}", _totalFrames, _droppedFrames);
    }

    public void SetVfoAHz(long hz)
    {
        _rxFreqHz = (uint)Math.Clamp(hz, 0L, uint.MaxValue);
        var running = _rxTask is not null;
        _log.LogInformation("p2.tune hz={Hz} running={Running} hpSeq={Seq}",
            _rxFreqHz, running, _seqCmdHp);
        if (running) SendCmdHighPriority(run: true);
    }

    public void SetSampleRateKhz(int rateKhz)
    {
        _sampleRateKhz = rateKhz;
        if (_rxTask is not null)
        {
            SendCmdRx();
        }
    }

    /// <summary>
    /// Set the connected board kind. Should be called once at connect time,
    /// before <see cref="StartAsync"/>. Plumbed from
    /// <c>DspPipelineService.ConnectP2Async</c> so RX-decode quirks scoped to
    /// specific board families can be applied without polluting the hot path
    /// with an out-of-band lookup.
    /// </summary>
    public void SetBoardKind(HpsdrBoardKind kind)
    {
        _boardKind = kind;
    }

    /// <summary>
    /// Per-board, per-sample-rate gain correction applied on top of the
    /// int24→[-1,+1] normalisation in <c>HandleDdcPacket</c>.
    ///
    /// Hermes-class radios on Protocol 2 (notably the Brick2 SDR, which is
    /// Hermes-fork firmware on Hermes-Lite-style hardware) deliver RX IQ at
    /// 48 kHz with ~+29 dB more amplitude than at higher sample rates,
    /// because the on-board CIC/decimator gain isn't compensated by the
    /// firmware at the lowest decimation factor. Without this scaler the
    /// S-meter and panadapter clip / saturate at 48 kHz on Brick2 — the
    /// symptom that left linoobs's waterfall blank in issue #171.
    ///
    /// Reference: deskhpsdr <c>src/new_protocol.c:2516-2530</c> — the
    /// scaler is gated on <c>NEW_DEVICE_HERMES</c> (wire byte 0x01) only,
    /// NOT on <c>NEW_DEVICE_HERMES2</c> (wire byte 0x02). HermesII firmware
    /// (ANAN-10E / ANAN-100B) does not exhibit the +29 dB 48 kHz lift, so
    /// the scaler must stay gated on <see cref="HpsdrBoardKind.Hermes"/>
    /// alone — widening it to HermesII would knock 29 dB off legitimate
    /// ANAN-10E / ANAN-100B RX. Constant <c>0.0354813389</c> is
    /// <c>10^(-29/20)</c>.
    ///
    /// All other (board, rate) combinations return 1.0 — vanilla HL2, ANAN
    /// 10E / 100B, ANAN G2 / 7000DLE / 8000DLE / Orion, etc. are unaffected.
    /// </summary>
    public static double IqGainCorrection(HpsdrBoardKind board, int sampleRateKhz)
    {
        if (board == HpsdrBoardKind.Hermes && sampleRateKhz == 48)
        {
            return 0.0354813389; // -29 dB
        }
        return 1.0;
    }

    public void SetNumAdc(byte numAdc)
    {
        _numAdc = numAdc;
    }

    public void SetPreamp(bool on)
    {
        _preampOn = on;
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    public void SetAttenuator(int db)
    {
        _rxStepAttnDb = (byte)Math.Clamp(db, 0, 31);
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    public void SetDriveByte(byte value)
    {
        _driveByte = value;
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    public void SetOcMasks(byte txMask, byte rxMask)
    {
        _ocTxMask = (byte)(txMask & 0x7F);
        _ocRxMask = (byte)(rxMask & 0x7F);
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    public void SetPaEnabled(bool enabled)
    {
        _paEnabled = enabled;
        if (_rxTask is not null) SendCmdGeneral();
    }

    public void SetMox(bool on)
    {
        _moxOn = on;
        if (!on) ResetTxIq();
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    public void SetTune(bool on)
    {
        _tuneActive = on;
        if (!on) ResetTxIq();
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    /// <summary>
    /// Arm or disarm PureSignal feedback streaming. When on:
    ///   - <c>SendCmdRx</c> enables DDC0 alongside the user-visible DDC2 and
    ///     synchronises DDC1→DDC0 (byte 1363 = 0x02) so the radio sends
    ///     paired DDC0/DDC1 IQ on port 1035.
    ///   - <c>SendCmdHighPriority</c> sets <c>ALEX_PS_BIT (0x00040000)</c>
    ///     in alex0/alex1 and, during xmit, mirrors DDC0+DDC1 phase words
    ///     to the TX DUC frequency.
    ///   - The packet decoder switches to the paired format (6B DDC0 + 6B
    ///     DDC1 per sample, repeating).
    /// When off the radio reverts to the standard non-PS RX layout and any
    /// in-flight paired packets are discarded by the decoder.
    /// </summary>
    public void SetPsFeedbackEnabled(bool on)
    {
        if (_psFeedbackEnabled == on) return;
        _psFeedbackEnabled = on;
        // Reset accumulator so we don't mix old samples with new on a re-arm.
        _psBlockFill = 0;
        if (_rxTask is not null)
        {
            // Re-emit RX-spec (DDC enables / sync bit) and HighPriority (PS
            // bit / DDC0/1 phase) so the radio honours the new state on the
            // very next sample window.
            SendCmdRx();
            SendCmdHighPriority(run: true);
        }
    }

    /// <summary>
    /// Choose between Internal feedback coupler and External (Bypass)
    /// feedback antenna. Drives <c>ALEX_RX_ANTENNA_BYPASS</c> in alex0
    /// during xmit + PS armed (pihpsdr new_protocol.c:1284-1296). No
    /// effect on RxSpecific / TxSpecific buffers.
    /// </summary>
    public void SetPsFeedbackSource(bool external)
    {
        if (_psFeedbackExternal == external) return;
        _psFeedbackExternal = external;
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    public ChannelReader<PsFeedbackFrame> PsFeedbackFrames => _psFeedbackFrames.Reader;

    /// <summary>
    /// Push a block of interleaved float IQ (−1..+1) into the TX-DUC stream.
    /// The block is buffered; when the accumulator reaches 240 complex
    /// samples a 1444-byte P2 packet is sent to port 1029 (pihpsdr
    /// new_protocol.c:1909-1942 — new_protocol_txiq_thread). Caller owns the
    /// input buffer; we copy. Samples are scaled by pihpsdr's ggain=0.896
    /// (transmitter.c:1761) to compensate for the end-of-chain FIR gain and
    /// then quantized to signed 24-bit BE.
    /// </summary>
    public void SendTxIq(ReadOnlySpan<float> iqInterleaved)
    {
        if (_sock is null || _rxTask is null) return;
        if ((iqInterleaved.Length & 1) != 0)
            throw new ArgumentException("interleaved length must be even (I,Q pairs)", nameof(iqInterleaved));

        lock (_txIqGate)
        {
            int idx = 0;
            while (idx < iqInterleaved.Length)
            {
                int capacity = _txIqScratch.Length - _txIqScratchCount;
                int copyLen = Math.Min(capacity, iqInterleaved.Length - idx);
                iqInterleaved.Slice(idx, copyLen).CopyTo(_txIqScratch.AsSpan(_txIqScratchCount));
                _txIqScratchCount += copyLen;
                idx += copyLen;
                if (_txIqScratchCount >= _txIqScratch.Length)
                {
                    FlushTxIqLocked();
                }
            }
        }
    }

    private void ResetTxIq()
    {
        lock (_txIqGate) _txIqScratchCount = 0;
        // Drain any queued-but-unsent packets so a fresh key-down starts
        // from an empty FIFO model and the radio isn't playing 10 ms of
        // the previous transmission's IQ when PTT re-engages.
        while (_txIqQueue.Reader.TryRead(out _)) { }
    }

    private void FlushTxIqLocked()
    {
        // The 0.896 trim pihpsdr applies in transmitter.c:1707-1712 is
        // CW-path-only — it compensates for the CW shaped pulse skipping
        // WDSP TXA's end-of-chain FIR. The regular TXA path (which is what
        // Zeus feeds — mic and TUN both go through TXA) takes the samples
        // unscaled (transmitter.c:1739-1754), so no pre-quantize trim here.
        var p = new byte[BufLen];
        WriteBeU32(p, 0, _seqCmdTxIq++);
        for (int i = 0; i < TxIqSamplesPerPacket; i++)
        {
            float fi = _txIqScratch[i * 2];
            float fq = _txIqScratch[i * 2 + 1];
            int vi = Int24Clamp(fi);
            int vq = Int24Clamp(fq);
            int off = 4 + i * 6;
            p[off + 0] = (byte)((vi >> 16) & 0xff);
            p[off + 1] = (byte)((vi >> 8) & 0xff);
            p[off + 2] = (byte)(vi & 0xff);
            p[off + 3] = (byte)((vq >> 16) & 0xff);
            p[off + 4] = (byte)((vq >> 8) & 0xff);
            p[off + 5] = (byte)(vq & 0xff);
        }
        // Enqueue instead of sending inline. The sender task drains this at
        // the DAC rate so the radio's TX FIFO stays level — sending the full
        // 8-packet burst from one WDSP cycle straight to the wire overfills
        // then starves the FIFO, showing up as a pulsed carrier.
        _txIqQueue.Writer.TryWrite(p);
        _txIqScratchCount = 0;
    }

    private async Task TxIqSenderLoop(CancellationToken ct)
    {
        // Port of pihpsdr's new_protocol_txiq_thread (new_protocol.c:1909-1997).
        // Maintains a software model of the radio's TX FIFO fill level: each
        // packet adds 240 samples, wall-clock elapses drain at 192 kHz. When
        // the modeled level exceeds the target (1250 samples ≈ 6.5 ms) we hold
        // for 1 ms before sending the next packet. Below the target we send
        // as fast as the queue delivers — that's the startup ramp that fills
        // the radio's FIFO to its steady-state depth.
        var reader = _txIqQueue.Reader;
        var ep = new IPEndPoint(_radioEndpoint!.Address, 1029);
        double fifoSamples = 0.0;
        long lastTicks = Stopwatch.GetTimestamp();
        double ticksPerSecond = Stopwatch.Frequency;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[] packet;
                try { packet = await reader.ReadAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (ChannelClosedException) { break; }

                // Drain by wall-clock since the previous send.
                long now = Stopwatch.GetTimestamp();
                double elapsedSec = (now - lastTicks) / ticksPerSecond;
                fifoSamples -= elapsedSec * TxDacSampleRate;
                if (fifoSamples < 0.0) fifoSamples = 0.0;
                lastTicks = now;

                // If the radio's FIFO would overflow, wait a tick before the
                // next send. The 1 ms delay is coarse but well within the
                // FIFO's 6.5 ms target headroom, so no underrun risk.
                if (fifoSamples > TxFifoTargetSamples)
                {
                    try { await Task.Delay(1, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    now = Stopwatch.GetTimestamp();
                    elapsedSec = (now - lastTicks) / ticksPerSecond;
                    fifoSamples -= elapsedSec * TxDacSampleRate;
                    if (fifoSamples < 0.0) fifoSamples = 0.0;
                    lastTicks = now;
                }

                fifoSamples += TxIqSamplesPerPacket;
                try { _sock!.SendTo(packet, ep); }
                catch (ObjectDisposedException) { break; }
                catch (SocketException ex)
                {
                    _log.LogWarning(ex, "p2.txiq send failed");
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "p2.txiq sender exited with error");
        }
    }

    private static int Int24Clamp(float v)
    {
        if (v >  1.0f) v =  1.0f;
        if (v < -1.0f) v = -1.0f;
        // 8_388_607 = 2^23 - 1. Using the ceiling (8_388_608) would map +1.0
        // to a value that, when sign-extended on the far side, wraps to
        // −8_388_608 — a full-scale negative spike on the loudest sample.
        return (int)MathF.Round(v * 8_388_607.0f);
    }

    private void SendCmdGeneral()
    {
        var p = new byte[60];
        WriteBeU32(p, 0, _seqCmdGeneral++);
        p[4] = 0x00;
        WriteBeU16(p, 5, 1025);
        WriteBeU16(p, 7, 1026);
        WriteBeU16(p, 9, 1027);
        WriteBeU16(p, 11, 1025);
        WriteBeU16(p, 13, 1028);
        WriteBeU16(p, 15, 1029);
        WriteBeU16(p, 17, 1035);
        WriteBeU16(p, 19, 1026);
        WriteBeU16(p, 21, 1027);
        p[23] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(24), 512);
        p[26] = 16;
        p[27] = 0;
        p[28] = 32;
        // Matches pihpsdr new_protocol_general for ORION2/SATURN hardware:
        // [37] bit 3 = phase-word mode (radio reads phase increments at the
        // DDC-frequency offsets, not raw Hz), [38] = hardware-timer enable,
        // [58] = PA enable, [59] = Alex0|Alex1 enable (0x03 is required on
        // MkII for the BPF board to honour the alex bits further down).
        p[37] = 0x08;
        p[38] = 0x01;
        // [58] bit 0 = PA enable (piHPSDR `new_protocol.c:658-677`; Thetis
        // `network.c` SendGeneral). The old hard-coded 0x01 became the
        // default; PaSettingsStore now owns the bit.
        p[58] = (byte)(_paEnabled ? 0x01 : 0x00);
        p[59] = 0x03;
        _sock!.SendTo(p, new IPEndPoint(_radioEndpoint!.Address, 1024));
    }

    // macOS IP_BOUND_IF socket-option name. Defined in `netinet/in.h` as 25;
    // not exposed as a named constant in .NET so we declare it here.
    // Behavior: forces traffic on this socket to egress a specific interface
    // by ifindex, bypassing the routing table's interface selection.
    private const int IP_BOUND_IF = 25;

    /// <summary>
    /// Best-effort macOS ARP-priming workaround. Sends a single zero-byte
    /// UDP datagram to the radio's port 1024 bound (via IP_BOUND_IF) to the
    /// interface the kernel chose for the radio's IP. The kernel resolves
    /// ARP as a side effect so subsequent real packets ship promptly instead
    /// of racing the first SendTo. Mirrors deskhpsdr's <c>p2_prime_route()</c>
    /// at <c>src/new_protocol.c:453-474</c>.
    ///
    /// Caller is the only authority on platform gating — this helper assumes
    /// it's being invoked on macOS already. Every failure path is swallowed
    /// and logged; the priming is opportunistic, never load-bearing for the
    /// connect succeeding.
    /// </summary>
    internal static void PrimeMacOSUdpRoute(IPAddress radioIp, ILogger log)
    {
        try
        {
            // Step 1: ask the kernel which local IP it would use to reach
            // the radio. UDP Connect doesn't generate any traffic — it only
            // installs a "default destination" in the socket and resolves
            // the route — so LocalEndPoint after Connect is the local IP
            // the kernel routes through.
            int ifIndex;
            using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                probe.Connect(new IPEndPoint(radioIp, 1024));
                var localIp = (probe.LocalEndPoint as IPEndPoint)?.Address;
                if (localIp is null)
                {
                    log.LogDebug("p2.prime.macos no localIp for radio={Radio}", radioIp);
                    return;
                }
                ifIndex = FindInterfaceIndexForLocalAddress(localIp);
                if (ifIndex <= 0)
                {
                    log.LogDebug("p2.prime.macos no interface for local={Local} radio={Radio}", localIp, radioIp);
                    return;
                }
            }

            // Step 2: open a fresh UDP socket, bind it to that interface
            // index, and send a single byte to port 1024 on the radio. The
            // datagram is intentionally garbage — port 1024 (CmdGeneral
            // from-host) is one the radio's protocol layer ignores on a
            // payload it can't parse, but the link-layer ARP exchange that
            // *gets* it there is the whole point.
            using var prime = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var ifIndexBytes = BitConverter.GetBytes(ifIndex);
            prime.SetRawSocketOption(
                optionLevel: (int)SocketOptionLevel.IP,
                optionName: IP_BOUND_IF,
                optionValue: ifIndexBytes);
            prime.SendTo(new byte[] { 0 }, new IPEndPoint(radioIp, 1024));
            log.LogInformation("p2.prime.macos sent radio={Radio} ifIndex={Ix}", radioIp, ifIndex);
        }
        catch (Exception ex)
        {
            // ARP priming is opportunistic; the regular SendCmdGeneral that
            // follows will resolve ARP eventually. Don't fail the connect
            // over a priming hiccup.
            log.LogDebug(ex, "p2.prime.macos failed radio={Radio}", radioIp);
        }
    }

    /// <summary>
    /// Look up the OS interface index for the NIC that owns
    /// <paramref name="localAddr"/>. Returns 0 if no match — the priming
    /// caller treats that as "skip priming and continue."
    /// </summary>
    internal static int FindInterfaceIndexForLocalAddress(IPAddress localAddr)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var props = nic.GetIPProperties();
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.Equals(localAddr))
                {
                    var ipv4 = props.GetIPv4Properties();
                    return ipv4?.Index ?? 0;
                }
            }
        }
        return 0;
    }

    /// <summary>
    /// Per-board base DDC index for the user-visible RX. OrionMkII/Saturn/G2
    /// family reserves DDC0/DDC1 for PureSignal feedback so the operator's RX
    /// lives at DDC2. Single-ADC Hermes-class radios (Brick2 on wire byte
    /// 0x01; ANAN-10E / ANAN-100B on wire byte 0x02) have no reserved PS
    /// feedback slots — the RX is at DDC0. Default OrionMkII preserves Zeus'
    /// historical P2 wire shape for every existing board.
    /// Reference: deskhpsdr <c>src/new_protocol.c:1692-1698</c> — only
    /// <c>NEW_DEVICE_ANGELIA / ORION / ORION2 / SATURN</c> use the
    /// <c>ddc = 2 + i</c> offset; <c>NEW_DEVICE_HERMES</c> and
    /// <c>NEW_DEVICE_HERMES2</c> both fall through to <c>ddc = i</c>.
    /// </summary>
    public static int RxBaseDdc(HpsdrBoardKind board) => board switch
    {
        HpsdrBoardKind.Hermes    => HermesRxDdc,
        HpsdrBoardKind.HermesII  => HermesRxDdc,
        _                        => G2RxDdc,
    };

    // Static byte composer — pure function over (seq, numAdc, sampleRateKhz,
    // psEnabled, boardKind). Exposed internal so wire-format tests don't
    // need a live socket. SendCmdRx constructs the same bytes and pushes to
    // UDP. boardKind defaults to OrionMkII for backward compat — every
    // pre-Brick2 caller (and every pre-Brick2 test) keeps the DDC2 wire shape.
    internal static byte[] ComposeCmdRxBuffer(
        uint seq,
        byte numAdc,
        ushort sampleRateKhz,
        bool psEnabled,
        HpsdrBoardKind boardKind = HpsdrBoardKind.OrionMkII)
    {
        var p = new byte[BufLen];
        WriteBeU32(p, 0, seq);
        p[4] = numAdc;
        p[5] = 0;
        p[6] = 0;
        int rxDdc = RxBaseDdc(boardKind);
        byte ddcEnable = (byte)(1 << rxDdc);

        if (psEnabled)
        {
            // PS feedback layout is OrionMkII/Saturn-specific (DDC0+DDC1
            // paired with bit 1363 sync). Single-ADC Hermes-class radios
            // (Hermes/0x01, HermesII/0x02) don't have this hardware — leave
            // the PS block alone. psEnabled should never be set true for
            // these boards upstream, but if it ever is we'd rather silently
            // no-op than scribble bytes the radio will reject.
            if (boardKind != HpsdrBoardKind.Hermes &&
                boardKind != HpsdrBoardKind.HermesII)
            {
                ddcEnable |= 0x01;
                p[17] = 0x00;
                WriteBeU16(p, 18, 192);
                p[22] = 24;
                p[23] = numAdc;
                WriteBeU16(p, 24, 192);
                p[28] = 24;
                p[1363] = 0x02;
            }
        }

        p[7] = ddcEnable;
        int off = 17 + rxDdc * 6;
        p[off + 0] = 0x00;
        WriteBeU16(p, off + 1, sampleRateKhz);
        p[off + 5] = 24;
        return p;
    }

    private void SendCmdRx()
    {
        // Mirrors pihpsdr new_protocol_receive_specific for the MkII:
        //   n_adc = G2 has two physical ADCs; DDC2 enabled by bit 2 in the
        //   enable mask; the DDC config block sits at 17 + 2*6 = 29. DDC0/1
        //   stay disabled by default — those slots are reserved by the radio
        //   for the PureSignal / Diversity hardware pair. When PS is armed,
        //   ComposeCmdRxBuffer also enables DDC0, configures DDC0/1 at
        //   192 kHz / 24-bit, and sets byte 1363 = 0x02 to sync DDC1→DDC0
        //   (pihpsdr new_protocol.c:1611-1630).
        var p = ComposeCmdRxBuffer(_seqCmdRx++, _numAdc, (ushort)_sampleRateKhz, _psFeedbackEnabled, _boardKind);
        _sock!.SendTo(p, new IPEndPoint(_radioEndpoint!.Address, 1025));
    }

    private void SendCmdTx()
    {
        var p = ComposeCmdTxBuffer(_seqCmdTx++, (ushort)_sampleRateKhz, _txStepAttnDb, _paEnabled, _psFeedbackEnabled);
        _sock!.SendTo(p, new IPEndPoint(_radioEndpoint!.Address, 1026));
    }

    // Test-seamed Compose for the CmdTx (TxSpecific) packet. Layout per
    // pihpsdr new_protocol.c new_protocol_tx_specific (1502-1594) and
    // saturnmain.c::saturn_handle_duc_specific:
    //   bytes 0..3   : sequence (BE)
    //   byte  4      : num_dac (1 on G2)
    //   bytes 14..15 : DAC sample rate kHz (BE) — Zeus-only; Saturn ignores
    //   byte  57     : reserved on Saturn (FPGA does not read)
    //   byte  58     : ADC1 TX step attenuator (TX-DAC reference loopback)
    //   byte  59     : ADC0 TX step attenuator (PA-coupler feedback)
    //
    // pihpsdr's PA-protection / PS asymmetry (new_protocol.c:1540-1547):
    //   if (xmit && pa_enabled) { p[58]=31; p[59]=31; }   // protect both ADCs
    //   if (puresignal)         { p[59]=tx->attenuation; } // ONLY byte 59
    //
    // Byte 58 is NEVER overridden by PS — the TX-DAC reference ADC needs
    // its own protection independent of where AutoAttenuate parks the
    // PA-feedback ADC. If we let PS write to byte 58 too, the first
    // attenuator step drops the loopback reference in lockstep with the
    // feedback, leaving the gain ratio uncorrected and starving calcc.
    //
    // When PS is OFF we keep the historical Zeus shape (value across
    // 57/58/59) — operator's normal voice TX has been validated working
    // with that wire form on G2 MkII, so we don't ship a wire change
    // beyond the PS-armed window in this patch.
    internal static byte[] ComposeCmdTxBuffer(uint seq, ushort sampleRateKhz, byte txStepAttnDb, bool paEnabled, bool psEnabled)
    {
        var p = new byte[60];
        WriteBeU32(p, 0, seq);
        p[4] = 1;                 // num_dac
        WriteBeU16(p, 14, sampleRateKhz);

        if (psEnabled)
        {
            // PS-armed canonical pihpsdr shape: byte 58 holds PA-protection
            // for the TX-DAC reference; byte 59 takes the dynamic step-att
            // so calcc can read the post-PA envelope.
            p[57] = 0;
            p[58] = paEnabled ? (byte)31 : (byte)0;
            p[59] = txStepAttnDb;
        }
        else
        {
            // Historical Zeus shape — preserved so normal voice TX wire
            // form is unchanged. Revisit when bringing the full pihpsdr
            // PA-protection invariant to non-PS TX.
            p[57] = txStepAttnDb;
            p[58] = txStepAttnDb;
            p[59] = txStepAttnDb;
        }
        return p;
    }

    /// <summary>
    /// Set the TX step attenuator (0..31 dB) and re-emit the CmdTx packet
    /// so the radio honours the new value on the next transmit cycle. Bytes
    /// 57/58/59 — Thetis network.c:1238-1242. Used by the PS auto-attenuate
    /// loop to ramp feedback level into the 128..181 window when calcc
    /// rejects fits because the loopback is too hot or too quiet.
    /// </summary>
    public void SetTxAttenuationDb(byte db)
    {
        if (db > 31) db = 31;
        if (_txStepAttnDb == db) return;
        _txStepAttnDb = db;
        if (_rxTask is not null) SendCmdTx();
        _log.LogInformation("p2.txAttn db={Db}", db);
    }

    private void SendCmdHighPriority(bool run)
    {
        var p = new byte[BufLen];
        WriteBeU32(p, 0, _seqCmdHp++);
        // Byte 4 bit 0 = run, bit 1 = PTT. Thetis network.c:924-925 and
        // pihpsdr new_protocol.c:746-757 both set bit 1 whenever the radio
        // should key — covers both mic-MOX and TUN. Without this bit the
        // radio stays in RX regardless of drive / tune state.
        p[4] = (byte)((_moxOn || _tuneActive ? 0x02 : 0x00) | (run ? 0x01 : 0x00));

        // Frequency field is a PHASE word (general[37] bit 3 set) — radio
        // reads a 32-bit phase increment, not Hz. pihpsdr computes this as
        //   phase = freq_hz * 2^32 / 122_880_000
        // The G2 MkII puts the user-visible RX0 at DDC slot 2, so the phase
        // goes to bytes 9 + 2*4 = 17..20. Hermes-class (Brick2) puts RX0 at
        // DDC slot 0, so the phase goes to bytes 9..12 (`9 + 0*4`). Bytes
        // not used by the active RX slot stay zero on the non-PS path. TX
        // DUC phase is written to 329..332 regardless of board.
        uint rxPhase = (uint)(_rxFreqHz * HzToPhase);
        int rxDdc = RxBaseDdc(_boardKind);
        WriteBeU32(p, 9 + rxDdc * 4, rxPhase);
        WriteBeU32(p, 329, rxPhase);

        // PureSignal — when armed, DDC0 + DDC1 phase words also need to
        // track the TX frequency during xmit so the feedback DDC samples
        // the actual TX coupler signal. pihpsdr new_protocol.c:827-839.
        if (_psFeedbackEnabled && _moxOn)
        {
            // For now mirror the RX freq onto the TX side — the radio's
            // single-VFO assumption today means TX = RX. Multi-VFO support
            // is a follow-up.
            uint txPhase = rxPhase;
            WriteBeU32(p, 9, txPhase);     // DDC0 = TX freq
            WriteBeU32(p, 13, txPhase);    // DDC1 = TX freq
        }

        // Drive level (0..255) at byte 345. Set by RadioService after applying
        // per-band PA gain calibration. Honored by the radio only while run=1
        // and TX is keyed elsewhere (byte 4 bit 1). piHPSDR `new_protocol.c:860`.
        p[345] = _driveByte;

        // OC outputs (7-bit mask) shifted left by 1 into byte 1401. The TX
        // mask applies whenever MOX is on (whether keyed by the operator or
        // by TUN) and the RX mask applies otherwise. The piHPSDR-style global
        // "OCtune" override was removed in #124 for hardware-safety reasons:
        // a global override layered on top of the per-band OC TX mask could
        // hand an external amp a confused band-select state during a steady
        // tune carrier and damage the finals. Thetis behaves this way too.
        byte ocBits = (_moxOn || _tuneActive) ? _ocTxMask : _ocRxMask;
        p[1401] = (byte)((ocBits & 0x7F) << 1);

        // Mercury attenuator byte: bit 0 = RX0 preamp, bit 1 = RX1 preamp
        // (Thetis network.c:1037).
        p[1403] = (byte)(_preampOn ? 0x01 : 0x00);

        // ADC0 step attenuator (0-31 dB). Thetis network.c:1057.
        p[1443] = _rxStepAttnDb;

        // Alex words. Bit positions and BPF selections per pihpsdr's alex.h +
        // new_protocol.c (function new_protocol_high_priority, device cases
        // NEW_DEVICE_ORION2 / NEW_DEVICE_SATURN). Offsets: Alex0 at 1432..1435,
        // Alex1 at 1428..1431. During TX both words need ALEX_TX_RELAY
        // (pihpsdr new_protocol.c:989-992) so the T/R relay on the LPF board
        // flips to the TX path; without it the TX signal reaches the antenna
        // through the RX filters and DAC images radiate as out-of-band
        // harmonics. Alex1 additionally gets RX_GNDonTX to short the RX input
        // while keyed, protecting the ADC.
        bool xmit = _moxOn || _tuneActive;
        uint alexCommon = ComputeAlexWord(_rxFreqHz, _rxFreqHz, txAnt: 1);
        uint alex0 = alexCommon | (xmit ? ALEX_TX_RELAY : 0u);
        uint alex1 = alexCommon | (xmit ? ALEX_TX_RELAY | ALEX1_ANAN7000_RX_GNDonTX : 0u);
        // ALEX_PS_BIT (0x00040000): pihpsdr new_protocol.c:994-998 ORs this
        // into alex0 (during xmit) and alex1 (always-on while PS armed). The
        // BPF board uses it to swap to the feedback-coupler tap on the TX
        // path so DDC0/DDC1 see the post-PA signal.
        if (_psFeedbackEnabled)
        {
            alex1 |= AlexPsBit;
            if (xmit) alex0 |= AlexPsBit;
        }
        // External (Bypass) feedback antenna — pihpsdr new_protocol.c:1284-
        // 1296 ORs ALEX_RX_ANTENNA_BYPASS into alex0 only during xmit when
        // PS is armed and the operator selected the external path. Internal
        // coupler leaves this bit clear.
        if (_psFeedbackEnabled && _psFeedbackExternal && xmit)
        {
            alex0 |= AlexRxAntennaBypass;
        }
        WriteBeU32(p, 1428, alex1);
        WriteBeU32(p, 1432, alex0);
        _sock!.SendTo(p, new IPEndPoint(_radioEndpoint!.Address, 1027));
    }

    // ANAN-7000 / Orion-II / Saturn (G2 MkII) BPF board constants. Copied
    // verbatim from pihpsdr's alex.h — these are the RX BPF selections the
    // MkII's filter board expects. The older ALEX_*_HPF constants used for
    // ANAN-100 / classic Alex do NOT work on MkII — the filter board
    // silently selects "nothing" and all RF is cut off at the ADC.
    private const uint ALEX_ANAN7000_RX_BYPASS_BPF = 0x00001000;
    private const uint ALEX_ANAN7000_RX_160_BPF    = 0x00000040;
    private const uint ALEX_ANAN7000_RX_80_60_BPF  = 0x00000020;
    private const uint ALEX_ANAN7000_RX_40_30_BPF  = 0x00000010;
    private const uint ALEX_ANAN7000_RX_20_15_BPF  = 0x00000002;
    private const uint ALEX_ANAN7000_RX_12_10_BPF  = 0x00000004;
    private const uint ALEX_ANAN7000_RX_6_PRE_BPF  = 0x00000008;

    // TX LPF constants (used during TX; harmless during RX but worth setting
    // correctly so the BPF board stays in a sane latched state if the radio
    // momentarily T/Rs).
    private const uint ALEX_160_LPF        = 0x00800000;
    private const uint ALEX_80_LPF         = 0x00400000;
    private const uint ALEX_60_40_LPF      = 0x00200000;
    private const uint ALEX_30_20_LPF      = 0x00100000;
    private const uint ALEX_17_15_LPF      = 0x80000000;
    private const uint ALEX_12_10_LPF      = 0x40000000;
    private const uint ALEX_6_BYPASS_LPF   = 0x20000000;

    // TX antenna select.
    private const uint ALEX_TX_ANTENNA_1   = 0x01000000;
    private const uint ALEX_TX_ANTENNA_2   = 0x02000000;
    private const uint ALEX_TX_ANTENNA_3   = 0x04000000;

    // Flips the T/R relay on the LPF board so the TX path reaches the antenna
    // through the selected TX LPF instead of through the RX BPF path. OR'd
    // into both alex0 and alex1 during TX (pihpsdr new_protocol.c:989-992).
    private const uint ALEX_TX_RELAY       = 0x08000000;
    // PureSignal feedback-coupler enable. OR'd into alex1 always when PS is
    // armed and into alex0 during xmit (pihpsdr new_protocol.c:994-998).
    internal const uint AlexPsBit          = 0x00040000;
    // PS External (Bypass) antenna select — pihpsdr new_protocol.c:1284-1296
    // ORs ALEX_RX_ANTENNA_BYPASS into alex0 during xmit + PS armed when the
    // operator picks the external feedback path. Internal coupler leaves
    // this bit clear.
    internal const uint AlexRxAntennaBypass = 0x00000800;
    // Alex1-only: grounds the RX input while keyed so the hot TX field doesn't
    // back-feed into the Mercury ADC (pihpsdr alex.h ANAN7000_RX_GNDonTX).
    private const uint ALEX1_ANAN7000_RX_GNDonTX = 0x00000100;

    /// <summary>
    /// Compose the alex0 word the way <see cref="SendCmdHighPriority"/>
    /// does, exposed internal so wire-format tests can assert the
    /// PureSignal-related bits without standing up a socket. Mirrors the
    /// in-line logic at SendCmdHighPriority &gt; alex0 calculation.
    /// </summary>
    internal static uint ComposeAlex0ForTest(
        uint rxFreqHz,
        bool moxOn,
        bool psEnabled,
        bool psExternal)
    {
        uint alexCommon = ComputeAlexWord(rxFreqHz, rxFreqHz, txAnt: 1);
        uint alex0 = alexCommon | (moxOn ? ALEX_TX_RELAY : 0u);
        if (psEnabled && moxOn) alex0 |= AlexPsBit;
        if (psEnabled && psExternal && moxOn) alex0 |= AlexRxAntennaBypass;
        return alex0;
    }

    internal static uint ComputeAlexWord(uint rxFreqHz, uint txFreqHz, int txAnt)
    {
        uint word = 0;
        word |= BpfBitsAnan7000(rxFreqHz);
        word |= LpfBitsAnan7000(txFreqHz);
        word |= txAnt switch
        {
            1 => ALEX_TX_ANTENNA_1,
            2 => ALEX_TX_ANTENNA_2,
            3 => ALEX_TX_ANTENNA_3,
            _ => ALEX_TX_ANTENNA_1,
        };
        return word;
    }

    // RX BPF band splits lifted from pihpsdr new_protocol.c
    // (function new_protocol_high_priority, ADC0 BPFfreq selection).
    internal static uint BpfBitsAnan7000(uint freqHz)
    {
        if (freqHz <  1_500_000u) return ALEX_ANAN7000_RX_BYPASS_BPF;
        if (freqHz <  2_100_000u) return ALEX_ANAN7000_RX_160_BPF;
        if (freqHz <  5_500_000u) return ALEX_ANAN7000_RX_80_60_BPF;
        if (freqHz < 11_000_000u) return ALEX_ANAN7000_RX_40_30_BPF;
        if (freqHz < 22_000_000u) return ALEX_ANAN7000_RX_20_15_BPF;
        if (freqHz < 35_000_000u) return ALEX_ANAN7000_RX_12_10_BPF;
        return ALEX_ANAN7000_RX_6_PRE_BPF;
    }

    // TX LPF band splits. Thresholds match pihpsdr new_protocol.c:1204-1218
    // exactly (strict > rather than >= so the band edges route the way the
    // LPF board expects; off-by-one on a threshold lets the wrong filter
    // pass harmonics at e.g. 24.9 MHz or 16.4 MHz).
    internal static uint LpfBitsAnan7000(uint freqHz)
    {
        if (freqHz > 35_600_000u) return ALEX_6_BYPASS_LPF;
        if (freqHz > 24_000_000u) return ALEX_12_10_LPF;
        if (freqHz > 16_500_000u) return ALEX_17_15_LPF;
        if (freqHz >  8_000_000u) return ALEX_30_20_LPF;
        if (freqHz >  5_000_000u) return ALEX_60_40_LPF;
        if (freqHz >  2_500_000u) return ALEX_80_LPF;
        return ALEX_160_LPF;
    }

    // Mirrors pihpsdr's new_protocol_timer_thread:
    //   HighPriority every 100 ms, RX/TX specific every 200 ms, General
    //   every 800 ms. The G2 MkII expects this cadence once the hardware
    //   watchdog is enabled in CmdGeneral[38] — without it the radio
    //   treats the stream as abandoned and freezes IQ within ~1 s.
    private async Task KeepaliveLoop(CancellationToken ct)
    {
        int cycle = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                cycle = (cycle % 8) + 1;
                SendCmdHighPriority(run: true);
                switch (cycle)
                {
                    case 2: case 4: case 6:
                        SendCmdRx();
                        break;
                    case 1: case 3: case 5: case 7:
                        SendCmdTx();
                        break;
                    case 8:
                        SendCmdRx();
                        SendCmdGeneral();
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "p2.keepalive exited with error");
        }
    }

    private void RxLoop(CancellationToken ct)
    {
        var buf = new byte[2048];
        var sock = _sock!;
        sock.ReceiveTimeout = 500;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n;
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    n = sock.ReceiveFrom(buf, ref from);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    continue;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted
                                              || ex.SocketErrorCode == SocketError.OperationAborted)
                {
                    break;
                }

                var srcPort = ((IPEndPoint)from).Port;
                if (srcPort >= 1035 && srcPort <= 1041 && n == BufLen)
                {
                    int ddcIndex = srcPort - 1035;
                    if (_psFeedbackEnabled && ddcIndex == 0)
                    {
                        // PS-armed paired-DDC packet: 6B DDC0 (TX-mod-IQ) + 6B
                        // DDC1 (feedback) interleaved per sample. pihpsdr
                        // process_ps_iq_data, new_protocol.c:2463-2510.
                        HandlePsPairedPacket(buf);
                    }
                    else
                    {
                        HandleDdcPacket(buf, ddcIndex);
                    }
                }
                else if (srcPort == 1025 && n >= HiPriStatusMinBytes)
                {
                    // Hi-priority status (issue #174). Thetis decodes this at
                    // network.c:683-756 (case portIdx == 0). The fields we
                    // surface drive the operator's TX power meter — without
                    // this, the bar reads zero on every P2-connected radio
                    // because TxMetersService had no telemetry feed.
                    HandleHiPriStatusPacket(buf);
                }
                // mic samples (1026), wideband ADC0..7 (1027..1034)
                // intentionally ignored for now — separate features.
            }
        }
        finally
        {
            _iqFrames.Writer.TryComplete();
        }
    }

    private void HandleDdcPacket(byte[] buf, int ddcIndex)
    {
        var seq = BinaryPrimitives.ReadUInt32BigEndian(buf);
        if (ddcIndex == 0)
        {
            if (_haveFirstDdc0 && seq != _lastDdc0Seq + 1)
            {
                Interlocked.Increment(ref _droppedFrames);
            }
            _haveFirstDdc0 = true;
            _lastDdc0Seq = seq;
        }

        // 238 complex samples: I (int24 BE) + Q (int24 BE), starting at byte 16.
        // We own the array for the lifetime of the IqFrame the downstream
        // pump consumes; there's no back-channel to Return it to a pool, so
        // plain GC allocation is both simpler and correct.
        const int samplesPerPacket = DiscoverySamplesPerPacket;
        var samples = new double[samplesPerPacket * 2];
        // 1/2^23 normalises int24 to [-1,+1]; the per-board correction is 1.0
        // for everything except Hermes@48 kHz (Brick2 quirk — see IqGainCorrection).
        double scale = (1.0 / 8388608.0) * IqGainCorrection(_boardKind, _sampleRateKhz);
        for (int i = 0; i < samplesPerPacket; i++)
        {
            int off = 16 + i * 6;
            int iRaw = (buf[off] << 16) | (buf[off + 1] << 8) | buf[off + 2];
            if ((iRaw & 0x800000) != 0) iRaw |= unchecked((int)0xFF000000);
            int qRaw = (buf[off + 3] << 16) | (buf[off + 4] << 8) | buf[off + 5];
            if ((qRaw & 0x800000) != 0) qRaw |= unchecked((int)0xFF000000);
            samples[i * 2] = iRaw * scale;
            samples[i * 2 + 1] = qRaw * scale;
        }

        var frame = new IqFrame(
            InterleavedSamples: new ReadOnlyMemory<double>(samples, 0, samplesPerPacket * 2),
            SampleCount: samplesPerPacket,
            SampleRateHz: _sampleRateKhz * 1000,
            Sequence: seq,
            TimestampNs: _stopwatch.ElapsedTicks * 1_000_000_000L / Stopwatch.Frequency);

        Interlocked.Increment(ref _totalFrames);
        // iter5: prefer the synchronous sink when attached — bypasses the
        // Channel<T> hop that costs a TP wake-up per frame on the consumer.
        var sinkSnap = Volatile.Read(ref _rxSink);
        if (sinkSnap != null)
        {
            try { sinkSnap.OnIqFrame(in frame); }
            catch (Exception ex) { _log.LogError(ex, "p2.rx.sink_threw kind=iq"); }
        }
        else
        {
            _iqFrames.Writer.TryWrite(frame);
        }
    }

    // 1 Hz log throttle for hi-pri status. The first packet logs immediately
    // so a fresh connect produces a clear "we're seeing the radio's
    // telemetry" line; subsequent packets log at most once per second. Read
    // and written only on the RX thread, so plain ticks are fine.
    private long _lastHiPriLogTicks;

    /// <summary>
    /// Decode the Protocol-2 hi-priority status packet (UDP 1025). Field
    /// offsets mirror Thetis <c>network.c:689-716</c>:
    /// <list type="bullet">
    ///   <item>byte 0 — bit 0 PTT, bit 4 PLL locked</item>
    ///   <item>bytes 2..3 — exciter power ADC (BE u16)</item>
    ///   <item>bytes 10..11 — PA forward power ADC (BE u16)</item>
    ///   <item>bytes 18..19 — PA reverse power ADC (BE u16)</item>
    /// </list>
    /// Pure function — exposed for unit tests against captured radio
    /// payloads. Caller must guarantee the buffer covers the 0..19 range.
    /// </summary>
    public static P2TelemetryReading DecodeHiPriStatus(ReadOnlySpan<byte> buf)
    {
        ushort exciter = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(2, 2));
        ushort fwd = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(10, 2));
        ushort rev = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(18, 2));
        bool ptt = (buf[0] & 0x01) != 0;
        bool pll = (buf[0] & 0x10) != 0;
        return new P2TelemetryReading(
            FwdAdc: fwd,
            RevAdc: rev,
            ExciterAdc: exciter,
            PttIn: ptt,
            PllLocked: pll);
    }

    /// <summary>
    /// RX-thread handler for the hi-priority status packet. Decodes via
    /// <see cref="DecodeHiPriStatus"/>, throttle-logs at 1 Hz, and dispatches
    /// to <see cref="TelemetryReceived"/> subscribers.
    /// </summary>
    private void HandleHiPriStatusPacket(byte[] buf)
    {
        // Skip the 4-byte BE u32 sequence number that prefixes every P2 UDP
        // packet (Thetis network.c:531 — `memcpy(bufp, readbuf + 4, 56)`).
        // Without this slice the decoder reads the sequence bytes for
        // exciter/fwd/rev — that's the bug behind issue #174's "exciter
        // climbs by 1, FWD/REV stuck at zero" log signature.
        var reading = DecodeHiPriStatus(buf.AsSpan(HiPriSeqHeaderBytes));

        Interlocked.Increment(ref _hiPriPackets);

        // Throttled log so an operator (or a rack-test session for #174) can
        // confirm the path is alive without spamming. Cadence matches P1's
        // `p1.tx.rate` line: one line / second while a stream is active.
        // Promoted to Information so `dotnet run` / journalctl renders it
        // without a debug-level config tweak — the operator's first
        // post-fix sanity check needs to be friction-free.
        long nowTicks = _stopwatch.ElapsedTicks;
        long elapsedMs = (nowTicks - _lastHiPriLogTicks) * 1000 / Stopwatch.Frequency;
        if (_lastHiPriLogTicks == 0 || elapsedMs >= 1000)
        {
            _lastHiPriLogTicks = nowTicks;
            _log.LogInformation(
                "p2.hi_pri.rx pkts={Pkts} fwd={Fwd} rev={Rev} exc={Exc} ptt={Ptt} pll={Pll}",
                Interlocked.Read(ref _hiPriPackets),
                reading.FwdAdc, reading.RevAdc, reading.ExciterAdc,
                reading.PttIn, reading.PllLocked);
        }

        // Subscriber list is captured once so a handler that unsubscribes
        // mid-invocation doesn't NRE. Same pattern P1 uses.
        var handler = TelemetryReceived;
        if (handler is not null)
        {
            try { handler(reading); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "p2.hi_pri.handler threw");
            }
        }
    }

    // PS-armed packet shape on UDP 1035: 16-byte header (4 seq, 8 timestamp,
    // 4 reserved) followed by 119 sample pairs at 12 bytes each (6B DDC0 +
    // 6B DDC1). 16 + 119*12 = 1444 = BufLen. We accumulate into the 1024-
    // sample paired buffers and emit a PsFeedbackFrame per full block.
    //
    // Sample layout per pair (big-endian, signed 24-bit), per pihpsdr
    // new_protocol.c:1615-1616:
    //   off+0..2 : DDC0 I  (PS_RX_FEEDBACK — post-PA coupler — pscc's "rx")
    //   off+3..5 : DDC0 Q
    //   off+6..8 : DDC1 I  (PS_TX_FEEDBACK — TX-DAC loopback — pscc's "tx")
    //   off+9..11: DDC1 Q
    private void HandlePsPairedPacket(byte[] buf)
    {
        var seq = BinaryPrimitives.ReadUInt32BigEndian(buf);
        // Read samplesperframe from the packet header (pihpsdr
        // new_protocol.c:2475). G2 at 192 kHz emits 238 samples/frame
        // = 119 pairs/packet — the prior hardcoded literal happened to
        // match. Defensive bounds check + fallback to 119 on any garbage
        // value keeps the decoder working if the radio reports something
        // unexpected (older firmware, future variants).
        int samplesPerFrame = (buf[14] << 8) | buf[15];
        int samplesPerPacket = samplesPerFrame / 2;
        if (samplesPerPacket <= 0 || samplesPerPacket > 200)
        {
            _log.LogWarning(
                "p2.psPaired bad samplesPerFrame={N}, falling back to 119",
                samplesPerFrame);
            samplesPerPacket = 119;
        }

        for (int i = 0; i < samplesPerPacket; i++)
        {
            int off = 16 + i * 12;
            // DecodePsPairForTest is the canonical mapping (DDC0=rx, DDC1=tx).
            // Reusing it here keeps the test-asserted contract identical to
            // the live decode path so the regression guard is real.
            var (sampleRxI, sampleRxQ, sampleTxI, sampleTxQ) =
                DecodePsPairForTest(new ReadOnlySpan<byte>(buf, off, 12));
            _psRxI[_psBlockFill] = sampleRxI;
            _psRxQ[_psBlockFill] = sampleRxQ;
            _psTxI[_psBlockFill] = sampleTxI;
            _psTxQ[_psBlockFill] = sampleTxQ;

            if (_psBlockFill == 0) _psBlockStartSeq = seq;
            _psBlockFill++;

            if (_psBlockFill >= PsFeedbackBlockSize)
            {
                // Copy out — caller may reuse the buffers immediately.
                var txI = new float[PsFeedbackBlockSize];
                var txQ = new float[PsFeedbackBlockSize];
                var rxI = new float[PsFeedbackBlockSize];
                var rxQ = new float[PsFeedbackBlockSize];
                Array.Copy(_psTxI, txI, PsFeedbackBlockSize);
                Array.Copy(_psTxQ, txQ, PsFeedbackBlockSize);
                Array.Copy(_psRxI, rxI, PsFeedbackBlockSize);
                Array.Copy(_psRxQ, rxQ, PsFeedbackBlockSize);
                var psFrame = new PsFeedbackFrame(txI, txQ, rxI, rxQ, _psBlockStartSeq);
                // iter5: prefer the synchronous sink when attached.
                var psSinkSnap = Volatile.Read(ref _rxSink);
                if (psSinkSnap != null)
                {
                    try { psSinkSnap.OnPsFeedbackFrame(in psFrame); }
                    catch (Exception ex) { _log.LogError(ex, "p2.rx.sink_threw kind=psfb"); }
                }
                else
                {
                    _psFeedbackFrames.Writer.TryWrite(psFrame);
                }
                _psBlockFill = 0;
            }
        }
    }

    private static int SignExtend24(int raw)
    {
        if ((raw & 0x800000) != 0) raw |= unchecked((int)0xFF000000);
        return raw;
    }

    /// <summary>
    /// Test seam — decode a single sample-pair from a PS paired packet and
    /// return the (rxI, rxQ, txI, txQ) destination assignments per the
    /// pihpsdr DDC0=RX_FEEDBACK / DDC1=TX_FEEDBACK contract. Used by tests
    /// to guard against re-introducing the round-1 swap bug.
    /// </summary>
    internal static (float rxI, float rxQ, float txI, float txQ)
        DecodePsPairForTest(ReadOnlySpan<byte> pair)
    {
        if (pair.Length < 12) throw new ArgumentException("pair must be 12 bytes", nameof(pair));
        const float scale = 1f / 8388608f;
        int d0i = SignExtend24((pair[0]  << 16) | (pair[1]  << 8) | pair[2]);
        int d0q = SignExtend24((pair[3]  << 16) | (pair[4]  << 8) | pair[5]);
        int d1i = SignExtend24((pair[6]  << 16) | (pair[7]  << 8) | pair[8]);
        int d1q = SignExtend24((pair[9]  << 16) | (pair[10] << 8) | pair[11]);
        // DDC0 -> rx, DDC1 -> tx.
        return (d0i * scale, d0q * scale, d1i * scale, d1q * scale);
    }

    public void Dispose()
    {
        try { StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        _sock?.Dispose();
        _sock = null;
        _rxCts?.Dispose();
        _rxCts = null;
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        _sock?.Dispose();
        _sock = null;
        _rxCts?.Dispose();
        _rxCts = null;
    }

    private static void WriteBeU16(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)((value >> 8) & 0xff);
        buf[offset + 1] = (byte)(value & 0xff);
    }

    private static void WriteBeU32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)((value >> 24) & 0xff);
        buf[offset + 1] = (byte)((value >> 16) & 0xff);
        buf[offset + 2] = (byte)((value >> 8) & 0xff);
        buf[offset + 3] = (byte)(value & 0xff);
    }
}
