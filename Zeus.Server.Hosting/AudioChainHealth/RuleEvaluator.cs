// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

using Zeus.Contracts;

namespace Zeus.Server.AudioChainHealth;

/// <summary>
/// One fired rule's payload — what the service needs to assemble an
/// <see cref="AudioChainVerdict"/> for the wire, plus the absolute
/// apply target the apply endpoint will consume.
/// </summary>
public readonly record struct FiredRule(
    AudioChainRule Rule,
    string Message,
    string ApplyLabel,
    AudioChainApplyAction? Apply);

/// <summary>
/// Stateful rule evaluator. Owns per-rule sustained-violation latches +
/// hysteresis state. Designed to be ticked at ~2 Hz by
/// <see cref="AudioChainHealthService"/>; each tick computes the
/// currently-fired rules from a fresh
/// <see cref="AudioChainReadings"/> + <see cref="RuleContext"/>.
///
/// <para><b>Hysteresis:</b> A rule that's not currently raw-tripping
/// transitions to tripping only when the reading crosses
/// <see cref="AudioChainRule.EnterThreshold"/>. Once tripping, it stays
/// tripping until the reading crosses
/// <see cref="AudioChainRule.ExitThreshold"/> in the opposite
/// direction. This stops a value sitting near the boundary from
/// flickering the verdict.</para>
///
/// <para><b>Sustained-violation:</b> A raw-tripping rule does NOT fire
/// until it has been tripping continuously for the rule's window
/// (default 3 s warn / 5 s error). Speech gaps and plosive transients
/// don't fire warnings.</para>
///
/// <para><b>Sticky once fired:</b> When raw-tripping clears (the
/// reading recovers past the exit threshold), the fired latch is NOT
/// reset — the warning stays on the wire so the operator has time to
/// read it and decide whether to act. The verdict's <c>Message</c> and
/// apply payload are cached from the most recent raw-tripping tick and
/// replayed verbatim while the warning is sticky-but-recovered. The
/// fired bit clears only when the rule's applicability scope changes
/// (mode switch / MOX off / similar) or when the operator dismisses
/// the verdict frontend-side (which is session-only and never visible
/// to the server). Re-tripping while sticky updates the cached
/// payload from the current readings.</para>
///
/// <para><b>Applicability:</b> When the rule's
/// <see cref="AudioChainRule.AppliesIn"/> returns false (e.g. an SSB
/// rule under CW), the evaluator clears all latched state for that
/// rule and emits no fired result. Mode switches do NOT preserve a
/// half-elapsed sustained-violation window across modes — nor a
/// sticky fired bit. Coming back into scope means a fresh start.</para>
/// </summary>
public sealed class RuleEvaluator
{
    public static readonly TimeSpan DefaultWarnWindow = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan DefaultErrorWindow = TimeSpan.FromSeconds(5);

