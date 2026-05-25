// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Brian Keating (EI6LF) and contributors.

using System.Runtime.InteropServices;

namespace Zeus.Server;

/// <summary>
/// Thin managed wrapper around a miniaudio capture device. Mirrors
/// <see cref="MiniAudioOutput"/> in shape — owns the GC roots for the
/// unmanaged callback delegates so they survive across the FFI boundary.
///
/// The data callback runs on miniaudio's audio worker thread. The buffer is
/// interleaved float32, sized <c>frameCount * channels</c>. The buffer is
/// only valid for the duration of the callback — copy out, don't hold a
/// reference.
/// </summary>
internal sealed class MiniAudioInput : IDisposable
{
    internal delegate void FramesCallback(ReadOnlySpan<float> input, uint frameCount, uint channels);

    private readonly FramesCallback _onFrames;
    private readonly Action<int>? _onNotify;
    private readonly MiniAudioInterop.CaptureCallback _nativeDataCb;
    private readonly MiniAudioInterop.NotifyCallback _nativeNotifyCb;
    private IntPtr _handle;
    private bool _started;
    private bool _disposed;

    public uint SampleRate { get; private set; }
    public uint Channels { get; private set; }

    /// <summary>The miniaudio backend that actually opened the device
    /// ("WASAPI" / "DirectSound" / "Core Audio" / ...). See
    /// <see cref="MiniAudioOutput.BackendName"/> for why this matters.</summary>
    public string BackendName { get; private set; } = "unknown";

    /// <summary>Negotiated internal period size in frames.</summary>
    public uint BufferFrames { get; private set; }

    /// <summary>Negotiated internal period count.</summary>
    public uint Periods { get; private set; }

    public bool IsRunning => _started;

    public MiniAudioInput(
        FramesCallback onFrames,
        Action<int>? onNotify = null,
        uint preferSampleRate = 48_000,
        uint preferChannels = 1,
        uint periodFrames = 480,
        uint periods = 2)
    {
        _onFrames = onFrames ?? throw new ArgumentNullException(nameof(onFrames));
        _onNotify = onNotify;

        MiniAudioInterop.EnsureResolverRegistered();

        _nativeDataCb = NativeDataCallback;
        _nativeNotifyCb = NativeNotifyCallback;

        _handle = MiniAudioInterop.InputCreate(
            preferSampleRate,
            preferChannels,
            periodFrames,
            periods,
            Marshal.GetFunctionPointerForDelegate(_nativeDataCb),
            Marshal.GetFunctionPointerForDelegate(_nativeNotifyCb),
            IntPtr.Zero);

        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "miniaudio: failed to open default capture device (zeus_ma_input_create returned NULL).");
        }

        SampleRate = MiniAudioInterop.InputSampleRate(_handle);
        Channels = MiniAudioInterop.InputChannels(_handle);
        BackendName = MiniAudioInterop.InputBackendName(_handle);
        BufferFrames = MiniAudioInterop.InputBufferFrames(_handle);
        Periods = MiniAudioInterop.InputPeriods(_handle);
    }

    public void Start()
    {
        if (_started || _handle == IntPtr.Zero) return;
        int rc = MiniAudioInterop.InputStart(_handle);
        if (rc != 0)
        {
            throw new InvalidOperationException("miniaudio: failed to start capture device.");
        }
        _started = true;
    }

    public void Stop()
    {
        if (!_started || _handle == IntPtr.Zero) return;
        MiniAudioInterop.InputStop(_handle);
        _started = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            MiniAudioInterop.InputDestroy(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private unsafe void NativeDataCallback(IntPtr user, IntPtr inBuffer, uint frameCount, uint channels)
    {
        if (inBuffer == IntPtr.Zero || frameCount == 0 || channels == 0) return;
        int total = checked((int)(frameCount * channels));
        var span = new ReadOnlySpan<float>((void*)inBuffer, total);
        try
        {
            _onFrames(span, frameCount, channels);
        }
        catch
        {
            // Swallow at the FFI boundary; the host-level service that owns
            // this MiniAudioInput should be the one to surface errors.
        }
    }

    private void NativeNotifyCallback(IntPtr user, int kind)
    {
        try { _onNotify?.Invoke(kind); } catch { /* swallow */ }
    }
}
