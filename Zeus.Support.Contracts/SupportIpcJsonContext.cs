// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Serialization;

namespace Zeus.Support.Contracts;

/// <summary>
/// Source-generated System.Text.Json metadata for the support IPC contract.
/// Serialising through the base <see cref="SupportIpcMessage"/> emits the
/// polymorphic <c>kind</c> discriminator, so a reader can deserialise back to the
/// concrete subtype without knowing it ahead of time. CamelCase + string enums
/// keep the framed JSON human-readable in a packet/pipe trace.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SupportIpcMessage))]
public sealed partial class SupportIpcJsonContext : JsonSerializerContext;
