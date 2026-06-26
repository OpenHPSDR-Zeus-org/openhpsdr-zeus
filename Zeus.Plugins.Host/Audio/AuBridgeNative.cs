using System.Runtime.InteropServices;

namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// Production implementation of <see cref="IVstBridgeNative"/> for macOS
/// Audio Units (AUv2): a thin P/Invoke façade over the C ABI in
/// <c>native/zeus-au-bridge/include/zau.h</c>. It is a SECOND backend
/// behind the same managed seam as <see cref="VstBridgeNative"/> — the
/// VST3 bridge is untouched. <see cref="VstHostAudioPlugin"/> selects this
/// implementation when a manifest's <c>audio.format</c> is <c>"au"</c>.
///
/// The native library ships under <c>runtimes/osx-{x64,arm64}/native</c>
/// only; on Windows/Linux <see cref="AuBridgeNativeLoader"/> finds nothing
/// and every call degrades to a clean passthrough status, mirroring the
/// VST3 bridge's graceful-degrade contract.
///
/// Method mapping onto <see cref="IVstBridgeNative"/>:
/// <list type="bullet">
///   <item><see cref="LoadVst3"/> → <c>zau_load</c>; the <c>path</c>
///   argument carries the AU identity string
///   <c>"type:subtype:manufacturer"</c> (four-char codes), e.g.
///   <c>"aufx:lpas:appl"</c> for Apple's AULowpass.</item>
///   <item>Editor methods host the AU's native GUI (ABI v2): <c>EditorOpen</c>
///   opens the vendor Cocoa view (or an AUGenericView fallback) in a
///   bridge-owned NSWindow on the main thread, <c>EditorClose</c> tears it
///   down, and <c>EditorIsOpen</c> reports the live window state.</item>
/// </list>
/// </summary>
public sealed partial class AuBridgeNative : IVstBridgeNative
{
    /// <summary>Native library name. Resolves to libzeus-au-bridge.dylib on
    /// macOS via <see cref="AuBridgeNativeLoader"/>; absent (and therefore a
    /// no-op) on other platforms.</summary>
    public const string LibraryName = "zeus-au-bridge";

    static AuBridgeNative()
    {
        AuBridgeNativeLoader.EnsureResolverRegistered();
    }

    public AuBridgeNative()
    {
        AuBridgeNativeLoader.EnsureResolverRegistered();
    }

    public int Init(int abi) => zau_init(AuBridgeAbi.Current);

    /// <summary>Load an Audio Unit. <paramref name="path"/> carries the
    /// <c>type:subtype:manufacturer</c> AU identity string, not a file
    /// path — the AU is resolved from the OS AudioComponent registry.</summary>
    public int LoadVst3(string path, int channels, int sampleRate, int blockSize, out nint handle)
        => zau_load(path, channels, sampleRate, blockSize, out handle);

    public unsafe int Process(nint handle, ReadOnlySpan<float> input, Span<float> output, int frames)
    {
        fixed (float* pIn = input)
        fixed (float* pOut = output)
        {
            return zau_process(handle, pIn, pOut, frames);
        }
    }

    public int SetParameter(nint handle, uint paramId, double normalized)
        => zau_set_param(handle, paramId, normalized);

    public int Unload(nint handle) => zau_unload(handle);

    public int Shutdown() => zau_shutdown();

    /// <summary>
    /// One enumerated Audio Unit effect: its load <see cref="Id"/>
    /// (type:subtype:manufacturer), display <see cref="Name"/>, and
    /// <see cref="Manufacturer"/>.
    /// </summary>
    public readonly record struct AuEffect(string Id, string Name, string Manufacturer);

