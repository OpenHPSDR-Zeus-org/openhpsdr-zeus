// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.VirtualRadio.Discovery;
using Zeus.VirtualRadio.Observation;
using Zeus.VirtualRadio.Rf;

namespace Zeus.VirtualRadio.P1;

/// <summary>
/// The Protocol-1 virtual-radio engine: binds UDP 1024, answers discovery,
/// accepts a direct connection, decodes inbound EP2 host commands
/// (<see cref="Ep2Decoder"/>), and streams EP6 RX-IQ (<see cref="Ep6Encoder"/>)
/// with synthetic signal (<see cref="SyntheticIqGenerator"/>) and calibration-
/// inverted FWD/REF telemetry (<see cref="RfTelemetryModel"/>) at the true
/// ~381 pkt/s cadence. This is the Phase-1 vertical slice (ANAN-10E / HermesII).
///
/// One UDP socket is owned by the engine: the receive loop classifies each
/// datagram (discovery probe → reply; start/stop → begin/stop the RX stream;
/// EP2 data frame → decode into <see cref="HostCommandState"/>), and the send
/// loop streams EP6 to the host endpoint observed from inbound traffic, paced by
/// a <see cref="Stopwatch"/> at the negotiated sample rate. Both loops are
/// cancellable; the socket is disposed on shutdown.
/// </summary>
public sealed class Protocol1Engine : IVirtualRadio
{
    private readonly VirtualRadioProfile _profile;
    private readonly ILogger<Protocol1Engine> _logger;
    private readonly HostCommandState _state = new();
    private readonly CommandLog _commandLog = new();

    private readonly DiscoveryResponder _discovery;
    private readonly Ep2Decoder _decoder = new();
    private readonly Ep6Encoder _encoder = new();
    private readonly SyntheticIqGenerator _iq;
    private readonly RfTelemetryModel _rf;

    // Guards _state mutation (receive loop) against reads (send loop / Snapshot).
    private readonly object _stateGate = new();

    // Host endpoint observed from inbound traffic; the send loop streams EP6
    // here. Reference write/read is atomic; updated on every recognised frame.
    private volatile IPEndPoint? _hostEndPoint;

    // Last decoded RX NCO frequency (Hz). Falls back to the profile seed until
    // the host commands an RxFreq. Read by the send loop for the IQ baseband
    // offset; written under _stateGate.
    private long _currentRxHz;

    // UDP port the engine binds. Defaults to the well-known Protocol-1 radio
    // port (1024); overridable only so the hermetic loopback test can bind a
    // free ephemeral port instead of contending for 1024.
    private readonly int _port;

    // EP6 monotonic sequence (radio-assigned). Send loop only.
    private uint _ep6Sequence;

    // Inbound EP2 sequence-gap tracking (best-effort observability).
    private uint _lastEp2Seq;
    private bool _seenEp2Seq;

    // Counters (cross-thread; Interlocked).
    private long _ep6PacketsSent;
    private long _ep2PacketsReceived;
    private long _seqGaps;

    // Edge-trigger de-dup for CommandDecoded: the round-robin re-sends the same
    // Config/RxFreq/DriveFilter every few ms, so we only surface a command when
    // its decoded summary changes (mirrors the p1.tx.rate "1 Hz + edge" idiom).
    private readonly Dictionary<string, string> _lastSummaryByKind = new();

