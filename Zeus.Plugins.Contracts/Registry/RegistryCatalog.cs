using System.Text.Json.Serialization;

namespace Zeus.Plugins.Contracts.Registry;

/// <summary>
/// Top-level <c>registry.json</c> served by the plugin registry repo
/// (default <c>OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins</c>).
/// </summary>
public sealed record RegistryCatalog
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("generated")]
    public DateTimeOffset Generated { get; init; }

    [JsonPropertyName("plugins")]
    public IReadOnlyList<PluginEntry> Plugins { get; init; } = Array.Empty<PluginEntry>();
}

public sealed record PluginEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("author")]
    public string Author { get; init; } = "";

    [JsonPropertyName("license")]
    public string License { get; init; } = "";

    [JsonPropertyName("homepage")]
    public string? Homepage { get; init; }

    [JsonPropertyName("categories")]
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

    [JsonPropertyName("verified")]
    public bool Verified { get; init; }

    [JsonPropertyName("versions")]
    public IReadOnlyList<PluginVersion> Versions { get; init; } = Array.Empty<PluginVersion>();
}

public sealed record PluginVersion
{
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("sdkAbi")]
    public int SdkAbi { get; init; }

    [JsonPropertyName("sdkMinVersion")]
    public required string SdkMinVersion { get; init; }

    /// <summary>
    /// RIDs the plugin supports. <c>any</c> = managed-only, no native bits.
    /// </summary>
    [JsonPropertyName("platforms")]
    public IReadOnlyList<string> Platforms { get; init; } = new[] { "any" };

    [JsonPropertyName("downloadUrl")]
    public required string DownloadUrl { get; init; }

    /// <summary>Hex-encoded SHA-256 of the zip at <see cref="DownloadUrl"/>.</summary>
    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }
}