    /// <summary>
    /// Enumerate installed AUv2 effect components from the OS AudioComponent
    /// registry via the native bridge. Returns an empty list when the bridge
    /// is unavailable (non-macOS, or dylib missing) — callers should still
    /// macOS-gate this for clarity, but it degrades safely either way.
    /// </summary>
    public IReadOnlyList<AuEffect> EnumerateEffects()
    {
        int needed;
        try
        {
            if (zau_enumerate_effects(null, 0, out needed) != VstBridgeStatus.Ok || needed <= 0)
                return Array.Empty<AuEffect>();
        }
        catch (DllNotFoundException) { return Array.Empty<AuEffect>(); }
        catch (EntryPointNotFoundException) { return Array.Empty<AuEffect>(); }

        var buffer = new byte[needed];
        int written;
        if (zau_enumerate_effects(buffer, buffer.Length, out written) != VstBridgeStatus.Ok)
            return Array.Empty<AuEffect>();

        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Min(written, buffer.Length));
        var result = new List<AuEffect>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length >= 1 && fields[0].Length > 0)
            {
                result.Add(new AuEffect(
                    Id: fields[0],
                    Name: fields.Length > 1 ? fields[1] : fields[0],
                    Manufacturer: fields.Length > 2 ? fields[2] : string.Empty));
            }
        }
        return result;
    }

    // AUv2 Cocoa-view editor hosting (ABI v2). The native bridge opens the AU's
    // vendor GUI — or an AUGenericView parameter editor as a fallback — in a
    // bridge-owned NSWindow on the main thread. zau_status_t mirrors
    // VstBridgeStatus value-for-value (Ok=0 … NotImplemented=8 … Other=255), so
    // the native int is already a VstBridgeStatus and is returned verbatim. The
    // DllNotFound/EntryPointNotFound guards mirror EnumerateEffects: on
    // non-macOS (or an old/absent dylib) the library never resolves, so each
    // call degrades to NotImplemented (open/close) or false (is-open) instead of
    // throwing — the same graceful-degrade contract the VST3 bridge honours.
    public int EditorOpen(nint handle, string title)
    {
        try { return zau_editor_open(handle, title); }
        catch (DllNotFoundException) { return VstBridgeStatus.NotImplemented; }
        catch (EntryPointNotFoundException) { return VstBridgeStatus.NotImplemented; }
    }

    public int EditorClose(nint handle)
    {
        try { return zau_editor_close(handle); }
        catch (DllNotFoundException) { return VstBridgeStatus.NotImplemented; }
        catch (EntryPointNotFoundException) { return VstBridgeStatus.NotImplemented; }
    }

    public bool EditorIsOpen(nint handle)
    {
        try { return zau_editor_is_open(handle) != 0; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    // --- P/Invoke imports ---------------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "zau_init")]
    private static partial int zau_init(int abi);

    [LibraryImport(LibraryName, EntryPoint = "zau_load", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int zau_load(string identifier, int channels, int sampleRate, int blockSize, out nint handle);

    [LibraryImport(LibraryName, EntryPoint = "zau_process")]
    private static unsafe partial int zau_process(nint handle, float* input, float* output, int frames);

    [LibraryImport(LibraryName, EntryPoint = "zau_set_param")]
    private static partial int zau_set_param(nint handle, uint paramId, double normalized);

    [LibraryImport(LibraryName, EntryPoint = "zau_unload")]
    private static partial int zau_unload(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "zau_shutdown")]
    private static partial int zau_shutdown();

    [LibraryImport(LibraryName, EntryPoint = "zau_enumerate_effects")]
    private static partial int zau_enumerate_effects(byte[]? buffer, int bufferLen, out int outLen);

    [LibraryImport(LibraryName, EntryPoint = "zau_editor_open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int zau_editor_open(nint handle, string title);

    [LibraryImport(LibraryName, EntryPoint = "zau_editor_close")]
    private static partial int zau_editor_close(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "zau_editor_is_open")]
    private static partial int zau_editor_is_open(nint handle);
}

/// <summary>
/// Audio Unit bridge ABI version. Bumped in lockstep with breaking changes
/// to the C ABI in <c>zau.h</c>. Independent of <see cref="VstBridgeAbi"/>
/// (the VST3 bridge ABI) and of the .NET plugin SDK ABI.
/// </summary>
public static class AuBridgeAbi
{
    // v1: init / load / process / set_param / unload / shutdown.
    // v2: + editor hosting (zau_editor_open / zau_editor_close / zau_editor_is_open).
    public const int Current = 2;
}
