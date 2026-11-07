using Xunit;

namespace EgyChina.Pos.Tests;

/// <summary>
/// PosLog is process-static. Tests that SetDirectory/Reset must not run in parallel.
/// </summary>
[CollectionDefinition("PosLog", DisableParallelization = true)]
public sealed class PosLogCollection;
