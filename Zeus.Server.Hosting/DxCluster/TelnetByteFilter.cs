// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Server.DxCluster;

/// <summary>
/// Strips Telnet (RFC 854) IAC option-negotiation sequences from an inbound byte
/// stream, leaving only the application text. Stateful across calls so a sequence
/// that straddles a read boundary is handled correctly. No I/O — pure byte
/// transform, fully unit-testable.
///
/// We are a passive reader of a (mostly line-mode) cluster, so we never reply to
/// negotiations — most clusters work fine without WILL/WONT handshakes, and the
/// few control bytes are simply discarded. <c>IAC IAC</c> (0xFF 0xFF) is the one
/// case that yields a literal 0xFF data byte.
/// </summary>
public sealed class TelnetByteFilter
{
    private const byte IAC = 0xFF;
    private const byte SB = 0xFA;  // begin subnegotiation
    private const byte SE = 0xF0;  // end subnegotiation
    private const byte WILL = 0xFB;
    private const byte WONT = 0xFC;
    private const byte DO = 0xFD;
    private const byte DONT = 0xFE;

    private enum S { Normal, Iac, Option, SubNeg, SubNegIac }

    private S _state = S.Normal;

    /// <summary>
    /// Process <paramref name="count"/> bytes from <paramref name="input"/>,
    /// appending the surviving data bytes to <paramref name="sink"/>.
    /// </summary>
    public void Process(byte[] input, int count, List<byte> sink)
    {
        for (int i = 0; i < count; i++)
        {
            byte b = input[i];
            switch (_state)
            {
                case S.Normal:
                    if (b == IAC) _state = S.Iac;
                    else sink.Add(b);
                    break;

                case S.Iac:
                    if (b == IAC) { sink.Add(IAC); _state = S.Normal; }   // escaped literal 0xFF
                    else if (b is WILL or WONT or DO or DONT) _state = S.Option;
                    else if (b == SB) _state = S.SubNeg;
                    else _state = S.Normal;                                // 2-byte command, drop
                    break;

                case S.Option:
                    // The single option byte after WILL/WONT/DO/DONT — drop it.
                    _state = S.Normal;
                    break;

                case S.SubNeg:
                    if (b == IAC) _state = S.SubNegIac;                    // maybe IAC SE
                    break;                                                 // else still inside SB, drop

                case S.SubNegIac:
                    if (b == SE) _state = S.Normal;                        // end of subnegotiation
                    else if (b == IAC) _state = S.SubNegIac;              // escaped 0xFF inside SB, drop
                    else _state = S.SubNeg;                                // back inside SB
                    break;
            }
        }
    }
}
