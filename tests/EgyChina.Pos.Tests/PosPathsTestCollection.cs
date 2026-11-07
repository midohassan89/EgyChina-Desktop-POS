using Xunit;

namespace EgyChina.Pos.Tests;

/// <summary>
/// Serializes tests that mutate the process-wide <see cref="EgyChina.Pos.Data.PosPaths"/> override.
/// </summary>
[CollectionDefinition("PosPathsSerial", DisableParallelization = true)]
public sealed class PosPathsSerialCollection;
