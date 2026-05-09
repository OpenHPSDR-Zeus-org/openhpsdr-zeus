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
/// Static descriptor returned by <see cref="IZeusPlugin.Metadata"/>. The
/// values here are baked into the plugin assembly; the equivalent fields in
/// <see cref="PluginManifest"/> (loaded from <c>plugin.json</c>) are what
/// the host actually trusts for discovery and permission decisions. The two
/// must agree — the loader rejects plugins whose Metadata.Id disagrees with
/// the manifest Id.
/// </summary>
/// <param name="Id">Reverse-DNS identifier, e.g. <c>com.example.rotator</c>.</param>
/// <param name="Name">Human-readable name shown in the Plugins settings panel.</param>
/// <param name="Version">Semantic version string, e.g. <c>1.2.0</c>.</param>
/// <param name="Author">Author name and contact.</param>
/// <param name="Description">One-paragraph description of what the plugin does.</param>
/// <param name="HomepageUrl">Optional project URL (GitHub repo, docs site).</param>
/// <param name="Capabilities">Flags this plugin requires to function.</param>
public sealed record PluginMetadata(
    string Id,
    string Name,
    string Version,
    string Author,
    string Description,
    Uri? HomepageUrl,
    PluginCapabilities Capabilities);
