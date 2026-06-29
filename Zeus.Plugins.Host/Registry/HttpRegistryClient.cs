using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts.Registry;

namespace Zeus.Plugins.Host.Registry;

/// <summary>
/// HTTPS-fetched registry client. Default source is the
/// OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins repo's <c>registry.json</c>;
/// operators can override via <c>RegistryClientOptions.SourceUrl</c>
/// (e.g. point at a private fork or a self-hosted file).
/// </summary>
public sealed class HttpRegistryClient : IRegistryClient
{
    public const string DefaultUrl =
        "https://raw.githubusercontent.com/OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins/main/registry.json";

    private readonly HttpClient _http;
    private readonly ILogger<HttpRegistryClient>? _log;
    private readonly RegistryClientOptions _options;

    private RegistryCatalog? _cached;
    private DateTimeOffset _cachedAt;

    public HttpRegistryClient(
        HttpClient http,
        RegistryClientOptions? options = null,
        ILogger<HttpRegistryClient>? log = null)
    {
        _http = http;
        _log = log;
        _options = options ?? new RegistryClientOptions();
    }

    public string SourceUrl => _options.SourceUrl;

    public async Task<RegistryCatalog> FetchAsync(CancellationToken ct)
    {
        if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < _options.CacheTtl)
        {
            return _cached;
        }

        if (!_options.SourceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !_options.SourceUrl.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) &&
            !_options.SourceUrl.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            throw new RegistryFetchException(
                $"refusing to fetch registry over insecure transport: {_options.SourceUrl}");
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _options.SourceUrl);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var catalog = await resp.Content
                .ReadFromJsonAsync<RegistryCatalog>(cancellationToken: ct)
                .ConfigureAwait(false);

            if (catalog is null)
                throw new RegistryFetchException("registry response deserialised to null");

            _cached = catalog;
            _cachedAt = DateTimeOffset.UtcNow;
            _log?.LogInformation(
                "Fetched plugin registry from {Url}: {Count} entries",
                _options.SourceUrl, catalog.Plugins.Count);

            return catalog;
        }
        catch (HttpRequestException ex)
        {
            throw new RegistryFetchException(
                $"failed to fetch registry from {_options.SourceUrl}: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new RegistryFetchException(
                $"registry at {_options.SourceUrl} is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Force a refresh on the next FetchAsync call.</summary>
    public void InvalidateCache() => _cached = null;
}

public sealed record RegistryClientOptions
{
    public string SourceUrl { get; init; } = HttpRegistryClient.DefaultUrl;

    /// <summary>How long the in-memory catalog cache is reused. Default 5 minutes.</summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(5);
}
