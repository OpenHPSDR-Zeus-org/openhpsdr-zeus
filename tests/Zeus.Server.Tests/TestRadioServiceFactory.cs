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

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Protocol1;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Test-only factory that constructs a minimal <see cref="RadioService"/>
/// wired to a caller-supplied <see cref="IDspEngine"/> stub and a known
/// initial state (mode, VFO, filter, sample rate). Exercises the public
/// surface only — no UDP sockets, no LiteDB, no background threads.
/// </summary>
internal static class TestRadioServiceFactory
{
    /// <summary>
    /// Build a <see cref="RadioService"/> configured for Zero Beat unit tests:
    /// <list type="bullet">
    ///   <item>Engine provider returns <paramref name="engine"/>.</item>
    ///   <item>Mode is set to <paramref name="mode"/>.</item>
    ///   <item>VFO is set to <paramref name="vfoHz"/>.</item>
    ///   <item>Filter is set to <paramref name="filterLowHz"/>..<paramref name="filterHighHz"/>.</item>
    ///   <item>Sample rate is 48 000 Hz (matching the 2.93 Hz/bin test math).</item>
    /// </list>
    /// The returned service does NOT call any Protocol1Client methods (no
    /// active connection), so all radio-wire calls on the service are safe
    /// no-ops.
    /// </summary>
    public static RadioService WithEngine(
        IDspEngine engine,
        RxMode mode = RxMode.CWU,
        long vfoHz = 14_060_000,
        int filterLowHz = -1000,
        int filterHighHz = 1000)
    {
        // Use throw-away in-memory stores — no file I/O in tests.
        var dbPath = Path.Combine(Path.GetTempPath(), $"zeus-zb-test-{Guid.NewGuid():N}.db");
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, dbPath + ".dsp");
        var paStore  = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, dbPath + ".pa");

        var radio = new RadioService(
            NullLoggerFactory.Instance,
            dspStore,
            paStore,
            engineProvider: () => engine);

        // Drive the service into the test-desired initial state via the same
        // public API that production code uses. SetMode first so the mode-
        // based filter-sign logic runs before SetFilter overrides it.
        radio.SetMode(mode);
        radio.SetVfo(vfoHz);
        // Override the filter with a caller-specified symmetric window.
        // SetFilter accepts signed values directly; a (-1000,+1000) window
        // covers ±341 bins at 48 kHz / 16 384 bins and includes the
        // off-DC test peaks used in RadioServiceZeroBeatTests.
        radio.SetFilter(filterLowHz, filterHighHz);
        radio.SetSampleRate(HpsdrSampleRate.Rate48k);

        return radio;
    }
}
