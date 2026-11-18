using Tandem.App.Resources;

namespace Tandem.App.Tests;

/// <summary>Tests that switch <see cref="Loc"/>'s language. The language is process-wide, so they run alone,
/// never alongside other tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageCollection
{
    public const string Name = "Language";
}

/// <summary>Restores the language that was current when the test started.</summary>
public abstract class LanguageTestBase : IDisposable
{
    private readonly string _saved = Loc.Language;

    public void Dispose()
    {
        Loc.SetLanguage(_saved);
        GC.SuppressFinalize(this);
    }
}
