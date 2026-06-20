// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// TX / PureSignal snapshot. STRICTLY READ-ONLY — this probe reads the cached
/// StateDto (drive %, TUN %, PsEnabled, PS hardware-peak, feedback attenuation)
/// and the per-board rated watts, and NEVER calls any setter, arm/disarm, or
/// calibration path. PureSignal is a burn-zone subsystem: reading
/// <c>PsEnabled</c> is permitted, mutating PS state is a hard violation, so
/// this probe only inspects the snapshot the server already published.
/// </summary>
public sealed class TxPsProbe : IDiagnosticProbe
{
    public string Id => "tx-ps";

    public DiagnosticSection Collect(DiagnosticContext ctx)
    {
        var items = new List<DiagnosticKeyValue>();
        try
        {
            var radio = ctx.Services.GetService<RadioService>();
            if (radio is null)
            {
                items.Add(new("status", "unavailable"));
                return new DiagnosticSection(Id, "TX & PureSignal", items);
            }

            var state = radio.Snapshot();

            // ---- Drive / TUN drive (operator slider positions, %) ----
            items.Add(new("tx.drivePct", state.DrivePct.ToString()));
            items.Add(new("tx.tunePct", state.TunePct.ToString()));
            items.Add(new("tx.moxPreKeyDelayMs", radio.TxMoxPreKeyDelayMs.ToString()));

            // ---- Rated power for the connected board (read-only static) ----
            var board = radio.ConnectedBoardKind;
            var variant = radio.EffectiveOrionMkIIVariant;
            items.Add(new("tx.maxPowerWatts", PaDefaults.GetMaxPowerWatts(board, variant).ToString()));

            // MOX / TUN: RadioService exposes these only as change events +
            // setters, so there is no read-only runtime getter to sample here.
            items.Add(new("tx.moxState", "not exposed (event/setter only)"));
            items.Add(new("tx.tunState", "not exposed (event/setter only)"));

            // ---- PureSignal (READ-ONLY) ----
            // PsEnabled is always published false at startup by design (the
            // standing PS auto-arm safety invariant); we report the snapshot
            // value verbatim and never write it.
            items.Add(new("ps.enabled", state.PsEnabled ? "true" : "false"));
            items.Add(new("ps.hwPeak", state.PsHwPeak.ToString("0.####")));
            items.Add(new("ps.hwPeakDefault", state.PsHwPeakDefault.ToString("0.####")));
            items.Add(new("ps.txFeedbackAttenuationDb", state.PsTxFeedbackAttenuationDb.ToString()));
            items.Add(new("ps.txFeedbackAttenuationDbMin", state.PsTxFeedbackAttenuationDbMin.ToString()));
            items.Add(new("ps.feedbackSource", state.PsFeedbackSource.ToString()));
            items.Add(new("ps.auto", state.PsAuto ? "true" : "false"));
            items.Add(new("ps.autoAttenuate", state.PsAutoAttenuate ? "true" : "false"));
            items.Add(new("ps.correcting", state.PsCorrecting ? "true" : "false"));
            items.Add(new("ps.calibrationStalled", state.PsCalibrationStalled ? "true" : "false"));
        }
        catch (Exception ex)
        {
            items.Add(new("status", "error"));
            items.Add(new("error", ex.GetType().Name));
        }

        return new DiagnosticSection(Id, "TX & PureSignal", items);
    }
}
