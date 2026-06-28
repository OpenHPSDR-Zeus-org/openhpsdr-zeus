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
/// </summary>
internal sealed class SaturnSpeakerAudioSink : IRxAudioSink, IHostedService, IDisposable
{
    private const uint FrameRateHz = 48_000;
    private const int SpeakerAudioPort = 1028;
    private const int PacketFrames = 64;
    private const int PacketBytes = 4 + PacketFrames * 2 * sizeof(short);
    private const int TargetRefreshMs = 1_000;

    private readonly RadioService _radio;
    private readonly RadioSpeakerSettingsStore _settings;
    private readonly ILogger<SaturnSpeakerAudioSink> _log;
    private readonly object _sync = new();
    private readonly byte[] _packet = new byte[PacketBytes];

    private Socket? _socket;
    private IPEndPoint? _target;
    private int _packetFrames;
    private uint _sequence;
    private long _nextRefreshMs;
    private long _droppedPackets;
    private bool _wasEligible;
    private ConnectionStatus _lastStatus = ConnectionStatus.Disconnected;
    private string? _lastEndpoint;

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
        lock (_sync)
        {
            RefreshTargetLocked(_radio.Snapshot(), force: true);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _radio.StateChanged -= OnRadioStateChanged;
        _settings.Changed -= OnSettingsChanged;
        lock (_sync)
        {
            CloseSocketLocked();
        }
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

        lock (_sync)
        {
            // Mirror the P1 sink's MOX mute: while transmitting, don't carry the
            // operator's TX-monitor / CW sidetone out to the radio's speaker jack.
            // Drop the buffered partial packet so RX resumes clean on unkey.
            if (_radio.IsMox)
            {
                _packetFrames = 0;
                return;
            }
            if (_socket is null) RefreshTargetIfDueLocked();
            if (_socket is null) return;

            foreach (float sample in frame.Samples.Span)
            {
                int offset = 4 + _packetFrames * 2 * sizeof(short);
                short pcm = FloatToPcm16(sample);
                BinaryPrimitives.WriteInt16BigEndian(_packet.AsSpan(offset, sizeof(short)), pcm);
                BinaryPrimitives.WriteInt16BigEndian(_packet.AsSpan(offset + sizeof(short), sizeof(short)), pcm);

                _packetFrames++;
                if (_packetFrames == PacketFrames)
                {
                    SendPacketLocked();
                }
            }
        }
    }

    private void OnRadioStateChanged(StateDto state)
    {
        lock (_sync)
        {
            RefreshTargetLocked(state, force: false);
        }
    }

    private void OnSettingsChanged()
    {
        lock (_sync)
        {
            if (_settings.Enabled)
            {
                // Operator just opted in — re-evaluate the target so audio starts
                // flowing on the next published frame rather than waiting up to a
                // second for the lazy refresh path inside Publish.
                RefreshTargetLocked(_radio.Snapshot(), force: true);
            }
            else
            {
                // Drop the buffered tail and close the socket so a later re-enable
                // starts clean rather than replaying stale samples or sequence
                // numbers that pre-date the operator's last toggle.
                _packetFrames = 0;
                CloseSocketLocked();
            }
        }
    }

    private void RefreshTargetIfDueLocked()
    {
        long now = Environment.TickCount64;
        if (now < _nextRefreshMs) return;
        _nextRefreshMs = now + TargetRefreshMs;

        RefreshTargetLocked(_radio.Snapshot(), force: true);
    }

    private void RefreshTargetLocked(StateDto state, bool force)
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
            CloseSocketLocked();
            return;
        }

        var target = new IPEndPoint(radioEndpoint.Address, SpeakerAudioPort);
        if (_target is not null && _target.Equals(target) && _socket is not null)
        {
            return;
        }

        CloseSocketLocked();

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
            CloseSocketLocked();
            _log.LogWarning(ex, "audio.radio.speaker.p2 open failed target={Target}", target);
        }
    }

    private void SendPacketLocked()
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
            CloseSocketLocked();
            _wasEligible = false;
            _log.LogWarning(ex, "audio.radio.speaker.p2 send failed dropped={DroppedPackets}", dropped);
        }
        finally
        {
            _packetFrames = 0;
        }
    }

    private void CloseSocketLocked()
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

    public void Dispose()
    {
        _radio.StateChanged -= OnRadioStateChanged;
        _settings.Changed -= OnSettingsChanged;

        lock (_sync)
        {
            CloseSocketLocked();
        }
    }
}
