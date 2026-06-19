using Xunit;

namespace Zeus.Plugins.Host.Tests;

/// <summary>
/// xUnit collection for tests that MEASURE timing or allocation and are
/// therefore sensitive to parallel CPU/scheduling/GC contention. Marking them
/// with <c>[Collection("LoadSensitive")]</c> + DisableParallelization makes the
/// collection run on its own — not concurrently with the rest of the assembly —
/// so the measurement isn't perturbed by sibling tests. This is the systemic
/// fix for the intermittent CI flakes that appeared once the assembly grew
/// (the watchdog/crash-loop timing tests and the no-allocation hot-path test).
/// </summary>
[CollectionDefinition("LoadSensitive", DisableParallelization = true)]
public sealed class LoadSensitiveCollection { }
