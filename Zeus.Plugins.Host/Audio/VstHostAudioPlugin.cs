using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// <see cref="IAudioPlugin"/> implementation that hosts a single VST3
/// effect via <see cref="IVstBridgeNative"/>. Synthesised by the host
/// when a plugin's manifest declares <c>audio.vst3Path</c> — plugin
/// authors don't write this themselves.
/// </summary>
public sealed class VstHostAudioPlugin : IAudioPlugin, IAsyncDisposable
{
    private readonly IVstBridgeNative _bridge;
    private readonly string _vst3Path;
    private readonly string _pluginRootPath;
    private readonly ILogger? _log;
    private nint _handle;

    public VstHostAudioPlugin(
        IVstBridgeNative bridge,
        AudioBlock manifestAudio,
        string pluginRootPath,
        string displayName,
        ILogger? log = null)
    {
        _bridge = bridge;
        _pluginRootPath = pluginRootPath;
        _log = log;
        DisplayName = displayName;
        Requirements = new AudioPluginRequirements(
            SampleRate: manifestAudio.SampleRate,
            Channels: manifestAudio.Channels,
            BlockSize: 256);
        _vst3Path = manifestAudio.Vst3Path
            ?? throw new ArgumentException("audio.vst3Path is required for VstHostAudioPlugin");
    }

    public string DisplayName { get; }
    public AudioPluginRequirements Requirements { get; }

    /// <summary>
    /// Safety gate: actually loading a real .vst3 through the native
    /// bridge currently risks a hard crash (the bridge has only ever
    /// been exercised against non-existent test paths; real plugins can
    /// segfault on load). Until the native bridge is hardened, the load
    /// is OFF by default — a hosted VST registers and sits in the chain
    /// but passes audio through bit-identical (Process short-circuits on
    /// a zero handle). Set ZEUS_ENABLE_VST_LOAD=1 to opt in (e.g. while
    /// developing the native side). See native/zeus-vst-bridge.
    /// </summary>
    /// <summary>Test override for <see cref="NativeLoadEnabled"/>; null = use the env var.</summary>
    internal static bool? NativeLoadEnabledOverride;

    private static bool NativeLoadEnabled =>
        NativeLoadEnabledOverride
        ?? Environment.GetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD") == "1";

    public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct)
    {
        if (!NativeLoadEnabled)
        {
            _log?.LogInformation(
                "VST host '{Name}' registered but native load is disabled "
                + "(ZEUS_ENABLE_VST_LOAD!=1); passing audio through until the "
                + "native bridge is hardened.", DisplayName);
            return Task.CompletedTask; // _handle stays 0 → Process passes through
        }

        // Bridge init is idempotent — the native side ref-counts.
        var initStatus = _bridge.Init(VstBridgeAbi.Current);
        if (initStatus != VstBridgeStatus.Ok)
            throw new PluginLoadException(
                $"VST bridge init failed (status={initStatus}); is zeus-vst-bridge installed?");

        var absPath = Path.IsPathRooted(_vst3Path)
            ? _vst3Path
            : Path.Combine(_pluginRootPath, _vst3Path);

        if (!File.Exists(absPath) && !Directory.Exists(absPath))
            throw new PluginLoadException($"VST3 path not found: {absPath}");

        var status = _bridge.LoadVst3(
            absPath,
            Requirements.Channels,
            Requirements.SampleRate,
            Requirements.BlockSize,
            out _handle);

        if (status != VstBridgeStatus.Ok || _handle == 0)
            throw new PluginLoadException(
                $"VST3 load failed for {absPath} (status={status})");

        _log?.LogInformation(
            "VST host loaded {Path} (channels={Channels} sr={SampleRate} block={Block})",
            absPath, Requirements.Channels, Requirements.SampleRate, Requirements.BlockSize);
        return Task.CompletedTask;
    }

    public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
    {
        if (_handle == 0)
        {
            input.CopyTo(output); // safety: pass through if not initialised
            return;
        }

        var status = _bridge.Process(_handle, input, output, ctx.Frames);
        if (status != VstBridgeStatus.Ok)
        {
            // Realtime path: NEVER throw, NEVER log here (allocation).
            // Pass through on bridge failure — the operator will see a
            // status surface up via the next non-realtime poll.
            input.CopyTo(output);
        }
    }

    /// <summary>
    /// True once the VST is natively loaded (the editor can only open
    /// when there's a real plugin instance behind the handle). False when
    /// native load is gated off (<see cref="NativeLoadEnabled"/>) or the
    /// load failed — in those states the slot passes audio through and
    /// has no GUI to show.
    /// </summary>
    public bool IsNativelyLoaded => _handle != 0;

    /// <summary>Whether the plugin's native editor window is currently open.</summary>
    public bool IsEditorOpen => _handle != 0 && _bridge.EditorIsOpen(_handle);

    /// <summary>
    /// Open the plugin's native editor (its real GUI) in a bridge-owned
    /// OS window. Returns false if the VST isn't natively loaded or the
    /// bridge reports failure (e.g. editor unsupported on this platform).
    /// </summary>
    public bool OpenEditor()
    {
        if (_handle == 0)
        {
            _log?.LogInformation(
                "VST '{Name}' editor open requested but no native handle "
                + "(native load disabled or load failed).", DisplayName);
            return false;
        }
        var status = _bridge.EditorOpen(_handle, DisplayName);
        if (status != VstBridgeStatus.Ok)
            _log?.LogWarning("VST '{Name}' editor open failed (status={Status}).", DisplayName, status);
        return status == VstBridgeStatus.Ok;
    }

    /// <summary>Close the plugin's native editor window if open.</summary>
    public void CloseEditor()
    {
        if (_handle == 0) return;
        _bridge.EditorClose(_handle);
    }

    public Task ShutdownAudioAsync(CancellationToken ct)
    {
        if (_handle != 0)
        {
            _bridge.Unload(_handle);
            _handle = 0;
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_handle != 0)
        {
            try { _bridge.Unload(_handle); } catch { /* swallow */ }
            _handle = 0;
        }
        return ValueTask.CompletedTask;
    }
}
