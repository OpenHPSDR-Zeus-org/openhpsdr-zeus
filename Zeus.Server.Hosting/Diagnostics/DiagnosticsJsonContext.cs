// SPDX-License-Identifier: GPL-2.0-or-later
//
// Live Diagnostics API v2 — System.Text.Json source-generation context.
//
// Performance is the #1 constraint here. The aggregate health frame is pushed
// over the hub at 1-2 Hz to every connected client, and the index/snapshot
// endpoints are polled — so the v2 DTOs serialise through fast, pre-generated
// metadata instead of per-call reflection.
//
// This context is INSERTED at the front of the host's TypeInfoResolverChain
// (see ZeusHost.Build). Types listed here use generated metadata; every other
// DTO in the app (including the anonymous hardware-diagnostics snapshot and all
// legacy contracts) falls through to the reflection resolver still in the chain,
// so nothing else changes. CamelCase + string enums match the existing Web
// defaults, keeping the wire output byte-identical.

using System.Text.Json.Serialization;
using Zeus.Contracts;

namespace Zeus.Server.Diagnostics;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
// Framework / aggregate DTOs (owned here, pushed + polled hot).
[JsonSerializable(typeof(DiagnosticsIndexDto))]
[JsonSerializable(typeof(DiagnosticsProviderInfoDto))]
[JsonSerializable(typeof(ProviderSelfCheckReportDto))]
[JsonSerializable(typeof(SelfCheckResultDto))]
[JsonSerializable(typeof(DiagnosticsHealthDto))]
// Typed per-provider snapshot DTOs.
[JsonSerializable(typeof(DspLiveDiagnosticsDto))]
[JsonSerializable(typeof(DspModernizationEvidenceSnapshotDto))]
[JsonSerializable(typeof(FrontendAudioPlaybackDiagnosticsDto))]
public sealed partial class DiagnosticsJsonContext : JsonSerializerContext
{
}
