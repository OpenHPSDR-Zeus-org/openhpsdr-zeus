// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Zeus.Server.FrontPanel;

namespace Zeus.Server.Tests;

public sealed class G2PanelProtocolTests
{
    private static List<PanelEvent> Decode(string s)
    {
        var p = new AndromedaParser();
        var events = new List<PanelEvent>();
        p.Feed(s, events.Add);
        return events;
    }

    [Fact]
    public void Button_press_decodes_id_and_value()
    {
        var ev = Assert.IsType<PanelEvent.Button>(Assert.Single(Decode("ZZZP071;")));
        Assert.Equal(7, ev.Id);   // MOX on the G2-Ultra
        Assert.Equal(1, ev.V);    // pressed
    }

    [Fact]
    public void Encoder_clockwise_is_positive_counterclockwise_is_negative()
    {
        var cw = Assert.IsType<PanelEvent.Encoder>(Assert.Single(Decode("ZZZE063;")));
        Assert.Equal(6, cw.Id);   // DRIVE encoder
        Assert.Equal(3, cw.Ticks);

        // +50 on the id marks counter-clockwise; ticks negate.
        var ccw = Assert.IsType<PanelEvent.Encoder>(Assert.Single(Decode("ZZZE562;")));
        Assert.Equal(6, ccw.Id);
        Assert.Equal(-2, ccw.Ticks);
    }

    [Fact]
    public void Encoder_zero_ticks_is_dropped()
    {
        Assert.Empty(Decode("ZZZE060;"));
    }

    [Theory]
    [InlineData("ZZZU05;", 5)]    // raw 5 -> speedup[5] = 5, up = +5
    [InlineData("ZZZU13;", 17)]   // raw 13 -> speedup[13] = 17
    [InlineData("ZZZD13;", -17)]  // down negates
    [InlineData("ZZZU31;", 124)]  // raw 31 > 30 -> 31 * speedup[31](=4)
    public void Vfo_steps_apply_acceleration_curve(string cmd, int expected)
    {
        var ev = Assert.IsType<PanelEvent.Vfo>(Assert.Single(Decode(cmd)));
        Assert.Equal(expected, ev.Steps);
    }

    [Theory]
    [InlineData(1, "AfRx2")]
    [InlineData(2, "AgcRx2")]
    [InlineData(3, "AfRx1")]
    [InlineData(4, "AgcRx1")]
    [InlineData(5, "Multi")]
    [InlineData(6, "Drive")]
    [InlineData(7, "RitXit")]
    [InlineData(8, "Atten")]
    [InlineData(9, "FilterHigh")]
    [InlineData(10, "FilterLow")]
    [InlineData(11, "DivGain")]
    [InlineData(12, "DivPhase")]
    public void G2_ultra_encoder_map_matches_deskhpsdr_type5_defaults(
        int encoderId,
        string expectedAction)
    {
        Assert.Equal(expectedAction, G2PanelActionRouter.G2UltraEncoderActionName(encoderId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(20)]
    public void G2_ultra_unassigned_encoder_ids_are_ignored(int encoderId)
    {
        Assert.Null(G2PanelActionRouter.G2UltraEncoderActionName(encoderId));
    }

    [Theory]
    [InlineData(1, 0, 1, "ToggleMuteRx2")]
    [InlineData(2, 0, 1, "ToggleMuteRx1")]
    [InlineData(3, 0, 1, "CycleMulti")]
    [InlineData(4, 0, 1, "AtuTune")]
    [InlineData(5, 0, 1, "ToggleTwoTone")]
    [InlineData(6, 0, 1, "ToggleTune")]
    [InlineData(7, 0, 1, "ToggleMox")]
    [InlineData(8, 0, 1, "ToggleCtun")]
    [InlineData(9, 0, 1, "ToggleLock")]
    [InlineData(10, 0, 1, "SwapVfos")]
    [InlineData(11, 0, 1, "CycleRitXit")]
    [InlineData(12, 0, 1, "ClearRitXit")]
    [InlineData(13, 0, 1, "FilterCutDefault")]
    [InlineData(14, 1, 0, "ModePlus")]
    [InlineData(15, 1, 0, "FilterPlus")]
    [InlineData(16, 0, 1, "BandPlus")]
    [InlineData(17, 0, 1, "ModeMinus")]
    [InlineData(18, 0, 1, "FilterMinus")]
    [InlineData(19, 0, 1, "BandMinus")]
    [InlineData(20, 0, 1, "CopyAtoB")]
    [InlineData(21, 0, 1, "CopyBtoA")]
    [InlineData(22, 0, 1, "ToggleSplit")]
    [InlineData(23, 1, 0, "ToggleSnb")]
    [InlineData(24, 1, 0, "ToggleNb")]
    [InlineData(25, 1, 0, "CycleNr")]
    [InlineData(27, 0, 1, "Band160")]
    [InlineData(28, 0, 1, "Band80")]
    [InlineData(29, 0, 1, "Band60")]
    [InlineData(30, 0, 1, "Band40")]
    [InlineData(31, 0, 1, "Band30")]
    [InlineData(32, 0, 1, "Band20")]
    [InlineData(33, 0, 1, "Band17")]
    [InlineData(34, 0, 1, "Band15")]
    [InlineData(35, 0, 1, "Band12")]
    [InlineData(36, 0, 1, "Band10")]
    [InlineData(37, 0, 1, "Band6")]
    [InlineData(38, 0, 1, "BandLfMf")]
    [InlineData(41, 0, 1, "ToggleDiversity")]
    public void G2_ultra_button_map_matches_deskhpsdr_type5_defaults(
        int buttonId,
        int previousValue,
        int value,
        string expectedAction)
    {
        Assert.Equal(expectedAction, G2PanelActionRouter.G2UltraButtonActionNameForTransition(
            buttonId,
            previousValue,
            value));
    }

    [Theory]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(25)]
    public void G2_ultra_menu_long_press_transitions_are_ignored_by_zeus(int buttonId)
    {
        Assert.Null(G2PanelActionRouter.G2UltraButtonActionNameForTransition(buttonId, 1, 2));
    }

    [Theory]
    [InlineData(26)]
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(42)]
    public void G2_ultra_reserved_buttons_are_ignored(int buttonId)
    {
        Assert.Null(G2PanelActionRouter.G2UltraButtonActionNameForTransition(buttonId, 0, 1));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(10, 1)]
    [InlineData(50, 5)]
    [InlineData(100, 10)]
    [InlineData(500, 50)]
    [InlineData(1000, 60)]
    [InlineData(5000, 60)]
    [InlineData(0, 50)]
    public void Vfo_encoder_divisor_scales_with_configured_step(int stepHz, int expected)
    {
        Assert.Equal(expected, G2PanelActionRouter.VfoEncoderDivisorForStep(stepHz));
    }

