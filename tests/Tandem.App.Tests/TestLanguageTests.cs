using Tandem.App.Resources;

namespace Tandem.App.Tests;

public class TestLanguageTests
{
    [Fact]
    public void Tests_run_in_english_regardless_of_system_language()
    {
        Assert.Equal("en", Loc.Language);
        Assert.Equal("Cancel", Loc.Get("Button_Cancel"));
    }
}
