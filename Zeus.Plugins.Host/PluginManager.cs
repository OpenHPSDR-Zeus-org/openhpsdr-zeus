using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;

namespace Zeus.Plugins.Host;

/// <summary>
/// Top-level orchestrator. Scans the plugin root on startup, loads each
/// plugin under try/catch + per-call timeout, exposes a snapshot of
/// activated plugins for the REST API + frontend.
/// </summary>
public sealed class PluginManager : IHostedService, IAsyncDisposable
{
    private readonly PluginLoader _loader;
    private readonly PluginSettingsStore _settings;
    private readonly IServiceProvider _services;
    private readonly ILoggerFactory _logFactory;
    private readonly ILogger<PluginManager> _log;
    private readonly PluginManagerOptions _options;

    private readonly ConcurrentDictionary<string, ActivatedPlugin> _active = new();
    private int _started; // 0 = pending, 1 = StartAsync ran

    public PluginManager(
        PluginLoader loader,
        PluginSettingsStore settings,
        IServiceProvider services,
        ILoggerFactory logFactory,
        PluginManagerOptions? options = null)
    {
        _loader = loader;
        _settings = settings;
        _services = services;
        _logFactory = logFactory;
        _log = logFactory.CreateLogger<PluginManager>();
        _options = options ?? new PluginManagerOptions();
    }

    /// <summary>Snapshot of currently-active plugins. Order is undefined.</summary>
    public IReadOnlyCollection<ActivatedPlugin> Active => _active.Values.ToArray();

    /// <summary>Try to find an active plugin by id.</summary>
    public ActivatedPlugin? Find(string id) => _active.TryGetValue(id, out var p) ? p : null;

    /// <summary>Raised AFTER a plugin's IZeusPlugin.InitializeAsync returns
    /// cleanly and the plugin is registered in <see cref="Active"/>.
    /// Subscribers run synchronously on whichever thread called
    /// <see cref="ActivateAsync"/>; throws from subscribers are logged and
    /// swallowed so a buggy subscriber can't break activation.</summary>
    public event Action<ActivatedPlugin>? PluginActivated;

    /// <summary>Raised BEFORE the plugin's ShutdownAsync is called. The
    /// plugin is already removed from <see cref="Active"/>; subscribers
    /// can free per-plugin host-side resources (audio chain slots, HTTP
    /// route entries, etc.).</summary>
    public event Action<ActivatedPlugin>? PluginDeactivated;

