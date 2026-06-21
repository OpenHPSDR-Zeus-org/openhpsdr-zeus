// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Desktop-mode TX mic capture: replaces the browser → WS MicPcm uplink with
// a direct miniaudio capture device feed into TxAudioIngest. The two paths
// share the existing TxAudioIngest entry point (OnMicPcmBytes) so the WDSP
// TXA chain, IQ ring, and protocol packers don't know which transport
// supplied the audio.
//
// Frame shape: f32le little-endian, 960 samples mono @ 48 kHz (20 ms
// blocks), matching the browser worklet's MicPcm payload exactly. The
// AudioWorklet on the SPA emits the same shape; this just produces it from
// the selected OS input device instead of getUserMedia.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Desktop-mode hosted service that opens the selected OS input device via
/// miniaudio and feeds 960-sample (20 ms @ 48 kHz mono) f32le blocks into
/// <see cref="TxAudioIngest.OnMicPcmBytes"/>. Service mode never registers
/// this service so the .dylib never loads from the server-only build.
///
/// <para>The mic device runs continuously once started; gating on MOX /
/// monitor state happens downstream in <see cref="TxAudioIngest"/>, exactly
/// as the existing browser path does. This keeps the capture warm so the
/// first key-up doesn't lose the first ~20 ms of audio waiting for the OS
/// to spin up the input device.</para>
///
/// <para>Downmix: when the OS hands us a stereo / multichannel input
/// (USB mics often default to stereo), we average across channels into a
/// mono sample. Resample: miniaudio's internal resampler converts to 48 kHz
/// if the device's native rate is something else (44.1 kHz common on Mac
/// laptops). Both happen behind the data callback so this code only deals
/// with 48 kHz mono frames.</para>
/// </summary>
internal sealed class NativeMicCapture : IHostedService, IDisposable
{
    private const int MicBlockSamples = 960;        // 20 ms @ 48 kHz, matches browser worklet
    private const int MicBlockBytes = MicBlockSamples * 4;

    // Peak-broadcast cadence: 10 Hz → one frame per ~4800 samples at 48 kHz.
    // The frontend MicMeter visual rate is ~20 Hz (50 ms tick in
    // use-mic-uplink.ts), so 10 Hz is comfortably under the Nyquist of what
    // the human eye can see on a meter bar; the operator perceives a
    // continuously animated level, not a stepped one. Halving this to 5 Hz
    // would feel sluggish on quick consonant transients; doubling to 20 Hz
    // adds wire churn for no visible gain. See report for the trade-off.
    private const int PeakWindowSamples = 4800;     // 100 ms @ 48 kHz

    private readonly TxAudioIngest _ingest;
    private readonly StreamingHub _hub;
    private readonly AudioPluginBridge _bridge;
    private readonly ILogger<NativeMicCapture> _log;
    private readonly AudioDeviceSettingsStore? _deviceSettings;

    private MiniAudioInput? _input;
    private readonly object _deviceSync = new();
    private string? _activeInputDeviceId;
    private readonly float[] _accum = new float[MicBlockSamples];
    private int _accumFill;
    // Pre-allocated payload buffer reused for every flush; OnMicPcmBytes
    // takes ReadOnlyMemory<byte> but doesn't retain it, so reuse is safe.
    private readonly byte[] _payload = new byte[MicBlockBytes];

    // Peak accumulator. Updated on the audio worker thread (miniaudio
    // callback) only — no cross-thread access — so no interlocked needed.
    private float _peakWindow;
    private int _peakWindowFill;

    private long _totalSamplesIn;
    private long _totalBlocksOut;
    private long _totalPeakFramesOut;

    // Suppression counter for plugin-preview faults so a buggy plugin
    // can't spam the log from the miniaudio worker thread. Matches the
    // 4-suppression pattern used at the WDSP TX seam.
    private int _previewErrLogged;

    public NativeMicCapture(
        TxAudioIngest ingest,
        StreamingHub hub,
        AudioPluginBridge bridge,
        ILogger<NativeMicCapture> log,
        AudioDeviceSettingsStore? deviceSettings = null)
    {
        _ingest = ingest;
        _hub = hub;
        _bridge = bridge;
        _log = log;
        _deviceSettings = deviceSettings;
    }

