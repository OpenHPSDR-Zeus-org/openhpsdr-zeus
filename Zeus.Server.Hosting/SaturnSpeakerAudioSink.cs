// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Christian Suarez (N9WAR), and contributors.
//
// Protocol-2 radio-side speaker sink. DeskHPSDR / Thetis feed the radio's
// onboard codec (TLV320 → speaker / headphone / line-out jack) under
// Protocol 2 by sending compact UDP audio packets to the firmware's speaker
// port (1028). This sink gives Zeus the same path so a P2 operator can hear
// RX through the radio's own speaker jack, mirroring the P1 RadioSpeakerAudioSink
// behaviour. Works for any P2 board with an onboard codec (Hermes / ANAN-10E /
// ANAN-100D/200D / HermesC10 / Saturn family) — the byte_to_32bits gateware
// instance is wired identically across the lineup (issue #1122).

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Zeus.Contracts;
using Zeus.Protocol2;

namespace Zeus.Server;

/// <summary>
/// Sends RX audio to the connected Protocol-2 radio's onboard codec via UDP
/// port 1028. Gated on the operator opt-in <see cref="RadioSpeakerSettingsStore"/>
/// (default off) so a Zeus user who already hears RX audio host-side doesn't get
/// doubled output until they ask for it. The Protocol-1 counterpart is
/// <see cref="RadioSpeakerAudioSink"/> (writes into the EP2 frame's L/R slots
/// instead of opening a separate UDP socket); the two are mutually exclusive at
/// runtime because they self-gate on which protocol is currently active.
///
/// <para>Threading: <see cref="Publish"/> is called on the DSP RX tick thread
/// (the same thread that feeds <see cref="NativeAudioSink"/>'s playback ring).
/// It writes mono float samples into an in-process SPSC ring and signals a
/// dedicated sender thread that drains the ring, packs PCM packets, and runs
/// the 16 UDP syscalls per audio frame. This keeps the DSP tick off the
/// network hot path so the host soundcard's playback ring is filled on time
/// (issue #1148). All socket lifecycle (open / refresh on state change /
/// close on disable) also runs on the sender thread; cross-thread events from
/// <see cref="RadioService"/> and <see cref="RadioSpeakerSettingsStore"/> are
/// reduced to volatile flag flips + a wake on the worker.</para>
/// </summary>
internal sealed class SaturnSpeakerAudioSink : IRxAudioSink, IHostedService, IDisposable
{
    private const uint FrameRateHz = 48_000;
    private const int SpeakerAudioPort = 1028;
    private const int PacketFrames = 64;
    private const int PacketBytes = 4 + PacketFrames * 2 * sizeof(short);
    private const int TargetRefreshMs = 1_000;

    // ~170 ms @ 48 kHz mono = 8192 samples. Power of two for the FloatSpscRing
    // mask wrap. The sender drains ~750 packets/sec (one packet per 64 samples
    // ≈ 1.33 ms), so even worst-case scheduler latency between a DSP burst and
    // the sender waking fits in a fraction of this. Sized for jitter slack, not
    // capacity — keep small so a stalled sender doesn't accumulate seconds of
    // stale audio queued for the radio's speaker jack.
    private const int RingCapacity = 8_192;

    // Sender wake timeout (ms) — covers the (rare) case where a wake signal was
    // missed across a flag toggle (settings/state change racing the wait), and
    // is the upper bound on the data-bound park (waiting for the next publish).
    // At 100 ms it's well below human-perceptible latency; the common path is
    // wake-on-publish (~30 Hz) and never hits this timer.
    private const int WorkerWakeTimeoutMs = 100;

