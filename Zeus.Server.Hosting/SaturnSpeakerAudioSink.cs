// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Christian Suarez (N9WAR), and contributors.
//
// Co-located Saturn/G2 appliance speaker sink. DeskHPSDR feeds the Saturn
// speaker/headphone path by sending compact UDP audio packets to p2app's
// speaker port; this sink gives Zeus the same low-latency appliance route
// without depending on Linux desktop audio devices.

using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Sends desktop-mode RX audio directly to the Saturn/G2 speaker UDP endpoint
/// when Zeus is running on the same Linux host as the radio.
/// </summary>
internal sealed class SaturnSpeakerAudioSink : IRxAudioSink, IHostedService, IDisposable
{
    private const uint FrameRateHz = 48_000;
    private const int SpeakerAudioPort = 1028;
    private const int PacketFrames = 64;
    private const int PacketBytes = 4 + PacketFrames * 2 * sizeof(short);
    private const int TargetRefreshMs = 1_000;

    private readonly RadioService _radio;
    private readonly ILogger<SaturnSpeakerAudioSink> _log;
    private readonly object _sync = new();
    private readonly byte[] _packet = new byte[PacketBytes];
    private readonly bool _linux = OperatingSystem.IsLinux();

    private Socket? _socket;
    private IPEndPoint? _target;
    private int _packetFrames;
    private uint _sequence;
    private long _nextRefreshMs;
    private long _droppedPackets;
    private bool _wasEligible;
    private ConnectionStatus _lastStatus = ConnectionStatus.Disconnected;
    private string? _lastEndpoint;

    public SaturnSpeakerAudioSink(RadioService radio, ILogger<SaturnSpeakerAudioSink> log)
    {
        _radio = radio;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_linux) return Task.CompletedTask;

        _radio.StateChanged += OnRadioStateChanged;
        lock (_sync)
        {
            RefreshTargetLocked(_radio.Snapshot(), force: true);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_linux) return Task.CompletedTask;

        _radio.StateChanged -= OnRadioStateChanged;
        lock (_sync)
        {
            CloseSocketLocked();
        }
        return Task.CompletedTask;
    }

    public void Publish(in AudioFrame frame)
    {
        if (!_linux) return;
        if (frame.Channels != 1 || frame.SampleRateHz != FrameRateHz) return;

        lock (_sync)
        {
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
        if (!_linux) return;

        lock (_sync)
        {
            RefreshTargetLocked(state, force: false);
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

        if (state.Status != ConnectionStatus.Connected
            || string.IsNullOrWhiteSpace(state.Endpoint)
            || !caps.HasAudioAmplifier
            || !RadioService.TryParseEndpoint(state.Endpoint, out var radioEndpoint)
            || !IsLocalAddress(radioEndpoint.Address))
        {
            if (_wasEligible)
            {
                _log.LogInformation("audio.saturn.speaker disabled");
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
                "audio.saturn.speaker enabled target={Target} board={Board} variant={Variant}",
                target,
                board,
                variant);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            CloseSocketLocked();
            _log.LogWarning(ex, "audio.saturn.speaker open failed target={Target}", target);
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
            _log.LogWarning(ex, "audio.saturn.speaker send failed dropped={DroppedPackets}", dropped);
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

    private static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.Equals(address)) return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_linux)
        {
            _radio.StateChanged -= OnRadioStateChanged;
        }

        lock (_sync)
        {
            CloseSocketLocked();
        }
    }
}
