// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.VirtualRadio.Discovery;
using Zeus.VirtualRadio.Observation;
using Zeus.VirtualRadio.Rf;

namespace Zeus.VirtualRadio.P2;

/// <summary>
/// The Protocol-2 virtual-radio engine (Phase-3 + the PureSignal single-ADC
/// time-mux slice for the ANAN-10E / HermesII). Binds the P2 UDP port set
/// (1024 CmdGeneral, 1025 CmdRx, 1026 CmdTx, 1027 CmdHighPriority, 1029 TX-IQ)
/// and a DDC0 send socket (source port 1035), answers P2 discovery as a
/// HermesII (board byte 0x02), decodes the host commands, streams synthetic
/// DDC0 RX-IQ + PTT-gated FWD/REF hi-priority status, and — when the host arms
/// the single-ADC PS time-mux (CmdRx byte 1363 <c>Mux</c> bit) during a keyed
/// burst — flips DDC0 to the coupler/reference interleaved feedback layout the
/// client decodes in <c>HandlePsPairedPacket</c>.
///
/// Each radio→host stream is sent from the matching SOURCE port (the client
/// demuxes inbound by source port — the #1 thing to get wrong), so hi-priority
/// status goes out of the 1025 socket and DDC0 IQ/feedback out of the 1035
/// socket. The host endpoint is observed from inbound command traffic; on
/// loopback Zeus direct-connects, so discovery is dormant there.
/// </summary>
public sealed class Protocol2Engine : IVirtualRadio
{
    private readonly VirtualRadioProfile _profile;
    private readonly ILogger<Protocol2Engine> _logger;
    private readonly HostCommandState _state = new();
    private readonly CommandLog _commandLog = new();

    private readonly P2CmdDecoder _decoder;
    private readonly P2RxDdcEncoder _rxEncoder = new();
    private readonly P2HiPriStatusEncoder _hiPriEncoder = new();
    private readonly SyntheticIqGenerator _iq;
    private readonly RfTelemetryModel _rf;
    private readonly PaDistortionModel _distortion;
    private readonly TxReferenceSource _txRef;

    private readonly object _stateGate = new();
    private volatile IPEndPoint? _hostEndPoint;

    // The PS feedback burst runs DDC0 at 192 kHz (the burst descriptor rate).
    private const double PsBurstRateHz = 192_000.0;

    // Byte-59 protective floor the host MUST seed while the PS time-mux is
    // armed (the DAC feedback hits the only RX ADC during TX). Below this the
    // emulator asserts ADC-overload in the hi-priority status — the safety
    // signal a real G2E/10E would raise on a first key-down with byte 59 = 0.
    internal const byte TxAdcProtectFloorDb = 1;

    // ~5 Hz hi-priority status. A few Hz is enough for the meter to render.
    private static readonly TimeSpan HiPriPeriod = TimeSpan.FromMilliseconds(200);

    private long _currentTunedHz;

    private long _ddc0PacketsSent;
    private long _cmdPacketsReceived;
    private long _psFeedbackPacketsSent;

    // De-dup edge-trigger for CommandDecoded (steady re-sends of the same
    // command should not flood observers).
    private readonly Dictionary<string, string> _lastSummaryByKind = new();

