namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// Seam between <see cref="VstHostAudioPlugin"/> and the native VST3
/// bridge. The production implementation P/Invokes
/// <c>libzeus-vst-bridge</c> (see <c>native/zeus-vst-bridge/</c>);
/// tests substitute a fake to avoid linking the native lib in CI.
///
/// All methods return a non-zero status on failure; 0 means OK. The
/// underlying C ABI lives in <c>native/zeus-vst-bridge/include/zvst.h</c>.
/// </summary>
public interface IVstBridgeNative
{
    /// <summary>Initialise the bridge. <paramref name="abi"/> MUST equal
    /// <see cref="VstBridgeAbi.Current"/>; the native lib refuses
    /// otherwise so a wire-format mismatch surfaces immediately.</summary>
    int Init(int abi);

    /// <summary>Load a VST3 file. On success, <paramref name="handle"/>
    /// is set to a non-zero opaque value; returns 0. Other status codes
    /// match <c>zvst_status_t</c>.</summary>
    int LoadVst3(string path, int channels, int sampleRate, int blockSize, out nint handle);

    /// <summary>Process one block. <paramref name="input"/> and
    /// <paramref name="output"/> are planar float32 buffers of length
    /// <c>channels * frames</c>.</summary>
    int Process(nint handle, ReadOnlySpan<float> input, Span<float> output, int frames);

    /// <summary>Set one normalized [0..1] parameter on the loaded plugin.</summary>
    int SetParameter(nint handle, uint paramId, double normalized);

    /// <summary>Release the loaded plugin. The handle is invalid afterwards.</summary>
    int Unload(nint handle);

    /// <summary>Shutdown the bridge — releases process-wide resources.</summary>
    int Shutdown();

    /// <summary>
    /// Open the plugin's native editor (its real GUI) in a bridge-owned
    /// OS window. <paramref name="title"/> is the window caption (plugin
    /// display name). Idempotent — a second open while one is up returns
    /// OK. Windows-only at present; returns <see cref="VstBridgeStatus.NotImplemented"/>
    /// on other platforms.
    /// </summary>
    int EditorOpen(nint handle, string title);

    /// <summary>Close the plugin's editor window if open. Idempotent;
    /// blocks until the editor UI thread has torn down.</summary>
    int EditorClose(nint handle);

    /// <summary>True if the plugin's editor window is currently open.</summary>
    bool EditorIsOpen(nint handle);

    /// <summary>
    /// Enumerate the audio-effect classes in a VST3 file/bundle WITHOUT
    /// activating them — the in-process plugin scanner. On <see cref="VstBridgeStatus.Ok"/>,
    /// <paramref name="json"/> is a UTF-8 JSON array of
    /// <c>{uid,name,category,vendor}</c> objects (empty array when the file
    /// has no audio-effect class). Other status codes match <c>zvst_status_t</c>.
    /// Defaulted so existing test fakes need no change; the production bridge
    /// overrides it via <c>zvst_describe</c>.
    /// </summary>
    int Describe(string path, out string json)
    {
        json = "[]";
        return VstBridgeStatus.NotImplemented;
    }
}

/// <summary>
/// One audio-effect class enumerated from a VST3 file by
/// <see cref="IVstBridgeNative.Describe"/>. <see cref="Uid"/> is the class
/// TUID string — the identifier that selects this exact sub-plugin when a
/// single file (a "shell") hosts several.
/// </summary>
public sealed record VstPluginDescriptor(string Uid, string Name, string Category, string Vendor);

/// <summary>
/// Status codes returned by <see cref="IVstBridgeNative"/>. Mirror
/// <c>zvst_status_t</c> in <c>native/zeus-vst-bridge/include/zvst.h</c>.
/// </summary>
public static class VstBridgeStatus
{
    public const int Ok                  = 0;
    public const int AbiMismatch         = 1;
    public const int FileNotFound        = 2;
    public const int NotAVst3            = 3;
    public const int NoAudioEffectClass  = 4;
    public const int ActivateFailed      = 5;
    public const int InvalidHandle       = 6;
    public const int InvalidArguments    = 7;
    public const int NotImplemented      = 8;
    public const int Other               = 255;
}

/// <summary>
/// Native bridge ABI version. Bumped in lockstep with breaking changes
/// to the C ABI in <c>zvst.h</c>. Independent of the .NET plugin SDK
/// ABI in <see cref="Zeus.Plugins.Contracts.AbiVersion"/>.
/// </summary>
public static class VstBridgeAbi
{
    // v2: added the editor entry points (zvst_editor_open/close/is_open).
    public const int Current = 2;
}