    // #1148 candidate (bench-gated): pace the radio-speaker UDP egress at the
    // codec's native 48 kHz cadence — one 64-sample packet per ~1333 µs ≈ 750
    // pkt/s — instead of firing a whole DSP tick's worth of packets in one
    // back-to-back burst (the one thing PR #1154 did NOT change). This mirrors
    // deskHPSDR / Thetis, which pace audio to the radio at ~1333 µs spacing with
    // 300-1000 µs adaptive waits (deskHPSDR new_protocol.c audio send loop). The
    // ring is the jitter buffer; pacing only reshapes egress timing and never
    // lowers AVERAGE throughput — catch-up (see DrainAndSend) keeps the long-run
    // rate equal to the inbound rate. PENDING #1148 telemetry to confirm the
    // burst is what perturbs inbound IQ; safe to ship because the worst case
    // (coarse platform timer) degrades to small groups, never worse than the
    // pre-existing single 16-burst.
    private const int TargetPacketsPerSec = 750;
    private static readonly long PacketIntervalTicks =
        Math.Max(1, Stopwatch.Frequency / TargetPacketsPerSec);
    // Bound how far the schedule may lag real time before we abandon the missed
    // slots and realign to "now". This caps the catch-up burst after a scheduler
    // stall so a stalled sender drains the ring at the paced rate rather than
    // dumping it all at once — yet it is generous enough (≈48 packets ≈ 64 ms)
    // that a coarse platform timer (Windows ~15 ms wake granularity) releases in
    // small groups without ever throttling steady-state throughput.
    private static readonly long MaxCatchupBehindTicks = PacketIntervalTicks * 48;
    private static readonly double TicksPerMs = Math.Max(1.0, Stopwatch.Frequency / 1000.0);
    private static readonly double UsPerTick = 1_000_000.0 / Stopwatch.Frequency;

    private readonly RadioService _radio;
    private readonly RadioSpeakerSettingsStore _settings;
    private readonly ILogger<SaturnSpeakerAudioSink> _log;
    private readonly FloatSpscRing _ring = new(RingCapacity);
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly ManualResetEventSlim _idle = new(true);
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _packet = new byte[PacketBytes];

    // Sender-thread state (single owner — only WorkerLoop reads/writes these
    // after StartAsync completes). The reflection-based test helpers read
    // _socket and _sequence after waiting for the worker to go idle.
    private Socket? _socket;
    private IPEndPoint? _target;
    private int _packetFrames;
    private uint _sequence;
    private long _nextRefreshMs;
    private long _droppedPackets;
    private long _droppedSamples;
    private bool _wasEligible;
    private ConnectionStatus _lastStatus = ConnectionStatus.Disconnected;
    private string? _lastEndpoint;

    // #1148 pacing schedule (sender-thread-owned). _nextSendTicks is the
    // monotonic Stopwatch deadline for the next packet (0 = realign to "now" on
    // the next drain — set on fresh start, drain, disable, and socket loss).
    // _pacingWaitTicks is how long the worker should park before the next due
    // packet (0 = no pending deadline, park for data / the full wake timeout).
    private long _nextSendTicks;
    private long _pacingWaitTicks;

    // Injectable clock + send observer for deterministic pacing tests — no
    // sockets, no real time. Null/Stopwatch in production.
    private Func<long> _clock = Stopwatch.GetTimestamp;
    private Action<long>? _sentObserverForTest;

    // #1148 sender-side telemetry (sender-thread-owned). Emitted at ~1 Hz so an
    // OFF-vs-ON radio-speaker capture can be correlated with p2.rxdiag /
    // dsp.tickdiag by timestamp: packets/sec, the MAX burst sent in a single
    // DrainAndSend (proves pacing spreads egress), inter-send gap mean/max, and
    // the running WouldBlock drop counters.
    private long _diagLastEmitTicks;
    private long _diagPacketsSent;
    private int _diagBurstThisDrain;
    private int _diagMaxBurst;
    private long _diagSendGapSumUs;
    private long _diagSendGapMaxUs;
    private long _diagSendGapCount;
    private long _diagLastSendTicks;

