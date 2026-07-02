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
    /// &gt;1 = Deep/multi). Default 3 mirrors the decoder's default passes so the
    /// first session decodes exactly as deep as before this page existed. 1..4.
    /// </summary>
    int DecodePasses = 3,
    bool ShowOnlyCq = false,
    bool HideWorkedBefore = false,
    // ── Logging ─────────────────────────────────────────────────────────────
    bool AutoLog = true,
    bool PromptBeforeLog = false,
    bool ClearDxAfterLog = true,
    bool ReportToComment = false,
    // ── Waterfall / display (per-mode) ───────────────────────────────────────
    // The digital workspace's waterfall/panadapter preferences, persisted PER
    // mode so FT8/FT4/WSPR each remember their own view. These mirror the
    // main-console display-settings-store fields but are scoped to the digital
    // pop-out so they survive desktop restarts (the webview's localStorage is
    // port-scoped and lost on every launch). Pure display — none affect the air.
    double WfDbMin = -140,
    double WfDbMax = -50,
    /// <summary>Waterfall colormap id ("blue" | "inferno" | "viridis").</summary>
    string Palette = "blue",
    /// <summary>Resolution-bandwidth selector ("auto" or an Hz token).</summary>
    string Rbw = "auto",
    /// <summary>Waterfall averaging/smoothing frames (0 = none).</summary>
    int Smoothing = 0,
    /// <summary>Display zoom factor (1.0 = full span).</summary>
    double Zoom = 1.0,
    /// <summary>Display span in Hz.</summary>
    int SpanHz = 3000)
{
    public const int MinOffsetHz = 200;
    public const int MaxTxOffsetHz = 4000;
    public const int MaxMacroLength = 13;     // FT8 free-text is 13 chars
    public const int MinPasses = 1;
    public const int MaxPasses = 4;

    // Waterfall/display defaults + bounds. WfDbMin/Max mirror the main console's
    // FIXED_DB_MIN/MAX (-140..-50). The dB window is bounded to ±200 with a
    // minimum span so a corrupt/hand-crafted row can never collapse the colormap
    // onto a single colour (the same MIN_SPAN guard the frontend store applies).
    public const double DefaultWfDbMin = -140;
    public const double DefaultWfDbMax = -50;
    public const double DbAbsLimit = 200;
    public const double MinDbSpan = 20;
    public const string DefaultPalette = "blue";
    public const string DefaultRbw = "auto";
    public const int DefaultSmoothing = 0;
    public const int MinSmoothing = 0;
    public const int MaxSmoothing = 10;
    public const double DefaultZoom = 1.0;
    public const double MinZoom = 1.0;
    public const double MaxZoom = 64.0;
    public const int DefaultSpanHz = 3000;
    public const int MinSpanHz = 500;
    public const int MaxSpanHz = 6000;

    private static readonly string[] ValidPalettes = { "blue", "inferno", "viridis" };

    /// <summary>
    /// Clamp/trim so a hand-crafted POST or stale row can't push out-of-range
    /// values: offset bounded, passes 1..4, slot 0/1, macros trimmed/capped,
    /// retries non-negative, and the waterfall/display block bounded (dB window
    /// sanitised, palette whitelisted, zoom/span/smoothing clamped).
    /// </summary>
    public Ft8Settings Normalized()
    {
        var (wfMin, wfMax) = SanitizeDbRange(WfDbMin, WfDbMax);
        return this with
        {
            DefaultTxSlot = DefaultTxSlot == 0 ? 0 : 1,
            DefaultTxOffsetHz = Math.Clamp(DefaultTxOffsetHz, MinOffsetHz, MaxTxOffsetHz),
            CallerMaxRetries = Math.Max(0, CallerMaxRetries),
            DecodePasses = Math.Clamp(DecodePasses, MinPasses, MaxPasses),
            CqMessage = Cap(CqMessage, 32),
            CqDxMessage = Cap(CqDxMessage, 32),
            FreeTextMacro = Cap(FreeTextMacro, MaxMacroLength),
            WfDbMin = wfMin,
            WfDbMax = wfMax,
            Palette = NormalizePalette(Palette),
            Rbw = NormalizeRbw(Rbw),
            Smoothing = Math.Clamp(Smoothing, MinSmoothing, MaxSmoothing),
            Zoom = ClampDouble(Zoom, MinZoom, MaxZoom, DefaultZoom),
            SpanHz = Math.Clamp(SpanHz, MinSpanHz, MaxSpanHz),
        };
    }

    private static string Cap(string? s, int max)
    {
        var t = (s ?? "").Trim();
        return t.Length > max ? t[..max] : t;
    }

    // Reset to defaults if either endpoint is non-finite, out of ±DbAbsLimit, or
    // the span is below MinDbSpan (a degenerate window renders one flat colour).
    private static (double Min, double Max) SanitizeDbRange(double min, double max)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max)) return (DefaultWfDbMin, DefaultWfDbMax);
        if (min < -DbAbsLimit || max > DbAbsLimit) return (DefaultWfDbMin, DefaultWfDbMax);
        if (max - min < MinDbSpan) return (DefaultWfDbMin, DefaultWfDbMax);
        return (min, max);
    }

    private static double ClampDouble(double v, double lo, double hi, double fallback) =>
        double.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;

    private static string NormalizePalette(string? p)
    {
        var t = (p ?? "").Trim().ToLowerInvariant();
        return Array.IndexOf(ValidPalettes, t) >= 0 ? t : DefaultPalette;
    }

    private static string NormalizeRbw(string? r)
    {
        var t = (r ?? "").Trim();
        return t.Length == 0 ? DefaultRbw : (t.Length > 16 ? t[..16] : t);
    }
}
