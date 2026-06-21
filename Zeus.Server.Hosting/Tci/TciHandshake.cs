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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Zeus.Contracts;

namespace Zeus.Server.Tci;

/// <summary>
/// Builds the TCI handshake command sequence sent immediately after WebSocket
/// upgrade. Each command is a self-contained semicolon-terminated string; the
/// caller sends each as its own WebSocket text frame, matching Thetis
/// (TCIServer.cs sendTextFrame) and the ExpertSDR3 TCI 2.0 wire convention.
/// Order matters: protocol/device first, audio-stream negotiation before any
/// state, then start; ready; last.
/// </summary>
public static class TciHandshake
{
    public static IReadOnlyList<string> BuildHandshake(StateDto state, int sampleRate, bool moxOn, bool tunOn, int drivePercent)
    {
        var cmds = new List<string>(40);

        cmds.Add(TciProtocol.Command("protocol", TciProtocol.ProtocolName, TciProtocol.ProtocolVersion));
        cmds.Add(TciProtocol.Command("device", TciProtocol.DeviceName));

        cmds.Add(TciProtocol.Command("receive_only", false));
        cmds.Add(TciProtocol.Command("trx_count", 1));
        cmds.Add(TciProtocol.Command("channels_count", 1));

        cmds.Add(TciProtocol.Command("vfo_limits", 0, 61_440_000));

        int halfRate = sampleRate / 2;
        cmds.Add(TciProtocol.Command("if_limits", -halfRate, halfRate));

        cmds.Add(TciProtocol.Command("modulations_list", "AM,SAM,DSB,LSB,USB,CWL,CWU,FM,DIGL,DIGU,SPEC,DRM"));

        cmds.Add(TciProtocol.Command("iq_samplerate", sampleRate));
        cmds.Add(TciProtocol.Command("audio_samplerate", 48000));

        // Audio stream negotiation — default to channels=2 (stereo) for broad
        // TCI client compatibility; clients may opt into channels=1 later.
        cmds.Add(TciProtocol.Command("audio_stream_sample_type", "float32"));
        cmds.Add(TciProtocol.Command("audio_stream_channels", 2));
        cmds.Add(TciProtocol.Command("audio_stream_samples", 2048));
        cmds.Add(TciProtocol.Command("tx_stream_audio_buffering", 50));

        // Master volume tracks RxAfGainDb so TCI clients see the live value
        // immediately on connect. mon_volume is the TX sidetone bus (TCI spec
        // §5.5) — independent of RX audio gain — so it stays a placeholder
        // until Zeus has a real monitor path.
        cmds.Add(TciProtocol.Command("volume", (int)Math.Round(state.RxAfGainDb)));
        cmds.Add(TciProtocol.Command("mute", false));
        cmds.Add(TciProtocol.Command("mon_volume", -20));
        cmds.Add(TciProtocol.Command("mon_enable", false));

        cmds.Add(TciProtocol.Command("dds", 0, CwOffset.EffectiveLoHz(state)));
        cmds.Add(TciProtocol.Command("if", 0, 0, 0));
        cmds.Add(TciProtocol.Command("if", 0, 1, 0));
        cmds.Add(TciProtocol.Command("vfo", 0, 0, state.VfoHz));
        cmds.Add(TciProtocol.Command("vfo", 0, 1, state.VfoHz));

        string tciMode = TciProtocol.ModeToTci(state.Mode);
        cmds.Add(TciProtocol.Command("modulation", 0, tciMode));

        cmds.Add(TciProtocol.Command("rx_enable", 0, true));
        cmds.Add(TciProtocol.Command("split_enable", 0, false));
        cmds.Add(TciProtocol.Command("tx_enable", 0, moxOn || tunOn));
        cmds.Add(TciProtocol.Command("trx", 0, moxOn));
        cmds.Add(TciProtocol.Command("tune", 0, tunOn));

        cmds.Add(TciProtocol.Command("rx_mute", 0, false));
        cmds.Add(TciProtocol.Command("rx_filter_band", 0, state.FilterLowHz, state.FilterHighHz));

        cmds.Add(TciProtocol.Command("drive", 0, drivePercent));
        cmds.Add(TciProtocol.Command("tune_drive", 0, drivePercent));

        cmds.Add(TciProtocol.Command("tx_frequency", state.VfoHz));

        cmds.Add(TciProtocol.Command("start"));
        cmds.Add(TciProtocol.Command("ready"));

        return cmds;
    }
}
