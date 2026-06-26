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
