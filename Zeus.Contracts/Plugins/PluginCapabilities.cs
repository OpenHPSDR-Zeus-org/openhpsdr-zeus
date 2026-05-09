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
/// Capabilities a plugin may declare in its manifest. The host grants each
/// flag explicitly (opt-in model). Subsystems on <see cref="IPluginContext"/>
/// that map to a denied capability are exposed as <c>null</c>, so plugins
/// pay no runtime cost for capabilities they did not request.
/// </summary>
[Flags]
public enum PluginCapabilities
{
    None             = 0,
    /// <summary>Read radio state — frequency, mode, band, MOX. Required for almost any useful plugin.</summary>
    ReadRadioState   = 1 << 0,
    /// <summary>Mutate radio state — set VFO, mode, MOX. Powerful; user-prompted.</summary>
    ControlRadio     = 1 << 1,
    /// <summary>Open sockets / HTTP clients to arbitrary hosts.</summary>
    NetworkAccess    = 1 << 2,
    /// <summary>Read files outside the plugin's own settings store.</summary>
    FileSystemRead   = 1 << 3,
    /// <summary>Write files outside the plugin's own settings store.</summary>
    FileSystemWrite  = 1 << 4,
    /// <summary>Spawn child processes.</summary>
    ProcessSpawn     = 1 << 5,
    /// <summary>
    /// Subscribe to RX/TX audio sample buffers. Hot path — implementations
    /// MUST be sync, allocation-free, and O(samples). Async, I/O, or large
    /// allocations in an audio handler will glitch the radio.
    /// </summary>
    AudioStream      = 1 << 6,
}
