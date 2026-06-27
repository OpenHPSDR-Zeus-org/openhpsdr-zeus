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
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server.Hosting.Support;

namespace Zeus.Server.Tests;

/// <summary>
/// The support "api" channel proxy must be bulletproof: read-only, allowlisted,
/// loopback-only, traversal-proof, and size-bounded. These exercise each gate
/// without a live PeerConnection (the proxy is factored out precisely so it can be).
/// </summary>
public sealed class SupportApiProxyTests
{
    private const string LoopbackBase = "http://127.0.0.1:6060";

    [Fact]
    public async Task Get_Allowlisted_ProxiesToLoopback()
    {
        var factory = new StubHttpClientFactory(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal($"{LoopbackBase}/api/diagnostics/v2", req.RequestUri!.ToString());
            return Json("{\"ok\":true}");
        });
        var proxy = new SupportApiProxy(factory, LoopbackBase, NullLogger.Instance);

        var reply = await proxy.HandleAsync(new SupportApiRequest(7, "GET", "/api/diagnostics/v2"));

        Assert.Equal(7, reply.Id);
        Assert.Equal(200, reply.Status);
        Assert.Equal("{\"ok\":true}", reply.Body);
        Assert.Equal("application/json; charset=utf-8", reply.ContentType);
        Assert.Equal(1, factory.CallCount);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task MutatingMethod_Is405_NeverHitsLoopback(string method)
    {
        var factory = new StubHttpClientFactory(_ => Json("nope"));
        var proxy = new SupportApiProxy(factory, LoopbackBase, NullLogger.Instance);

        var reply = await proxy.HandleAsync(new SupportApiRequest(1, method, "/api/state"));

        Assert.Equal(405, reply.Status);
        Assert.Equal(0, factory.CallCount); // refused before any loopback call
    }

    [Fact]
    public async Task NonAllowlistedPath_Is403()
    {
        var factory = new StubHttpClientFactory(_ => Json("secret"));
        var proxy = new SupportApiProxy(factory, LoopbackBase, NullLogger.Instance);

        var reply = await proxy.HandleAsync(new SupportApiRequest(2, "GET", "/api/prefs/databases"));

        Assert.Equal(403, reply.Status);
        Assert.Equal(0, factory.CallCount);
    }

    [Theory]
    [InlineData("/api/state/../prefs/databases")]
    [InlineData("/api/diagnostics/%2e%2e/prefs")]
    public async Task Traversal_Is403_NeverHitsLoopback(string path)
    {
        var factory = new StubHttpClientFactory(_ => Json("secret"));
        var proxy = new SupportApiProxy(factory, LoopbackBase, NullLogger.Instance);

        var reply = await proxy.HandleAsync(new SupportApiRequest(3, "GET", path));

        Assert.Equal(403, reply.Status);
        Assert.Equal(0, factory.CallCount);
    }

    [Fact]
    public async Task NonLoopbackBase_Is502_SsrfGuard()
    {
        // Even with an allowlisted path, a non-loopback base must never be reached.
        var factory = new StubHttpClientFactory(_ => Json("offbox"));
        var proxy = new SupportApiProxy(factory, "http://198.51.100.7", NullLogger.Instance);

        var reply = await proxy.HandleAsync(new SupportApiRequest(4, "GET", "/api/state"));

        Assert.Equal(502, reply.Status);
        Assert.Equal(0, factory.CallCount);
    }

    [Fact]
    public async Task NonAbsolutePath_Is502()
    {
        var factory = new StubHttpClientFactory(_ => Json("x"));
        var proxy = new SupportApiProxy(factory, LoopbackBase, NullLogger.Instance);

        var reply = await proxy.HandleAsync(new SupportApiRequest(5, "GET", "api/state"));

        Assert.Equal(502, reply.Status);
        Assert.Equal(0, factory.CallCount);
    }

    [Fact]
    public async Task Head_ProxiesAsGet_AndDiscardsBody()
    {
        HttpMethod? seen = null;
        var factory = new StubHttpClientFactory(req => { seen = req.Method; return Json("{\"v\":1}"); });
        var proxy = new SupportApiProxy(factory, LoopbackBase, NullLogger.Instance);

        var reply = await proxy.HandleAsync(new SupportApiRequest(6, "HEAD", "/api/version"));

        Assert.Equal(HttpMethod.Get, seen);  // HEAD is proxied as GET
        Assert.Equal(200, reply.Status);
        Assert.Equal("", reply.Body);        // body discarded for HEAD
    }

    [Fact]
    public async Task OversizedResponse_Is502()
    {
        var factory = new StubHttpClientFactory(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("small")),
            };
            resp.Content.Headers.ContentLength = 5L * 1024 * 1024; // > 4 MiB cap
            return resp;
        });
        var proxy = new SupportApiProxy(factory, LoopbackBase, NullLogger.Instance);

        var reply = await proxy.HandleAsync(new SupportApiRequest(8, "GET", "/api/diagnostics/v2"));

        Assert.Equal(502, reply.Status);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public int CallCount { get; private set; }

        public StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> respond)
            => _respond = respond;

        public HttpClient CreateClient(string name) => new(new StubHandler(this));

        private sealed class StubHandler(StubHttpClientFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.CallCount++;
                return Task.FromResult(owner._respond(request));
            }
        }
    }
}