    private sealed class State
    {
        // Raw-tripping state — post-hysteresis threshold check, but
        // before the sustained-violation gate.
        public bool RawTripping;
        // Wall-clock instant the current raw-tripping run started.
        // Null when not raw-tripping.
        public DateTime? RawTrippingSince;
        // Sustained-violation gate has passed; this is the bit the
        // service reads to decide "is the verdict on the wire?"
        // Sticky: once true it stays true through raw-tripping
        // transitions until the rule goes out of scope.
        public bool Fired;
        // Snapshot of the most-recently-tripping message + apply
        // payload, replayed when the rule is sticky-fired but no
        // longer raw-tripping. Captured on every raw-tripping tick
        // so a worsening reading updates what the operator sees.
        public string? CachedMessage;
        public string? CachedApplyLabel;
        public AudioChainApplyAction? CachedApply;
    }

    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);

    /// <summary>
    /// Evaluate every rule against the current readings + context and
    /// return the set of fired rules (sustained-violation gate passed).
    /// Caller assembles the <see cref="AudioChainVerdict"/> wire payload
    /// + apply-target cache from the result.
    ///
    /// Allocates one result list per call. The state dictionary is
    /// reused across calls — no per-tick GC churn on the latches.
    /// </summary>
    public List<FiredRule> Evaluate(
        IReadOnlyList<AudioChainRule> rules,
        in AudioChainReadings readings,
        in RuleContext ctx,
        DateTime nowUtc)
    {
        var fired = new List<FiredRule>();
        foreach (var rule in rules)
        {
            var state = GetOrCreateState(rule.Name);

            if (!rule.AppliesIn(ctx))
            {
                // Out-of-scope: scrub the latch so a mode switch never
                // resurrects a stale fired bit when the rule comes back
                // into scope. This is the ONLY path that clears a
                // sticky-fired verdict.
                state.RawTripping = false;
                state.RawTrippingSince = null;
                state.Fired = false;
                state.CachedMessage = null;
                state.CachedApplyLabel = null;
                state.CachedApply = null;
                continue;
            }

            float value = rule.Read(readings);
            bool tripping = state.RawTripping
                ? IsStillTripping(rule, value)
                : IsBreachingEnter(rule, value);

            if (tripping && !state.RawTripping)
            {
                state.RawTripping = true;
                state.RawTrippingSince = nowUtc;
            }
            else if (!tripping && state.RawTripping)
            {
                state.RawTripping = false;
                state.RawTrippingSince = null;
                // Sticky: leave state.Fired alone here. The verdict
                // stays on the wire (using the cached message+apply)
                // until the rule's scope changes — operators get to
                // read the warning at human speed instead of having
                // it disappear with the recovering reading.
            }

            if (state.RawTripping && !state.Fired)
            {
                var window = rule.SustainedWindow ?? DefaultWindowFor(rule.Severity);
                if (state.RawTrippingSince is { } since && nowUtc - since >= window)
                {
                    state.Fired = true;
                }
            }

            if (state.Fired)
            {
                // While raw-tripping is true, recompute message + apply
                // from live readings so a worsening value updates the
                // verdict text. When sticky-but-recovered (raw-tripping
                // false), replay the cached payload so the message
                // doesn't drift to "Mic at -22 dBFS" while showing the
                // "Mic too low" warning.
                string message;
                string applyLabel;
                AudioChainApplyAction? apply;
                if (state.RawTripping)
                {
                    message = rule.Message(readings, ctx);
                    applyLabel = rule.ApplyLabel?.Invoke(readings, ctx) ?? string.Empty;
                    apply = rule.Apply?.Invoke(readings, ctx);
                    state.CachedMessage = message;
                    state.CachedApplyLabel = applyLabel;
                    state.CachedApply = apply;
                }
                else
                {
                    // Cache MUST be populated — Fired only transitions
                    // to true under RawTripping=true, and that branch
                    // above always writes the cache.
                    message = state.CachedMessage ?? rule.Message(readings, ctx);
                    applyLabel = state.CachedApplyLabel
                        ?? rule.ApplyLabel?.Invoke(readings, ctx)
                        ?? string.Empty;
                    apply = state.CachedApply ?? rule.Apply?.Invoke(readings, ctx);
                }
                fired.Add(new FiredRule(
                    Rule: rule,
                    Message: message,
                    ApplyLabel: applyLabel,
                    Apply: apply));
            }
        }
        return fired;
    }

    /// <summary>Forget every rule's latched state. Intended for service
    /// restart / radio disconnect — fresh start means fresh windows.</summary>
    public void Reset() => _states.Clear();

    private State GetOrCreateState(string name)
    {
        if (_states.TryGetValue(name, out var existing)) return existing;
        var s = new State();
        _states[name] = s;
        return s;
    }

    private static TimeSpan DefaultWindowFor(AudioChainSeverity sev) =>
        sev == AudioChainSeverity.Error ? DefaultErrorWindow : DefaultWarnWindow;

    private static bool IsBreachingEnter(AudioChainRule rule, float value) =>
        rule.Direction == TripDirection.Above
            ? value > rule.EnterThreshold
            : value < rule.EnterThreshold;

    private static bool IsStillTripping(AudioChainRule rule, float value) =>
        rule.Direction == TripDirection.Above
            ? value > rule.ExitThreshold
            : value < rule.ExitThreshold;
}