    public async Task StartAsync(CancellationToken ct)
    {
        // StartAsync may be invoked manually before app.Run() so that
        // PluginEndpoints.MapAll sees an already-populated Active set;
        // the hosted-service path then re-invokes it. Guard against the
        // second call, otherwise activated plugins would be torn down
        // and replaced — invalidating any backend-route closures that
        // captured the first instance.
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        if (_options.SafeMode)
        {
            _log.LogWarning("Plugin safe mode enabled — skipping plugin discovery.");
            return;
        }

        var root = _options.PluginRoot ?? PluginRoot.EnsureExists();
        if (!Directory.Exists(root)) Directory.CreateDirectory(root);
        _log.LogInformation("Plugin root: {Root}", root);

        var pluginDirs = Directory.EnumerateDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, "plugin.json")))
            .ToArray();

        foreach (var dir in pluginDirs)
        {
            try
            {
                await ActivateAsync(dir, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to activate plugin from {Dir}", dir);
            }
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        foreach (var entry in _active.Values.ToArray())
        {
            await DeactivateAsync(entry, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Load + initialise a single plugin directory. Idempotent
    /// per id — if a plugin with the same id is already loaded it is
    /// shut down first.</summary>
    public async Task<ActivatedPlugin> ActivateAsync(string pluginDir, CancellationToken ct)
    {
        var loaded = _loader.Load(pluginDir);
        var id = loaded.Manifest.Id;

        if (_active.TryRemove(id, out var existing))
        {
            await DeactivateAsync(existing, ct).ConfigureAwait(false);
        }

        var granted = ComputeGrantedCapabilities(loaded.Manifest);
        var pluginLogger = _logFactory.CreateLogger($"plugin/{id}");

        var ctx = new PluginContext(
            pluginId: id,
            manifest: loaded.Manifest,
            pluginRootPath: pluginDir,
            granted: granted,
            logger: pluginLogger,
            settings: _settings.ForPlugin(id),
            radio: granted.HasFlag(PluginCapabilities.ReadRadioState)
                ? _services.GetService<IRadioStateReader>()
                : null,
            radioController: granted.HasFlag(PluginCapabilities.ControlRadio)
                ? _services.GetService<IRadioController>()
                : null,
            // Audio playback sink (local monitor + on-air TX inject). Provided
            // by the host when available; on-air only reaches the air under
            // operator MOX, so this is not capability-gated here.
            playback: _services.GetService<IAudioPlaybackSink>());

        using (var initCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            initCts.CancelAfter(_options.InitTimeout);
            try
            {
                await loaded.Plugin.InitializeAsync(ctx, initCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "InitializeAsync threw for plugin {Id} — unloading", id);
                loaded.LoadContext.Unload();
                throw new PluginLoadException(
                    $"plugin '{id}' threw during InitializeAsync: {ex.Message}", ex);
            }
        }

        var activated = new ActivatedPlugin(loaded, ctx);
        _active[id] = activated;

        try { PluginActivated?.Invoke(activated); }
        catch (Exception ex) { _log.LogWarning(ex, "PluginActivated subscriber threw for {Id}", id); }

        return activated;
    }

    /// <summary>Shut down + unload an activated plugin. Idempotent.</summary>
    public async Task DeactivateAsync(string id, CancellationToken ct)
    {
        if (_active.TryRemove(id, out var entry))
            await DeactivateAsync(entry, ct).ConfigureAwait(false);
    }

    private async Task DeactivateAsync(ActivatedPlugin entry, CancellationToken ct)
    {
        var id = entry.Loaded.Manifest.Id;
        // Remove from the active map first so a concurrent caller can't
        // see a half-shutdown plugin. Idempotent: the public
        // DeactivateAsync(id) path also TryRemoves before calling us.
        _active.TryRemove(id, out _);

        // Notify subscribers BEFORE the plugin's own ShutdownAsync so
        // host-side per-plugin resources (audio chain slots, etc.) can
        // be released while the plugin instance is still alive.
        try { PluginDeactivated?.Invoke(entry); }
        catch (Exception ex) { _log.LogWarning(ex, "PluginDeactivated subscriber threw for {Id}", id); }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.ShutdownTimeout);
        try
        {
            await entry.Loaded.Plugin.ShutdownAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ShutdownAsync threw for plugin {Id} — continuing unload", id);
        }
        finally
        {
            try { entry.Loaded.LoadContext.Unload(); }
            catch (Exception ex) { _log.LogWarning(ex, "ALC unload threw for plugin {Id}", id); }
            _log.LogInformation("Deactivated plugin {Id}", id);
        }
    }

    private static PluginCapabilities ComputeGrantedCapabilities(PluginManifest m)
    {
        // v1 grants every declared capability; user-prompt UI is iter 5.
        // PersistSettings is implicit per ADR.
        return m.ParseCapabilities();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _settings.Dispose();
    }
}

/// <summary>Tunable timeouts + flags for <see cref="PluginManager"/>.</summary>
public sealed record PluginManagerOptions
{
    public TimeSpan InitTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public bool SafeMode { get; init; } = false;

    /// <summary>
    /// Override the plugin discovery root. Null (default) defers to
    /// <see cref="PluginRoot.Get"/>. Setting this explicitly lets tests
    /// run in parallel without fighting over the process-global
    /// <c>ZEUS_PLUGINS_PATH</c> env var.
    /// </summary>
    public string? PluginRoot { get; init; }
}

/// <summary>Runtime state for one currently-active plugin.</summary>
public sealed record ActivatedPlugin(LoadedPlugin Loaded, IPluginContext Context);