    // Cross-thread signals. Volatile reads/writes are sufficient — these are
    // single-bit flags, not data values; the worker re-snapshots radio/settings
    // state when it acts on them so there's nothing to atomicise.
    private volatile bool _refreshRequested;
    private volatile bool _drainRequested;

    // Set first thing in Dispose so the cross-thread signallers stop touching
    // _wake before it is disposed. See SignalWake.
    private volatile bool _disposed;

    private Thread? _worker;

    public SaturnSpeakerAudioSink(
        RadioService radio,
        RadioSpeakerSettingsStore settings,
        ILogger<SaturnSpeakerAudioSink> log)
    {
        _radio = radio;
        _settings = settings;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _radio.StateChanged += OnRadioStateChanged;
        _settings.Changed += OnSettingsChanged;
        _refreshRequested = true;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "zeus-p2-speaker-tx",
        };
        _worker.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _radio.StateChanged -= OnRadioStateChanged;
        _settings.Changed -= OnSettingsChanged;
        _cts.Cancel();
        _wake.Set();
        try { _worker?.Join(TimeSpan.FromSeconds(2)); }
        catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    public void Publish(in AudioFrame frame)
    {
        if (frame.Channels != 1 || frame.SampleRateHz != FrameRateHz) return;
        if (!_settings.Enabled) return;
        // Protocol gate: this UDP→1028 path is Protocol-2 only. The same RX
        // AudioFrame is fanned to every IRxAudioSink regardless of protocol, so
        // without this a dual-protocol codec board (e.g. ANAN-10E) connected over
        // P1 would also blast packets at the radio's P2 speaker port — which P1
        // firmware doesn't bind — causing pointless traffic + a per-second
        // socket re-flap. RadioSpeakerAudioSink owns P1 via IsProtocol1Active.
        if (!_radio.IsProtocol2Active) return;

        // Mirror the P1 sink's MOX mute: while transmitting, don't carry the
        // operator's TX-monitor / CW sidetone out to the radio's speaker jack.
        // Ask the worker to drop the buffered tail (and the partial packet) so
        // RX resumes clean on unkey instead of replaying the pre-key tail.
        if (_radio.IsMox)
        {
            _drainRequested = true;
            SignalWake();
            return;
        }

        var src = frame.Samples.Span;
        int written = _ring.Write(src);
        if (written < src.Length)
        {
            Interlocked.Add(ref _droppedSamples, src.Length - written);
        }
        SignalWake();
    }

    // Wake the sender, tolerating a late call that races Dispose. Publish (on
    // the DSP tick thread) and the RadioService / settings event handlers can
    // all fire after the sink is disposed during host shutdown — the sink stays
    // registered as an IRxAudioSink, so PublishAudio can still fan a frame to
    // us, and a state/settings event can still arrive in the unsubscribe window.
    // Setting a disposed ManualResetEventSlim would throw ObjectDisposedException
    // on the real-time audio thread and take it down; swallow that one race.
    private void SignalWake()
    {
        if (_disposed) return;
        try { _wake.Set(); }
        catch (ObjectDisposedException) { /* raced Dispose — nothing to wake */ }
    }

    private void OnRadioStateChanged(StateDto state)
    {
        _refreshRequested = true;
        SignalWake();
    }

    private void OnSettingsChanged()
    {
        // Disable also asks the worker to drop the buffered tail so a later
        // re-enable starts clean rather than replaying stale samples that
        // pre-date the operator's last toggle. Worker re-reads _settings.Enabled
        // inside RefreshTarget so a flip-on path is covered by the same refresh.
        if (!_settings.Enabled) _drainRequested = true;
        _refreshRequested = true;
        SignalWake();
    }

