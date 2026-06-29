// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// LanProxyService — the server side of the "LAN Browser" panel. It lets an
// operator (especially one connected REMOTELY over the WebRTC tunnel) reach the
// web UI of devices on the *radio host's* LAN — router, antenna rotator, amp,
// another SDR's console — that the remote browser cannot reach directly. The
// Zeus server, which sits on that LAN, fetches the page on the operator's behalf
// and returns it; for HTML it rewrites sub-resource URLs so CSS/JS/images route
// back through this same proxy.
//
// SECURITY POSTURE — this is an authenticated reach into the home network, so it
// is deny-by-default and tightly scoped:
//   - Only http/https, GET only.
//   - The target host must resolve EXCLUSIVELY to RFC1918 / IPv6-ULA private
//     addresses. ANY resolved address outside those ranges → refused. This
//     blocks the public internet, cloud metadata (169.254.169.254), and
//     DNS-rebinding (a name that resolves to one private + one public address).
//   - LOOPBACK IS BLOCKED ON PURPOSE: allowing 127.0.0.1 would let a remote
//     session re-enter Zeus's own Kestrel (and bypass the WebRTC tunnel's
//     secrets/QRZ denylist) or any other local-only service. The LAN proxy is
//     for *other* machines on the LAN, not this host.
//   - Redirects are followed manually, re-validating every hop against the same
//     private-range rule, capped at a few hops.
//   - Response bodies are size-capped.
//
// Nothing here touches the radio / DSP / TX path.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Zeus.Server;

/// <summary>Outcome of a <see cref="LanProxyService.FetchAsync"/> call.</summary>
public sealed record LanProxyResult(
    int Status,
    string? ContentType,
    byte[] Body,
    string? Error)
{
    public bool Ok => Error is null;

    public static LanProxyResult Fail(int status, string error) =>
        new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(error), error);
}

/// <summary>
/// Fetches a private-LAN web resource on behalf of the LAN Browser panel,
/// validating the target stays within private address ranges and rewriting HTML
/// so sub-resources load back through the proxy. Singleton.
/// </summary>
public sealed partial class LanProxyService
{
    /// <summary>The loopback path this proxy is exposed at; the HTML rewriter
    /// points sub-resource URLs here (<c>?url=&lt;absolute&gt;</c>).</summary>
    public const string ProxyPath = "/api/lan/proxy";

    /// <summary>Named <see cref="IHttpClientFactory"/> client for LAN fetches —
    /// no auto-redirect (we follow + re-validate manually), modest timeout.</summary>
    public const string HttpClientName = "ZeusLanProxy";

    // 8 MiB is plenty for a device admin page plus an inlined asset; keeps a
    // hostile/huge LAN target from exhausting memory. Per-asset, not per-session.
    private const int MaxBodyBytes = 8 * 1024 * 1024;
    private const int MaxRedirects = 4;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<LanProxyService> _log;

