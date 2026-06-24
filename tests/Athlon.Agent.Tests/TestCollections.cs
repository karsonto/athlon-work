namespace Athlon.Agent.Tests;

public static class TestCollections
{
    public const string Sta = "STA tests";
}

[CollectionDefinition(TestCollections.Sta, DisableParallelization = true)]
public sealed class StaTestCollection;