    private void WorkerLoop()
    {
        var ct = _cts.Token;
        Span<float> scratch = stackalloc float[PacketFrames];

        while (!ct.IsCancellationRequested)
        {
            _idle.Set();
            // Deadline-bound park when a paced packet is pending; otherwise wait
            // for the next publish (or the safety timeout). Round the pacing wait
            // UP to at least 1 ms so we never spin on a sub-millisecond timeout —
            // on a coarse platform timer this simply releases packets in small
            // groups, which is the documented graceful degradation.
            int waitMs = _pacingWaitTicks > 0
                ? Math.Clamp((int)Math.Ceiling(_pacingWaitTicks / TicksPerMs), 1, WorkerWakeTimeoutMs)
                : WorkerWakeTimeoutMs;
            try { _wake.Wait(waitMs, ct); }
            catch (OperationCanceledException) { break; }
            _wake.Reset();
            _idle.Reset();

            if (ct.IsCancellationRequested) break;

            if (_refreshRequested)
            {
                _refreshRequested = false;
                RefreshTarget(_radio.Snapshot(), force: true);
            }

            if (_drainRequested)
            {
                _drainRequested = false;
                _ring.Clear();
                _packetFrames = 0;
                _nextSendTicks = 0; // realign the pacing schedule on resume
            }

            if (_socket is null)
            {
                RefreshTargetIfDue();
                if (_socket is null)
                {
                    // No target yet — discard anything the producer wrote
                    // before the gate evaluated (or while the socket was
                    // being re-opened) so we don't carry stale audio across
                    // a connection flap.
                    _ring.Clear();
                    _packetFrames = 0;
                    _nextSendTicks = 0;
                    _pacingWaitTicks = 0;
                    continue;
                }
            }

            DrainAndSend(scratch);
            if (_socket is not null) MaybeEmitDiag();
        }

        _idle.Set();
        CloseSocket();
    }

    // Drain the ring into 64-sample packets and release them on a monotonic
    // 750 pkt/s schedule (issue #1148). The ring itself is the jitter buffer;
    // this loop never busy-spins — when the next packet is not yet due it parks
    // the worker (via _pacingWaitTicks) until the deadline or the next publish.
    // Average throughput is preserved: when the schedule falls behind real time
    // (a scheduler hiccup, or a coarse platform timer waking late) packets are
    // released back-to-back to catch up, bounded by MaxCatchupBehindTicks so a
    // stalled sender can't dump the whole ring at once.
    private void DrainAndSend(Span<float> scratch)
    {
        _diagBurstThisDrain = 0;
        while (true)
        {
            long now = _clock();

            // Fresh start / post-drain: anchor the schedule to "now" so the
            // first packet of a new RX burst ships promptly (no backlog of
            // "due" slots accumulated across an idle gap).
            if (_nextSendTicks == 0) _nextSendTicks = now;

            // Catch-up cap: never let the schedule lag real time by more than
            // MaxCatchupBehindTicks. This bounds the burst after a stall while
            // still letting the long-run rate equal the inbound rate.
            long earliest = now - MaxCatchupBehindTicks;
            if (_nextSendTicks < earliest) _nextSendTicks = earliest;

            if (now < _nextSendTicks)
            {
                // Next packet not due yet — park until the deadline (or an
                // earlier publish wake). Bounded; never busy-spins.
                _pacingWaitTicks = _nextSendTicks - now;
                return;
            }

            int need = PacketFrames - _packetFrames;
            int read = _ring.Read(scratch[..need]);
            if (read == 0)
            {
                // Nothing buffered: realign the schedule and wait for the
                // producer to wake us with fresh audio.
                _nextSendTicks = 0;
                _pacingWaitTicks = 0;
                return;
            }

            for (int i = 0; i < read; i++)
            {
                int offset = 4 + _packetFrames * 2 * sizeof(short);
                short pcm = FloatToPcm16(scratch[i]);
                BinaryPrimitives.WriteInt16BigEndian(_packet.AsSpan(offset, sizeof(short)), pcm);
                BinaryPrimitives.WriteInt16BigEndian(_packet.AsSpan(offset + sizeof(short), sizeof(short)), pcm);
                _packetFrames++;
            }

            if (_packetFrames < PacketFrames)
            {
                // Partial packet — need more samples before this slot can ship.
                // Wait for the next publish rather than the pacing deadline.
                _pacingWaitTicks = 0;
                return;
            }

            if (!EmitPacket(now)) return; // socket died mid-drain
            _nextSendTicks += PacketIntervalTicks;
        }
    }

