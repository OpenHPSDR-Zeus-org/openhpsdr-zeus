// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// Renders an assembled <see cref="DiagnosticReport"/> to paste-ready Markdown and
/// builds the prefilled GitHub "new issue" URL. Plain-language throughout — the
/// operator reads this before they ever post it. Culture-invariant formatting so
/// reports look the same on every platform/locale.
/// </summary>
internal static class MarkdownReportRenderer
{
    public const string GithubNewIssueBase =
        "https://github.com/OpenHPSDR-Zeus-org/openhpsdr-zeus/issues/new";

    /// <summary>Rough ceiling for the whole prefilled URL before we drop the log block.</summary>
    public const int MaxUrlLength = 7000;

    /// <summary>Render the full report (including the recent-log block) to Markdown.</summary>
    public static string Render(DiagnosticReport report) => Render(report, includeLog: true);

    /// <summary>
    /// Render the report to Markdown. When <paramref name="includeLog"/> is false the
    /// recent-log section is replaced with a short note pointing the reader at the
    /// in-app "Copy report" button (used only to keep the GitHub URL under the limit).
    /// </summary>
    public static string Render(DiagnosticReport report, bool includeLog)
    {
        var sb = new StringBuilder(2048);

        sb.Append("Thanks for reporting a problem with Zeus. This report was generated ")
          .Append("automatically by the \"Report a problem\" button — it describes what ")
          .Append("you were doing, what Zeus could work out about the cause, and a ")
          .Append("snapshot of the recent log. No personal data is included.")
          .Append("\n\n");

        // ## What I was doing
        sb.Append("## What I was doing\n\n");
        var label = string.IsNullOrWhiteSpace(report.SymptomLabel)
            ? "Something else or not sure"
            : report.SymptomLabel;
        sb.Append("**Symptom:** ").Append(label).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(report.FreeText))
        {
            sb.Append("**Details:**\n\n");
            sb.Append(report.FreeText!.Trim()).Append("\n\n");
        }

        // ## Findings
        sb.Append("## Findings\n\n");
        if (report.Findings.Count == 0)
        {
            sb.Append("_No known-issue patterns matched. The system snapshot and log ")
              .Append("below should help a maintainer investigate._\n\n");
        }
        else
        {
            foreach (var f in report.Findings)
            {
                sb.Append("- ").Append(SeverityBadge(f.Severity)).Append(' ')
                  .Append("**").Append(f.Title).Append("**\n\n");
                sb.Append("  ").Append(f.Detail.Replace("\n", "\n  ")).Append('\n');
                if (!string.IsNullOrWhiteSpace(f.DocRef))
                    sb.Append("\n  See: `").Append(f.DocRef).Append("`\n");
                sb.Append('\n');
            }
        }

        // ## System
        sb.Append("## System\n\n");
        if (report.Sections.Count == 0)
        {
            sb.Append("_No system sections were collected._\n\n");
        }
        else
        {
            foreach (var section in report.Sections)
            {
                sb.Append("### ").Append(section.Title).Append("\n\n");
                if (section.Items.Count == 0)
                {
                    sb.Append("_(no values)_\n\n");
                    continue;
                }
                foreach (var item in section.Items)
                    sb.Append("- **").Append(item.Key).Append(":** ")
                      .Append(item.Value).Append('\n');
                sb.Append('\n');
            }
        }

        // ## Recent log
        sb.Append("## Recent log (last ")
          .Append(report.RecentLog.Count.ToString(CultureInfo.InvariantCulture))
          .Append(" lines)\n\n");
        if (includeLog)
        {
            sb.Append("```text\n");
            foreach (var line in report.RecentLog)
                sb.Append(line).Append('\n');
            sb.Append("```\n");
        }
        else
        {
            sb.Append("(Log omitted from this link to keep it short — click ")
              .Append("\"Copy report\" in Zeus and paste the full text here.)\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Build the prefilled GitHub "new issue" URL. Uses the full Markdown body when it
    /// fits under <see cref="MaxUrlLength"/>; otherwise re-renders without the recent-log
    /// block (the full log always stays in the returned report Markdown, which the UI
    /// copies). <paramref name="fullMarkdown"/> is the body that includes the log.
    /// </summary>
    public static string BuildGithubIssueUrl(DiagnosticReport report, string fullMarkdown)
    {
        var title = string.IsNullOrWhiteSpace(report.SymptomLabel)
            ? "Problem report"
            : "[Report] " + report.SymptomLabel;

        string url = Compose(title, fullMarkdown);
        if (url.Length <= MaxUrlLength)
            return url;

        // Too long — drop the log block from the URL body only.
        string trimmed = Render(report, includeLog: false);
        return Compose(title, trimmed);
    }

    private static string Compose(string title, string body)
    {
        var sb = new StringBuilder(GithubNewIssueBase.Length + body.Length * 2);
        sb.Append(GithubNewIssueBase)
          .Append("?title=").Append(Uri.EscapeDataString(title))
          .Append("&body=").Append(Uri.EscapeDataString(body))
          .Append("&labels=bug");
        return sb.ToString();
    }

    private static string SeverityBadge(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Critical => "[CRITICAL]",
        DiagnosticSeverity.Likely => "[LIKELY CAUSE]",
        DiagnosticSeverity.Warning => "[WARNING]",
        _ => "[INFO]",
    };
}
