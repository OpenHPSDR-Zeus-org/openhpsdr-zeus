// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class FrontendDspSceneDiagnosticsServiceTests
{
    [Fact]
    public void Update_StoresSanitizedFrontendSceneForDiagnosticsSnapshot()
    {
        var service = new FrontendDspSceneDiagnosticsService();

        var stored = service.Update(new FrontendDspSceneDiagnosticsRequest(
            SourceClientId: "  client   one  ",
            Mode: "USB",
            SignalProfile: "dx",
            SignalReason: "sparse weak signal",
            SmartNrProfile: "NR2",
            SmartNrReason: "SSB noise profile",
            SmartNrRecommendation: "Hold levels; use Smart NR/filtering",
            SmartNrHeldByRxChain: false,
            SmartNrRxChainLabel: "RX chain optimized",
            MaxSnrDb: 18.24,
            CoherentMaxSnrDb: 17.86,
            OccupiedPct: 4.42,
            CoherentOccupiedPct: 2.36,
            ImpulsivePct: 0.12,
            PeakCount: 3,
            CoherentPeakCount: 2));

        Assert.Equal("client one", stored.SourceClientId);
        Assert.Equal(18.2, stored.MaxSnrDb);
        Assert.Equal(17.9, stored.CoherentMaxSnrDb);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(service.Snapshot()));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.Equal("dx", root.GetProperty("signalProfile").GetString());
        Assert.Equal("NR2", root.GetProperty("smartNrProfile").GetString());
        Assert.Equal(3, root.GetProperty("peakCount").GetInt32());
        Assert.True(root.GetProperty("ageMs").GetInt64() >= 0);
    }
}
