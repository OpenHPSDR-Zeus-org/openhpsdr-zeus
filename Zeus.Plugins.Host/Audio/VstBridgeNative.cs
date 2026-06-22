using System.Runtime.InteropServices;

namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// Production implementation of <see cref="IVstBridgeNative"/>: thin
/// P/Invoke façade over the C ABI in
/// <c>native/zeus-vst-bridge/include/zvst.h</c>.
///
/// The native library is shipped under <c>runtimes/&lt;rid&gt;/native</c>
/// in publish and installer layouts. <see cref="VstBridgeNativeLoader"/>
/// registers the resolver that probes that RID-specific path before the
/// OS fallback path. Tests that don't care about the native side
/// substitute a fake <see cref="IVstBridgeNative"/>.
/// </summary>
public sealed partial class VstBridgeNative : IVstBridgeNative
{
    /// <summary>Native library name. Resolves to libzeus-vst-bridge.dylib
    /// (macOS), libzeus-vst-bridge.so (Linux), zeus-vst-bridge.dll
    /// (Windows) via .NET's standard library-resolution rules.</summary>
    public const string LibraryName = "zeus-vst-bridge";

    static VstBridgeNative()
    {
        VstBridgeNativeLoader.EnsureResolverRegistered();
    }

    public VstBridgeNative()
    {
        VstBridgeNativeLoader.EnsureResolverRegistered();
    }

    public int Init(int abi) => zvst_init(abi);

    public int LoadVst3(string path, int channels, int sampleRate, int blockSize, out nint handle)
        => zvst_load_vst3(path, channels, sampleRate, blockSize, out handle);

    public unsafe int Process(nint handle, ReadOnlySpan<float> input, Span<float> output, int frames)
    {
        fixed (float* pIn = input)
        fixed (float* pOut = output)
        {
            return zvst_process(handle, pIn, pOut, frames);
        }
    }

    public int SetParameter(nint handle, uint paramId, double normalized)
        => zvst_set_param(handle, paramId, normalized);

    public int Unload(nint handle) => zvst_unload(handle);

    public int Shutdown() => zvst_shutdown();

    public int EditorOpen(nint handle, string title) => zvst_editor_open(handle, title);

    public int EditorClose(nint handle) => zvst_editor_close(handle);

    public bool EditorIsOpen(nint handle) => zvst_editor_is_open(handle) != 0;

    public int Describe(string path, out string json)
    {
        // 64 KiB holds a large shell file's worth of plugin metadata; the
        // native side reports the full length so a rare overflow grows once.
        var buf = new byte[64 * 1024];
        int status = zvst_describe(path, buf, buf.Length, out int len);
        if (status != VstBridgeStatus.Ok) { json = "[]"; return status; }
        if (len > buf.Length)
        {
            buf = new byte[len + 1];
            status = zvst_describe(path, buf, buf.Length, out len);
            if (status != VstBridgeStatus.Ok) { json = "[]"; return status; }
        }
        int n = Math.Min(len, buf.Length);
        json = System.Text.Encoding.UTF8.GetString(buf, 0, n);
        return VstBridgeStatus.Ok;
    }

    /// <summary>
    /// Convenience scan: <see cref="Describe"/> + JSON parse. Returns an empty
    /// list on any failure (unloadable file, non-VST3, malformed JSON) so a
    /// directory walk can skip a bad file without throwing.
    /// </summary>
    public static IReadOnlyList<VstPluginDescriptor> Scan(IVstBridgeNative bridge, string path)
    {
        try
        {
            if (bridge.Describe(path, out var json) != VstBridgeStatus.Ok) return [];
            return System.Text.Json.JsonSerializer
                       .Deserialize<List<VstPluginDescriptor>>(json, ScanJsonOpts) ?? [];
        }
        catch
        {
            return [];
        }
    }

    // JSON from the native scanner uses lower-case keys (uid/name/category/
    // vendor); match them to the PascalCase record positionally, case-insensitive.
    private static readonly System.Text.Json.JsonSerializerOptions ScanJsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    // --- P/Invoke imports ---------------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "zvst_init")]
    private static partial int zvst_init(int abi);

    [LibraryImport(LibraryName, EntryPoint = "zvst_load_vst3", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int zvst_load_vst3(string path, int channels, int sampleRate, int blockSize, out nint handle);

    [LibraryImport(LibraryName, EntryPoint = "zvst_process")]
    private static unsafe partial int zvst_process(nint handle, float* input, float* output, int frames);

    [LibraryImport(LibraryName, EntryPoint = "zvst_set_param")]
    private static partial int zvst_set_param(nint handle, uint paramId, double normalized);

    [LibraryImport(LibraryName, EntryPoint = "zvst_unload")]
    private static partial int zvst_unload(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "zvst_shutdown")]
    private static partial int zvst_shutdown();

    [LibraryImport(LibraryName, EntryPoint = "zvst_editor_open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int zvst_editor_open(nint handle, string title);

    [LibraryImport(LibraryName, EntryPoint = "zvst_editor_close")]
    private static partial int zvst_editor_close(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "zvst_editor_is_open")]
    private static partial int zvst_editor_is_open(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "zvst_describe", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int zvst_describe(string path, [Out] byte[] outJson, int outCap, out int outLen);
}
