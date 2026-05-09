// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// HelloWorld sample plugin: minimal IZeusPlugin implementation used as
// the canonical "does the plugin loader work?" sample and as the test
// fixture for PluginManagerTests.

using Microsoft.Extensions.Logging;
using Zeus.Contracts.Plugins;

namespace OpenHpsdr.Zeus.Plugins.HelloWorld;

public sealed class Plugin : IZeusPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "com.openhpsdr.zeus.helloworld",
        Name: "Hello World",
        Version: "1.0.0",
        Author: "OpenHPSDR Zeus contributors",
        Description: "Sample plugin that logs a friendly greeting on init. Demonstrates the minimum surface area required to be loaded by the Zeus plugin host.",
        HomepageUrl: null,
        Capabilities: PluginCapabilities.None);

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        context.Logger.LogInformation("Hello from {PluginId} v{Version}!", Metadata.Id, Metadata.Version);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}
