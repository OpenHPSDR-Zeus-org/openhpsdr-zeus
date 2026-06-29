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
using System.Net;
using System.Net.Sockets;
using Zeus.Contracts;

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

    // Sender wake timeout — covers the (rare) case where a wake signal was
    // missed across a flag toggle (settings/state change racing the wait). At
    // 100 ms it's well below human-perceptible latency; the common path is
    // wake-on-publish (~46 Hz) and never hits the timer.
    private static readonly TimeSpan WorkerWakeTimeout = TimeSpan.FromMilliseconds(100);

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

    // Cross-thread signals. Volatile reads/writes are sufficient — these are
    // single-bit flags, not data values; the worker re-snapshots radio/settings
    // state when it acts on them so there's nothing to atomicise.
    private volatile bool _refreshRequested;
    private volatile bool _drainRequested;

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
            _wake.Set();
            return;
        }

        var src = frame.Samples.Span;
        int written = _ring.Write(src);
        if (written < src.Length)
        {
            Interlocked.Add(ref _droppedSamples, src.Length - written);
        }
        _wake.Set();
    }

    private void OnRadioStateChanged(StateDto state)
    {
        _refreshRequested = true;
        _wake.Set();
    }

    private void OnSettingsChanged()
    {
        // Disable also asks the worker to drop the buffered tail so a later
        // re-enable starts clean rather than replaying stale samples that
        // pre-date the operator's last toggle. Worker re-reads _settings.Enabled
        // inside RefreshTarget so a flip-on path is covered by the same refresh.
        if (!_settings.Enabled) _drainRequested = true;
        _refreshRequested = true;
        _wake.Set();
    }

    private void WorkerLoop()
    {
        var ct = _cts.Token;
        Span<float> scratch = stackalloc float[PacketFrames];

        while (!ct.IsCancellationRequested)
        {
            _idle.Set();
            try { _wake.Wait(WorkerWakeTimeout, ct); }
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
                    continue;
                }
            }

            DrainAndSend(scratch);
        }

        _idle.Set();
        CloseSocket();
    }

    private void DrainAndSend(Span<float> scratch)
    {
        while (true)
        {
            int need = PacketFrames - _packetFrames;
            int read = _ring.Read(scratch[..need]);
            if (read == 0) return;

            for (int i = 0; i < read; i++)
            {
                int offset = 4 + _packetFrames * 2 * sizeof(short);
                short pcm = FloatToPcm16(scratch[i]);
                BinaryPrimitives.WriteInt16BigEndian(_packet.AsSpan(offset, sizeof(short)), pcm);
                BinaryPrimitives.WriteInt16BigEndian(_packet.AsSpan(offset + sizeof(short), sizeof(short)), pcm);
                _packetFrames++;
            }

            if (_packetFrames == PacketFrames)
            {
                SendPacket();
                if (_socket is null) return;
            }
        }
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
            socket.Connect(target);
            _socket = socket;
            _target = target;
            _packetFrames = 0;
            _wasEligible = true;

            _log.LogInformation(
                "audio.radio.speaker.p2 enabled target={Target} board={Board} variant={Variant}",
                target,
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
    }

    internal static short FloatToPcm16(float sample)
    {
        if (float.IsNaN(sample)) return 0;
        if (sample <= -1.0f) return short.MinValue;
        if (sample >= 1.0f) return short.MaxValue;

        float scale = sample < 0.0f ? 32768.0f : short.MaxValue;
        return (short)MathF.Round(sample * scale);
    }

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
