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
// Zeus is an independent reimplementation in .NET — not a fork.
// Thetis is the authoritative reference; see ATTRIBUTIONS.md.

using Zeus.Contracts;

namespace Zeus.Server;

// Thetis default filter preset tables from console.cs:5182–5585
// InitFilterPresets. Numbers are signed Hz VFO-relative. CW uses
// default cw_pitch=600 Hz. DIGL/DIGU use default offset=0.
// Reference: docs/proposals/research/thetis-filter-ux.md §2.
public static class FilterPresets
{
    private const int CwPitch = 600;

    private static readonly FilterPresetEntry[] Lsb =
    [
        new("F1",   "5.0k", -5100, -100,  false),
        new("F2",   "4.4k", -4500, -100,  false),
        new("F3",   "3.8k", -3900, -100,  false),
        new("F4",   "3.3k", -3400, -100,  false),
        new("F5",   "2.9k", -3000, -100,  false),
        new("F6",   "2.7k", -2800, -100,  false),
        new("F7",   "2.4k", -2500, -100,  false),
        new("F8",   "2.1k", -2200, -100,  false),
        new("F9",   "1.8k", -1900, -100,  false),
        new("F10",  "1.0k", -1100, -100,  false),
        new("VAR1", "Var 1", -2800, -100, true),
        new("VAR2", "Var 2", -2800, -100, true),
    ];

    private static readonly FilterPresetEntry[] Usb =
    [
        new("F1",   "5.0k",  100, 5100,  false),
        new("F2",   "4.4k",  100, 4500,  false),
        new("F3",   "3.8k",  100, 3900,  false),
        new("F4",   "3.3k",  100, 3400,  false),
        new("F5",   "2.9k",  100, 3000,  false),
        new("F6",   "2.7k",  100, 2800,  false),
        new("F7",   "2.4k",  100, 2500,  false),
        new("F8",   "2.1k",  100, 2200,  false),
        new("F9",   "1.8k",  100, 1900,  false),
        new("F10",  "1.0k",  100, 1100,  false),
        new("VAR1", "Var 1",  100, 2800, true),
        new("VAR2", "Var 2",  100, 2800, true),
    ];

    private static readonly FilterPresetEntry[] Cwl =
    [
        // low = -cw_pitch - half, high = -cw_pitch + half
        new("F1",   "1.0k", -(CwPitch + 500), -(CwPitch - 500), false),
        new("F2",   "800",  -(CwPitch + 400), -(CwPitch - 400), false),
        new("F3",   "600",  -(CwPitch + 300), -(CwPitch - 300), false),
        new("F4",   "500",  -(CwPitch + 250), -(CwPitch - 250), false),
        new("F5",   "400",  -(CwPitch + 200), -(CwPitch - 200), false),
        new("F6",   "250",  -(CwPitch + 125), -(CwPitch - 125), false),
        new("F7",   "150",  -(CwPitch +  75), -(CwPitch -  75), false),
        new("F8",   "100",  -(CwPitch +  50), -(CwPitch -  50), false),
        new("F9",   "50",   -(CwPitch +  25), -(CwPitch -  25), false),
        new("F10",  "25",   -(CwPitch +  13), -(CwPitch -  13), false),
        new("VAR1", "Var 1", -(CwPitch + 250), -(CwPitch - 250), true),
        new("VAR2", "Var 2", -(CwPitch + 250), -(CwPitch - 250), true),
    ];

    private static readonly FilterPresetEntry[] Cwu =
    [
        // low = cw_pitch - half, high = cw_pitch + half
        new("F1",   "1.0k", CwPitch - 500, CwPitch + 500, false),
        new("F2",   "800",  CwPitch - 400, CwPitch + 400, false),
        new("F3",   "600",  CwPitch - 300, CwPitch + 300, false),
        new("F4",   "500",  CwPitch - 250, CwPitch + 250, false),
        new("F5",   "400",  CwPitch - 200, CwPitch + 200, false),
        new("F6",   "250",  CwPitch - 125, CwPitch + 125, false),
        new("F7",   "150",  CwPitch -  75, CwPitch +  75, false),
        new("F8",   "100",  CwPitch -  50, CwPitch +  50, false),
        new("F9",   "50",   CwPitch -  25, CwPitch +  25, false),
        new("F10",  "25",   CwPitch -  13, CwPitch +  13, false),
        new("VAR1", "Var 1", CwPitch - 250, CwPitch + 250, true),
        new("VAR2", "Var 2", CwPitch - 250, CwPitch + 250, true),
    ];

