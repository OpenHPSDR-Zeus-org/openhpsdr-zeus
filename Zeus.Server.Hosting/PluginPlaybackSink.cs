// SPDX-License-Identifier: GPL-2.0-or-later
//
// PluginPlaybackSink — host implementation of IAudioPlaybackSink. Lets a
// plugin (e.g. the Recorder) play a clip locally (mixed into RX audio via the
// audition sink) or inject it into the TX chain to go on the air. Mirrors how
// the built-in WavRecorderService plays back: on-air goes through
// TxAudioIngest.OnMicPcmBytesFromWav (processed by the normal TX chain) and
// only reaches the air under operator MOX; local goes through the audition
// sink. This wrapper NEVER keys the radio.

using System.Buffers;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts.Audio;

namespace Zeus.Server;

internal sealed class PluginPlaybackSink : IAudioPlaybackSink
{
    // TxAudioIngest accepts mic blocks of exactly this size (960 samples f32le).
    private const int MicBlockSamples = 960;
    private const int MicBlockBytes = MicBlockSamples * 4;

    private readonly TxAudioIngest _txIngest;
    private readonly DspPipelineService _pipeline;
    private readonly TxService _tx;
    private readonly ILogger<PluginPlaybackSink> _log;

    // Carry-over buffer so PlayOnAir can re-block arbitrary spans into the
    // 960-sample TX mic blocks the ingest requires. Single-threaded use is
    // expected (the plugin's playback pump), so a plain field is fine.
    private readonly float[] _airCarry = new float[MicBlockSamples];
    private int _airCarryLen;

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
    }

    public bool IsMoxOn => _tx.IsMoxOn;

    // Local playback mixes straight into the RX audio bus (see
    // DspPipelineService.EnqueueMonitorAudio), which reaches every sink in both
    // browser and desktop modes — so no audition enable/restore is needed. The
    // token is a harmless no-op kept for contract symmetry.
    public IDisposable BeginLocalMonitor() => NoopSession.Instance;

    public bool PlayLocal(ReadOnlySpan<float> samples, int sampleRate)
        => _pipeline.EnqueueMonitorAudio(samples);

    public long LocalMonitorBacklog => _pipeline.MonitorBacklog;

    public void PlayOnAir(ReadOnlySpan<float> samples, int sampleRate)
    {
        // Re-block into 960-sample TX mic frames, carrying any remainder to the
        // next call. f32le bytes == float on little-endian (all supported RIDs).
        int pos = 0;

        // Top up an in-flight carry block first.
        if (_airCarryLen > 0)
        {
            int need = MicBlockSamples - _airCarryLen;
            int take = Math.Min(need, samples.Length);
            samples.Slice(0, take).CopyTo(_airCarry.AsSpan(_airCarryLen, take));
            _airCarryLen += take;
            pos += take;
            if (_airCarryLen == MicBlockSamples)
            {
                EmitAir(_airCarry.AsSpan(0, MicBlockSamples));
                _airCarryLen = 0;
            }
        }

        while (samples.Length - pos >= MicBlockSamples)
        {
            EmitAir(samples.Slice(pos, MicBlockSamples));
            pos += MicBlockSamples;
        }

        int rem = samples.Length - pos;
        if (rem > 0)
        {
            samples.Slice(pos, rem).CopyTo(_airCarry.AsSpan(0, rem));
            _airCarryLen = rem;
        }
    }

    private void EmitAir(ReadOnlySpan<float> block)
    {
        // f32le payload — copy the floats into a byte buffer the ingest owns
        // for the synchronous call.
        var bytes = ArrayPool<byte>.Shared.Rent(MicBlockBytes);
        try
        {
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(block).CopyTo(bytes);
            _txIngest.OnMicPcmBytesFromWav(new ReadOnlyMemory<byte>(bytes, 0, MicBlockBytes));
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
