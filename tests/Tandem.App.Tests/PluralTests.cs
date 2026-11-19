using Tandem.App.Resources;

namespace Tandem.App.Tests;

[Collection(LanguageCollection.Name)]
public class PluralTests : LanguageTestBase
{
    [Theory]
    [InlineData(1, PluralForm.One)]
    [InlineData(21, PluralForm.One)]
    [InlineData(101, PluralForm.One)]
    [InlineData(2, PluralForm.Few)]
    [InlineData(3, PluralForm.Few)]
    [InlineData(4, PluralForm.Few)]
    [InlineData(22, PluralForm.Few)]
    [InlineData(104, PluralForm.Few)]
    [InlineData(0, PluralForm.Many)]
    [InlineData(5, PluralForm.Many)]
    [InlineData(11, PluralForm.Many)]
    [InlineData(12, PluralForm.Many)]
    [InlineData(14, PluralForm.Many)]
    [InlineData(19, PluralForm.Many)]
    [InlineData(25, PluralForm.Many)]
    [InlineData(111, PluralForm.Many)]
    [InlineData(112, PluralForm.Many)]
    public void Russian_forms(long n, PluralForm expected)
    {
        Loc.SetLanguage("ru");
        Assert.Equal(expected, Loc.PluralFormOf(n));
    }

    [Theory]
    [InlineData(1, PluralForm.One)]
    [InlineData(0, PluralForm.Many)]
    [InlineData(2, PluralForm.Many)]
    [InlineData(21, PluralForm.Many)]
    public void English_forms(long n, PluralForm expected)
    {
        Loc.SetLanguage("en");
        Assert.Equal(expected, Loc.PluralFormOf(n));
    }

    [Fact]
    public void Count_words_the_number_in_russian()
    {
        Loc.SetLanguage("ru");
        Assert.Equal("1 объект", Loc.Count("Count_Items", 1));
        Assert.Equal("3 объекта", Loc.Count("Count_Items", 3));
        Assert.Equal("5 объектов", Loc.Count("Count_Items", 5));
        Assert.Equal("11 объектов", Loc.Count("Count_Items", 11));
        Assert.Equal("21 объект", Loc.Count("Count_Items", 21));
        Assert.Equal("2 операции", Loc.Count("Count_Operations", 2));
    }

    [Fact]
    public void Count_words_the_number_in_english()
    {
        Loc.SetLanguage("en");
        Assert.Equal("1 item", Loc.Count("Count_Items", 1));
        Assert.Equal("3 items", Loc.Count("Count_Items", 3));
        Assert.Equal("1 operation", Loc.Count("Count_Operations", 1));
        Assert.Equal("4 operations", Loc.Count("Count_Operations", 4));
    }
}
