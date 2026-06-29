// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server;

/// <summary>
/// Computes the minimal sequence of incremental VST-engine control commands that
/// transforms the engine's CURRENT loaded chain into a DESIRED chain, so a live
/// operator edit (add / remove / reorder a single plugin) no longer tears down
/// and re-instantiates the entire chain via <c>load_chain</c>.
///
/// <para><b>Why this exists:</b> <c>load_chain</c> "clears + parallel-loads" every
/// plugin (see <c>docs/designs/vst-engine-bridge-protocol.md</c> §2.1). Sending it
/// on every chain edit means adding one VST re-instantiates the whole rack — slow
/// when a heavy plugin (e.g. a Waves shell) is already loaded, and it drops every
/// running plugin's internal DSP state (compressor envelopes, reverb tails,
/// lookahead), punching an audible gap into TX/RX audio on each edit. The engine
/// already exposes cheap incremental ops (<c>add_plugin</c> / <c>remove_plugin</c>
/// / <c>move_plugin</c>); this just uses them.</para>
///
/// <para><b>Determinism:</b> control commands are dispatched serially on the
/// engine's message thread in send order (protocol §2), so the indices each
/// command carries are computed against a local simulation of the chain AS THE
/// ENGINE WILL SEE IT when that command runs — independent of when the engine's
/// asynchronous <c>chain</c> events arrive back. A botched sequence is self-healing
/// anyway: the next <c>chain</c> event reconciles the id→slot map.</para>
/// </summary>
internal static class VstChainDiff
{
    /// <summary>One slot in the desired chain: the Zeus plugin id, its absolute
    /// VST3 file path, the descriptor uid (empty = single-plugin file), and the
    /// last-known opaque base64 state ("" = load defaults).</summary>
    internal readonly record struct DesiredSlot(string Id, string File, string Uid, string State);

    /// <summary>
    /// Build the ordered incremental commands to turn <paramref name="current"/>
    /// (engine slot order, by Zeus id) into <paramref name="desired"/>. Each item
    /// is an anonymous object ready for <c>VstEngineController.SendCommand</c>.
    ///
    /// <para>Returns <c>null</c> when an incremental diff is unsafe / pointless and
    /// the caller should fall back to a full <c>load_chain</c> (no current chain to
    /// diff against, or a duplicate id in either list). Returns an EMPTY list when
    /// the chains already match — the caller should then send nothing (skipping the
    /// redundant reload that the old code performed unconditionally).</para>
    /// </summary>
    internal static List<object>? Compute(
        IReadOnlyList<string> current,
        IReadOnlyList<DesiredSlot> desired)
    {
        // Nothing loaded yet → a fresh load_chain is simpler and just as cheap.
        if (current.Count == 0) return null;

        // Duplicate ids would make index math ambiguous; reload is the safe path.
        var currentSet = new HashSet<string>(current, StringComparer.Ordinal);
        if (currentSet.Count != current.Count) return null;

        var desiredById = new Dictionary<string, DesiredSlot>(StringComparer.Ordinal);
        foreach (var d in desired) desiredById[d.Id] = d;
        if (desiredById.Count != desired.Count) return null;

        var cmds = new List<object>();
        // working simulates the engine's chain after each emitted command.
        var working = new List<string>(current);

        // 1. Remove plugins that are no longer wanted. Walk high→low so each
        //    removal leaves the indices of not-yet-visited entries unchanged.
        var wantIds = new HashSet<string>(desiredById.Keys, StringComparer.Ordinal);
        for (int i = working.Count - 1; i >= 0; i--)
        {
            if (!wantIds.Contains(working[i]))
            {
                cmds.Add(new { cmd = "remove_plugin", index = i });
                working.RemoveAt(i);
            }
        }

        // 2. Positional pass: fix each target slot in ascending order. Once slot i
        //    is correct it is never touched again, so positions [0, i) always match
        //    desired and `working` has at least i entries when we reach i.
        for (int i = 0; i < desired.Count; i++)
        {
            var d = desired[i];
            int j = working.IndexOf(d.Id);
            if (j == i) continue;

            if (j < 0)
            {
                // Missing → insert at its final index, then restore its voicing.
                cmds.Add(new { cmd = "add_plugin", file = d.File, uid = d.Uid, index = i });
                if (d.State.Length > 0)
                    cmds.Add(new { cmd = "set_plugin_state", index = i, state = d.State });
                working.Insert(i, d.Id);
            }
            else
            {
                // Present but out of place (j > i, since [0, i) is already locked).
                cmds.Add(new { cmd = "move_plugin", from = j, to = i });
                var moved = working[j];
                working.RemoveAt(j);
                working.Insert(i, moved);
            }
        }

        return cmds;
    }
}