    // Emit one full packet. Returns false only when the production socket went
    // away (so the drain loop stops). The test observer simulates a successful
    // send — advancing the sequence and recording the emit time — with no socket.
    private bool EmitPacket(long now)
    {
        var obs = _sentObserverForTest;
        if (obs is not null)
        {
            BinaryPrimitives.WriteUInt32BigEndian(_packet.AsSpan(0, sizeof(uint)), _sequence++);
            _packetFrames = 0;
            RecordSend(now);
            obs(now);
            return true;
        }

        SendPacket(); // resets _packetFrames in its finally
        RecordSend(now);
        return _socket is not null;
    }

    private void RecordSend(long now)
    {
        _diagPacketsSent++;
        _diagBurstThisDrain++;
        if (_diagBurstThisDrain > _diagMaxBurst) _diagMaxBurst = _diagBurstThisDrain;
        if (_diagLastSendTicks != 0)
        {
            long gapUs = (long)((now - _diagLastSendTicks) * UsPerTick);
            _diagSendGapSumUs += gapUs;
            if (gapUs > _diagSendGapMaxUs) _diagSendGapMaxUs = gapUs;
            _diagSendGapCount++;
        }
        _diagLastSendTicks = now;
    }

    // #1148 sender-side ~1 Hz diagnostic. Worker-thread-owned; called after each
    // DrainAndSend while a socket is open.
    private void MaybeEmitDiag()
    {
        long now = _clock();
        if (_diagLastEmitTicks == 0) { _diagLastEmitTicks = now; return; }
        long elapsed = now - _diagLastEmitTicks;
        if (elapsed < Stopwatch.Frequency) return;
        _diagLastEmitTicks = now;

        double secs = elapsed / (double)Stopwatch.Frequency;
        double pps = secs > 0 ? _diagPacketsSent / secs : 0;
        long meanGapUs = _diagSendGapCount > 0 ? _diagSendGapSumUs / _diagSendGapCount : 0;

        _log.LogInformation(
            "audio.radio.speaker.p2.diag pkts/s={Pps:F0} maxBurst={MaxBurst} sendGapUs(mean/max)={Mean}/{Max} droppedPkts={DropPkts} droppedSamples={DropSamp}",
            pps, _diagMaxBurst, meanGapUs, _diagSendGapMaxUs,
            Interlocked.Read(ref _droppedPackets), Interlocked.Read(ref _droppedSamples));

        _diagPacketsSent = 0;
        _diagMaxBurst = 0;
        _diagSendGapSumUs = 0;
        _diagSendGapMaxUs = 0;
        _diagSendGapCount = 0;
    }

    private void RefreshTargetIfDue()
    {
        long now = Environment.TickCount64;
        if (now < _nextRefreshMs) return;
        _nextRefreshMs = now + TargetRefreshMs;

        RefreshTarget(_radio.Snapshot(), force: true);
    }