    public Protocol2Engine(
        VirtualRadioProfile profile,
        ILogger<Protocol2Engine>? logger = null,
        bool psDistortion = false)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _logger = logger ?? NullLogger<Protocol2Engine>.Instance;
        _decoder = new P2CmdDecoder(profile.Board);
        _iq = new SyntheticIqGenerator(profile);
        _rf = new RfTelemetryModel(profile);
        _distortion = new PaDistortionModel(psDistortion);
        _txRef = new TxReferenceSource(PsBurstRateHz);
        _currentTunedHz = profile.TunedHz;
        _state.SampleRateKhz = profile.SampleRateKhz;
    }

    /// <inheritdoc />
    public event Action<DecodedHostCommand>? CommandDecoded;

    /// <summary>True once the PA distortion model is shaping the PS coupler.</summary>
    public bool PsDistortionEnabled => _distortion.Enabled;

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken ct)
    {
        // One socket per port so each stream can be sent from its matching
        // source port. The send-only DDC0 socket binds 1035.
        using var sockGeneral = Bind(P2Wire.CmdGeneralPort);
        using var sockRx = Bind(P2Wire.CmdRxPort);
        using var sockTx = Bind(P2Wire.CmdTxPort);
        using var sockHp = Bind(P2Wire.CmdHighPriorityPort);
        using var sockTxIq = Bind(P2Wire.TxIqPort);
        using var sockDdc0 = Bind(P2Wire.RxDataPortBase);

        _logger.LogInformation(
            "vradio.p2 listening on {Bind} ports {G}/{Rx}/{Tx}/{Hp}/{TxIq} (+DDC0 {Ddc0}) as {Board}, rate {Rate}kHz, tones {Tones}, psDistortion={Dist}",
            _profile.BindAddress, P2Wire.CmdGeneralPort, P2Wire.CmdRxPort, P2Wire.CmdTxPort,
            P2Wire.CmdHighPriorityPort, P2Wire.TxIqPort, P2Wire.RxDataPortBase, _profile.Board,
            _profile.SampleRateKhz, _profile.Tones.Count, _distortion.Enabled);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new[]
        {
            ReceiveLoopAsync(sockGeneral, P2Wire.CmdGeneralPort, linked.Token),
            ReceiveLoopAsync(sockRx, P2Wire.CmdRxPort, linked.Token),
            ReceiveLoopAsync(sockTx, P2Wire.CmdTxPort, linked.Token),
            ReceiveLoopAsync(sockHp, P2Wire.CmdHighPriorityPort, linked.Token),
            ReceiveLoopAsync(sockTxIq, P2Wire.TxIqPort, linked.Token),
            Task.Run(() => Ddc0SendLoopAsync(sockDdc0, linked.Token), CancellationToken.None),
            Task.Run(() => HiPriStatusLoopAsync(sockRx, linked.Token), CancellationToken.None),
        };

        try
        {
            await Task.WhenAny(tasks).ConfigureAwait(false);
        }
        finally
        {
            linked.Cancel();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogDebug(ex, "vradio.p2 loop teardown."); }
        }
    }

    private Socket Bind(int port)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 1 << 20,
            SendBufferSize = 1 << 20,
        };
        s.EnableBroadcast = true;
        s.Bind(new IPEndPoint(_profile.BindAddress, port));
        return s;
    }

    private async Task ReceiveLoopAsync(Socket socket, int destPort, CancellationToken ct)
    {
        var buffer = new byte[2048];
        var any = new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult rx;
            try
            {
                rx = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "vradio.p2 receive error on {Port} (continuing).", destPort);
                continue;
            }

            var span = buffer.AsSpan(0, rx.ReceivedBytes);
            var remote = (IPEndPoint)rx.RemoteEndPoint;

            // Discovery probe on port 1024 → reply to the sender. (Dormant on
            // loopback: Zeus direct-connects and never probes loopback.)
            if (destPort == P2Wire.CmdGeneralPort && P2DiscoveryReply.IsDiscoveryProbe(span))
            {
                await ReplyToDiscoveryAsync(socket, remote, ct).ConfigureAwait(false);
                continue;
            }

            // TX-IQ stream (port 1029) → ingest as the PS reference. Not a
            // command; never marks state but does mark the host endpoint.
            if (destPort == P2Wire.TxIqPort)
            {
                _hostEndPoint = remote;
                IngestTxIq(span);
                continue;
            }

            IReadOnlyList<DecodedHostCommand> events;
            lock (_stateGate)
            {
                events = _decoder.Decode(destPort, span, _state);
                if (events.Count > 0)
                {
                    _hostEndPoint = remote;
                    if (_state.RxFreqHz != 0) _currentTunedHz = _state.RxFreqHz;
                }
            }

            if (events.Count == 0)
                continue;

            Interlocked.Increment(ref _cmdPacketsReceived);
            foreach (DecodedHostCommand cmd in events)
                PublishIfChanged(cmd);
        }
    }

    private async Task ReplyToDiscoveryAsync(Socket socket, IPEndPoint sender, CancellationToken ct)
    {
        var reply = new byte[P2DiscoveryReply.ReplyLength];
        P2DiscoveryReply.Build(
            reply, _profile, DiscoveryResponder.DefaultMac,
            codeVersion: DiscoveryResponder.DefaultCodeVersion, numReceivers: 2, busy: false);
        try
        {
            await socket.SendToAsync(reply, SocketFlags.None, sender, ct).ConfigureAwait(false);
            _logger.LogInformation("vradio.p2 discovery reply → {Peer} as {Board}.", sender, _profile.Board);
        }
        catch (OperationCanceledException) { }
        catch (SocketException ex) { _logger.LogDebug(ex, "vradio.p2 discovery send error."); }
    }

    private void IngestTxIq(ReadOnlySpan<byte> packet)
    {
        // BE u32 seq, then 240 complex int24 BE from offset 4.
        if (packet.Length < P2Wire.TxIqPayloadOffset + P2Wire.TxIqSamplesPerPacket * P2Wire.TxIqSampleStride)
            return;
        Span<float> iq = stackalloc float[P2Wire.TxIqSamplesPerPacket * 2];
        const float scale = 1f / (float)P2Wire.Int24FullScale;
        for (int i = 0; i < P2Wire.TxIqSamplesPerPacket; i++)
        {
            int off = P2Wire.TxIqPayloadOffset + i * P2Wire.TxIqSampleStride;
            iq[2 * i] = SignExtend24((packet[off] << 16) | (packet[off + 1] << 8) | packet[off + 2]) * scale;
            iq[2 * i + 1] = SignExtend24((packet[off + 3] << 16) | (packet[off + 4] << 8) | packet[off + 5]) * scale;
        }
        _txRef.Ingest(iq);
    }

    private static int SignExtend24(int raw)
    {
        if ((raw & 0x800000) != 0) raw |= unchecked((int)0xFF000000);
        return raw;
    }

    private void PublishIfChanged(DecodedHostCommand cmd)
    {
        lock (_lastSummaryByKind)
        {
            if (_lastSummaryByKind.TryGetValue(cmd.CommandKind, out string? prev) && prev == cmd.Summary)
                return;
            _lastSummaryByKind[cmd.CommandKind] = cmd.Summary;
        }
        _commandLog.Add(cmd);
        try { CommandDecoded?.Invoke(cmd); }
        catch (Exception ex) { _logger.LogWarning(ex, "vradio.p2 CommandDecoded handler threw."); }
    }

    private async Task Ddc0SendLoopAsync(Socket socket, CancellationToken ct)
    {
        var packet = new byte[P2Wire.BufLen];
        var rxIq = new double[2 * P2RxDdcEncoder.RxSamplesPerPacket];
        var refBuf = new double[2 * P2RxDdcEncoder.PsPairsPerPacket];
        var coupBuf = new double[2 * P2RxDdcEncoder.PsPairsPerPacket];
        uint seq = 0;

        var sw = Stopwatch.StartNew();
        long nextTick = sw.ElapsedTicks;

        while (!ct.IsCancellationRequested)
        {
            IPEndPoint? host = _hostEndPoint;
            bool running, feedback;
            int rateKhz;
            long tunedHz;
            lock (_stateGate)
            {
                running = _state.Running;
                // The host only sets byte 1363 during an armed keyed burst, so
                // the Mux bit alone is the gateware-faithful discriminator.
                feedback = _state.PsArmedBurst;
                rateKhz = _state.SampleRateKhz > 0 ? _state.SampleRateKhz : _profile.SampleRateKhz;
                tunedHz = _currentTunedHz;
            }

            if (!running || host is null)
            {
                try { await Task.Delay(10, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                nextTick = sw.ElapsedTicks;
                continue;
            }

            double intervalSec;
            if (feedback)
            {
                // Clean reference → coupler-through-PA (identity if distortion
                // off). Coupler is DDC0 (pscc "rx"), reference is DDC1 (pscc "tx").
                _txRef.Fill(refBuf, P2RxDdcEncoder.PsPairsPerPacket);
                for (int i = 0; i < P2RxDdcEncoder.PsPairsPerPacket; i++)
                {
                    var (ci, cq) = _distortion.Apply(refBuf[2 * i], refBuf[2 * i + 1]);
                    coupBuf[2 * i] = ci;
                    coupBuf[2 * i + 1] = cq;
                }
                _rxEncoder.EncodePsFeedback(packet, seq++, coupBuf, refBuf);
                Interlocked.Increment(ref _psFeedbackPacketsSent);
                intervalSec = P2RxDdcEncoder.PsPairsPerPacket / PsBurstRateHz;
            }
            else
            {
                _iq.Generate(rxIq, P2RxDdcEncoder.RxSamplesPerPacket, tunedHz);
                _rxEncoder.EncodeRxIq(packet, seq++, rxIq);
                intervalSec = P2RxDdcEncoder.RxSamplesPerPacket / (rateKhz * 1000.0);
            }

            try
            {
                await socket.SendToAsync(packet, SocketFlags.None, host, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _ddc0PacketsSent);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) { _logger.LogDebug(ex, "vradio.p2 DDC0 send error."); }

            long intervalTicks = (long)(intervalSec * Stopwatch.Frequency);
            nextTick += intervalTicks;
            long now = sw.ElapsedTicks;
            long remain = nextTick - now;
            if (remain <= 0) { nextTick = now; continue; }

            double remainMs = remain * 1000.0 / Stopwatch.Frequency;
            if (remainMs > 1.5)
            {
                try { await Task.Delay((int)(remainMs - 1.0), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            while (sw.ElapsedTicks < nextTick && !ct.IsCancellationRequested)
                Thread.SpinWait(40);
        }
    }

    private async Task HiPriStatusLoopAsync(Socket socket, CancellationToken ct)
    {
        uint seq = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(HiPriPeriod, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            IPEndPoint? host = _hostEndPoint;
            if (host is null) continue;

            byte driveByte, txAttn;
            bool mox, feedback;
            long tunedHz;
            lock (_stateGate)
            {
                driveByte = _state.DriveByte;
                mox = _state.Mox;
                feedback = _state.PsArmedBurst;
                txAttn = _state.TxStepAttnDb;
                tunedHz = _currentTunedHz;
            }

            RfTelemetry tel = _rf.Compute(driveByte, tunedHz, mox);
            // Model the single-ADC PS hazard: with the time-mux armed and keyed,
            // a missing byte-59 protective seed (txAttn below the floor) slams
            // the DAC feedback into the only RX ADC at 0 dB → ADC overload. A
            // correctly-seeded host clears it.
            byte overload = (feedback && txAttn < TxAdcProtectFloorDb) ? (byte)0x01 : (byte)0x00;

            byte[] packet = _hiPriEncoder.Build(seq++, tel, ptt: mox, pllLocked: true, overload);
            try
            {
                await socket.SendToAsync(packet, SocketFlags.None, host, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) { _logger.LogDebug(ex, "vradio.p2 hi-pri send error."); }
        }
    }

    /// <inheritdoc />
    public VirtualRadioStatus Snapshot()
    {
        byte driveByte;
        bool mox;
        long tunedHz;
        lock (_stateGate)
        {
            driveByte = _state.DriveByte;
            mox = _state.Mox;
            tunedHz = _currentTunedHz;
        }

        RfTelemetry tel = _rf.Compute(driveByte, tunedHz, mox);
        VirtualRadioProfile profile = _profile with { TunedHz = tunedHz };

        return new VirtualRadioStatus(
            Profile: profile,
            ConnectedHost: _hostEndPoint?.ToString(),
            Mox: mox,
            FwdWatts: tel.FwdWatts,
            RefWatts: tel.RefWatts,
            Swr: tel.Swr,
            Ep6PacketsSent: Interlocked.Read(ref _ddc0PacketsSent),
            Ep2PacketsReceived: Interlocked.Read(ref _cmdPacketsReceived),
            SeqGaps: 0,
            LastCommands: _commandLog.Snapshot());
    }

    // ---- Test observability (InternalsVisibleTo) --------------------------

    /// <summary>Most-recently decoded MOX state (test hook).</summary>
    internal bool DecodedMox { get { lock (_stateGate) return _state.Mox; } }

    /// <summary>Most-recently decoded drive byte (test hook).</summary>
    internal byte DecodedDriveByte { get { lock (_stateGate) return _state.DriveByte; } }

    /// <summary>Whether the host has armed the PS time-mux burst (byte 1363).</summary>
    internal bool PsBurstArmed { get { lock (_stateGate) return _state.PsArmedBurst; } }

    /// <summary>Most-recently decoded CmdTx byte 59 (the TX-time ADC attenuator).</summary>
    internal byte DecodedTxStepAttnDb { get { lock (_stateGate) return _state.TxStepAttnDb; } }

    /// <summary>Count of PS feedback packets streamed on DDC0.</summary>
    internal long PsFeedbackPacketsSent => Interlocked.Read(ref _psFeedbackPacketsSent);
}
