using System.Runtime.CompilerServices;
using Tandem.App.Resources;

namespace Tandem.App.Tests;

/// <summary>Tests that switch <see cref="Loc"/>'s language. The language is process-wide, so they run alone,
/// never alongside other tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageCollection
{
    public const string Name = "Language";
}

/// <summary>Puts every test back on <see cref="TestLanguage.Default"/> when it ends.</summary>
public abstract class LanguageTestBase : IDisposable
{
    public void Dispose()
    {
        Loc.SetLanguage(TestLanguage.Default);
        GC.SuppressFinalize(this);
    }
}

/// <summary>Tests run in English whatever the machine's system language is.</summary>
public static class TestLanguage
{
    public const string Default = "en";

    [ModuleInitializer]
    internal static void Initialize() => Loc.SetLanguage(Default);
}
