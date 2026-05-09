// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Contracts.Plugins;

/// <summary>
/// The single entry point a Zeus plugin assembly must implement. The
/// loader scans loaded assemblies for the unique non-abstract type
/// implementing this interface and instantiates it via the parameterless
/// constructor. Plugins that need DI must do their own composition inside
/// <see cref="InitializeAsync"/>.
///
/// Lifetime contract:
///   * Constructor — must be cheap and side-effect-free; no I/O, no thread
///     start. The host may instantiate and discard during scanning.
///   * <see cref="InitializeAsync"/> — runs once at host startup with a
///     10-second timeout. Acquire resources, register subscribers, start
///     background work. Throwing here marks the plugin failed; other
///     plugins continue loading.
///   * <see cref="ShutdownAsync"/> — runs once at host shutdown with a
///     5-second timeout. Release resources, stop background work. Throwing
///     here is logged but does not delay the host.
/// </summary>
public interface IZeusPlugin
{
    /// <summary>Static descriptor; must agree with the manifest.</summary>
    PluginMetadata Metadata { get; }

    /// <summary>Initialise the plugin. Honour the cancellation token.</summary>
    Task InitializeAsync(IPluginContext context, CancellationToken ct);

    /// <summary>Shut the plugin down. Honour the cancellation token.</summary>
    Task ShutdownAsync(CancellationToken ct);
}
