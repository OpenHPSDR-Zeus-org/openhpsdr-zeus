// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// Board identity + static fingerprint. Reads <see cref="RadioService"/>'s
/// <c>ConnectedBoardKind</c> / <c>EffectiveOrionMkIIVariant</c>, then resolves
/// the read-only per-board capability table, PA defaults (rated watts + PA
/// gain), and the forward-power calibration bucket. All lookups are pure
/// static functions over the board kind — no radio I/O, no mutation.
/// </summary>
public sealed class BoardProbe : IDiagnosticProbe
{
    public string Id => "board";

    public DiagnosticSection Collect(DiagnosticContext ctx)
    {
        var items = new List<DiagnosticKeyValue>();
        try
        {
            var radio = ctx.Services.GetService<RadioService>();
            if (radio is null)
            {
                items.Add(new("status", "unavailable"));
                return new DiagnosticSection(Id, "Board", items);
            }

            var board = radio.ConnectedBoardKind;
            var variant = radio.EffectiveOrionMkIIVariant;

            items.Add(new("board.kind", board.ToString()));
            // Variant only disambiguates the 0x0A (OrionMkII) wire-byte family.
            if (board == HpsdrBoardKind.OrionMkII)
                items.Add(new("board.variant", variant.ToString()));

            // Static capability fingerprint (Zeus.Contracts.BoardCapabilities).
            var caps = BoardCapabilitiesTable.For(board, variant);
            items.Add(new("caps.rxAdcCount", caps.RxAdcCount.ToString()));
            items.Add(new("caps.mkiiBpf", caps.MkiiBpf ? "true" : "false"));
            items.Add(new("caps.adcSupplyMv", caps.AdcSupplyMv.ToString()));
            items.Add(new("caps.lrAudioSwap", caps.LrAudioSwap ? "true" : "false"));
            items.Add(new("caps.hasVolts", caps.HasVolts ? "true" : "false"));
            items.Add(new("caps.hasAmps", caps.HasAmps ? "true" : "false"));
            items.Add(new("caps.hasAudioAmplifier", caps.HasAudioAmplifier ? "true" : "false"));
            items.Add(new("caps.hasSteppedAttenuationRx2", caps.HasSteppedAttenuationRx2 ? "true" : "false"));
            items.Add(new("caps.supportsPathIllustrator", caps.SupportsPathIllustrator ? "true" : "false"));
            items.Add(new("caps.maxPowerWatts", caps.MaxPowerWatts.ToString()));
            items.Add(new("caps.maxRxSampleRateHz", caps.MaxRxSampleRateHz.ToString()));
            items.Add(new("caps.hasHl2OptionalToggles", caps.HasHl2OptionalToggles ? "true" : "false"));
            items.Add(new("caps.supportsG2AdcOptions", caps.SupportsG2AdcOptions ? "true" : "false"));
            items.Add(new("caps.supportsAnvelinaDxOc", caps.SupportsAnvelinaDxOc ? "true" : "false"));

            // PA defaults (read-only static seeds).
            items.Add(new("pa.maxPowerWatts", PaDefaults.GetMaxPowerWatts(board, variant).ToString()));

            // Forward-power calibration bucket for this board / variant.
            var cal = RadioCalibrations.For(board, variant);
            items.Add(new("cal.bridgeVolt", cal.BridgeVolt.ToString("0.###")));
            items.Add(new("cal.refVoltage", cal.RefVoltage.ToString("0.###")));
            items.Add(new("cal.adcCalOffset", cal.AdcCalOffset.ToString()));
            items.Add(new("cal.maxWatts", cal.MaxWatts.ToString("0.###")));
        }
        catch (Exception ex)
        {
            items.Add(new("status", "error"));
            items.Add(new("error", ex.GetType().Name));
        }

        return new DiagnosticSection(Id, "Board", items);
    }
}