    public Protocol1Engine(
        VirtualRadioProfile profile,
        ILogger<Protocol1Engine>? logger = null,
        int port = DiscoveryResponder.DiscoveryPort)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _logger = logger ?? NullLogger<Protocol1Engine>.Instance;
        _port = port;
        _discovery = new DiscoveryResponder(profile);
        _iq = new SyntheticIqGenerator(profile);
        _rf = new RfTelemetryModel(profile);
        _currentRxHz = profile.TunedHz;
        _state.SampleRateKhz = profile.SampleRateKhz;
    }

    /// <inheritdoc />
    public event Action<DecodedHostCommand>? CommandDecoded;

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 256 * 1024,
            SendBufferSize = 256 * 1024,
        };
        socket.EnableBroadcast = true;
        socket.Bind(new IPEndPoint(_profile.BindAddress, _port));

        _logger.LogInformation(
            "vradio.p1 listening on {Bind}:{Port} as {Board} ({Protocol}), rate {Rate}kHz, tones {Tones}",
            _profile.BindAddress, _port, _profile.Board, _profile.Protocol,
            _profile.SampleRateKhz, _profile.Tones.Count);

        // Cancel both loops together; either failing tears the engine down.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task receive = ReceiveLoopAsync(socket, linked.Token);
        Task send = Task.Run(() => SendLoopAsync(socket, linked.Token), CancellationToken.None);

        try
        {
            await Task.WhenAny(receive, send).ConfigureAwait(false);
        }
        finally
        {
            linked.Cancel();
            try { await Task.WhenAll(receive, send).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
            catch (Exception ex) { _logger.LogDebug(ex, "vradio.p1 loop teardown."); }
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
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
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "vradio.p1 receive error (continuing).");
                continue;
            }
            catch (ObjectDisposedException) { break; }

            var span = buffer.AsSpan(0, rx.ReceivedBytes);
            var remote = (IPEndPoint)rx.RemoteEndPoint;

            // Discovery probe (0xEF FE 02): reply to the sender, do not treat as
            // a host connection. On loopback Zeus never probes (direct-connect),
            // so this path is dormant there but ready for a LAN bind.
            if (DiscoveryResponder.IsDiscoveryProbe(span))
            {
                if (_discovery.TryBuildReply(span, out byte[] reply))
                {
                    try
                    {
                        await socket.SendToAsync(reply, SocketFlags.None, remote, ct).ConfigureAwait(false);
                        _logger.LogInformation("vradio.p1 discovery reply → {Peer} as {Board}.", remote, _profile.Board);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (SocketException ex) { _logger.LogDebug(ex, "vradio.p1 discovery send error."); }
                }
                continue;
            }

            // Any recognised host frame (start/stop or EP2 data) marks the host
            // endpoint the RX stream is delivered to.
            IReadOnlyList<DecodedHostCommand> events;
            lock (_stateGate)
            {
                events = _decoder.Decode(span, _state);
                if (events.Count > 0)
                {
                    _hostEndPoint = remote;
                    _currentRxHz = _state.RxFreqHz != 0 ? _state.RxFreqHz : _currentRxHz;
                }
            }

            if (events.Count == 0)
                continue;

            // EP2 data frame: count it and track inbound sequence gaps.
            if (span.Length == Ep2Decoder.Ep2PacketLength)
            {
                Interlocked.Increment(ref _ep2PacketsReceived);
                TrackEp2Sequence(BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4)));
            }

            foreach (DecodedHostCommand cmd in events)
                PublishIfChanged(cmd);
        }
    }

    private void TrackEp2Sequence(uint seq)
    {
        if (_seenEp2Seq && seq > _lastEp2Seq)
        {
            long gap = (long)seq - _lastEp2Seq - 1;
            if (gap > 0) Interlocked.Add(ref _seqGaps, gap);
        }
        _seenEp2Seq = true;
        _lastEp2Seq = seq;
    }

    /// <summary>
    /// Edge-triggered publish: feed the command log + raise
    /// <see cref="CommandDecoded"/> only when this kind's decoded summary
    /// changed, so the round-robin's steady re-sends don't flood observers.
    /// </summary>
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
        catch (Exception ex) { _logger.LogWarning(ex, "vradio.p1 CommandDecoded handler threw."); }
    }

    private async Task SendLoopAsync(Socket socket, CancellationToken ct)
    {
        // Reusable scratch: one EP6 packet, its IQ source buffer, an empty mic
        // span (audio is a later phase).
        var packet = new byte[Ep6Encoder.Ep6PacketLength];
        var iq = new double[2 * Ep6Encoder.ComplexSamplesPerPacket];

        var sw = Stopwatch.StartNew();
        long nextTick = sw.ElapsedTicks;

        while (!ct.IsCancellationRequested)
        {
            IPEndPoint? host = _hostEndPoint;
            bool running;
            byte driveByte;
            bool mox;
            long bandHz;
            long tunedHz;
            int rateKhz;
            lock (_stateGate)
            {
                running = _state.Running;
                driveByte = _state.DriveByte;
                mox = _state.Mox;
                bandHz = _state.TxFreqHz != 0 ? _state.TxFreqHz : _currentRxHz;
                tunedHz = _currentRxHz;
                rateKhz = _state.SampleRateKhz > 0 ? _state.SampleRateKhz : _profile.SampleRateKhz;
            }

            if (!running || host is null)
            {
                // Idle: nothing to stream yet. Sleep briefly and rebase the
                // pacing clock so we don't burst on the first packet after start.
                try { await Task.Delay(10, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                nextTick = sw.ElapsedTicks;
                continue;
            }

            double sampleRateHz = rateKhz * 1000.0;

            _iq.Generate(iq, Ep6Encoder.ComplexSamplesPerPacket, tunedHz);
            RfTelemetry tel = _rf.Compute(driveByte, bandHz, mox);
            // C0[0] hardware-PTT echo follows the decoded host MOX (the firmware's
            // debounced clean_PTT_in), independent of drive amplitude — so a
            // keyed-with-zero-drive frame still echoes PTT. mic span omitted
            // (audio is a later phase).
            _encoder.Encode(packet, _ep6Sequence++, iq, tel, mox);

            try
            {
                await socket.SendToAsync(packet, SocketFlags.None, host, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _ep6PacketsSent);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "vradio.p1 EP6 send error to {Host}.", host);
            }

            // Pace at the true EP6 cadence: ComplexSamplesPerPacket / sampleRate
            // seconds per packet (48k → 2.625 ms → ~381 pkt/s). Stopwatch-based
            // so the host's own clock pacing (Zeus paces TX off received RX) sees
            // a steady stream, not whatever Task.Delay rounds to.
            long intervalTicks = (long)(Ep6Encoder.ComplexSamplesPerPacket / sampleRateHz * Stopwatch.Frequency);
            nextTick += intervalTicks;
            long now = sw.ElapsedTicks;
            long remain = nextTick - now;
            if (remain <= 0)
            {
                // Fell behind (or sub-ms cadence we can't sleep precisely): rebase
                // and free-run; the kernel buffers absorb the small burst.
                nextTick = now;
                continue;
            }

            double remainMs = remain * 1000.0 / Stopwatch.Frequency;
            if (remainMs > 1.5)
            {
                try { await Task.Delay((int)(remainMs - 1.0), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            // Spin off the sub-millisecond remainder for cadence accuracy.
            while (sw.ElapsedTicks < nextTick && !ct.IsCancellationRequested)
                Thread.SpinWait(40);
        }
    }

    /// <inheritdoc />
    public VirtualRadioStatus Snapshot()
    {
        byte driveByte;
        bool mox;
        long bandHz;
        long tunedHz;
        lock (_stateGate)
        {
            driveByte = _state.DriveByte;
            mox = _state.Mox;
            bandHz = _state.TxFreqHz != 0 ? _state.TxFreqHz : _currentRxHz;
            tunedHz = _currentRxHz;
        }

        RfTelemetry tel = _rf.Compute(driveByte, bandHz, mox);
        VirtualRadioProfile profile = _profile with { TunedHz = tunedHz };

        return new VirtualRadioStatus(
            Profile: profile,
            ConnectedHost: _hostEndPoint?.ToString(),
            Mox: mox,
            FwdWatts: tel.FwdWatts,
            RefWatts: tel.RefWatts,
            Swr: tel.Swr,
            Ep6PacketsSent: Interlocked.Read(ref _ep6PacketsSent),
            Ep2PacketsReceived: Interlocked.Read(ref _ep2PacketsReceived),
            SeqGaps: Interlocked.Read(ref _seqGaps),
            LastCommands: _commandLog.Snapshot());
    }

    // ---- Test observability (InternalsVisibleTo) --------------------------
    // The loopback integration test asserts the engine decoded the host's drive
    // byte / MOX without reaching into the wire. Snapshot() surfaces watts (a
    // function of the drive byte) but not the raw byte, so expose it here.

    /// <summary>The most-recently decoded transmit drive byte (test hook).</summary>
    internal byte DecodedDriveByte
    {
        get { lock (_stateGate) { return _state.DriveByte; } }
    }

    /// <summary>The most-recently decoded MOX state (test hook).</summary>
    internal bool DecodedMox
    {
        get { lock (_stateGate) { return _state.Mox; } }
    }
}
