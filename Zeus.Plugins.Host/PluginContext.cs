using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;

namespace Zeus.Plugins.Host;

/// <summary>
/// Host-side implementation of <see cref="IPluginContext"/>. Built per
/// plugin during activation; the radio reader / controller slots are
/// nulled out per the granted capability set.
/// </summary>
internal sealed class PluginContext : IPluginContext
{
    public PluginContext(
        string pluginId,
        PluginManifest manifest,
        string pluginRootPath,
        PluginCapabilities granted,
        ILogger logger,
        IPluginSettings settings,
        IRadioStateReader? radio,
        IRadioController? radioController,
        IAudioPlaybackSink? playback = null,
        IQrzLookup? qrz = null)
    {
        PluginId = pluginId;
        Manifest = manifest;
        PluginRootPath = pluginRootPath;
        GrantedCapabilities = granted;
        Logger = logger;
        Settings = settings;
        Radio = radio;
        RadioController = radioController;
        Playback = playback;
        Qrz = qrz;
    }

    public string PluginId { get; }
    public PluginManifest Manifest { get; }
    public ILogger Logger { get; }
    public string PluginRootPath { get; }
    public PluginCapabilities GrantedCapabilities { get; }
    public IPluginSettings Settings { get; }
    public IRadioStateReader? Radio { get; }
    public IRadioController? RadioController { get; }
    public IAudioPlaybackSink? Playback { get; }
    public IQrzLookup? Qrz { get; }
}
