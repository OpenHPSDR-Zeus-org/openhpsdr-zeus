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

using Microsoft.Extensions.Logging;

namespace Zeus.Contracts.Plugins;

/// <summary>
/// Sandbox boundary handed to a plugin during <see cref="IZeusPlugin.InitializeAsync"/>.
/// The plugin must keep this reference for its lifetime; the host uses the
/// presence/absence of subsystems on this interface to enforce the
/// capability flags declared in the manifest.
///
/// PR-A skeleton: only PluginId + Logger are wired. Subsystems (radio
/// state, scoped settings, network, filesystem) land in PR-B and PR-C
/// alongside capability enforcement and the per-plugin settings store.
/// </summary>
public interface IPluginContext
{
    /// <summary>Reverse-DNS plugin id, identical to the manifest Id.</summary>
    string PluginId { get; }

    /// <summary>
    /// Logger scoped to this plugin (category prefix is the plugin id) so
    /// log lines from a plugin are easy to filter and route to the
    /// per-plugin diagnostics page in the future.
    /// </summary>
    ILogger Logger { get; }
}
