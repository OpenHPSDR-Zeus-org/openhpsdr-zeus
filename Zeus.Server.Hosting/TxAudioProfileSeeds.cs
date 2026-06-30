// SPDX-License-Identifier: GPL-2.0-or-later
//
// Starter TX Audio Profiles. These replace the old fixed 3-up TX station
// profiles (studio-ssb / essb / dx) — now seeded as named, EDITABLE profiles so
// nothing is lost when the fixed system is retired. Values are a 1:1 port of
// the former frontend constants in zeus-web/src/audio/tx-station-profile.ts
// (mic/leveler/leveling/CFC/bandpass/density). Seeded once into an empty
// collection; an operator who deletes a starter does not see it resurrected.

using Zeus.Contracts;

namespace Zeus.Server;

public static class TxAudioProfileSeeds
{
    private static readonly DateTime SeedUtc = DateTime.UtcNow;

    public static IReadOnlyList<TxAudioProfileDto> Starters { get; } = new[]
    {
        StudioSsb(),
        EssbWide(),
        DxPunch(),
    };

    private static TxAudioProfileDto StudioSsb() => new(
        Id: "studio-ssb",
        Name: "Studio SSB",
        MicGainDb: 0,
        LevelerMaxGainDb: 8,
        TxLeveling: new TxLevelingConfig(
            AlcMaxGainDb: 3, AlcDecayMs: 10,
            LevelerEnabled: true, LevelerDecayMs: 100,
            CompressorEnabled: false, CompressorGainDb: 0),
        CfcConfig: new CfcConfig(
            Enabled: true, PostEqEnabled: true, PreCompDb: 1.25, PrePeqDb: 0,
            Bands: new[]
            {
                new CfcBand(80,   0.5, -4),
                new CfcBand(150,  1,   -2),
                new CfcBand(250,  2,   -1),
                new CfcBand(500,  3,    0),
                new CfcBand(900,  4,    0.5),
                new CfcBand(1500, 5,    1),
                new CfcBand(2200, 4.5,  1.5),
                new CfcBand(2800, 3.5,  1.5),
                new CfcBand(3500, 2,   -1),
                new CfcBand(5000, 1,   -3),
            }),
        TxPhaseRotator: TxPhaseRotatorConfig.ThetisVoiceDefault(),
        LowCutHz: 150, HighCutHz: 2900,
        ProcessingMode: "native",
        MasterBypass: false,
        ChainOrder: new List<string>(),
        ChainParked: new List<string>(),
        VstPluginStates: new Dictionary<string, string>(),
        NativePluginStates: new Dictionary<string, Dictionary<string, string>>(),
        TargetSpectralDensity: 55,
        CreatedUtc: SeedUtc, UpdatedUtc: SeedUtc);

    private static TxAudioProfileDto EssbWide() => new(
        Id: "essb-wide",
        Name: "eSSB Wide",
        MicGainDb: -4,
        LevelerMaxGainDb: 11,
        TxLeveling: new TxLevelingConfig(
            AlcMaxGainDb: 3, AlcDecayMs: 10,
            LevelerEnabled: true, LevelerDecayMs: 180,
            CompressorEnabled: false, CompressorGainDb: 0),
        CfcConfig: new CfcConfig(
            Enabled: true, PostEqEnabled: true, PreCompDb: 1.5, PrePeqDb: 0,
            Bands: new[]
            {
                new CfcBand(50,   0.5, -1.6),
                new CfcBand(90,   1.5,  1.8),
                new CfcBand(140,  2.6,  2.6),
                new CfcBand(220,  3.8,  2.3),
                new CfcBand(400,  4.8,  0.8),
                new CfcBand(750,  5.6, -0.4),
                new CfcBand(1200, 5.4,  0),
                new CfcBand(2200, 4.8,  1.2),
                new CfcBand(3400, 3.4,  1.4),
                new CfcBand(5000, 2.1, -1.2),
            }),
        TxPhaseRotator: TxPhaseRotatorConfig.ThetisVoiceDefault(),
        LowCutHz: 40, HighCutHz: 5000,
        ProcessingMode: "native",
        MasterBypass: false,
        ChainOrder: new List<string>(),
        ChainParked: new List<string>(),
        VstPluginStates: new Dictionary<string, string>(),
        NativePluginStates: new Dictionary<string, Dictionary<string, string>>(),
        TargetSpectralDensity: 100,
        CreatedUtc: SeedUtc, UpdatedUtc: SeedUtc);

    private static TxAudioProfileDto DxPunch() => new(
        Id: "dx-punch",
        Name: "DX Punch",
        MicGainDb: -2,
        LevelerMaxGainDb: 12,
        TxLeveling: new TxLevelingConfig(
            AlcMaxGainDb: 3, AlcDecayMs: 8,
            LevelerEnabled: true, LevelerDecayMs: 70,
            CompressorEnabled: false, CompressorGainDb: 0),
        CfcConfig: new CfcConfig(
            Enabled: true, PostEqEnabled: true, PreCompDb: 2.25, PrePeqDb: 0,
            Bands: new[]
            {
                new CfcBand(120,  1,   -5),
                new CfcBand(220,  2.2, -3.2),
                new CfcBand(350,  3.4, -1.4),
                new CfcBand(550,  4.6,  0.6),
                new CfcBand(850,  5.8,  1.5),
                new CfcBand(1200, 6.4,  2.3),
                new CfcBand(1700, 6.2,  2.8),
                new CfcBand(2300, 5.6,  2.2),
                new CfcBand(2850, 4.2,  0.4),
                new CfcBand(3600, 2.4, -3.6),
            }),
        TxPhaseRotator: TxPhaseRotatorConfig.ThetisVoiceDefault(),
        LowCutHz: 300, HighCutHz: 2850,
        ProcessingMode: "native",
        MasterBypass: false,
        ChainOrder: new List<string>(),
        ChainParked: new List<string>(),
        VstPluginStates: new Dictionary<string, string>(),
        NativePluginStates: new Dictionary<string, Dictionary<string, string>>(),
        TargetSpectralDensity: 100,
        CreatedUtc: SeedUtc, UpdatedUtc: SeedUtc);
}
