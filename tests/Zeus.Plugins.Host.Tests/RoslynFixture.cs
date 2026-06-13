using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Zeus.Plugins.Host.Tests;

/// <summary>
/// Compiles a synthetic plugin assembly into a temp directory and
/// writes a matching plugin.json so PluginLoader can find both. Each
/// fixture is fully isolated — disposing removes the temp dir.
/// </summary>
internal sealed class RoslynFixture : IDisposable
{
    public string PluginDir { get; }
    public string AssemblyName { get; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    private RoslynFixture(string pluginDir, string assemblyName)
    {
        PluginDir = pluginDir;
        AssemblyName = assemblyName;
    }

    /// <summary>
    /// Build a fixture with the supplied C# source and manifest.
    /// <paramref name="csharpSource"/> must define a public type that
    /// implements <c>Zeus.Plugins.Contracts.IZeusPlugin</c>.
    /// </summary>
    public static RoslynFixture Create(
        string assemblyName,
        string csharpSource,
        string manifestJson)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "zeus-plugin-fixtures",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        // Reference assemblies — built from TRUSTED_PLATFORM_ASSEMBLIES
        // (the runtime's resolved assembly list, canonical across
        // every .NET install layout) with explicit shared-framework
        // preference when two copies of the same dll exist. Required
        // because some packages (e.g. Microsoft.NET.Test.Sdk) drop
        // older copies of Microsoft.Extensions.Logging.Abstractions
        // into bin/ that don't match the runtime's 10.0.x copy in
        // Microsoft.AspNetCore.App / Microsoft.NETCore.App. Without
        // the framework preference, Roslyn picks the older bin/
        // copy and CS1705 (version mismatch) fires when
        // Zeus.Plugins.Contracts.dll references the newer runtime
        // assembly.
        var byName = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);

        static bool IsSharedFramework(string p)
            => p.Contains($"{Path.DirectorySeparatorChar}shared{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

        // True only for managed assemblies. Native DLLs that share the
        // output dir — the zeus-vst-bridge.dll that CopyVstBridgeDylib
        // stages once the native bridge is built — have no CLI metadata.
        // MetadataReference.CreateFromFile succeeds lazily on them, so the
        // failure would otherwise surface much later as CS0009
        // ("PE image doesn't contain managed metadata") at Emit time and
        // break every fixture-compiling test. Filter them out up front.
        static bool HasManagedMetadata(string p)
        {
            try
            {
                using var fs = File.OpenRead(p);
                using var pe = new PEReader(fs);
                return pe.HasMetadata;
            }
            catch { return false; }
        }

        void Consider(string dllPath)
        {
            if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath)) return;
            var key = Path.GetFileName(dllPath);
            if (byName.TryGetValue(key, out var existing))
            {
                // Shared framework copy preferred over package copies
                // (the runtime activates the framework version at
                // execution time anyway).
                var existingPath = existing.Display ?? "";
                var existingIsShared = IsSharedFramework(existingPath);
                var thisIsShared = IsSharedFramework(dllPath);
                if (existingIsShared || !thisIsShared) return;
                // current is shared, existing isn't — overwrite.
            }
            if (!HasManagedMetadata(dllPath)) return;
            try { byName[key] = MetadataReference.CreateFromFile(dllPath); }
            catch { /* non-managed dll, skip */ }
        }

        // 1. Trusted platform assemblies — the canonical list.
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                Consider(path);
        }

        // 2. Test binary's output dir — picks up ProjectReference outputs
        //    (Zeus.Plugins.Contracts.dll, Zeus.Plugins.Host.dll) that
        //    aren't in the TPA list because they're not framework refs.
        foreach (var dll in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
            Consider(dll);

        // 3. Anything loaded via reflection not yet covered.
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.IsDynamic || string.IsNullOrEmpty(a.Location)) continue;
            Consider(a.Location);
        }

        var refs = byName.Values.ToList();

        // Ensure Zeus.Plugins.Contracts is present even if not yet
        // loaded (it isn't in any of the dirs above — lives in
        // this test project's bin via ProjectReference output).
        var contractsAsm = typeof(Zeus.Plugins.Contracts.IZeusPlugin).Assembly;
        var contractsName = Path.GetFileName(contractsAsm.Location);
        if (!byName.ContainsKey(contractsName))
            refs.Add(MetadataReference.CreateFromFile(contractsAsm.Location));

        var syntax = CSharpSyntaxTree.ParseText(csharpSource);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { syntax },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var dllPath = Path.Combine(tempRoot, assemblyName + ".dll");
        var emit = compilation.Emit(dllPath);
        if (!emit.Success)
        {
            var diagnostics = string.Join(
                Environment.NewLine,
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            throw new InvalidOperationException("Roslyn compile failed:" + Environment.NewLine + diagnostics);
        }

        File.WriteAllText(Path.Combine(tempRoot, "plugin.json"), manifestJson);
        return new RoslynFixture(tempRoot, assemblyName);
    }

    public void Dispose()
    {
        try { Directory.Delete(PluginDir, recursive: true); }
        catch { /* best effort */ }
    }
}
