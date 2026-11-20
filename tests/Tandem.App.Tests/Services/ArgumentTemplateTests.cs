using Tandem.App.Services;

namespace Tandem.App.Tests.Services;

public class ArgumentTemplateTests
{
    private static readonly ArgumentTemplate.Context WithCursor = new("/cur", "/other", "/cur/a", ["/cur/a", "/cur/b"]);

    private static readonly ArgumentTemplate.Context WithoutCursor = new("/cur", "/other", null, []);

    [Theory]
    [InlineData("-a %p", true, new[] { "-a", "/cur" })]
    [InlineData("%s", true, new[] { "/cur/a", "/cur/b" })]
    [InlineData("--files=%s", true, new[] { "--files=/cur/a /cur/b" })]
    [InlineData("\"my file\" %f", true, new[] { "my file", "/cur/a" })]
    [InlineData("--out=%t/x %n", true, new[] { "--out=/other/x", "a" })]
    [InlineData("100%% %f", false, new[] { "100%" })]
    [InlineData("\"a \\\"b\\\"\"", true, new[] { "a \"b\"" })]
    [InlineData("\"c:\\\\x\" %%p", true, new[] { "c:\\x", "%p" })]
    [InlineData("  -v   %p  ", true, new[] { "-v", "/cur" })]
    [InlineData("", true, new string[0])]
    [InlineData("\"\" x", true, new[] { "", "x" })]
    [InlineData("%n %s --all=%s", false, new string[0])]
    [InlineData("50% off%", true, new[] { "50%", "off%" })]
    [InlineData("\"%f and %n\"", true, new[] { "/cur/a and a" })]
    [InlineData("\"unclosed %p", true, new[] { "unclosed /cur" })]
    [InlineData("back\\slash", true, new[] { "back\\slash" })]
    [InlineData("\"%s\"", true, new[] { "/cur/a", "/cur/b" })]
    [InlineData("--x=\"a b\" y", true, new[] { "--x=a b", "y" })]
    public void Expand(string template, bool cursor, string[] expected) =>
        Assert.Equal(expected, ArgumentTemplate.Expand(template, cursor ? WithCursor : WithoutCursor));

    [Fact]
    public void Paths_with_spaces_and_quotes_stay_single_arguments()
    {
        const string folder = "/Users/me/My \"odd\" folder's";
        var file = folder + "/a b.txt";
        var context = new ArgumentTemplate.Context(folder, "/other side", file, [file, folder + "/c\td"]);

        Assert.Equal([folder, "--open=" + file, "/other side", file, folder + "/c\td", "a b.txt"],
            ArgumentTemplate.Expand("%p --open=%f %t %s %n", context));
    }

    [Fact]
    public void Name_of_the_cursor_item_is_its_last_path_segment()
    {
        var context = new ArgumentTemplate.Context("/cur", "/other", "/cur/Folder.app/", []);
        Assert.Equal(["Folder.app"], ArgumentTemplate.Expand("%n", context));
    }

    [Fact]
    public void Other_folder_placeholder_without_an_other_folder_makes_the_line_unusable()
    {
        var context = WithCursor with { OtherPath = null };

        Assert.Throws<ArgumentTemplate.OtherFolderUnavailableException>(() =>
            ArgumentTemplate.Expand("cp %s %t", context));
        Assert.Throws<ArgumentTemplate.OtherFolderUnavailableException>(() =>
            ArgumentTemplate.Expand("%n --to=%t", WithoutCursor with { OtherPath = null }));
        Assert.Equal(["cp", "/cur/a", "/cur/b", "%t"], ArgumentTemplate.Expand("cp %s %%t", context));
    }
}
