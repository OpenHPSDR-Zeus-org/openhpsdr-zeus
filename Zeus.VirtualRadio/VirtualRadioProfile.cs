// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Net;
using Zeus.Contracts;

namespace Zeus.VirtualRadio;

/// <summary>
/// One synthetic RX tone: a carrier at <paramref name="FreqHz"/> placed at
/// baseband offset <c>(FreqHz − tunedHz)</c>, at <paramref name="Dbc"/> relative
/// to full scale (e.g. -73 dBc ≈ S9). Consumed by the synthetic IQ generator.
/// </summary>
public readonly record struct ToneSpec(long FreqHz, double Dbc);

/// <summary>
/// Immutable configuration of one virtual radio: the <c>{Board, Variant,
/// Protocol}</c> triple plus the RX-signal / binding parameters. Construct via
/// <see cref="Create"/>, which validates the triple against
/// <see cref="BoardProtocolSupport"/> and throws on an illegal combination
/// (e.g. HL2 + P2). The non-triple fields are <c>init</c> so callers compose
/// with a <c>with</c> expression after <see cref="Create"/>.
/// </summary>
public sealed record VirtualRadioProfile
{
    /// <summary>The HPSDR board this radio impersonates (drives the discovery
    /// board byte, calibration table, PA defaults, and capability fingerprint).</summary>
    public HpsdrBoardKind Board { get; }

    /// <summary>Disambiguates the 0x0A OrionMkII wire-byte alias family. Ignored
    /// for every board kind other than <see cref="HpsdrBoardKind.OrionMkII"/>.</summary>
    public OrionMkIIVariant Variant { get; }

    /// <summary>The wire stack this radio speaks. Validated against the board.</summary>
    public ProtocolVersion Protocol { get; }

    /// <summary>Initial RX tuned frequency in Hz (informational seed; the live
    /// tuned frequency tracks the host's decoded RxFreq commands once connected).</summary>
    public long TunedHz { get; init; }

    /// <summary>DDC / IQ sample rate in kHz (48 / 96 / 192 / 384 on P1).</summary>
    public int SampleRateKhz { get; init; } = 48;

    /// <summary>Synthetic RX tones to render into the IQ stream.</summary>
    public IReadOnlyList<ToneSpec> Tones { get; init; } = Array.Empty<ToneSpec>();

    /// <summary>Gaussian noise-floor level in dBc (e.g. -110) so the S-meter,
    /// waterfall, and AGC have something to settle on.</summary>
    public double NoiseFloorDbc { get; init; } = -110.0;

    /// <summary>Local address the emulator binds its radio sockets to.
    /// <c>127.0.0.1</c> is direct-connect only (Zeus discovery skips loopback);
    /// bind a LAN IP to appear in Zeus's discovery panel.</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    /// <summary>PureSignal feedback emission. OFF by default — burn-zone, gated
    /// to a later phase. Present here only so the type is ready; Phase 0/1 never
    /// reads it.</summary>
    public bool PureSignalEnabled { get; init; } = false;

    private VirtualRadioProfile(HpsdrBoardKind board, OrionMkIIVariant variant, ProtocolVersion protocol)
    {
        Board = board;
        Variant = variant;
        Protocol = protocol;
    }

    /// <summary>
    /// Validate and construct. Throws <see cref="ArgumentException"/> when
    /// <paramref name="board"/> cannot speak <paramref name="protocol"/>
    /// (per <see cref="BoardProtocolSupport"/>). The <paramref name="variant"/>
    /// only matters for the 0x0A OrionMkII family; it is accepted (and ignored)
    /// for other boards to keep call sites uniform.
    /// </summary>
    public static VirtualRadioProfile Create(
        HpsdrBoardKind board,
        ProtocolVersion protocol,
        OrionMkIIVariant variant = OrionMkIIVariant.G2)
    {
        if (!BoardProtocolSupport.Supports(board, protocol))
        {
            throw new ArgumentException(
                $"Board {board} does not support {protocol} " +
                $"(supported: {BoardProtocolSupport.For(board)}).",
                nameof(protocol));
        }

        return new VirtualRadioProfile(board, variant, protocol);
    }
}
