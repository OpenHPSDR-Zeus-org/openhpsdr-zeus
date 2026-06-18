using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Host;
using Zeus.Plugins.Host.Audio;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class RxVstEngineServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "zeus-rx-vst-engine-" + Guid.NewGuid().ToString("N"));
    private readonly PluginSettingsStore _settings;
    private readonly PluginManager _manager;
    private readonly RxChainOrderStore _store;

    public RxVstEngineServiceTests()
    {
        Directory.CreateDirectory(_root);
        _settings = new PluginSettingsStore(Path.Combine(_root, "settings.db"));
        _manager = new PluginManager(
            loader: new PluginLoader(NullLogger<PluginLoader>.Instance),
            settings: _settings,
            services: new ServiceCollection().BuildServiceProvider(),
            logFactory: NullLoggerFactory.Instance,
            options: new PluginManagerOptions { PluginRoot = Path.Combine(_root, "plugins") });
        _store = new RxChainOrderStore(
            NullLogger<RxChainOrderStore>.Instance,
            Path.Combine(_root, "rx-chain.db"));
    }

    [Fact]
    public async Task ProcessIfActive_ReturnsFalse_WhenNoRxVstSlotsAreActive()
    {
        var chainOrder = NewRxChainOrder();
        await using var controller = new VstEngineController();
        var service = new RxVstEngineService(
            _manager,
            chainOrder,
            controller,
            NullLogger<RxVstEngineService>.Instance);
        SetPrivateField(controller, "_active", true);

        Span<float> input = stackalloc float[] { 0.25f, -0.25f };
        Span<float> output = stackalloc float[] { 1f, 1f };
        var selected = service.ProcessIfActive(
            input,
            output,
            new AudioBlockContext(48_000, 1, input.Length, 0, false));

        Assert.False(selected);
        Assert.Equal<float>(new[] { 1f, 1f }, output.ToArray());
    }

    [Fact]
    public async Task ProcessIfActive_SelectsEngineRoute_ForActiveRxVstSlots()
    {
        var chainOrder = NewRxChainOrder();
        await using var controller = new VstEngineController();
        var service = new RxVstEngineService(
            _manager,
            chainOrder,
            controller,
            NullLogger<RxVstEngineService>.Instance);
        SetPrivateField(controller, "_active", true);
        SetPrivateField(service, "_activeVstCount", 1);

        Span<float> input = stackalloc float[] { 0.25f, -0.25f };
        Span<float> output = stackalloc float[] { 0f, 0f };
        var selected = service.ProcessIfActive(
            input,
            output,
            new AudioBlockContext(48_000, 1, input.Length, 0, false));

        Assert.True(selected);
        Assert.Equal<float>(new[] { 0.25f, -0.25f }, output.ToArray());
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _settings.Dispose();
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private RxChainOrderService NewRxChainOrder() =>
        new(_store, NullLogger<RxChainOrderService>.Instance);

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }
}