    private void RefreshTarget(StateDto state, bool force)
    {
        if (!force
            && _lastStatus == state.Status
            && string.Equals(_lastEndpoint, state.Endpoint, StringComparison.Ordinal))
        {
            return;
        }

        _lastStatus = state.Status;
        _lastEndpoint = state.Endpoint;

        var board = _radio.ConnectedBoardKind;
        var variant = _radio.EffectiveOrionMkIIVariant;
        var caps = BoardCapabilitiesTable.For(board, variant);

        // P2 RX-audio over UDP 1028 reaches the radio's onboard codec on every
        // board whose firmware instantiates byte_to_32bits(#1028) → TLV320:
        // Hermes / ANAN-10/10E/100/100B/100D/200D / HermesC10 / Saturn family.
        // HermesLite2 has no stream codec (HasOnboardCodec=false) and is
        // naturally excluded; the Protocol-1 path is owned by
        // RadioSpeakerAudioSink and self-gates on IsProtocol1Active. Also gate
        // the actual socket open on the operator opt-in so we don't hold a UDP
        // resource for a feature the operator never asked for.
        if (state.Status != ConnectionStatus.Connected
            || !_radio.IsProtocol2Active
            || string.IsNullOrWhiteSpace(state.Endpoint)
            || !_settings.Enabled
            || !caps.HasOnboardCodec
            || !RadioService.TryParseEndpoint(state.Endpoint, out var radioEndpoint))
        {
            if (_wasEligible)
            {
                _log.LogInformation("audio.radio.speaker.p2 disabled");
            }
            _wasEligible = false;
            CloseSocket();
            return;
        }

        var target = new IPEndPoint(radioEndpoint.Address, SpeakerAudioPort);
        if (_target is not null && _target.Equals(target) && _socket is not null)
        {
            return;
        }

        CloseSocket();

        try
        {
            var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
            {
                Blocking = false,
            };
            // Match Protocol2Client's UDP setup on this dedicated speaker socket:
            //  - SIO_UDP_CONNRESET off (issue #1218): without it, a single ICMP
            //    port-unreachable from the radio surfaces as WSAECONNRESET on the
            //    next Send and takes the whole audio socket down until the 1 s
            //    refresh reopens it. The main P2 socket disables this ioctl for
            //    the same reason; this one carries the same Windows risk.
            //  - Bind to the local address on the radio's subnet, on an
            //    ephemeral port. Leaving the socket unbound lets the OS pick a
            //    source address at Connect time; binding pins the source
            //    address to the same local IP the P2 command socket binds
            //    (parity with Protocol2Client), so the radio and Windows
            //    Firewall see one consistent flow across all P2 traffic.
            Protocol2Client.DisableUdpConnReset(socket);
            // NIC enumeration inside FindLocalAddressForSubnet can throw
            // NetworkInformationException (not a SocketException) during NIC
            // churn / sleep-resume — and RefreshTarget runs on the raw sender
            // thread, where an escaped throw kills the whole process. A failed
            // lookup just means bind-to-Any, same as a no-subnet-match result.
            IPAddress localBind;
            try { localBind = Protocol2Client.FindLocalAddressForSubnet(target.Address) ?? IPAddress.Any; }
            catch { localBind = IPAddress.Any; }
            socket.Bind(new IPEndPoint(localBind, 0));
            socket.Connect(target);
            _socket = socket;
            _target = target;
            _packetFrames = 0;
            _wasEligible = true;

            _log.LogInformation(
                "audio.radio.speaker.p2 enabled target={Target} localBind={Local} board={Board} variant={Variant}",
                target,
                localBind.Equals(IPAddress.Any) ? "ANY (no subnet match)" : localBind.ToString(),
                board,
                variant);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            CloseSocket();
            _log.LogWarning(ex, "audio.radio.speaker.p2 open failed target={Target}", target);
        }
    }

    private void SendPacket()
    {
        var socket = _socket;
        if (socket is null) return;

        BinaryPrimitives.WriteUInt32BigEndian(_packet.AsSpan(0, sizeof(uint)), _sequence++);

        try
        {
            int sent = socket.Send(_packet);
            if (sent != PacketBytes)
            {
                Interlocked.Increment(ref _droppedPackets);
            }
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.NoBufferSpaceAvailable)
        {
            Interlocked.Increment(ref _droppedPackets);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            var dropped = Interlocked.Read(ref _droppedPackets);
            CloseSocket();
            _wasEligible = false;
            _log.LogWarning(ex, "audio.radio.speaker.p2 send failed dropped={DroppedPackets}", dropped);
        }
        finally
        {
            _packetFrames = 0;
        }
    }