    private static readonly FilterPresetEntry[] Am =
    [
        new("F1",   "20k",  -10000, 10000, false),
        new("F2",   "18k",  -9000,   9000, false),
        new("F3",   "16k",  -8000,   8000, false),
        new("F4",   "12k",  -6000,   6000, false),
        new("F5",   "10k",  -5000,   5000, false),
        new("F6",   "9.0k", -4500,   4500, false),
        new("F7",   "8.0k", -4000,   4000, false),
        new("F8",   "7.0k", -3500,   3500, false),
        new("F9",   "6.0k", -3000,   3000, false),
        new("F10",  "5.0k", -2500,   2500, false),
        new("VAR1", "Var 1", -3000,  3000, true),
        new("VAR2", "Var 2", -3000,  3000, true),
    ];

    // SAM table is identical to AM in Thetis (console.cs:5493–5534)
    private static readonly FilterPresetEntry[] Sam = Am
        .Select(e => e with { SlotName = e.SlotName, Label = e.Label })
        .ToArray();

    private static readonly FilterPresetEntry[] Dsb =
    [
        new("F1",   "16k",  -8000,  8000, false),
        new("F2",   "12k",  -6000,  6000, false),
        new("F3",   "10k",  -5000,  5000, false),
        new("F4",   "8.0k", -4000,  4000, false),
        new("F5",   "6.6k", -3300,  3300, false),
        new("F6",   "5.2k", -2600,  2600, false),
        new("F7",   "4.0k", -2000,  2000, false),
        new("F8",   "3.1k", -1550,  1550, false),
        new("F9",   "2.9k", -1450,  1450, false),
        new("F10",  "2.4k", -1200,  1200, false),
        new("VAR1", "Var 1", -3300, 3300, true),
        new("VAR2", "Var 2", -3300, 3300, true),
    ];

    // DIGL centered on -digl_click_tune_offset (default 0)
    private static readonly FilterPresetEntry[] Digl =
    [
        new("F1",   "3.0k", -1500,  1500, false),
        new("F2",   "2.5k", -1250,  1250, false),
        new("F3",   "2.0k", -1000,  1000, false),
        new("F4",   "1.5k",  -750,   750, false),
        new("F5",   "1.0k",  -500,   500, false),
        new("F6",   "800",   -400,   400, false),
        new("F7",   "600",   -300,   300, false),
        new("F8",   "300",   -150,   150, false),
        new("F9",   "150",    -75,    75, false),
        new("F10",  "75",     -38,    38, false),
        new("VAR1", "Var 1",  -400,  400, true),
        new("VAR2", "Var 2",  -400,  400, true),
    ];

    // DIGU mirror of DIGL
    private static readonly FilterPresetEntry[] Digu = Digl
        .Select(e => e with { SlotName = e.SlotName, Label = e.Label })
        .ToArray();

    // FM has no operator-editable presets in Thetis; return empty.
    private static readonly FilterPresetEntry[] Fm = [];

    public static IReadOnlyList<FilterPresetEntry> DefaultsForMode(RxMode mode) => mode switch
    {
        RxMode.LSB  => Lsb,
        RxMode.USB  => Usb,
        RxMode.CWL  => Cwl,
        RxMode.CWU  => Cwu,
        RxMode.AM   => Am,
        RxMode.SAM  => Sam,
        RxMode.DSB  => Dsb,
        RxMode.DIGL => Digl,
        RxMode.DIGU => Digu,
        RxMode.FM   => Fm,
        _           => Usb,
    };
}

public sealed record FilterPresetEntry(
    string SlotName,
    string Label,
    int LowHz,
    int HighHz,
    bool IsVar);
