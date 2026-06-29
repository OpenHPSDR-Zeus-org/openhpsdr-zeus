// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Unit tests for <see cref="VstChainDiff"/> — the incremental VST-engine chain
/// diff that replaced the unconditional full <c>load_chain</c> on every edit.
/// Each emitted command is validated two ways: representative op sequences are
/// asserted directly, and an engine-semantics simulator applies the commands to
/// the current chain and asserts the result equals the desired order (so the
/// generated indices are provably correct, never out of range).
/// </summary>
public class VstChainDiffTests
{
    private static VstChainDiff.DesiredSlot Slot(string id, string state = "") =>
        // uid == id so the simulator can map add_plugin back to a Zeus id.
        new(id, File: "f_" + id, Uid: id, State: state);

    private static List<VstChainDiff.DesiredSlot> Desired(params string[] ids)
    {
        var list = new List<VstChainDiff.DesiredSlot>();
        foreach (var id in ids) list.Add(Slot(id));
        return list;
    }

    /// <summary>Apply the emitted commands to <paramref name="current"/> using the
    /// engine's documented slot semantics, returning the resulting id order.</summary>
    private static List<string> Apply(IReadOnlyList<string> current, IReadOnlyList<object> cmds)
    {
        var work = new List<string>(current);
        foreach (var c in cmds)
        {
            var e = JsonSerializer.SerializeToElement(c);
            switch (e.GetProperty("cmd").GetString())
            {
                case "remove_plugin":
                    work.RemoveAt(e.GetProperty("index").GetInt32());
                    break;
                case "add_plugin":
                    work.Insert(e.GetProperty("index").GetInt32(), e.GetProperty("uid").GetString()!);
                    break;
                case "move_plugin":
                    var from = e.GetProperty("from").GetInt32();
                    var to = e.GetProperty("to").GetInt32();
                    var moved = work[from];
                    work.RemoveAt(from);
                    work.Insert(to, moved);
                    break;
                case "set_plugin_state":
                    break; // no order effect
                default:
                    Assert.Fail($"unexpected cmd {e.GetProperty("cmd").GetString()}");
                    break;
            }
        }
        return work;
    }

    private static string CmdName(object c) =>
        JsonSerializer.SerializeToElement(c).GetProperty("cmd").GetString()!;

    [Fact]
    public void EmptyCurrent_ReturnsNull_SoCallerFullLoads()
    {
        Assert.Null(VstChainDiff.Compute(Array.Empty<string>(), Desired("a", "b")));
    }

    [Fact]
    public void IdenticalChains_ReturnEmpty_NoRedundantReload()
    {
        var cmds = VstChainDiff.Compute(new[] { "a", "b", "c" }, Desired("a", "b", "c"));
        Assert.NotNull(cmds);
        Assert.Empty(cmds!);
    }

    [Fact]
    public void AppendOnePlugin_EmitsSingleAddAtEnd()
    {
        var current = new[] { "a", "b" };
        var cmds = VstChainDiff.Compute(current, Desired("a", "b", "c"))!;
        Assert.Single(cmds);
        Assert.Equal("add_plugin", CmdName(cmds[0]));
        var add = JsonSerializer.SerializeToElement(cmds[0]);
        Assert.Equal(2, add.GetProperty("index").GetInt32());
        Assert.Equal("c", add.GetProperty("uid").GetString());
        Assert.Equal("f_c", add.GetProperty("file").GetString());
        Assert.Equal(new[] { "a", "b", "c" }, Apply(current, cmds));
    }

    [Fact]
    public void AddPluginWithState_EmitsAddThenSetState()
    {
        var current = new[] { "a" };
        var desired = new List<VstChainDiff.DesiredSlot> { Slot("a"), Slot("b", state: "BLOB==") };
        var cmds = VstChainDiff.Compute(current, desired)!;
        Assert.Equal(2, cmds.Count);
        Assert.Equal("add_plugin", CmdName(cmds[0]));
        Assert.Equal("set_plugin_state", CmdName(cmds[1]));
        var st = JsonSerializer.SerializeToElement(cmds[1]);
        Assert.Equal(1, st.GetProperty("index").GetInt32());
        Assert.Equal("BLOB==", st.GetProperty("state").GetString());
    }

    [Fact]
    public void AddPluginWithoutState_DoesNotEmitSetState()
    {
        var cmds = VstChainDiff.Compute(new[] { "a" }, Desired("a", "b"))!;
        Assert.Single(cmds);
        Assert.Equal("add_plugin", CmdName(cmds[0]));
    }

    [Fact]
    public void RemoveMiddlePlugin_EmitsSingleRemoveAtCorrectIndex()
    {
        var current = new[] { "a", "b", "c" };
        var cmds = VstChainDiff.Compute(current, Desired("a", "c"))!;
        Assert.Single(cmds);
        Assert.Equal("remove_plugin", CmdName(cmds[0]));
        Assert.Equal(1, JsonSerializer.SerializeToElement(cmds[0]).GetProperty("index").GetInt32());
        Assert.Equal(new[] { "a", "c" }, Apply(current, cmds));
    }

    [Theory]
    [InlineData("a,b,c", "c,b,a")]              // full reverse
    [InlineData("a,b,c,d", "a,c,b,d")]          // adjacent swap
    [InlineData("a,b,c", "b,c,a")]              // rotate
    [InlineData("a,b,c,d", "d,a,b,c")]          // move last to front
    public void Reorder_ProducesDesiredOrder(string currentCsv, string desiredCsv)
    {
        var current = currentCsv.Split(',');
        var desiredIds = desiredCsv.Split(',');
        var cmds = VstChainDiff.Compute(current, Desired(desiredIds))!;
        // Pure reorder uses only move_plugin (no add/remove).
        Assert.All(cmds, c => Assert.Equal("move_plugin", CmdName(c)));
        Assert.Equal(desiredIds, Apply(current, cmds));
    }

    [Theory]
    [InlineData("a,b,c", "a,b,c")]              // no-op
    [InlineData("a,b,c", "x,y,z")]              // full replacement
    [InlineData("a,b,c,d,e", "e,c,f")]          // mixed remove + add + reorder
    [InlineData("comp,eq,gate", "gate,comp,reverb")]
    public void MixedEdits_ApplyToDesiredOrder(string currentCsv, string desiredCsv)
    {
        var current = currentCsv.Split(',');
        var desiredIds = desiredCsv.Split(',');
        var cmds = VstChainDiff.Compute(current, Desired(desiredIds));
        Assert.NotNull(cmds);
        Assert.Equal(desiredIds, Apply(current, cmds!));
    }

    [Fact]
    public void RemoveAllPlugins_EmitsRemovesAndEndsEmpty()
    {
        var current = new[] { "a", "b" };
        var cmds = VstChainDiff.Compute(current, Desired())!;
        Assert.All(cmds, c => Assert.Equal("remove_plugin", CmdName(c)));
        Assert.Empty(Apply(current, cmds));
    }
}
