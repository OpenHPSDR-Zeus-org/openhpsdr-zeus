namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// Wire layout for the Zeus ↔ VSTHost engine shared-memory audio plane.
/// Mirrors <c>ZeusBridgeHeader</c> in the engine (KlayaR/VSTHost
/// <c>engine/src/ZeusAudioBridge.h</c>). Source of truth:
/// <c>docs/designs/vst-engine-bridge-protocol.md</c> (protocol v1).
///
/// Layout (little-endian): a 64-byte header, then an input region and an
/// output region, each <c>maxFrames * channels * 4</c> bytes rounded up to 64.
/// Samples are float32, interleaved by frame.
/// </summary>
internal static class VstEngineProtocol
{
    public const uint Magic = 0x5A565342;   // 'ZVSB'
    public const uint Version = 1;
    public const int HeaderBytes = 64;

    // Header field offsets.
    public const int OffMagic          = 0;   // u32
    public const int OffProtocol       = 4;   // u32
    public const int OffMaxFrames      = 8;   // u32
    public const int OffChannels       = 12;  // u32
    public const int OffRate           = 16;  // u32
    public const int OffFramesThisBlock = 20; // u32 (Zeus writes each block)
    public const int OffInSeq          = 24;  // u64 (Zeus increments before Set(.in))
    public const int OffOutSeq         = 32;  // u64 (engine sets = inSeq processed)
    public const int OffEngineState    = 40;  // u32 (engine: 0=init 1=running 2=draining)
    public const int OffFlags          = 44;  // u32 (engine: bit0 = bypassed/empty)

    public static int AlignUp64(int v) => (v + 63) & ~63;

    public static int RegionBytes(int maxFrames, int channels) =>
        AlignUp64(maxFrames * channels * sizeof(float));

    public static long TotalBytes(int maxFrames, int channels) =>
        HeaderBytes + 2L * RegionBytes(maxFrames, channels);

    public static string InEventName(string shm)  => shm + ".in";
    public static string OutEventName(string shm) => shm + ".out";
}
