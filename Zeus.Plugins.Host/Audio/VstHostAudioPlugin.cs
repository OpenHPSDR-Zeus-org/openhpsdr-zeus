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
    private readonly string _loadIdentity;
    private readonly bool _isAudioUnit;
    private readonly string _pluginRootPath;
    private readonly string _slot;
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
        _slot = manifestAudio.Slot;
        DisplayName = displayName;
        Requirements = new AudioPluginRequirements(
            SampleRate: manifestAudio.SampleRate,
            Channels: manifestAudio.Channels,
            BlockSize: manifestAudio.Slot.StartsWith("rx.", StringComparison.OrdinalIgnoreCase) ? 2048 : 1024);

        // Format selects the load identity. "au" loads a macOS Audio Unit by
        // its type:subtype:manufacturer triple (resolved from the OS registry,
        // not a file); anything else (default "vst3") loads from a VST3 path.
        // AudioPluginBridge picks the matching IVstBridgeNative backend; this
        // class stays backend-agnostic.
        _isAudioUnit = string.Equals(manifestAudio.Format, "au", StringComparison.OrdinalIgnoreCase);
        _loadIdentity = _isAudioUnit
            ? (manifestAudio.AuComponentId
                ?? throw new ArgumentException("audio.auComponentId is required when audio.format is \"au\""))
            : (manifestAudio.Vst3Path
                ?? throw new ArgumentException("audio.vst3Path is required for VstHostAudioPlugin"));
    }

    public string DisplayName { get; }
    public AudioPluginRequirements Requirements { get; }

    /// <summary>
    /// Load gate for in-process native plugin hosting (VST3 + Audio Unit). Both
    /// RX and TX native load default ON — TX was enabled by KB2UKA after
    /// bench-verifying AU TX hosting (audio confirmed changing under the editor)
    /// on 2026-06-26; it had previously been opt-in.
    ///
    /// Kill switches fall back to the crash-isolated out-of-process engine:
    /// <c>ZEUS_DISABLE_VST_LOAD=1</c> (all slots), <c>ZEUS_DISABLE_RX_VST_LOAD=1</c>
    /// (RX only), <c>ZEUS_DISABLE_TX_VST_LOAD=1</c> (TX only).
    /// <c>ZEUS_ENABLE_VST_LOAD=1</c> force-enables everything even if a per-side
    /// disable is set. Precedence: global disable &gt; force-enable &gt; per-side
    /// disable. See native/zeus-vst-bridge.
    /// </summary>
    /// <summary>Test override for <see cref="NativeLoadEnabled"/>; null = use the env var.</summary>
    internal static bool? NativeLoadEnabledOverride;

    private static bool NativeLoadEnabled(string slot)
    {
        if (NativeLoadEnabledOverride is { } forced) return forced;
        if (Environment.GetEnvironmentVariable("ZEUS_DISABLE_VST_LOAD") == "1") return false;
        if (Environment.GetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD") == "1") return true;
        if (slot.StartsWith("rx.", StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable("ZEUS_DISABLE_RX_VST_LOAD") != "1";
        // TX native load now defaults on (KB2UKA-approved 2026-06-26);
        // ZEUS_DISABLE_TX_VST_LOAD=1 is the TX-side kill switch.
        return Environment.GetEnvironmentVariable("ZEUS_DISABLE_TX_VST_LOAD") != "1";
    }

    /// <summary>
    /// Editor-guard signal ONLY — whether the TX editor-open fallback should
    /// treat TX as in-process-hostable when a plugin is <em>not</em> already
    /// natively loaded. Deliberately <b>decoupled</b> from the TX <em>load</em>
    /// default (<see cref="NativeLoadEnabled"/>, which now defaults TX on as of
    /// 2026-06-26): it stays pinned to the pre-flip explicit <c>ZEUS_ENABLE_VST_LOAD</c>
    /// opt-in so flipping the load default does not move the cross-platform
    /// editor engine-redirect UX (Windows in-process TX editor hosting is
    /// unverified). A TX plugin that actually loaded in-process is detected via
    /// <c>AudioPluginBridge.HostsPlugin</c> upstream and bypasses the guard
    /// regardless; this governs only the not-hosted fallback message. Mirrors the
    /// pre-flip <c>TxNativeLoadEnabled</c> semantics exactly.
    /// </summary>
    public static bool TxNativeEditorOptIn
    {
        get
        {
            if (NativeLoadEnabledOverride is { } forced) return forced;
            if (Environment.GetEnvironmentVariable("ZEUS_DISABLE_VST_LOAD") == "1") return false;
            return Environment.GetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD") == "1";
        }
    }

    public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct)
    {
        if (!NativeLoadEnabled(_slot))
        {
            _log?.LogInformation(
                "VST host '{Name}' registered but native load is disabled "
                + "(clear ZEUS_DISABLE_VST_LOAD / ZEUS_DISABLE_TX_VST_LOAD / "
                + "ZEUS_DISABLE_RX_VST_LOAD to re-enable); passing audio through.",
                DisplayName);
            return Task.CompletedTask; // _handle stays 0 → Process passes through
        }

        // Bridge init is idempotent — the native side ref-counts. (The AU
        // bridge ignores the abi argument and validates against its own
        // ZAU_ABI; VstBridgeAbi.Current is the VST3 ABI but the AU backend's
        // Init clamps to AuBridgeAbi.Current internally.)
        var initStatus = _bridge.Init(VstBridgeAbi.Current);
        if (initStatus != VstBridgeStatus.Ok)
            throw new PluginLoadException(
                $"audio bridge init failed (status={initStatus}); is the native bridge installed?");

        // For an Audio Unit the load identity is a registry triple, not a
        // filesystem path — pass it through unchanged and skip the file check.
        string loadIdentity;
        if (_isAudioUnit)
        {
            loadIdentity = _loadIdentity;
        }
        else
        {
            loadIdentity = Path.IsPathRooted(_loadIdentity)
                ? _loadIdentity
                : Path.Combine(_pluginRootPath, _loadIdentity);

            if (!File.Exists(loadIdentity) && !Directory.Exists(loadIdentity))
                throw new PluginLoadException($"VST3 path not found: {loadIdentity}");
        }

        var blockSize = Math.Max(1, host.CurrentBlockSize);
        var status = _bridge.LoadVst3(
            loadIdentity,
            Requirements.Channels,
            Requirements.SampleRate,
            blockSize,
            out _handle);

        if (status != VstBridgeStatus.Ok || _handle == 0)
            throw new PluginLoadException(
                $"{(_isAudioUnit ? "Audio Unit" : "VST3")} load failed for {loadIdentity} (status={status})");

        _log?.LogInformation(
            "{Kind} host loaded {Id} (channels={Channels} sr={SampleRate} block={Block})",
            _isAudioUnit ? "AU" : "VST", loadIdentity,
            Requirements.Channels, Requirements.SampleRate, blockSize);
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
    /// native load is gated off (<see cref="NativeLoadEnabled(string)"/>) or the
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
