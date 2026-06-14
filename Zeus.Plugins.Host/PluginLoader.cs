using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;

namespace Zeus.Plugins.Host;

/// <summary>
/// Loads one plugin from a directory containing <c>plugin.json</c>.
/// Stateless: every call creates a fresh <see cref="AssemblyLoadContext"/>.
/// </summary>
public sealed class PluginLoader
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly ILogger<PluginLoader> _log;

    public PluginLoader(ILogger<PluginLoader> log) => _log = log;

    /// <summary>
    /// Parse, validate, and activate the plugin in <paramref name="pluginDir"/>.
    /// Throws <see cref="PluginLoadException"/> for any failure mode.
    /// </summary>
    public LoadedPlugin Load(string pluginDir)
    {
        var manifest = ReadManifest(pluginDir);

        // Local load: allow an absolute audio.vst3Path. Operator directory
        // scans reference VSTs in place at their install location so stub +
        // sidecar plugins keep their dependency DLLs. Downloaded plugins are
        // validated strictly at install time (PluginInstaller), so this does
        // not widen the registry attack surface.
        var errors = ManifestValidator.Validate(manifest, allowAbsoluteAudioPath: true);
        if (errors.Count > 0)
            throw new PluginLoadException(
                $"manifest invalid: {string.Join("; ", errors)}");

        if (!ManifestValidator.IsAbiCompatible(manifest, AbiVersion.Current, AbiVersion.SdkVersion))
            throw new PluginLoadException(
                $"plugin '{manifest.Id}' requires SDK abi={manifest.Sdk.Abi} minVersion={manifest.Sdk.MinVersion}; "
                + $"host is abi={AbiVersion.Current} version={AbiVersion.SdkVersion}");

        var asmPath = Path.Combine(pluginDir, manifest.Entrypoint.Assembly);
        if (!File.Exists(asmPath))
            throw new PluginLoadException($"entrypoint assembly not found: {asmPath}");

        var alc = new PluginLoadContext(manifest.Id, asmPath);
        Assembly asm;
        try
        {
            asm = alc.LoadFromAssemblyPath(asmPath);
        }
        catch (Exception ex)
        {
            alc.Unload();
            throw new PluginLoadException($"failed to load assembly '{manifest.Entrypoint.Assembly}': {ex.Message}", ex);
        }

        Type? pluginType = ResolvePluginType(asm, manifest);
        if (pluginType is null)
        {
            alc.Unload();
            throw new PluginLoadException(
                $"no public IZeusPlugin implementation found in {manifest.Entrypoint.Assembly}"
                + (manifest.Entrypoint.Type is { } t ? $" (sought type '{t}')" : ""));
        }

        IZeusPlugin instance;
        try
        {
            instance = (IZeusPlugin)Activator.CreateInstance(pluginType)!;
        }
        catch (Exception ex)
        {
            alc.Unload();
            throw new PluginLoadException(
                $"failed to instantiate plugin type '{pluginType.FullName}': {ex.Message}", ex);
        }

        _log.LogInformation(
            "Loaded plugin {Id} v{Version} from {Dir}",
            manifest.Id, manifest.Version, pluginDir);

        return new LoadedPlugin(manifest, instance, alc, pluginDir);
    }

    private static PluginManifest ReadManifest(string pluginDir)
    {
        var manifestPath = Path.Combine(pluginDir, "plugin.json");
        if (!File.Exists(manifestPath))
            throw new PluginLoadException($"plugin.json not found in {pluginDir}");

        PluginManifest? m;
        try
        {
            var json = File.ReadAllText(manifestPath);
            m = JsonSerializer.Deserialize<PluginManifest>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new PluginLoadException($"plugin.json parse error: {ex.Message}", ex);
        }

        return m ?? throw new PluginLoadException("plugin.json deserialised to null");
    }

    private static Type? ResolvePluginType(Assembly asm, PluginManifest manifest)
    {
        if (manifest.Entrypoint.Type is { Length: > 0 } typeName)
            return asm.GetType(typeName, throwOnError: false, ignoreCase: false);

        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }

        return types.FirstOrDefault(t =>
            t is { IsClass: true, IsAbstract: false, IsPublic: true }
            && typeof(IZeusPlugin).IsAssignableFrom(t));
    }
}

/// <summary>Result of a successful <see cref="PluginLoader.Load"/> call.</summary>
public sealed record LoadedPlugin(
    PluginManifest Manifest,
    IZeusPlugin Plugin,
    AssemblyLoadContext LoadContext,
    string PluginDir);

/// <summary>Failure mode for <see cref="PluginLoader.Load"/>.</summary>
public sealed class PluginLoadException : Exception
{
    public PluginLoadException(string message) : base(message) { }
    public PluginLoadException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Collectible ALC with private dependency resolution. Plugin assemblies
/// in the plugin's own directory take priority; deps the host already
/// has loaded (System.*, Zeus.Plugins.Contracts) fall through to the
/// default context so the plugin sees the same types the host sees.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginId, string mainAssemblyPath)
        : base(name: pluginId, isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Force contracts + ASP.NET / runtime types to come from the
        // default context so plugin-defined IZeusPlugin and host-side
        // IZeusPlugin are the same Type identity. Without this, the
        // cast `(IZeusPlugin)Activator.CreateInstance(...)` throws.
        if (assemblyName.Name is { } n &&
            (n.StartsWith("Zeus.Plugins.Contracts", StringComparison.Ordinal) ||
             n.StartsWith("Microsoft.", StringComparison.Ordinal) ||
             n.StartsWith("System.", StringComparison.Ordinal) ||
             n == "netstandard"))
        {
            return null; // delegate to default ALC
        }

        var asmPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return asmPath is null ? null : LoadFromAssemblyPath(asmPath);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
