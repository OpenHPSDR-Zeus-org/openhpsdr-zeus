// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

// LiteDB v5's BsonMapper.Global is a process-wide static cache that is not
// thread-safe during the first GetEntityMapper(T) call for a given T. Two
// xUnit test classes constructing PaSettingsStore / DspSettingsStore /
// PsSettingsStore in parallel can race on populating PaBandEntry's field
// map and intermittently throw "Member Band not found on BsonMapper for
// type Zeus.Server.PaBandEntry" inside LiteCollection.EnsureIndex.
//
// Per-fixture temp DB paths (4a33523) only narrow the timing window — they
// don't remove the race because the mapper cache is global, not per-file.
// Production never hits this because the real radio runtime constructs the
// stores exactly once. Disabling class-level test parallelism for THIS
// assembly serializes those constructions and removes the race outright,
// at a measured cost of ~1s on a 200-test suite.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