    public string? ConfiguredInputDeviceId => _deviceSettings?.Get().InputDeviceId;
    public string? ActiveInputDeviceId => _activeInputDeviceId;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_deviceSync)
        {
            if (_input != null) return Task.CompletedTask;
            OpenInputLocked(ConfiguredInputDeviceId);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_deviceSync)
        {
            try { _input?.Stop(); }
            catch (Exception ex) { _log.LogWarning(ex, "audio.native.tx stop threw"); }
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_deviceSync)
        {
            CloseInputLocked(dispose: true);
        }
    }

    public Task SetInputDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        string? normalized = NormalizeDeviceId(deviceId);
        _deviceSettings?.SetInputDeviceId(normalized);

        lock (_deviceSync)
        {
            CloseInputLocked(dispose: true);
            ResetAccumulation();
            OpenInputLocked(normalized);
        }

        return Task.CompletedTask;
    }

    private void OpenInputLocked(string? requestedDeviceId)
    {
        try
        {
            _input = CreateInput(requestedDeviceId);
            _input.Start();
            _activeInputDeviceId = NormalizeDeviceId(requestedDeviceId);
            _log.LogInformation(
                "audio.native.tx mic open device={Device} rate={Rate}Hz channels={Channels}",
                _activeInputDeviceId is null ? "default" : "selected",
                _input.SampleRate, _input.Channels);
            return;
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(requestedDeviceId))
        {
            _log.LogWarning(ex, "audio.native.tx selected mic open failed; falling back to default");
            CloseInputLocked(dispose: true);
        }
        catch (Exception ex)
        {
            // Don't kill the host; desktop mode without a working mic should
            // still RX. The operator may not even have a mic plugged in.
            _log.LogWarning(ex, "audio.native.tx mic open failed; TX uplink disabled");
            CloseInputLocked(dispose: true);
            return;
        }

        try
        {
            _input = CreateInput(null);
            _input.Start();
            _activeInputDeviceId = null;
            _log.LogInformation(
                "audio.native.tx mic open device=default rate={Rate}Hz channels={Channels}",
                _input.SampleRate, _input.Channels);
        }
        catch (Exception fallbackEx)
        {
            _log.LogWarning(fallbackEx, "audio.native.tx default mic fallback open failed; TX uplink disabled");
            CloseInputLocked(dispose: true);
        }
    }

    private MiniAudioInput CreateInput(string? deviceId) =>
        new(
            onFrames: OnCaptureData,
            onNotify: OnDeviceNotification,
            deviceIdHex: NormalizeDeviceId(deviceId),
            preferSampleRate: 48_000,
            preferChannels: 1,
            periodFrames: 480,
            periods: 2);

    private void CloseInputLocked(bool dispose)
    {
        try { _input?.Stop(); }
        catch (Exception ex) { _log.LogWarning(ex, "audio.native.tx stop threw"); }
        if (dispose)
        {
            try { _input?.Dispose(); }
            catch (Exception ex) { _log.LogWarning(ex, "audio.native.tx dispose threw"); }
        }
        _input = null;
        _activeInputDeviceId = null;
    }

    private void ResetAccumulation()
    {
        _accumFill = 0;
        _peakWindow = 0f;
        _peakWindowFill = 0;
    }

    private void OnCaptureData(ReadOnlySpan<float> input, uint frameCount, uint channels)
    {
        int frames = (int)frameCount;
        int ch = (int)channels;
        if (frames == 0 || ch == 0) return;

        // Downmix to mono on the fly. For ch==1 this is a straight copy
        // through the accumulator. Peak (max |sample|) is computed in the
        // same pass so a separate scan is avoided; we accumulate it across
        // miniaudio callbacks until PeakWindowSamples is reached, then
        // publish a MicPeakFrame and reset.
        int srcIdx = 0;
        float winPeak = _peakWindow;
        while (srcIdx < frames)
        {
            int needBeforeFlush = MicBlockSamples - _accumFill;
            int take = Math.Min(needBeforeFlush, frames - srcIdx);

            if (ch == 1)
            {
                for (int i = 0; i < take; i++)
                {
                    float sample = SanitizeCapturedSample(input[srcIdx + i]);
                    _accum[_accumFill + i] = sample;
                    float a = sample;
                    if (a < 0) a = -a;
                    if (a > winPeak) winPeak = a;
                }
            }
            else
            {
                // Average across channels for each frame in this batch.
                float invCh = 1.0f / ch;
                for (int i = 0; i < take; i++)
                {
                    float mono = DownmixCapturedFrame(input, (srcIdx + i) * ch, ch, invCh);
                    _accum[_accumFill + i] = mono;
                    float a = mono < 0 ? -mono : mono;
                    if (a > winPeak) winPeak = a;
                }
            }

            _accumFill += take;
            srcIdx += take;
            _totalSamplesIn += take;
            _peakWindowFill += take;

            // Publish a MicPeakFrame at the configured cadence (~10 Hz). The
            // window can span multiple miniaudio callbacks; FlushPeakWindow
            // resets the accumulator after emitting.
            if (_peakWindowFill >= PeakWindowSamples)
            {
                _peakWindow = winPeak;
                FlushPeakWindow();
                winPeak = 0f;
            }

            if (_accumFill >= MicBlockSamples)
            {
                // Pre-MOX plugin meter preview tap — runs the audio plugin
                // chain on the same 960-sample mono block we're about to
                // hand to TxAudioIngest so per-plugin IN / OUT / GR meters
                // animate from live mic regardless of MOX state. The bridge
                // short-circuits internally when MOX or TX monitor is on
                // (the WDSP TX path is the canonical chain runner in those
                // cases), or when no plugins are attached. Output samples
                // are discarded — TxAudioIngest still gets the unmodified
                // mic block via FlushBlock, so the on-air audio path is
                // bit-identical to the no-preview case.
                // Only run the host-mic plugin preview when Host is the active
                // source. When a radio jack is selected the host mic is off the
                // air, so its per-plugin IN / OUT / GR preview meters must not
                // animate from it either (same source-awareness as the level
                // meter above).
                if (_ingest.ActiveSource == MicBlockSource.Host)
                {
                    try
                    {
                        _bridge.ProcessLivePreview(
                            new ReadOnlySpan<float>(_accum, 0, MicBlockSamples),
                            sampleRate: 48_000);
                    }
                    catch (Exception ex)
                    {
                        if (++_previewErrLogged <= 4)
                            _log.LogWarning(ex,
                                "audio.native.tx plugin preview threw (suppressed after 4)");
                    }
                }

                FlushBlock();
                _accumFill = 0;
            }
        }
        _peakWindow = winPeak;
    }

    private void FlushPeakWindow()
    {
        // The mic meter must reflect the ACTIVE TX audio source, not always the
        // host mic. When the operator selects a radio jack (Radio Mic / Line In /
        // Balanced) the host mic is gated off the air in TxAudioIngest — and it
        // must drop off the METER too, otherwise the operator sees host-mic level
        // on a radio source with nothing connected (the meter would "lie"). So:
        //   Host source  → this device's host-mic window peak.
        //   Radio source → the radio-jack peak TxAudioIngest tracked, which is
        //                   silence (0) when no UDP-1026 audio is arriving.
        // Either way this is a steady ~10 Hz heartbeat, so a radio source with no
        // input falls to silence rather than freezing on the last host value.
        //
        // Convert linear peak (0..1) to dBFS via the shared converter on the
        // contract type. Flooring + clipping happens inside LinearToDbfs.
        // Timestamp the frame here so a client can detect mic-stream stalls.
        float peakLinear = _ingest.ActiveSource == MicBlockSource.Host
            ? _peakWindow
            : _ingest.ConsumeRadioPeakLinear();
        float peakDbfs = MicPeakFrame.LinearToDbfs(peakLinear);
        long tsUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            _hub.Broadcast(new MicPeakFrame(peakDbfs, tsUnixMs));
        }
        catch (Exception ex)
        {
            // Broadcast is best-effort. A throw here must NOT stall the
            // audio callback (the OS will start dropping input frames if
            // the callback runs long).
            _log.LogWarning(ex, "audio.native.tx mic peak broadcast threw");
        }
        _peakWindow = 0f;
        _peakWindowFill = 0;
        _totalPeakFramesOut++;
    }

    private void FlushBlock()
    {
        // Encode f32le. On x64 / arm64 .NET targets, the host float endianness
        // matches the wire format (little-endian), so MemoryMarshal.AsBytes
        // produces the wanted payload directly. BinaryPrimitives is the safe
        // alternative if we ever target a big-endian runtime, but Zeus targets
        // none today.
        var srcBytes = MemoryMarshal.AsBytes(new ReadOnlySpan<float>(_accum, 0, MicBlockSamples));
        srcBytes.CopyTo(_payload);

        try
        {
            _ingest.OnMicPcmBytesFromMic(new ReadOnlyMemory<byte>(_payload));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "audio.native.tx ingest threw on flush");
        }
        _totalBlocksOut++;
    }

    private void OnDeviceNotification(int kind)
    {
        string label = kind switch
        {
            1 => "started",
            2 => "stopped",
            3 => "rerouted (default device changed)",
            4 => "interruption_began",
            5 => "interruption_ended",
            6 => "unlocked",
            _ => $"kind={kind}",
        };
        _log.LogInformation("audio.native.tx event {Event}", label);
    }

    internal static float SanitizeCapturedSample(float sample)
        => DspPipelineService.SanitizeAudioSample(sample);

    private static string? NormalizeDeviceId(string? deviceId)
    {
        var trimmed = deviceId?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    internal static float DownmixCapturedFrame(
        ReadOnlySpan<float> interleavedSamples,
        int frameBaseIndex,
        int channels,
        float inverseChannels)
    {
        float sum = 0f;
        for (int c = 0; c < channels; c++)
            sum += SanitizeCapturedSample(interleavedSamples[frameBaseIndex + c]);
        return SanitizeCapturedSample(sum * inverseChannels);
    }
}
