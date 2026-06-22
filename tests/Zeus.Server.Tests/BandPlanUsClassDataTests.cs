// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// Guards the shipped US FCC class band-plan data (issue #846). These plans drive
// a "are you legal to transmit here?" overlay, so a wrong sub-band edge is a real
// defect. Sources cross-checked against eCFR 47 CFR Part 97 (§97.301/§97.305/
// §97.313) and the ARRL US Amateur Radio Bands chart. The US plans are fully
// specified (no reliance on the IARU_R2 parent), so asserting directly on each
// file's own segments reflects the resolved privilege for every covered range.

using System.Text.Json;
using Xunit;

namespace Zeus.Server.Tests;

public class BandPlanUsClassDataTests
{
    private sealed record Seg(long LowHz, long HighHz, string Allocation, string ModeRestriction, int? MaxPowerW);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static string BandPlansDir()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "Zeus.Server.Hosting", "BandPlans");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("Could not locate Zeus.Server.Hosting/BandPlans from " + AppContext.BaseDirectory);
    }

    private static List<Seg> Load(string regionId)
    {
        var path = Path.Combine(BandPlansDir(), $"{regionId}.segments.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var arr = doc.RootElement.GetProperty("segments");
        return JsonSerializer.Deserialize<List<Seg>>(arr.GetRawText(), Json)!;
    }

    private static Seg? At(List<Seg> segs, long hz) =>
        segs.FirstOrDefault(s => s.LowHz <= hz && hz < s.HighHz);

    [Theory]
    [InlineData("US_FCC_TECHNICIAN")]
    [InlineData("US_FCC_GENERAL")]
    [InlineData("US_FCC_ADVANCED")]
    [InlineData("US_FCC_EXTRA")]
    public void Plan_is_sorted_and_non_overlapping(string regionId)
    {
        var segs = Load(regionId);
        Assert.NotEmpty(segs);
        for (int i = 1; i < segs.Count; i++)
        {
            Assert.True(segs[i].LowHz < segs[i].HighHz, $"{regionId}[{i}] low>=high");
            Assert.True(segs[i].LowHz >= segs[i - 1].HighHz, $"{regionId}[{i}] overlaps/out-of-order");
        }
    }

    // (regionId, freqHz, expectedAllocation, expectedMode, expectedMaxPowerW)
    [Theory]
    // Technician — legacy Novice HF at 200 W; excluded bands are non-Amateur.
    [InlineData("US_FCC_TECHNICIAN", 3_550_000, "Amateur", "CwOnly", 200)]    // 80m CW
    [InlineData("US_FCC_TECHNICIAN", 3_850_000, "Reserved", "Any", null)]     // 75m phone: no priv
    [InlineData("US_FCC_TECHNICIAN", 14_200_000, "Reserved", "Any", null)]    // 20m: none
    [InlineData("US_FCC_TECHNICIAN", 28_150_000, "Amateur", "CwAndDigital", 200)] // 10m data
    [InlineData("US_FCC_TECHNICIAN", 28_350_000, "Amateur", "PhoneOnly", 200)]    // 10m phone
    [InlineData("US_FCC_TECHNICIAN", 28_600_000, "Reserved", "Any", null)]    // 10m >28.5: none
    [InlineData("US_FCC_TECHNICIAN", 52_000_000, "Amateur", "Any", null)]     // 6m: full
    // General — the corrected phone edges (the old data had 75m at 3.600).
    [InlineData("US_FCC_GENERAL", 3_510_000, "Reserved", "Any", null)]        // 80m bottom: Extra-only
    [InlineData("US_FCC_GENERAL", 3_700_000, "Reserved", "Any", null)]        // 75m <3.8: no priv
    [InlineData("US_FCC_GENERAL", 3_850_000, "Amateur", "PhoneOnly", null)]   // 75m phone OK
    [InlineData("US_FCC_GENERAL", 7_150_000, "Reserved", "Any", null)]        // 40m 7.125–7.175: Adv/Extra
    [InlineData("US_FCC_GENERAL", 7_200_000, "Amateur", "PhoneOnly", null)]   // 40m phone OK
    [InlineData("US_FCC_GENERAL", 14_200_000, "Reserved", "Any", null)]       // 20m <14.225: no priv
    [InlineData("US_FCC_GENERAL", 14_250_000, "Amateur", "PhoneOnly", null)]
    [InlineData("US_FCC_GENERAL", 10_125_000, "Amateur", "CwAndDigital", 200)] // 30m 200W
    // Advanced — distinct edges between General and Extra.
    [InlineData("US_FCC_ADVANCED", 3_650_000, "Reserved", "Any", null)]       // 3.6–3.7: Extra-only
    [InlineData("US_FCC_ADVANCED", 3_750_000, "Amateur", "PhoneOnly", null)]  // 75m phone from 3.7
    [InlineData("US_FCC_ADVANCED", 14_160_000, "Reserved", "Any", null)]      // 14.15–14.175: Extra-only
    [InlineData("US_FCC_ADVANCED", 14_180_000, "Amateur", "PhoneOnly", null)] // 20m phone from 14.175
    // Extra — widest; bottom slices.
    [InlineData("US_FCC_EXTRA", 3_510_000, "Amateur", "CwAndDigital", null)]
    [InlineData("US_FCC_EXTRA", 3_650_000, "Amateur", "PhoneOnly", null)]     // phone from 3.6
    [InlineData("US_FCC_EXTRA", 14_160_000, "Amateur", "PhoneOnly", null)]    // phone from 14.15
    [InlineData("US_FCC_EXTRA", 7_010_000, "Amateur", "CwAndDigital", null)]
    public void Privilege_at_frequency_matches_FCC(string regionId, long hz, string alloc, string mode, int? power)
    {
        var seg = At(Load(regionId), hz);
        Assert.NotNull(seg);
        Assert.Equal(alloc, seg!.Allocation);
        Assert.Equal(mode, seg.ModeRestriction);
        Assert.Equal(power, seg.MaxPowerW);
    }
}
