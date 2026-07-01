// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class RfFilterEndpointTests : IClassFixture<RfFilterEndpointTests.Factory>
{
    private readonly Factory _factory;
    public RfFilterEndpointTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Get_Put_Reset_RoundTrip()
    {
        SetBoard(HpsdrBoardKind.OrionMkII);
        using var client = _factory.CreateClient();

        var initial = await client.GetFromJsonAsync<RfFilterSettingsDto>("/api/radio/rf-filters");
        Assert.NotNull(initial);
        Assert.False(initial!.CustomMatrixEnabled);
        Assert.NotEmpty(initial.Profiles);

        var put = await client.PutAsJsonAsync("/api/radio/rf-filters",
            new RfFilterSettingsSetRequest(
                CustomMatrixEnabled: true,
                RxBypassAll: true,
                RxBypassOnTx: false,
                RxBypassOnPureSignal: false,
                Profiles: initial.Profiles));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var changed = await put.Content.ReadFromJsonAsync<RfFilterSettingsDto>();
        Assert.NotNull(changed);
        Assert.True(changed!.CustomMatrixEnabled);
        Assert.True(changed.RxBypassAll);

        var reset = await client.PostAsync("/api/radio/rf-filters/reset", content: null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var afterReset = await reset.Content.ReadFromJsonAsync<RfFilterSettingsDto>();
        Assert.NotNull(afterReset);
        Assert.False(afterReset!.CustomMatrixEnabled);
        Assert.False(afterReset.RxBypassAll);
    }

    private void SetBoard(HpsdrBoardKind board)
    {
        using var scope = _factory.Services.CreateScope();
        var prefs = scope.ServiceProvider.GetRequiredService<PreferredRadioStore>();
        prefs.Set(board, overrideDetection: true);
    }

    public sealed class Factory : IsolatedPrefsFactory { }
}
