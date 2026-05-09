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
/// Plugin manifest deserialised from <c>plugin.json</c>. The manifest sits
/// alongside the plugin assembly inside its directory under the host's
/// plugin root (e.g. <c>~/.local/share/zeus/plugins/MyPlugin/</c>).
///
/// The host trusts the manifest for discovery and permission decisions;
/// the assembly's own <see cref="PluginMetadata"/> is verified against it
/// at load time.
/// </summary>
/// <param name="Id">Reverse-DNS identifier; must equal <c>Metadata.Id</c>.</param>
/// <param name="Name">Human-readable name.</param>
/// <param name="Version">Semantic version.</param>
/// <param name="Author">Author / contact.</param>
/// <param name="Description">One-paragraph description.</param>
/// <param name="HomepageUrl">Optional project URL.</param>
/// <param name="Assembly">Filename of the plugin DLL relative to the manifest directory.</param>
/// <param name="Capabilities">Capability strings (matching <see cref="PluginCapabilities"/> names) the plugin requests.</param>
/// <param name="ResourceLimits">Optional cooperative resource hints; not enforced in v1.</param>
/// <param name="Isolation">Always <c>"inprocess"</c> in v1. Reserved for future <c>"process"</c> isolation.</param>
public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    string Author,
    string Description,
    Uri? HomepageUrl,
    string Assembly,
    IReadOnlyList<string> Capabilities,
    PluginResourceLimits? ResourceLimits,
    string Isolation = "inprocess");

/// <summary>
/// Cooperative resource hints declared in the manifest. Not enforced in
/// v1; logged for diagnostics and may be used as guidance for the
/// permission prompt UI ("This plugin says it will use up to 256 MB").
/// </summary>
public sealed record PluginResourceLimits(
    int? MaxCpuPercent = null,
    int? MaxMemoryMB = null,
    int? MaxThreads = null,
    int? MaxNetworkKBps = null);
