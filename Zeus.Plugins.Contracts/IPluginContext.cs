using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts.Audio;

namespace Zeus.Plugins.Contracts;

/// <summary>
/// What the host gives a plugin during <see cref="IZeusPlugin.InitializeAsync"/>.
/// Capability-gated: <see cref="RadioController"/> et al. are null unless
/// the manifest declared the matching <see cref="PluginCapabilities"/> AND
/// the user granted it.
/// </summary>
public interface IPluginContext
{
    /// <summary>The plugin's stable id (e.g. <c>com.example.amplifier</c>).</summary>
    string PluginId { get; }

    /// <summary>The plugin's manifest as loaded from disk.</summary>
    PluginManifest Manifest { get; }

    /// <summary>Plugin-scoped logger. Each log line is tagged with the plugin id.</summary>
    ILogger Logger { get; }

    /// <summary>
    /// Absolute path to the plugin's installation directory. Plugins may
    /// read/write files under this root without needing
    /// <see cref="PluginCapabilities.FileSystemRead"/> /
    /// <see cref="PluginCapabilities.FileSystemWrite"/>.
    /// </summary>
    string PluginRootPath { get; }

    /// <summary>Capabilities actually granted (intersect of manifest + user choice).</summary>
    PluginCapabilities GrantedCapabilities { get; }

    /// <summary>Scoped key/value settings store (LiteDB-backed, isolated per plugin).</summary>
    IPluginSettings Settings { get; }

    /// <summary>
    /// Read-only radio state stream. Null if
    /// <see cref="PluginCapabilities.ReadRadioState"/> was not granted.
    /// </summary>
    IRadioStateReader? Radio { get; }

    /// <summary>
    /// Mutating radio controller. Null if
    /// <see cref="PluginCapabilities.ControlRadio"/> was not granted.
    /// </summary>
    IRadioController? RadioController { get; }

    /// <summary>
    /// Audio playback into the host — local monitor and on-air TX inject.
    /// Null if the host exposes no playback path (e.g. browser mode has no
    /// local-monitor sink). On-air playback only reaches the air while the
    /// operator has MOX engaged; this sink never keys the radio itself.
    /// Default implementation returns null so existing hosts / test doubles
    /// need no change.
    /// </summary>
    IAudioPlaybackSink? Playback => null;
}

/// <summary>Key/value persistence scoped to one plugin id.</summary>
public interface IPluginSettings
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>Read-only view of the current radio state. Granted by ReadRadioState.</summary>
public interface IRadioStateReader
{
    long FrequencyHz { get; }
    string Mode { get; }
    string Band { get; }
    bool Mox { get; }

    event Action<long> FrequencyChanged;
    event Action<string> ModeChanged;
    event Action<bool> MoxChanged;
}

/// <summary>Mutating radio controller. Granted by ControlRadio.</summary>
public interface IRadioController
{
    Task SetFrequencyAsync(long hz, CancellationToken ct = default);
    Task SetModeAsync(string mode, CancellationToken ct = default);
    Task SetMoxAsync(bool keyed, CancellationToken ct = default);
}
