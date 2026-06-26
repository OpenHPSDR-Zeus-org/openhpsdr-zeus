using System.Reflection;
using System.Runtime.InteropServices;

namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// Single owner of the <see cref="NativeLibrary.SetDllImportResolver"/> hook
/// for the <c>Zeus.Plugins.Host</c> assembly.
///
/// The runtime permits exactly ONE DllImport resolver per assembly — a second
/// <see cref="NativeLibrary.SetDllImportResolver"/> call on the same assembly
/// throws <see cref="InvalidOperationException"/>. The VST3 bridge
/// (<see cref="VstBridgeNativeLoader"/>) and the macOS Audio Unit bridge
/// (<see cref="AuBridgeNativeLoader"/>) both ship native libraries under
/// <c>runtimes/&lt;rid&gt;/native</c> in this same assembly, so they cannot
/// each register their own resolver. This registrar installs ONE resolver
/// that dispatches by library name to whichever per-library resolvers have
/// been registered, so adding a second native bridge never collides with the
/// first.
/// </summary>
internal static class NativeBridgeResolver
{
    private static readonly object Gate = new();
    private static bool _installed;
    private static readonly List<DllImportResolver> Resolvers = new();

    /// <summary>
    /// Register a per-library resolver and ensure the single assembly-level
    /// resolver is installed. Idempotent per <paramref name="resolver"/>
    /// instance: registering the same delegate twice is a no-op.
    /// </summary>
    internal static void Register(DllImportResolver resolver)
    {
        lock (Gate)
        {
            if (!Resolvers.Contains(resolver))
                Resolvers.Add(resolver);

            if (_installed) return;
            NativeLibrary.SetDllImportResolver(typeof(NativeBridgeResolver).Assembly, Dispatch);
            _installed = true;
        }
    }

    private static IntPtr Dispatch(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        DllImportResolver[] snapshot;
        lock (Gate) snapshot = Resolvers.ToArray();

        foreach (var resolver in snapshot)
        {
            var handle = resolver(libraryName, assembly, searchPath);
            if (handle != IntPtr.Zero) return handle;
        }
        return IntPtr.Zero;
    }
}
