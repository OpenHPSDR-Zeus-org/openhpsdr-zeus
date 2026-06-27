// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.
//
// Ft8Settings — the operator's persisted FT8/FT4 workspace preferences (the
// curated WSJT-X/JTDX KEEP set that is purely behaviour/UI, not radio control).
// Persisted server-side (zeus-prefs.db) so they survive desktop restarts — the
// frontend's old localStorage was port-scoped on the webview and lost on every
// launch. Operator IDENTITY (callsign/grid) is NOT here: it lives in the shared
// OperatorIdentity so spotting/FreeDV/TX all read one source.

namespace Zeus.Contracts;

/// <summary>
/// Persisted FT8/FT4 behaviour preferences surfaced on the FT8 Settings page.
/// Defaults are chosen so the first session behaves EXACTLY as it did before this
/// page existed (auto-sequence on, RR73 ack, disable-after-73 on, 3 decode
/// passes) — nothing an operator already feels changes until they touch a
/// control. Most flags are wired to the auto-sequence controller / macros / log
/// path; a couple (SkipGrid, ClearDxAfterLog) are persisted but not yet consumed
/// and are surfaced disabled ("coming soon") on the page. TX still requires an
/// explicit arm; none of these flags transmit on their own.
/// </summary>
public sealed record Ft8Settings(
    // ── TX & auto-sequence ──────────────────────────────────────────────────
    /// <summary>Master auto-sequence on/off. TX still requires ENABLE/arm.</summary>
    bool AutoSequence = true,
    bool CallFirst = false,
    bool HoldTxFreq = false,
    bool DisableTxAfter73 = true,
    /// <summary>Default TX slot: 0 = 1st (even), 1 = 2nd (odd).</summary>
    int DefaultTxSlot = 0,
    /// <summary>Default TX audio offset (Hz). Bounded on write.</summary>
    int DefaultTxOffsetHz = 1500,
    // Advanced sequence flags. RR73 ack is ON by default to match the engine's
    // pre-settings behaviour (the sequencer keys RR73) and modern WSJT-X FT8.
    bool Rr73InsteadOfRrr = true,
    bool SkipGrid = false,
    /// <summary>Caller max retries before giving up (0 = unlimited).</summary>
    int CallerMaxRetries = 0,
    // ── Macros ──────────────────────────────────────────────────────────────
    string CqMessage = "CQ",
    string CqDxMessage = "CQ DX",
    string FreeTextMacro = "",
    // ── Decode ──────────────────────────────────────────────────────────────
    /// <summary>
    /// Decode depth as passes, matching the engine scale (1 = Normal/floor,
    /// &gt;1 = Deep/multi). Default 3 mirrors Ft8Service.DecodePasses so the first
    /// session decodes exactly as deep as before this page existed. 1..4.
    /// </summary>
    int DecodePasses = 3,
    bool ShowOnlyCq = false,
    bool HideWorkedBefore = false,
    // ── Logging ─────────────────────────────────────────────────────────────
    bool AutoLog = true,
    bool PromptBeforeLog = false,
    bool ClearDxAfterLog = true,
    bool ReportToComment = false)
{
    public const int MinOffsetHz = 200;
    public const int MaxTxOffsetHz = 4000;
    public const int MaxMacroLength = 13;     // FT8 free-text is 13 chars
    public const int MinPasses = 1;
    public const int MaxPasses = 4;

    /// <summary>
    /// Clamp/trim so a hand-crafted POST or stale row can't push out-of-range
    /// values: offset bounded, passes 1..4, slot 0/1, macros trimmed/capped,
    /// retries non-negative.
    /// </summary>
    public Ft8Settings Normalized() => this with
    {
        DefaultTxSlot = DefaultTxSlot == 0 ? 0 : 1,
        DefaultTxOffsetHz = Math.Clamp(DefaultTxOffsetHz, MinOffsetHz, MaxTxOffsetHz),
        CallerMaxRetries = Math.Max(0, CallerMaxRetries),
        DecodePasses = Math.Clamp(DecodePasses, MinPasses, MaxPasses),
        CqMessage = Cap(CqMessage, 32),
        CqDxMessage = Cap(CqDxMessage, 32),
        FreeTextMacro = Cap(FreeTextMacro, MaxMacroLength),
    };

    private static string Cap(string? s, int max)
    {
        var t = (s ?? "").Trim();
        return t.Length > max ? t[..max] : t;
    }
}
