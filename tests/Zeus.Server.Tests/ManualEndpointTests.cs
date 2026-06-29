// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// Pins the GET /manual contract: the operator-manual PDF is built + bundled
// only at release time (release.yml `build-manual` job), so it is absent from
// the test host's base directory. The endpoint must 404 cleanly rather than
// throw — the About-panel link relies on that graceful absence in dev builds.

using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Zeus.Server.Tests;

public class ManualEndpointTests : IClassFixture<ManualEndpointTests.Factory>
{
    private readonly Factory _factory;
    public ManualEndpointTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Manual_WhenPdfNotBundled_Returns404()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/manual");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    public sealed class Factory : IsolatedPrefsFactory { }
}
