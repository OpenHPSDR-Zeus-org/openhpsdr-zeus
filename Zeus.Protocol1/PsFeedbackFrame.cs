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
//
// HL2 PureSignal feedback path informed by mi0bot/openhpsdr-thetis (the
// HL2-specific Thetis fork) and DL1YCF/pihpsdr — see
// docs/references/protocol-1/hermes-lite2-protocol.md "PureSignal feedback
// path" section.

namespace Zeus.Protocol1;

/// <summary>
/// One 1024-sample paired feedback block destined for WDSP <c>psccF</c>,
/// produced by <see cref="Protocol1Client"/> when the operator arms PS
/// against an HL2 + external coupler. Mirrors the shape of
/// <c>Zeus.Protocol2.PsFeedbackFrame</c> so the
/// DspPipelineService pump can treat both protocols uniformly.
///
/// On HL2 (this protocol) the TX-side reference is the operator's TX-IQ as
/// written to the radio's DUC (NOT a coupler tap — there isn't one on
/// HL2). The RX side comes from the dedicated feedback ADC (ADC1) routed
/// through DDC1 when the gateware honours <c>0x0a[22] = 1</c> and
/// MOX is asserted. See
/// docs/references/protocol-1/hermes-lite2-protocol.md "PureSignal
/// feedback path" for the wire mechanics.
///
/// SeqHint is the radio's EP6 sequence at the start of the block,
/// useful for diagnostic logging when the cal converges slowly. Not
/// used by WDSP.
///
/// <para><b>Buffer ownership:</b> <c>TxI/TxQ/RxI/RxQ</c> are rented from
/// <see cref="System.Buffers.ArrayPool{T}"/>.Shared by Protocol1Client. The
/// receiving sink (DspPipelineService.OnPsFeedbackFrame) owns the rentals
/// once the frame is handed off and MUST return them to the pool after the
/// engine has consumed them. The actual array length may exceed
/// <c>PsFeedbackBlockSize</c> (1024) — ArrayPool's size guarantee is
/// "at-least," so consumers must use a fixed 1024-sample view.</para>
/// </summary>
public readonly record struct PsFeedbackFrame(
    float[] TxI,
    float[] TxQ,
    float[] RxI,
    float[] RxQ,
    ulong SeqHint)
{
    /// <summary>Wire-fixed block length WDSP <c>psccF</c> expects, per
    /// pihpsdr <c>receiver.c:636</c>. The producer always fills exactly this
    /// many samples; the rented arrays may be larger (ArrayPool size
    /// guarantee is "at-least").</summary>
    public const int BlockSize = 1024;
}
