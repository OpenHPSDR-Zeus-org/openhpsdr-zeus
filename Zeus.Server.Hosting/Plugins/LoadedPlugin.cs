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

using System.Runtime.Loader;
using Zeus.Contracts.Plugins;

namespace Zeus.Server.Plugins;

/// <summary>
/// One entry in <see cref="PluginManager.Plugins"/>. A plugin appears here
/// after a load attempt, regardless of outcome — successful loads have
/// non-null Instance and null LoadError; failed loads have null Instance
/// and a populated LoadError so the upcoming /api/plugins endpoint can
/// surface diagnostics to the operator.
/// </summary>
public sealed record LoadedPlugin(
    PluginManifest Manifest,
    IZeusPlugin? Instance,
    AssemblyLoadContext? LoadContext,
    string? LoadError,
    DateTimeOffset LoadedAt);
