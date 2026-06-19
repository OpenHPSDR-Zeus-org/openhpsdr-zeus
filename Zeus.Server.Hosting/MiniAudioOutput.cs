// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Brian Keating (EI6LF) and contributors.

using System.Runtime.InteropServices;

namespace Zeus.Server;

/// <summary>
/// Thin managed wrapper around a miniaudio playback device. Owns the GC
/// roots for the unmanaged callback delegates so the runtime doesn't collect
/// them while the native side still holds function pointers.
///
/// The data callback runs on miniaudio's audio worker thread; the caller's
/// <see cref="OnFrames"/> delegate must NOT block, allocate, or take locks
/// the rest of the host might hold. It receives an interleaved float32
/// output span sized to <c>frameCount * channels</c>.
///
/// Lifetime: construct with the desired prefs (sample rate, channels,
/// periodSizeInFrames, periods), then call <see cref="Start"/>. The negotiated
/// device parameters are exposed via <see cref="SampleRate"/> /
/// <see cref="Channels"/> after <see cref="Start"/>. Call <see cref="Dispose"/>
/// to tear down — destroys the native handle and releases the delegate roots.
/// </summary>
internal sealed class MiniAudioOutput : IDisposable
{
    /// <summary>Callback signature delivered to the consumer. Buffer is
    /// interleaved float32 sized <c>frameCount * channels</c>.</summary>
    internal delegate void FramesCallback(Span<float> output, uint frameCount, uint channels);

    private readonly FramesCallback _onFrames;
    private readonly Action<int>? _onNotify;
    private readonly MiniAudioInterop.PlaybackCallback _nativeDataCb;
    private readonly MiniAudioInterop.NotifyCallback _nativeNotifyCb;
    private IntPtr _handle;
    private bool _started;
    private bool _disposed;

    /// <summary>The actual sample rate miniaudio negotiated with the OS
    /// (may be the operator-requested rate, may be the device's native rate
    /// if the request was 0 or rejected). Valid after construction.</summary>
    public uint SampleRate { get; private set; }

    /// <summary>The actual channel count miniaudio negotiated with the OS.
    /// Typically 2 (stereo) on consumer hardware. Valid after construction.</summary>
    public uint Channels { get; private set; }

    /// <summary>True once <see cref="Start"/> has succeeded.</summary>
    public bool IsRunning => _started;

    /// <param name="onFrames">Callback that fills <c>frameCount * channels</c>
    /// interleaved float32 samples.</param>
    /// <param name="onNotify">Optional device-state notification callback
    /// (1=started, 2=stopped, 3=rerouted, 4=interruption_began,
    /// 5=interruption_ended, 6=unlocked).</param>
    /// <param name="preferSampleRate">Requested sample rate; 0 = device
    /// native. miniaudio's internal resampler handles rate conversion when
    /// the device cannot match exactly.</param>
    /// <param name="preferChannels">Requested channel count; 0 = device
    /// native. 2 is the safe default for consumer hardware.</param>
    /// <param name="periodFrames">periodSizeInFrames; default 480 ≈ 10 ms
    /// at 48 kHz.</param>
    /// <param name="periods">Number of periods; default 2 (≈20 ms total).</param>
    /// <exception cref="InvalidOperationException">If miniaudio refuses to
    /// open the default output device. Logged by the caller.</exception>
    public MiniAudioOutput(
        FramesCallback onFrames,
        Action<int>? onNotify = null,
        string? deviceIdHex = null,
        uint preferSampleRate = 48_000,
        uint preferChannels = 2,
        uint periodFrames = 480,
        uint periods = 2)
    {
        _onFrames = onFrames ?? throw new ArgumentNullException(nameof(onFrames));
        _onNotify = onNotify;

        MiniAudioInterop.EnsureResolverRegistered();

        // Hold strong references to the delegates so they survive across the
        // P/Invoke boundary for the lifetime of the handle. GetFunctionPointerForDelegate
        // does NOT root the managed delegate — the field reference does.
        _nativeDataCb = NativeDataCallback;
        _nativeNotifyCb = NativeNotifyCallback;

        _handle = CreateNativeHandle(
            preferSampleRate,
            preferChannels,
            periodFrames,
            periods,
            deviceIdHex,
            Marshal.GetFunctionPointerForDelegate(_nativeDataCb),
            Marshal.GetFunctionPointerForDelegate(_nativeNotifyCb),
            IntPtr.Zero);

        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                deviceIdHex is null
                    ? "miniaudio: failed to open default playback device (zeus_ma_output_create returned NULL)."
                    : "miniaudio: failed to open selected playback device (zeus_ma_output_create_for_device returned NULL).");
        }

        SampleRate = MiniAudioInterop.OutputSampleRate(_handle);
        Channels = MiniAudioInterop.OutputChannels(_handle);
    }

    private static IntPtr CreateNativeHandle(
        uint preferSampleRate,
        uint preferChannels,
        uint periodFrames,
        uint periods,
        string? deviceIdHex,
        IntPtr dataCallback,
        IntPtr notifyCallback,
        IntPtr user)
    {
        if (string.IsNullOrWhiteSpace(deviceIdHex))
        {
            return MiniAudioInterop.OutputCreate(
                preferSampleRate,
                preferChannels,
                periodFrames,
                periods,
                dataCallback,
                notifyCallback,
                user);
        }

        IntPtr deviceIdPtr = Marshal.StringToCoTaskMemUTF8(deviceIdHex);
        try
        {
            return MiniAudioInterop.OutputCreateForDevice(
                preferSampleRate,
                preferChannels,
                periodFrames,
                periods,
                deviceIdPtr,
                dataCallback,
                notifyCallback,
                user);
        }
        finally
        {
            Marshal.FreeCoTaskMem(deviceIdPtr);
        }
    }

    /// <summary>Start the device. Idempotent.</summary>
    /// <exception cref="InvalidOperationException">If miniaudio refuses to
    /// start the device (e.g. device went away between create and start).</exception>
    public void Start()
    {
        if (_started || _handle == IntPtr.Zero) return;
        int rc = MiniAudioInterop.OutputStart(_handle);
        if (rc != 0)
        {
            throw new InvalidOperationException("miniaudio: failed to start playback device.");
        }
        _started = true;
    }

    /// <summary>Stop the device (idempotent). The handle remains valid for
    /// a subsequent <see cref="Start"/> — use <see cref="Dispose"/> to
    /// release.</summary>
    public void Stop()
    {
        if (!_started || _handle == IntPtr.Zero) return;
        MiniAudioInterop.OutputStop(_handle);
        _started = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            // ma_device_uninit() (inside zeus_ma_output_destroy) is documented
            // to block until the audio worker exits, so by the time we return
            // here the native side will no longer call back into our managed
            // delegates. Safe to drop the delegate roots after destroy.
            MiniAudioInterop.OutputDestroy(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private unsafe void NativeDataCallback(IntPtr user, IntPtr outBuffer, uint frameCount, uint channels)
    {
        if (outBuffer == IntPtr.Zero || frameCount == 0 || channels == 0) return;
        int total = checked((int)(frameCount * channels));
        var span = new Span<float>((void*)outBuffer, total);
        try
        {
            _onFrames(span, frameCount, channels);
        }
        catch
        {
            // Never let an exception cross the FFI boundary. Fill with silence
            // so the operator hears a glitch instead of a hard crash.
            span.Clear();
        }
    }

    private void NativeNotifyCallback(IntPtr user, int kind)
    {
        try { _onNotify?.Invoke(kind); } catch { /* swallow */ }
    }
}
