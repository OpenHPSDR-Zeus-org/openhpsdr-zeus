using System.Runtime.InteropServices;

namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// Production implementation of <see cref="IVstBridgeNative"/>: thin
/// P/Invoke façade over the C ABI in
/// <c>native/zeus-vst-bridge/include/zvst.h</c>.
///
/// The native library MUST be on the runtime load path (LD_LIBRARY_PATH
/// / DYLD_LIBRARY_PATH / PATH) — Zeus installs it next to the host
/// executable. Tests that don't care about the native side substitute
/// a fake <see cref="IVstBridgeNative"/>.
/// </summary>
public sealed partial class VstBridgeNative : IVstBridgeNative
{
    /// <summary>Native library name. Resolves to libzeus-vst-bridge.dylib
    /// (macOS), libzeus-vst-bridge.so (Linux), zeus-vst-bridge.dll
    /// (Windows) via .NET's standard library-resolution rules.</summary>
    public const string LibraryName = "zeus-vst-bridge";

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
}
