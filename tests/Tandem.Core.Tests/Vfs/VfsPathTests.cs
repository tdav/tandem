using Tandem.Core.Vfs;

namespace Tandem.Core.Tests.Vfs;

public class VfsPathTests
{
    [Theory]
    [InlineData("/Users/me/", "/Users/me")]
    [InlineData("//a///b", "/a/b")]
    [InlineData("/", "/")]
    [InlineData("///", "/")]
    public void Normalizes(string input, string expected) => Assert.Equal(expected, VfsPath.Local(input).Path);

    [Fact]
    public void Constructor_normalizes_too() =>
        Assert.Equal(new VfsPath("mem", "/a/b"), new VfsPath("mem", "/a//b/"));

    [Fact]
    public void Local_uses_local_provider() => Assert.Equal("local", VfsPath.Local("/a").ProviderId);

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    public void Rejects_non_absolute_paths(string input) =>
        Assert.Throws<ArgumentException>(() => VfsPath.Local(input));

    [Fact]
    public void Parent_and_name()
    {
        var p = VfsPath.Local("/a/b.txt");
        Assert.Equal("b.txt", p.Name);
        Assert.Equal(VfsPath.Local("/a"), p.Parent);
        Assert.Null(VfsPath.Local("/").Parent);
        Assert.True(VfsPath.Local("/").IsRoot);
        Assert.Equal("", VfsPath.Local("/").Name);
    }

    [Fact]
    public void Parent_of_top_level_entry_is_root()
    {
        Assert.Equal(VfsPath.Local("/"), VfsPath.Local("/a").Parent);
        Assert.False(VfsPath.Local("/a").IsRoot);
    }

    [Fact]
    public void Parent_keeps_provider() => Assert.Equal("mem", new VfsPath("mem", "/a/b").Parent!.Value.ProviderId);

    [Fact]
    public void Combine_appends_a_segment()
    {
        Assert.Equal(VfsPath.Local("/a/b"), VfsPath.Local("/a").Combine("b"));
        Assert.Equal(VfsPath.Local("/b"), VfsPath.Local("/").Combine("b"));
        Assert.Equal("mem", new VfsPath("mem", "/a").Combine("b").ProviderId);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("b/c")]
    public void Combine_rejects_names_that_are_not_a_single_segment(string name) =>
        Assert.Throws<ArgumentException>(() => VfsPath.Local("/a").Combine(name));

    [Fact]
    public void Descendant_check_respects_segment_boundaries()
    {
        Assert.True(VfsPath.Local("/a/b/c").IsSameOrDescendantOf(VfsPath.Local("/a/b")));
        Assert.False(VfsPath.Local("/a/bc").IsSameOrDescendantOf(VfsPath.Local("/a/b")));
        Assert.False(new VfsPath("mem", "/a/b").IsSameOrDescendantOf(VfsPath.Local("/a")));
    }

    [Fact]
    public void Descendant_check_handles_same_path_and_root()
    {
        Assert.True(VfsPath.Local("/a/b").IsSameOrDescendantOf(VfsPath.Local("/a/b")));
        Assert.True(VfsPath.Local("/a").IsSameOrDescendantOf(VfsPath.Local("/")));
        Assert.True(VfsPath.Local("/").IsSameOrDescendantOf(VfsPath.Local("/")));
        Assert.False(VfsPath.Local("/a").IsSameOrDescendantOf(VfsPath.Local("/a/b")));
    }
}
