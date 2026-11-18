using Tandem.Core.Listing;

namespace Tandem.Core.Tests.Listing;

public class FileMaskTests
{
    [Theory]
    [InlineData("*.txt", "a.TXT", true)]
    [InlineData("*.txt;*.md", "b.md", true)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "abbc", false)]
    [InlineData("", "anything", true)]
    [InlineData("*.txt", "a.txt.bak", false)]
    public void Mask(string pattern, string name, bool expected) =>
        Assert.Equal(expected, new FileMask(pattern).IsMatch(name));

    [Theory]
    [InlineData("*.txt; *.md", "b.md", true)]
    [InlineData("*.txt;", "b.md", false)]
    [InlineData("*", "", true)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("*a", "banana", true)]
    [InlineData("???", "ab", false)]
    [InlineData("REPORT*", "report-2026.pdf", true)]
    public void Mask_edge_cases(string pattern, string name, bool expected) =>
        Assert.Equal(expected, new FileMask(pattern).IsMatch(name));

    [Theory]
    [InlineData("*.*", "noext", true)]
    [InlineData("*.*", "a.txt", true)]
    [InlineData("*.*", ".gitignore", true)]
    [InlineData("*.txt; *.* ", "noext", true)]
    [InlineData("a.*", "abc", false)]
    [InlineData("a.*", "a.txt", true)]
    [InlineData("*.*x", "noext", false)]
    public void Star_dot_star_part_matches_every_name(string pattern, string name, bool expected) =>
        Assert.Equal(expected, new FileMask(pattern).IsMatch(name));

    [Fact]
    public void Matches_across_normalization_forms()
    {
        Assert.True(new FileMask("й*").IsMatch("й.txt"));     // NFC pattern, NFD name
        Assert.True(new FileMask("й?").IsMatch("йx"));        // NFD pattern, NFC name
        Assert.True(new FileMask("?.txt").IsMatch("й.txt"));       // NFD й counts as one character
        Assert.True(new FileMask("Й*").IsMatch("йф"));         // Cyrillic case-insensitive
    }
}