    [Theory]
    [InlineData(0, 49, 500, 0, 49)]
    [InlineData(49, 1, 500, 1, 0)]
    [InlineData(0, 128, 500, 2, 28)]
    [InlineData(28, 22, 500, 1, 0)]
    [InlineData(0, -49, 500, 0, -49)]
    [InlineData(-49, -1, 500, -1, 0)]
    [InlineData(0, 9, 10, 9, 0)]
    public void Vfo_encoder_divider_preserves_remainder_between_flushes(
        long accumulated,
        long incoming,
        int stepHz,
        long expectedLogicalSteps,
        long expectedRemainder)
    {
        var actual = G2PanelActionRouter.DivideVfoEncoderTicks(accumulated, incoming, stepHz);

        Assert.Equal(expectedLogicalSteps, actual.LogicalSteps);
        Assert.Equal(expectedRemainder, actual.RemainderTicks);
    }

    [Theory]
    [InlineData(7_145_000, 2, 500, 7_146_000)]
    [InlineData(7_145_800, 1, 1000, 7_147_000)]
    [InlineData(7_145_800, -1, 1000, 7_145_000)]
    [InlineData(7_145_800, 17, 1000, 7_163_000)]
    [InlineData(7_145_000, 1, 0, 7_145_500)]
    [InlineData(10, -10, 500, 0)]
    public void Vfo_steps_use_configured_step_and_snap_to_grid(
        long currentHz,
        long steps,
        int stepHz,
        long expected)
    {
        Assert.Equal(expected, G2PanelActionRouter.ApplyVfoSteps(currentHz, steps, stepHz));
    }

    [Fact]
    public void Version_reports_console_type()
    {
        var ev = Assert.IsType<PanelEvent.Version>(Assert.Single(Decode("ZZZS0500abc;")));
        Assert.Equal(5, ev.Type); // G2 Ultra (Mk2)
    }

    [Fact]
    public void Commands_split_across_feed_boundaries_are_reassembled()
    {
        var p = new AndromedaParser();
        var events = new List<PanelEvent>();
        p.Feed("ZZ", events.Add);
        p.Feed("ZP14", events.Add);
        p.Feed("1;ZZZP190;", events.Add); // two commands, one straddling the split
        Assert.Equal(2, events.Count);
        Assert.Equal(14, ((PanelEvent.Button)events[0]).Id);
        Assert.Equal(19, ((PanelEvent.Button)events[1]).Id);
    }

    [Fact]
    public void Junk_and_unknown_commands_are_ignored_without_desync()
    {
        // CR/LF stripped, an unrelated CAT query dropped, real command still seen.
        var events = Decode("\r\nFA00014250000;ZZZP081;");
        var ev = Assert.IsType<PanelEvent.Button>(Assert.Single(events));
        Assert.Equal(8, ev.Id);
    }

    [Fact]
    public void Led_command_is_zero_padded()
    {
        Assert.Equal("ZZZI011;", AndromedaParser.LedCommand(1, true));
        Assert.Equal("ZZZI120;", AndromedaParser.LedCommand(12, false));
    }
}