    private void CloseSocket()
    {
        if (_socket is not null)
        {
            try { _socket.Dispose(); }
            catch { /* best-effort */ }
        }
        _socket = null;
        _target = null;
        _packetFrames = 0;
        _nextSendTicks = 0; // realign the pacing schedule when a socket reopens
    }

    internal static short FloatToPcm16(float sample)
    {
        if (float.IsNaN(sample)) return 0;
        if (sample <= -1.0f) return short.MinValue;
        if (sample >= 1.0f) return short.MaxValue;

        float scale = sample < 0.0f ? 32768.0f : short.MaxValue;
        return (short)MathF.Round(sample * scale);
    }

    // ---- Pacing test seams (#1148). No sockets, no real time: drive a virtual
    // clock, feed the ring directly, observe simulated sends, and assert drain
    // cadence + max-burst. None of these are used in production. ----

    /// <summary>Test-only: override the monotonic clock the pacing schedule
    /// reads (default <see cref="Stopwatch.GetTimestamp"/>).</summary>
    internal Func<long> ClockForTest { set => _clock = value; }

    /// <summary>Test-only: observe each simulated packet send (argument is the
    /// virtual send time). Setting this routes <see cref="EmitPacket"/> through
    /// the observer instead of the real socket.</summary>
    internal Action<long>? SentObserverForTest { set => _sentObserverForTest = value; }

    /// <summary>Test-only: write samples straight into the sender ring, as the
    /// producer would, without going through <see cref="Publish"/>'s gates.
    /// Returns the count actually written.</summary>
    internal int WriteRingForTest(ReadOnlySpan<float> samples) => _ring.Write(samples);

    /// <summary>Test-only: run one <see cref="DrainAndSend"/> pass on the
    /// calling thread (the worker is not started in pacing tests).</summary>
    internal void DrainForTest()
    {
        Span<float> scratch = stackalloc float[PacketFrames];
        DrainAndSend(scratch);
    }

    /// <summary>Test-only: largest burst observed in a single
    /// <see cref="DrainAndSend"/> since the last diag emit.</summary>
    internal int MaxBurstForTest => _diagMaxBurst;

    /// <summary>Test-only: the pacing packet interval in Stopwatch ticks.</summary>
    internal static long PacketIntervalTicksForTest => PacketIntervalTicks;

    /// <summary>Test-only: the catch-up clamp in Stopwatch ticks.</summary>
    internal static long MaxCatchupBehindTicksForTest => MaxCatchupBehindTicks;

    /// <summary>Test-only: block until the worker has processed every signal
    /// posted before this call and is parked back in its wake-wait. Lets tests
    /// that mutate <see cref="RadioSpeakerSettingsStore"/> /
    /// <see cref="RadioService"/> state assert against socket / sequence
    /// snapshots without sleep-flake races against the worker thread.</summary>
    internal bool WaitForIdleForTest(TimeSpan timeout)
    {
        long deadlineTicks = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            long remainingMs = deadlineTicks - Environment.TickCount64;
            if (remainingMs <= 0) return false;
            _wake.Set();
            if (!_idle.Wait(TimeSpan.FromMilliseconds(Math.Min(20, remainingMs)))) continue;
            // Worker may have just set _idle right before reading the next
            // wake; loop until it actually settles with no pending work.
            if (!_refreshRequested && !_drainRequested && _ring.Count == 0) return true;
        }
    }

    public void Dispose()
    {
        // Stop the cross-thread signallers before tearing _wake down.
        _disposed = true;
        _radio.StateChanged -= OnRadioStateChanged;
        _settings.Changed -= OnSettingsChanged;

        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            _wake.Set();
            try { _worker?.Join(TimeSpan.FromSeconds(2)); }
            catch { /* best-effort */ }
        }

        _wake.Dispose();
        _idle.Dispose();
        _cts.Dispose();
        CloseSocket();
    }
}
