namespace Tandem.Core.Tests;

/// <summary>Tests that measure wall-clock time. They run alone, after the parallel tests, so other tests of this
/// assembly do not compete with them for the processor.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing";
}