    public LanProxyService(IHttpClientFactory httpFactory, ILogger<LanProxyService> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    /// <summary>
    /// Fetch <paramref name="rawUrl"/> from the LAN (following private-only
    /// redirects), returning the body — HTML rewritten so sub-resources proxy
    /// back through <see cref="ProxyPath"/>. Never throws for an
    /// expected/denied condition; returns a <see cref="LanProxyResult"/> with an
    /// HTTP-style status + message the panel can show.
    /// </summary>
    public async Task<LanProxyResult> FetchAsync(string? rawUrl, bool inline, CancellationToken ct)
    {
        if (!TryValidateTarget(rawUrl, out var uri, out var err))
            return LanProxyResult.Fail(403, err!);

        var client = _httpFactory.CreateClient(HttpClientName);
        var current = uri!;

        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            HttpResponseMessage resp;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, current);
                // A neutral, modern UA — some device UIs gate on it; never leak
                // the operator's real browser/identity to a LAN device.
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Zeus LAN Browser)");
                resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogInformation("lan.proxy fetch failed url={Url} err={Err}", current, ex.Message);
                return LanProxyResult.Fail(502, $"Could not reach {current.Host}: {ex.Message}");
            }

            using (resp)
            {
                // Manual redirect handling so every hop is re-validated against the
                // private-range rule (an open redirect to a public host is refused).
                if (IsRedirect(resp.StatusCode) && resp.Headers.Location is { } loc)
                {
                    var next = new Uri(current, loc); // resolves relative Location
                    if (!TryValidateTarget(next.ToString(), out var nextUri, out var redErr))
                        return LanProxyResult.Fail(403, $"Redirect blocked: {redErr}");
                    current = nextUri!;
                    continue;
                }

                var contentType = resp.Content.Headers.ContentType?.ToString();
                byte[] body;
                try
                {
                    body = await ReadCappedAsync(resp, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    return LanProxyResult.Fail(502, $"Response from {current.Host} exceeded {MaxBodyBytes / 1024 / 1024} MiB.");
                }

                if (IsHtml(contentType))
                {
                    var html = Decode(body, contentType);
                    html = inline
                        ? await InlineHtmlAsync(html, current, ct).ConfigureAwait(false)
                        : RewriteHtml(html, current);
                    body = Encoding.UTF8.GetBytes(html);
                }

                return new LanProxyResult((int)resp.StatusCode, contentType, body, null);
            }
        }

        return LanProxyResult.Fail(508, "Too many redirects.");
    }

    // -- Target validation ----------------------------------------------------

    /// <summary>
    /// True if <paramref name="rawUrl"/> is an http/https URL whose host
    /// resolves exclusively to private (RFC1918 / IPv6-ULA) addresses. On
    /// failure, <paramref name="error"/> explains why (for the panel).
    /// </summary>
    public bool TryValidateTarget(string? rawUrl, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            error = "No URL supplied.";
            return false;
        }
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var parsed))
        {
            error = "Not a valid absolute URL.";
            return false;
        }
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = "Only http and https are allowed.";
            return false;
        }

        IPAddress[] addrs;
        if (IPAddress.TryParse(parsed.Host, out var literal))
        {
            addrs = [literal];
        }
        else
        {
            try
            {
                addrs = Dns.GetHostAddresses(parsed.Host);
            }
            catch (Exception ex)
            {
                error = $"Could not resolve host '{parsed.Host}': {ex.Message}";
                return false;
            }
            if (addrs.Length == 0)
            {
                error = $"Host '{parsed.Host}' did not resolve.";
                return false;
            }
        }

        // EVERY resolved address must be private; a single public/loopback/
        // link-local address fails the whole target (anti-rebinding).
        foreach (var a in addrs)
        {
            if (!IsPrivate(a))
            {
                error = $"'{parsed.Host}' resolves to {a}, which is not a private LAN address. " +
                        "The LAN Browser only reaches RFC1918 / IPv6-ULA addresses.";
                return false;
            }
        }

        uri = parsed;
        return true;
    }

    /// <summary>
    /// True for an address inside a private LAN range: IPv4 10/8, 172.16/12,
    /// 192.168/16; IPv6 unique-local fc00::/7. Loopback, link-local (incl. cloud
    /// metadata 169.254.169.254), CGNAT 100.64/10, and all public addresses are
    /// rejected.
    /// </summary>
    internal static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes(); // big-endian
            return b[0] switch
            {
                10 => true,                                   // 10.0.0.0/8
                172 => b[1] >= 16 && b[1] <= 31,              // 172.16.0.0/12
                192 => b[1] == 168,                           // 192.168.0.0/16
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || IPAddress.IsLoopback(address)) return false;
            var b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC; // fc00::/7 unique-local
        }

        return false;
    }

    // -- HTML rewriting -------------------------------------------------------

    /// <summary>
    /// Rewrite a page's resource references so the browser loads them back
    /// through this proxy. Best-effort and deliberately conservative: it
    /// rewrites <c>href</c>/<c>src</c>/<c>action</c> attributes and CSS
    /// <c>url(...)</c>, resolving each against <paramref name="baseUri"/>. It
    /// will NOT make a JS-heavy SPA work (those build URLs at runtime); simple
    /// device admin pages render fine.
    /// </summary>
    internal static string RewriteHtml(string html, Uri baseUri)
    {
        // Honour an existing <base href> if the page sets one.
        var baseHrefMatch = BaseHrefRegex().Match(html);
        if (baseHrefMatch.Success &&
            Uri.TryCreate(baseUri, baseHrefMatch.Groups[1].Value, out var declared))
        {
            baseUri = declared;
        }

        html = AttrUrlRegex().Replace(html, m =>
        {
            var attr = m.Groups["attr"].Value;
            var quote = m.Groups["q"].Value;
            var val = m.Groups["url"].Value;
            var proxied = Proxify(val, baseUri);
            return proxied is null ? m.Value : $"{attr}={quote}{proxied}{quote}";
        });

        html = CssUrlRegex().Replace(html, m =>
        {
            var val = m.Groups["url"].Value.Trim('\'', '"', ' ');
            var proxied = Proxify(val, baseUri);
            return proxied is null ? m.Value : $"url(\"{proxied}\")";
        });

        return html;
    }

    /// <summary>Resolve a possibly-relative resource URL to an absolute one and
    /// wrap it as <c>/api/lan/proxy?url=&lt;encoded&gt;</c>, or null to leave the
    /// original untouched (anchors, data:/javascript:/mailto:, unparseable).</summary>
    private static string? Proxify(string value, Uri baseUri)
    {
        var v = value.Trim();
        if (v.Length == 0 || v[0] == '#') return null;
        if (v.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith(ProxyPath, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!Uri.TryCreate(baseUri, v, out var abs)) return null;
        if (abs.Scheme != Uri.UriSchemeHttp && abs.Scheme != Uri.UriSchemeHttps) return null;
        return $"{ProxyPath}?url={Uri.EscapeDataString(abs.ToString())}";
    }

    // -- HTML inlining (remote srcdoc mode) -----------------------------------

    // Total bytes we'll inline into one page, and the per-resource cap. Keeps a
    // heavy page from ballooning the srcdoc (and the remote tunnel response)
    // without bound. Pages that exceed the budget just render with some
    // resources missing rather than failing.
    private const int InlineBudgetBytes = 6 * 1024 * 1024;
    private const int InlinePerResourceBytes = 2 * 1024 * 1024;

    // Overall wall-clock budget for inlining one page (fetch every sub-resource +
    // base64-encode it). Kept comfortably UNDER the remote tunnel's LAN-proxy
    // client deadline (api-tunnel.ts LAN_PROXY_TIMEOUT_MS, 60 s) so the reply
    // always beats the client's timeout — the prior unbounded serial fetch could
    // run past the flat 15 s tunnel deadline and surface as a spurious "Remote
    // API request timed out." Sub-resources not fetched within the budget are
    // simply omitted (the page still renders), never an error.
    internal static readonly TimeSpan InlineMaxDuration = TimeSpan.FromSeconds(30);
    // Per-sub-resource ceiling so one slow/hung asset can't consume the whole
    // page budget on its own.
    private static readonly TimeSpan InlineSubResourceTimeout = TimeSpan.FromSeconds(5);

    // Injected into the inlined page so anchor clicks / form submits ask the Zeus
    // panel (the iframe's parent) to navigate, instead of the sandboxed srcdoc
    // iframe trying to load a URL it can't reach over the remote tunnel.
    private const string NavInterceptorScript =
        "<script>(function(){function s(u){if(u)parent.postMessage({type:'zeus-lan-nav',url:u},'*');}" +
        "document.addEventListener('click',function(e){var a=e.target.closest&&e.target.closest('[data-zeus-lan]');" +
        "if(a){e.preventDefault();s(a.getAttribute('data-zeus-lan'));}},true);" +
        "document.addEventListener('submit',function(e){var f=e.target.closest&&e.target.closest('[data-zeus-lan-form]');" +
        "if(f){e.preventDefault();s(f.getAttribute('data-zeus-lan-form'));}},true);})();</script>";

    /// <summary>
    /// Produce a self-contained HTML document for an iframe <c>srcdoc</c>: inline
    /// stylesheets and images (as data URIs) so the page renders with no further
    /// network access, and rewrite anchors/forms so navigation is relayed to the
    /// Zeus panel via <c>postMessage</c>. This is the remote path — the operator's
    /// browser can't reach the LAN, so everything the page needs must travel in
    /// the single tunnelled response. Best-effort: a JS-driven SPA won't fully
    /// render, but typical device admin pages do. Never throws.
    /// </summary>
    internal async Task<string> InlineHtmlAsync(string html, Uri baseUri, CancellationToken ct)
    {
        var baseHrefMatch = BaseHrefRegex().Match(html);
        if (baseHrefMatch.Success &&
            Uri.TryCreate(baseUri, baseHrefMatch.Groups[1].Value, out var declared))
        {
            baseUri = declared;
        }

        // Bound the whole inline operation. A device with many (or slow) sub-
        // resources must not push the tunnelled reply past the client's deadline;
        // when this elapses, in-flight/remaining sub-resource fetches fast-fail
        // and the asset is omitted. The outer `ct` (real client cancel) is still
        // honoured and propagated by FetchSubResourceAsync.
        using var inlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        inlineCts.CancelAfter(InlineMaxDuration);
        var ict = inlineCts.Token;

        int budget = InlineBudgetBytes;

        // Inline the url(...) assets (web fonts, background images) a CSS string
        // references as data: URIs, resolving each against its own stylesheet
        // base. Un-fetchable assets (over budget, unreachable, timed out) are
        // left as-is. This is what stops the operator's HTTPS, off-LAN browser
        // from trying to pull web fonts straight off the radio's LAN (the
        // "Failed to decode font / OTS parsing error" symptom).
        async Task<string> InlineCssAsync(string css, Uri cssBase) =>
            await ReplaceAsync(CssUrlRegex(), css, async m =>
            {
                var raw = m.Groups["url"].Value.Trim().Trim('\'', '"', ' ');
                if (raw.Length == 0
                    || raw[0] == '#'
                    || raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return m.Value;
                if (!Uri.TryCreate(cssBase, raw, out var abs)) return m.Value;
                if (abs.Scheme != Uri.UriSchemeHttp && abs.Scheme != Uri.UriSchemeHttps) return m.Value;
                var res = await FetchSubResourceAsync(abs, ict, ct).ConfigureAwait(false);
                if (res is null || budget - res.Value.bytes.Length < 0) return m.Value;
                budget -= res.Value.bytes.Length;
                var mime = res.Value.contentType ?? "application/octet-stream";
                return $"url(\"data:{mime};base64,{Convert.ToBase64String(res.Value.bytes)}\")";
            }).ConfigureAwait(false);

        // 1. Inline <style> blocks: inline their url(...) assets (page-relative).
        //    Done before stylesheet links so it only sees the page's own blocks.
        html = await ReplaceAsync(StyleBlockRegex(), html, async m =>
        {
            var css = await InlineCssAsync(m.Groups["css"].Value, baseUri).ConfigureAwait(false);
            return $"<style{m.Groups["attrs"].Value}>{css}</style>";
        }).ConfigureAwait(false);

        // 2. Stylesheets: <link rel="stylesheet" href="…"> → <style>…</style>,
        //    with the stylesheet's own url(...) assets inlined against its base.
        html = await ReplaceAsync(StylesheetLinkRegex(), html, async m =>
        {
            var href = m.Groups["url"].Value;
            if (!Uri.TryCreate(baseUri, href, out var abs)) return m.Value;
            var res = await FetchSubResourceAsync(abs, ict, ct).ConfigureAwait(false);
            if (res is null || budget - res.Value.bytes.Length < 0) return m.Value;
            budget -= res.Value.bytes.Length;
            var css = await InlineCssAsync(Encoding.UTF8.GetString(res.Value.bytes), abs).ConfigureAwait(false);
            return $"<style>{css}</style>";
        }).ConfigureAwait(false);

        // 3. Images: <img … src="…"> → data: URI.
        html = await ReplaceAsync(ImgTagRegex(), html, async m =>
        {
            var src = m.Groups["url"].Value;
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return m.Value;
            if (!Uri.TryCreate(baseUri, src, out var abs)) return m.Value;
            var res = await FetchSubResourceAsync(abs, ict, ct).ConfigureAwait(false);
            if (res is null || budget - res.Value.bytes.Length < 0) return m.Value;
            budget -= res.Value.bytes.Length;
            var mime = res.Value.contentType ?? "image/*";
            var dataUri = $"data:{mime};base64,{Convert.ToBase64String(res.Value.bytes)}";
            return m.Value.Replace(src, dataUri, StringComparison.Ordinal);
        }).ConfigureAwait(false);

        // 4. Anchors: route clicks to the parent panel instead of the dead iframe.
        html = AnchorHrefRegex().Replace(html, m =>
        {
            var href = m.Groups["url"].Value;
            if (href.Length == 0 || href[0] == '#'
                || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return m.Value;
            if (!Uri.TryCreate(baseUri, href, out var abs)) return m.Value;
            if (abs.Scheme != Uri.UriSchemeHttp && abs.Scheme != Uri.UriSchemeHttps) return m.Value;
            return $"<a href=\"#\" data-zeus-lan=\"{EscapeAttr(abs.ToString())}\"";
        });

        // 5. Forms: relay the action target too (GET forms; best-effort).
        html = FormActionRegex().Replace(html, m =>
        {
            var action = m.Groups["url"].Value;
            if (!Uri.TryCreate(baseUri, action, out var abs)) return m.Value;
            if (abs.Scheme != Uri.UriSchemeHttp && abs.Scheme != Uri.UriSchemeHttps) return m.Value;
            return $"<form data-zeus-lan-form=\"{EscapeAttr(abs.ToString())}\"";
        });

        // 6. Neutralise every reference that would otherwise make the operator's
        //    (HTTPS, off-LAN) browser fetch from the radio's LAN itself, which it
        //    cannot: framesets/iframes, external scripts, and any leftover
        //    src/href. Without this the srcdoc is NOT self-contained — those loads
        //    fail as mixed-content (http:// in an https page), DNS errors (LAN
        //    hostnames), or font-decode errors, spraying the console and breaking
        //    layout. This was the source of the "insecure frame" /
        //    ERR_NAME_NOT_RESOLVED symptoms. After this pass the page renders from
        //    inlined assets only.
        html = NeutralizeEscapingRefs(html);

        // 7. Inject the nav interceptor just before </body> (or append).
        int bodyEnd = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        html = bodyEnd >= 0 ? html.Insert(bodyEnd, NavInterceptorScript) : html + NavInterceptorScript;

        return html;
    }

    /// <summary>Fetch one sub-resource for inlining: validate it's private, GET it
    /// (under its own short timeout, also bounded by the page-wide
    /// <paramref name="inlineCt"/>), cap its size. Returns null on any failure or
    /// timeout — the caller then leaves the original reference (which the final
    /// neutralise pass blanks). A genuine client cancel (<paramref name="outerCt"/>)
    /// still propagates.</summary>
    private async Task<(string? contentType, byte[] bytes)?> FetchSubResourceAsync(
        Uri abs, CancellationToken inlineCt, CancellationToken outerCt)
    {
        if (!TryValidateTarget(abs.ToString(), out _, out _)) return null;
        try
        {
            using var perResource = CancellationTokenSource.CreateLinkedTokenSource(inlineCt);
            perResource.CancelAfter(InlineSubResourceTimeout);
            var client = _httpFactory.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, abs);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Zeus LAN Browser)");
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, perResource.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            if (resp.Content.Headers.ContentLength is > InlinePerResourceBytes) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(perResource.Token).ConfigureAwait(false);
            if (bytes.Length > InlinePerResourceBytes) return null;
            return (resp.Content.Headers.ContentType?.ToString(), bytes);
        }
        // Real client cancel propagates; a per-resource/page-budget timeout just
        // skips this asset.
        catch (OperationCanceledException) when (outerCt.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Blank every remaining network reference in an inlined page so the result is
    /// truly self-contained for an iframe <c>srcdoc</c>: iframes/framesets point at
    /// <c>about:blank</c>, and any leftover <c>src</c>/<c>href</c> to a fetchable
    /// target (external script, icon/preload link, media, an un-inlined asset) is
    /// emptied. In-page (<c>#</c>), <c>data:</c>, <c>about:</c>, and the
    /// non-navigating schemes are left untouched, as are the intercept anchors
    /// (which carry their target on <c>data-zeus-lan</c>, not <c>href</c>).
    /// </summary>
    internal static string NeutralizeEscapingRefs(string html)
    {
        // Iframes/framesets first — blank the src so the remote browser never
        // tries to load a LAN frame (the mixed-content / unreachable-frame source).
        html = FrameSrcRegex().Replace(html, m =>
            $"{m.Groups["pre"].Value} src=\"about:blank\"{m.Groups["post"].Value}");

        // Any residual src=/href= that still points somewhere fetchable → empty it.
        html = ResidualRefRegex().Replace(html, m =>
        {
            var url = m.Groups["url"].Value.Trim();
            if (url.Length == 0
                || url[0] == '#'
                || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
                return m.Value;
            return $"{m.Groups["attr"].Value}=\"\"";
        });

        return html;
    }

    private static string EscapeAttr(string s) =>
        s.Replace("&", "&amp;").Replace("\"", "&quot;");

    /// <summary>Async-aware regex replace (Regex.Replace has no async overload).</summary>
    private static async Task<string> ReplaceAsync(
        Regex regex, string input, Func<Match, Task<string>> replacer)
    {
        var sb = new StringBuilder();
        int last = 0;
        foreach (Match m in regex.Matches(input))
        {
            sb.Append(input, last, m.Index - last);
            sb.Append(await replacer(m).ConfigureAwait(false));
            last = m.Index + m.Length;
        }
        sb.Append(input, last, input.Length - last);
        return sb.ToString();
    }

    // -- helpers --------------------------------------------------------------

    private static bool IsRedirect(HttpStatusCode s) =>
        s is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
          or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect
          or HttpStatusCode.PermanentRedirect;

    private static bool IsHtml(string? contentType) =>
        contentType is not null &&
        contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase);

    private static string Decode(byte[] body, string? contentType)
    {
        // Honour an explicit charset; default UTF-8 (latin-1 bytes survive it for
        // the ASCII-only markup the rewriter touches).
        if (contentType is not null)
        {
            var idx = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var cs = contentType[(idx + 8)..].Trim().Trim('"');
                var semi = cs.IndexOf(';');
                if (semi >= 0) cs = cs[..semi];
                try { return Encoding.GetEncoding(cs).GetString(body); } catch { /* fall through */ }
            }
        }
        return Encoding.UTF8.GetString(body);
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength is > MaxBodyBytes)
            throw new InvalidOperationException("too large");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > MaxBodyBytes)
                throw new InvalidOperationException("too large");
            ms.Write(buf, 0, read);
        }
        return ms.ToArray();
    }

    [GeneratedRegex(@"<base\s[^>]*href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex BaseHrefRegex();

    // Matches href= / src= / action= with a quoted value.
    [GeneratedRegex(@"(?<attr>\b(?:href|src|action))\s*=\s*(?<q>[""'])(?<url>[^""']*)\k<q>", RegexOptions.IgnoreCase)]
    private static partial Regex AttrUrlRegex();

    // Matches CSS url(...) with optional quotes.
    [GeneratedRegex(@"url\(\s*(?<url>['""]?[^)'""]+['""]?)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrlRegex();

    // <link rel="stylesheet" … href="…"> (rel/href in either order).
    [GeneratedRegex(@"<link\b(?=[^>]*\brel\s*=\s*[""']?stylesheet)[^>]*\bhref\s*=\s*[""'](?<url>[^""']+)[""'][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex StylesheetLinkRegex();

    // <img … src="…" …> — whole tag captured so we can swap just the src value.
    [GeneratedRegex(@"<img\b[^>]*\bsrc\s*=\s*[""'](?<url>[^""']+)[""'][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImgTagRegex();

    // Opening <a … href="…"  (tag prefix only — replaced with an intercept anchor).
    [GeneratedRegex(@"<a\b[^>]*?\bhref\s*=\s*[""'](?<url>[^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex AnchorHrefRegex();

    // Opening <form … action="…"
    [GeneratedRegex(@"<form\b[^>]*?\baction\s*=\s*[""'](?<url>[^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex FormActionRegex();

    // A <style …>…</style> block: its attributes and inner CSS captured separately.
    [GeneratedRegex(@"<style(?<attrs>[^>]*)>(?<css>.*?)</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleBlockRegex();

    // <iframe|frame … src="…" …> split around src so the value can be blanked,
    // preserving the attributes on either side.
    [GeneratedRegex(@"(?<pre><(?:iframe|frame)\b[^>]*?)\bsrc\s*=\s*(?<q>[""'])[^""']*\k<q>(?<post>[^>]*>)", RegexOptions.IgnoreCase)]
    private static partial Regex FrameSrcRegex();

    // Any src= / href= with a quoted value (residual-reference neutraliser).
    [GeneratedRegex(@"(?<attr>\b(?:src|href))\s*=\s*(?<q>[""'])(?<url>[^""']*)\k<q>", RegexOptions.IgnoreCase)]
    private static partial Regex ResidualRefRegex();
}
