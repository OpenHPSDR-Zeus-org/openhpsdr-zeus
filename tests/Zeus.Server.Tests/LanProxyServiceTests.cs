// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;
using Xunit;

namespace Zeus.Server.Tests;

/// <summary>
/// Guards the LAN Browser proxy's security boundary: only private (RFC1918 /
/// IPv6-ULA) targets are reachable, and the HTML rewriter routes sub-resources
/// back through the proxy. These are the rules that keep an authenticated remote
/// operator from turning the proxy into an open/SSRF gateway into the public
/// internet or the radio host's own loopback services.
/// </summary>
public class LanProxyServiceTests
{
    [Theory]
    // IPv4 private ranges — allowed.
    [InlineData("10.0.0.1", true)]
    [InlineData("10.255.255.255", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.1.1", true)]
    // Just outside the private ranges — refused.
    [InlineData("172.15.0.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.169.0.1", false)]
    [InlineData("11.0.0.1", false)]
    // Public — refused.
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    // Loopback — refused on purpose (would re-enter Zeus's own Kestrel).
    [InlineData("127.0.0.1", false)]
    // Link-local incl. cloud metadata — refused.
    [InlineData("169.254.1.1", false)]
    [InlineData("169.254.169.254", false)]
    // CGNAT 100.64/10 — refused (not a home LAN range).
    [InlineData("100.64.0.1", false)]
    // IPv6 unique-local — allowed; loopback / link-local / public — refused.
    [InlineData("fd12:3456::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2001:4860:4860::8888", false)]
    public void IsPrivate_ClassifiesAddresses(string ip, bool expected)
    {
        Assert.Equal(expected, LanProxyService.IsPrivate(IPAddress.Parse(ip)));
    }

    [Fact]
    public void IsPrivate_IPv4MappedToIPv6_PublicStillRefused()
    {
        // ::ffff:8.8.8.8 must be unwrapped and judged as the public IPv4 it is.
        Assert.False(LanProxyService.IsPrivate(IPAddress.Parse("::ffff:8.8.8.8")));
        Assert.True(LanProxyService.IsPrivate(IPAddress.Parse("::ffff:192.168.1.1")));
    }

    private static LanProxyService NewService() =>
        new(new ThrowingHttpClientFactory(), NullLogger<LanProxyService>.Instance);

    [Theory]
    [InlineData("http://192.168.1.1/", true)]
    [InlineData("https://192.168.1.1:8443/admin", true)]
    [InlineData("http://10.0.0.5/status.html", true)]
    [InlineData("http://8.8.8.8/", false)]        // public
    [InlineData("http://127.0.0.1:6060/api/qrz", false)] // loopback re-entry blocked
    [InlineData("ftp://192.168.1.1/", false)]     // non-http scheme
    [InlineData("/relative/path", false)]          // not absolute
    [InlineData("", false)]
    public void TryValidateTarget_EnforcesSchemeAndRange(string url, bool expectedOk)
    {
        var svc = NewService();
        bool ok = svc.TryValidateTarget(url, out var uri, out var error);
        Assert.Equal(expectedOk, ok);
        if (expectedOk) { Assert.NotNull(uri); Assert.Null(error); }
        else { Assert.Null(uri); Assert.NotNull(error); }
    }

    [Fact]
    public void RewriteHtml_RoutesResourcesThroughProxy()
    {
        var baseUri = new Uri("http://192.168.1.50/index.html");
        var html =
            "<html><head><link href=\"/style.css\" rel=\"stylesheet\"></head>" +
            "<body><img src=\"img/logo.png\">" +
            "<a href=\"page2.html\">next</a>" +
            "<a href=\"#section\">anchor</a>" +
            "<img src=\"data:image/png;base64,AAAA\">" +
            "<form action=\"/login\"></form></body></html>";

        var rewritten = LanProxyService.RewriteHtml(html, baseUri);

        // Relative + absolute-path resources are resolved against the base and proxied.
        Assert.Contains("/api/lan/proxy?url=" + Uri.EscapeDataString("http://192.168.1.50/style.css"), rewritten);
        Assert.Contains("/api/lan/proxy?url=" + Uri.EscapeDataString("http://192.168.1.50/img/logo.png"), rewritten);
        Assert.Contains("/api/lan/proxy?url=" + Uri.EscapeDataString("http://192.168.1.50/page2.html"), rewritten);
        Assert.Contains("/api/lan/proxy?url=" + Uri.EscapeDataString("http://192.168.1.50/login"), rewritten);

        // In-page anchors and data: URIs are left untouched.
        Assert.Contains("href=\"#section\"", rewritten);
        Assert.Contains("src=\"data:image/png;base64,AAAA\"", rewritten);
    }

    [Fact]
    public void RewriteHtml_CssUrl_IsProxied()
    {
        var baseUri = new Uri("http://10.0.0.2/app/main.css");
        var css = "body{background:url('bg.png')} .x{background:url(\"/shared/x.svg\")}";
        var rewritten = LanProxyService.RewriteHtml(css, baseUri);
        Assert.Contains("/api/lan/proxy?url=" + Uri.EscapeDataString("http://10.0.0.2/app/bg.png"), rewritten);
        Assert.Contains("/api/lan/proxy?url=" + Uri.EscapeDataString("http://10.0.0.2/shared/x.svg"), rewritten);
    }

    [Fact]
    public void RewriteHtml_HonoursBaseHref()
    {
        var pageUri = new Uri("http://192.168.0.10/sub/page.html");
        var html = "<html><head><base href=\"http://192.168.0.10/root/\"></head>" +
                   "<body><img src=\"a.png\"></body></html>";
        var rewritten = LanProxyService.RewriteHtml(html, pageUri);
        // The <base href> wins over the page URL when resolving relatives.
        Assert.Contains("/api/lan/proxy?url=" + Uri.EscapeDataString("http://192.168.0.10/root/a.png"), rewritten);
    }

    [Fact]
    public async Task InlineHtml_RelaysNavigationViaPostMessage()
    {
        // No <link>/<img> → no sub-resource fetches, so the throwing factory is
        // never used; this exercises the pure anchor/form/script rewriting.
        var svc = NewService();
        var baseUri = new Uri("http://192.168.1.50/index.html");
        var html =
            "<html><body>" +
            "<a href=\"status.html\">status</a>" +
            "<a href=\"#top\">top</a>" +
            "<a href=\"javascript:void(0)\">js</a>" +
            "<form action=\"/login\"></form>" +
            "</body></html>";

        var result = await svc.InlineHtmlAsync(html, baseUri, default);

        // Real links become intercept anchors carrying the absolute target.
        Assert.Contains("data-zeus-lan=\"http://192.168.1.50/status.html\"", result);
        Assert.Contains("data-zeus-lan-form=\"http://192.168.1.50/login\"", result);
        // In-page + javascript: anchors are left alone.
        Assert.Contains("href=\"#top\"", result);
        Assert.Contains("javascript:void(0)", result);
        // The postMessage nav interceptor is injected.
        Assert.Contains("zeus-lan-nav", result);
        Assert.Contains("</body>", result);
    }

    // -- minimal fakes --------------------------------------------------------

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("network not expected in these tests");
    }
}
