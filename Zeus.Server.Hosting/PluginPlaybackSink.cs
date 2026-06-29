// SPDX-License-Identifier: GPL-2.0-or-later
//
// PluginPlaybackSink — host implementation of IAudioPlaybackSink. Lets a
// plugin (e.g. the Recorder) play a clip locally (mixed into RX audio via the
// preview sink) or inject it into the TX chain to go on the air. Mirrors how
// the Recorder plugin plays back: on-air goes through
// TxAudioIngest.OnMicPcmBytesFromWav (processed by the normal TX chain) and
// only reaches the air under operator MOX; local goes through the preview
// sink. This wrapper NEVER keys the radio.

using System.Buffers;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts.Audio;

namespace Zeus.Server;

internal sealed class PluginPlaybackSink : IAudioPlaybackSink
{
    private readonly TxAudioIngest _txIngest;
    private readonly DspPipelineService _pipeline;
    private readonly TxService _tx;
    private readonly ILogger<PluginPlaybackSink> _log;
    private readonly TxMicBlockResampler _airResampler;

    public PluginPlaybackSink(
        TxAudioIngest txIngest,
        DspPipelineService pipeline,
        TxService tx,
        ILogger<PluginPlaybackSink> log)
    {
        _txIngest = txIngest;
        _pipeline = pipeline;
        _tx = tx;
        _log = log;
        _airResampler = new TxMicBlockResampler(EmitAir);
    }

    public bool IsMoxOn => _tx.IsMoxOn;

    // Local playback mixes straight into the RX audio bus (see
    // DspPipelineService.EnqueueMonitorAudio), which reaches every sink in both
    // browser and desktop modes — so no preview enable/restore is needed. The
    // token is a harmless no-op kept for contract symmetry.
    public IDisposable BeginLocalMonitor() => NoopSession.Instance;

    public bool PlayLocal(ReadOnlySpan<float> samples, int sampleRate)
        => _pipeline.EnqueueMonitorAudio(samples);

    public long LocalMonitorBacklog => _pipeline.MonitorBacklog;

    public void PlayOnAir(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (!TxMicBlockResampler.IsSupportedInputSampleRate(sampleRate))
        {
            _log.LogDebug("plugin.playback.air dropped unsupported sampleRate={Rate}", sampleRate);
            return;
        }

        _airResampler.Accept(samples, sampleRate);
    }

    private void EmitAir(ReadOnlySpan<float> block)
    {
        // f32le payload — copy the floats into a byte buffer the ingest owns
        // for the synchronous call.
        var bytes = ArrayPool<byte>.Shared.Rent(TxMicBlockResampler.OutputBlockBytes);
        try
        {
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(block).CopyTo(bytes);
            _txIngest.OnMicPcmBytesFromWav(new ReadOnlyMemory<byte>(bytes, 0, TxMicBlockResampler.OutputBlockBytes));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private sealed class NoopSession : IDisposable
    {
        public static readonly NoopSession Instance = new();
        public void Dispose() { }
    }
}
