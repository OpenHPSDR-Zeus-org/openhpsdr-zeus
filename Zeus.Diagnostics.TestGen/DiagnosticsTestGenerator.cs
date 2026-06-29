// SPDX-License-Identifier: GPL-2.0-or-later
//
// Live Diagnostics API v2 — Layer 2 test framework.
//
// A Roslyn incremental source generator that emits ONE named xUnit test class
// per IDiagnosticsProvider into the Zeus.Server.Tests assembly. Each provider
// therefore shows up as its own discoverable, named test (e.g.
// DspLiveDiagnosticsProviderGeneratedTests) so a missing/broken provider is an
// obvious red test rather than a single data row in a [Theory].
//
// This COMPLEMENTS — does not replace — the hand-written reflection conformance
// [Theory] that exercises all providers generically.
//
// FEASIBILITY NOTE (load-bearing): the providers live in the REFERENCED assembly
// Zeus.Server.Hosting, not in the test project's own source. SyntaxProvider /
// ForAttributeWithMetadataName only see source in the CURRENT (test) compilation,
// so we must walk referenced-assembly SYMBOLS instead: take the compilation, get
// the IDiagnosticsProvider symbol via GetTypeByMetadataName, then enumerate types
// across SourceModule.ReferencedAssemblySymbols selecting concrete public classes
// that implement that interface (AllInterfaces).

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Zeus.Diagnostics.TestGen
{
    [Generator(LanguageNames.CSharp)]
    public sealed class DiagnosticsTestGenerator : IIncrementalGenerator
    {
        private const string ProviderInterfaceMetadataName =
            "Zeus.Server.Diagnostics.IDiagnosticsProvider";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // CompilationProvider is required because the provider types we want
            // live only in referenced assemblies, not in this compilation's syntax.
            var providers = context.CompilationProvider.Select(static (compilation, _) =>
                DiscoverProviders(compilation));

            context.RegisterSourceOutput(providers, static (spc, models) =>
            {
                foreach (var model in models)
                {
                    spc.AddSource(model.TypeName + ".g.cs",
                        SourceText.From(EmitTestClass(model), Encoding.UTF8));
                }
            });
        }

        private static ImmutableArray<ProviderModel> DiscoverProviders(Compilation compilation)
        {
            var ifaceSymbol = compilation.GetTypeByMetadataName(ProviderInterfaceMetadataName);
            if (ifaceSymbol is null)
            {
                // The test compilation does not (yet) reference Zeus.Server.Hosting.
                // Emit nothing rather than guessing.
                return ImmutableArray<ProviderModel>.Empty;
            }

            var results = new List<ProviderModel>();
            var seenTypeNames = new HashSet<string>();

            foreach (var reference in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                foreach (var type in EnumerateNamedTypes(reference.GlobalNamespace))
                {
                    if (!IsConcretePublicProvider(type, ifaceSymbol))
                        continue;

                    // Test-class identifiers are scoped to the generated namespace
                    // and keyed off the simple type name. Two providers with the
                    // same simple name in different namespaces would collide; if
                    // that ever happens, skip the duplicate deterministically so
                    // the build stays green (the conformance [Theory] still covers
                    // every instance by Id).
                    if (!seenTypeNames.Add(type.Name))
                        continue;

                    results.Add(new ProviderModel(typeName: type.Name));
                }
            }

            // Deterministic ordering keeps generated output stable across runs and
            // platforms (assembly enumeration order is not guaranteed).
            results.Sort(static (a, b) => string.CompareOrdinal(a.TypeName, b.TypeName));
            return results.ToImmutableArray();
        }

        private static IEnumerable<INamedTypeSymbol> EnumerateNamedTypes(INamespaceSymbol ns)
        {
            foreach (var member in ns.GetMembers())
            {
                if (member is INamespaceSymbol childNs)
                {
                    foreach (var nested in EnumerateNamedTypes(childNs))
                        yield return nested;
                }
                else if (member is INamedTypeSymbol type)
                {
                    yield return type;
                    foreach (var nested in EnumerateNestedTypes(type))
                        yield return nested;
                }
            }
        }

        private static IEnumerable<INamedTypeSymbol> EnumerateNestedTypes(INamedTypeSymbol type)
        {
            foreach (var nested in type.GetTypeMembers())
            {
                yield return nested;
                foreach (var deeper in EnumerateNestedTypes(nested))
                    yield return deeper;
            }
        }

        private static bool IsConcretePublicProvider(INamedTypeSymbol type, INamedTypeSymbol iface)
        {
            return type.TypeKind == TypeKind.Class
                && !type.IsAbstract
                && !type.IsStatic
                && type.DeclaredAccessibility == Accessibility.Public
                && !type.IsGenericType
                && type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, iface));
        }

        private static string EmitTestClass(ProviderModel model)
        {
            // Identifiers/strings here are all derived from C# symbol names, so they
            // are already valid C# and safe to interpolate verbatim.
            var sb = new StringBuilder();
            sb.Append(
@"// <auto-generated/>
#nullable enable
#pragma warning disable
//
// Live Diagnostics API v2 — Layer 2 generated test.
// Source: Zeus.Diagnostics.TestGen.DiagnosticsTestGenerator.
// One generated [Fact] suite per IDiagnosticsProvider. Do NOT edit by hand —
// add your own [Fact]s in a sibling partial-class file instead.

using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zeus.Server.Diagnostics;

namespace Zeus.Server.Tests
{
    /// <summary>
    /// Generated coverage for the <c>");
            sb.Append(model.TypeName);
            sb.Append(
@"</c> diagnostics provider.
    /// Partial so developers can add their own [Fact]s in a sibling file.
    /// </summary>
    public sealed partial class ");
            sb.Append(model.TypeName);
            sb.Append(
@"GeneratedTests
    {
        private const string ProviderTypeName = """);
            sb.Append(model.TypeName);
            sb.Append(
@""";

        private sealed class Factory : IsolatedPrefsFactory
        {
        }

        private static IDiagnosticsProvider ResolveProvider(DiagnosticsProviderRegistry registry)
        {
            var provider = registry.All.FirstOrDefault(p => p.GetType().Name == ProviderTypeName);
            Assert.True(
                provider is not null,
                $""Diagnostics provider '{ProviderTypeName}' was not registered in DI. "" +
                ""Every concrete IDiagnosticsProvider must be registered so the unified "" +
                ""v2 surface exposes it."");
            return provider!;
        }

        [Fact]
        public void Provider_IsRegistered_AndSnapshotIsNonNull()
        {
            using var factory = new Factory();
            var registry = factory.Services.GetRequiredService<DiagnosticsProviderRegistry>();

            var provider = ResolveProvider(registry);

            Assert.False(string.IsNullOrWhiteSpace(provider.Id), ""Provider Id must be non-empty."");
            Assert.False(string.IsNullOrWhiteSpace(provider.RouteSegment), ""Provider RouteSegment must be non-empty."");
            Assert.NotNull(provider.Snapshot());
        }

        [Fact]
        public async Task SnapshotEndpoint_Returns200_WithSchemaVersion()
        {
            using var factory = new Factory();
            using var client = factory.CreateClient();
            var registry = factory.Services.GetRequiredService<DiagnosticsProviderRegistry>();
            var provider = ResolveProvider(registry);

            var resp = await client.GetAsync(""/api/diagnostics/v2/"" + provider.RouteSegment);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            Assert.True(
                body.RootElement.TryGetProperty(""schemaVersion"", out _),
                ""Diagnostics snapshot JSON must expose a 'schemaVersion' property."");
        }

        [Fact]
        public async Task SelfCheckEndpoint_Returns200()
        {
            using var factory = new Factory();
            using var client = factory.CreateClient();
            var registry = factory.Services.GetRequiredService<DiagnosticsProviderRegistry>();
            var provider = ResolveProvider(registry);

            var resp = await client.GetAsync(""/api/diagnostics/v2/"" + provider.RouteSegment + ""/selfcheck"");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
    }
}
");
            return sb.ToString();
        }

        private readonly struct ProviderModel
        {
            public ProviderModel(string typeName)
            {
                TypeName = typeName;
            }

            public string TypeName { get; }
        }
    }
}
