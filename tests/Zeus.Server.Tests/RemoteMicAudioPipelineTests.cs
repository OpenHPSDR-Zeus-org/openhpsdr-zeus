using System.Buffers.Binary;
using Concentus;
using Concentus.Enums;
using Zeus.Server.Hosting.Remote;

namespace Zeus.Server.Tests;

/// <summary>
/// The remote voice path's decode core: Opus packets in → 960-sample, 20 ms,
/// 48 kHz mono f32le blocks out (exactly what TxAudioIngest consumes). Round-trips
/// real Opus through Concentus so the test exercises the actual decoder, not a
/// stub. The SIPSorcery RTP receive + injection wiring is bench-verified live.
/// </summary>
public sealed class RemoteMicAudioPipelineTests
{
    private const int Fs = 48000;
    private const int Frame = 960; // 20 ms

    private static byte[] EncodeTone(IOpusEncoder enc, int frameIndex)
    {
        var pcm = new short[Frame];
        for (int i = 0; i < Frame; i++)
        {
            int n = frameIndex * Frame + i;
            pcm[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * n / Fs));
        }
        var outBuf = new byte[4000];
        int len = enc.Encode(pcm, Frame, outBuf, outBuf.Length);
        return outBuf[..len];
    }

    [Fact]
    public void DecodesOpusFrames_EmitsOne960SampleF32leBlockEach()
    {
        var enc = OpusCodecFactory.CreateEncoder(Fs, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        var blocks = new List<byte[]>();
        var pipe = new RemoteMicAudioPipeline(b => blocks.Add(b.ToArray()));

        // Three 20 ms frames → three full ingest blocks (encoder warm-up settles
        // by the last, so the energy assertion is stable).
        for (int f = 0; f < 3; f++)
            pipe.Decode(EncodeTone(enc, f));

        Assert.Equal(3, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(Frame * 4, b.Length));

        // The last decoded block must carry the tone (Opus is lossy but preserves
        // energy) — proves the short→f32le conversion and re-block are correct.
        float peak = 0f;
        var last = blocks[^1];
        for (int i = 0; i < Frame; i++)
        {
            float s = BinaryPrimitives.ReadSingleLittleEndian(last.AsSpan(i * 4, 4));
            peak = Math.Max(peak, Math.Abs(s));
        }
        Assert.True(peak > 0.05f, $"expected non-silent decode, peak={peak}");
        // f32le range is normalised to [-1, 1].
        Assert.True(peak <= 1.0f, $"sample out of range, peak={peak}");
    }

    [Fact]
    public void EmptyPayload_EmitsNothing()
    {
        var blocks = new List<byte[]>();
        var pipe = new RemoteMicAudioPipeline(b => blocks.Add(b.ToArray()));
        pipe.Decode(ReadOnlySpan<byte>.Empty);
        Assert.Empty(blocks);
    }

    [Fact]
    public void DecodeLost_ConcealsWithoutThrowing_AndEmitsABlock()
    {
        var enc = OpusCodecFactory.CreateEncoder(Fs, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        var blocks = new List<byte[]>();
        var pipe = new RemoteMicAudioPipeline(b => blocks.Add(b.ToArray()));

        // Prime decoder state, then conceal a lost packet (PLC).
        pipe.Decode(EncodeTone(enc, 0));
        pipe.DecodeLost();

        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(Frame * 4, b.Length));
    }
}
