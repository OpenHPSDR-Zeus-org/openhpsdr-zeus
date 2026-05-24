// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

namespace Zeus.Contracts;

/// <summary>
/// Identifies who keyed MOX so the release path can refuse to drop the
/// transmitter on behalf of a different source. Without this tag, a TCI
/// peer's "trx false" while the CW engine is mid-message would silently
/// truncate the transmission, and a hardware-PTT falling edge while a UI
/// click is holding MOX would do the same in the other direction.
///
/// Release rule (enforced in <c>TxService.TrySetMox</c>):
/// <list type="bullet">
///   <item><c>UI</c> always wins — the operator's on-screen button is the
///         master override and can release MOX no matter who claimed it.</item>
///   <item>Any other source can release only what it itself claimed.</item>
///   <item><c>TxService.TryTripForAlert</c> bypasses the check entirely —
///         SWR / timeout trips must always cut RF.</item>
/// </list>
///
/// Wire-stable byte values — appending only. Callers that don't care about
/// the source path may pass <see cref="UI"/> and get the legacy behaviour.
/// </summary>
public enum MoxSource : byte
{
    /// <summary>The on-screen MOX button, REST <c>/api/tx/mox</c>, or any
    /// in-process default. Master override.</summary>
    UI = 0,
    /// <summary>A TCI peer (MSHV, JTDX, …) keyed via <c>trx:true</c>.</summary>
    Tci = 1,
    /// <summary>Hardware PTT input (foot switch, mic PTT, hand key) read
    /// through the radio's protocol C&amp;C status path.</summary>
    Hardware = 2,
    /// <summary>The host-side CW engine driving keying from
    /// <c>/api/cw/send</c>.</summary>
    Cwx = 3,
}
