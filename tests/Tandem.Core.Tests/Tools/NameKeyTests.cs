using Tandem.Core.Tools;

namespace Tandem.Core.Tests.Tools;

public sealed class NameKeyTests
{
    private const string Composed = "й.txt"; // "й" as one code point, as servers and archives write it
    private const string Decomposed = "й.txt"; // "и" + combining breve, as macOS reports it

    [Fact]
    public void Composed_and_decomposed_spellings_have_one_key_and_letter_case_has_two()
    {
        Assert.NotEqual(Composed, Decomposed);
        Assert.Equal(NameKey.Of(Composed), NameKey.Of(Decomposed));
        Assert.Equal(Composed, NameKey.Of(Decomposed));
        Assert.NotEqual(NameKey.Of("Notes.txt"), NameKey.Of("notes.txt"));
        Assert.NotEqual(NameKey.Of("Й.txt"), NameKey.Of(Decomposed)); // "Й" is another name
    }

    [Fact]
    public void SameOnMacDisk_ignores_case_and_normalization()
    {
        Assert.True(NameKey.SameOnMacDisk(Composed, Decomposed));
        Assert.True(NameKey.SameOnMacDisk("Й.TXT", Decomposed));
        Assert.True(NameKey.SameOnMacDisk("Notes.txt", "notes.TXT"));
        Assert.False(NameKey.SameOnMacDisk("notes.txt", "notes.md"));
    }

    [Fact]
    public void Name_with_an_unpaired_surrogate_is_its_own_key()
    {
        const string broken = "a\uD800b.txt"; // a lone high surrogate: string.Normalize throws on it

        Assert.Equal(broken, NameKey.Of(broken));
        Assert.True(NameKey.SameOnMacDisk(broken, "A\uD800B.TXT"));
        Assert.False(NameKey.SameOnMacDisk(broken, "ab.txt"));
    }
}
