// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Canonical RX2 accessor over the unified <see cref="StateDto.Receivers"/>
/// model. RX2's tuning state (VFO / mode / filter / AF gain) lives
/// authoritatively in the receivers array at index 1 — the flat VFO-B fields
/// (VfoBHz / ModeB / Filter*B / Rx2AfGainDb) were retired in the A/B wire
/// collapse. The flat RX2 *control* fields that remain (Rx2Enabled / Rx2Muted /
/// Rx2AudioMode / shared SampleRate) are overlaid onto the projected entry by
/// <c>RadioService.ProjectReceivers</c>, so this accessor returns the live
/// tuning while those callers re-source the control bits themselves.
/// </summary>
public static class ReceiverProjection
{
    /// <summary>Default RX2 entry used only before the array is first seeded
    /// (construction / hydration). Mirrors the old flat VFO-B defaults so a
    /// pre-seed read is byte-identical to the legacy behaviour.</summary>
    public static readonly ReceiverDto Rx2Seed = new(
        Index: 1, Enabled: false, AdcSource: 0,
        VfoHz: 14_200_000, Mode: RxMode.USB,
        FilterLowHz: 100, FilterHighHz: 2850, FilterPresetName: "VAR1",
        AfGainDb: 0.0, SampleRateHz: 192_000, Muted: false);

    /// <summary>The authoritative RX2 (index 1) receiver entry. Alloc-free (no
    /// LINQ, no enumerator) so realtime callers — the per-tick RX2
    /// <c>SetVfoHz</c> and the CTUN shift computation — stay zero-alloc on the
    /// audio/pipeline thread. Returns <see cref="Rx2Seed"/> if the array is not
    /// yet seeded (should not happen after construction).</summary>
    public static ReceiverDto Rx2(this StateDto s)
    {
        var r = s.Receivers;
        if (r is not null)
        {
            for (int i = 0; i < r.Count; i++)
                if (r[i].Index == 1) return r[i];
        }
        return Rx2Seed;
    }
}
