using Tandem.App.Resources;
using Tandem.App.Services;

namespace Tandem.App.Tests;

[Collection(LanguageCollection.Name)]
public class SizeFormatterTests : LanguageTestBase
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(2516582, "2.4 MB")]
    [InlineData(1073741824, "1.0 GB")]
    [InlineData(1099511627776, "1.0 TB")]
    [InlineData(1048575, "1.0 MB")] // 1023.999 KB rounds to 1024.0 KB, so it is shown in the next unit
    [InlineData(2251799813685248, "2048.0 TB")] // nothing above TB
    public void Formats_sizes_en(long bytes, string expected)
    {
        Loc.SetLanguage("en");
        Assert.Equal(expected, SizeFormatter.Format(bytes));
    }

    [Theory]
    [InlineData(0, "0 Б")]
    [InlineData(1023, "1023 Б")]
    [InlineData(1024, "1,0 КБ")]
    [InlineData(2516582, "2,4 МБ")]
    [InlineData(1073741824, "1,0 ГБ")]
    [InlineData(1099511627776, "1,0 ТБ")]
    public void Formats_sizes_ru(long bytes, string expected)
    {
        Loc.SetLanguage("ru");
        Assert.Equal(expected, SizeFormatter.Format(bytes));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Negative_size_is_shown_as_zero(long bytes)
    {
        Loc.SetLanguage("en");
        Assert.Equal("0 B", SizeFormatter.Format(bytes));
    }
}
