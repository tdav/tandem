using Tandem.App.Services;

namespace Tandem.App.Tests.Services;

/// <summary>The command line's <c>cd</c>: which input is a <c>cd</c>, and where it goes, from /a/b with home /home.</summary>
public class CdCommandParserTests
{
    private const string Current = "/a/b";
    private const string Home = "/home";

    private static CdCommand? Parse(string text) => CdCommandParser.Parse(text, Current, Home);

    [Theory]
    [InlineData("cd", "/home")]
    [InlineData("  cd  ", "/home")]
    [InlineData("cd ~", "/home")]
    [InlineData("cd ~/", "/home")]
    [InlineData("cd ~/x", "/home/x")]
    [InlineData("cd ~/x/../y", "/home/y")]
    [InlineData("cd ..", "/a")]
    [InlineData("cd ../..", "/")]
    [InlineData("cd ../../../..", "/")]
    [InlineData("cd /..", "/")]
    [InlineData("cd .", "/a/b")]
    [InlineData("cd c", "/a/b/c")]
    [InlineData("cd ./c/", "/a/b/c")]
    [InlineData("cd c/../d", "/a/b/d")]
    [InlineData("cd\tc", "/a/b/c")]
    [InlineData("cd   c   ", "/a/b/c")]
    [InlineData("cd /x", "/x")]
    [InlineData("cd //x//y/", "/x/y")]
    [InlineData("cd /", "/")]
    [InlineData("cd \"my dir\"", "/a/b/my dir")]
    [InlineData("cd 'my dir'", "/a/b/my dir")]
    [InlineData(@"cd my\ dir", "/a/b/my dir")]
    [InlineData("cd \"/x/my dir\"", "/x/my dir")]
    [InlineData("cd 'it'\"s\"", "/a/b/its")]
    [InlineData("cd '~'", "/a/b/~")]
    [InlineData("cd \"~/x\"", "/a/b/~/x")]
    [InlineData("cd ''", "/a/b")]
    [InlineData(@"cd 'a\b'", @"/a/b/a\b")]
    [InlineData("cd \"a\\\"b\"", "/a/b/a\"b")]
    [InlineData("cd $HOME", "/a/b/$HOME")]
    [InlineData("cd *", "/a/b/*")]
    public void Cd_resolves_its_target(string input, string expected)
    {
        var cd = Parse(input);

        Assert.NotNull(cd);
        Assert.Null(cd.Problem);
        Assert.Equal(expected, cd.Target);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ls")]
    [InlineData("cdx")]
    [InlineData("cd.")]
    [InlineData("cd..")]
    [InlineData("CD ..")]
    [InlineData("echo cd")]
    [InlineData("'cd' ..")]
    [InlineData("cd;ls")]
    public void Only_a_first_word_of_exactly_cd_is_a_cd(string input)
    {
        Assert.Null(Parse(input));
    }

    [Theory]
    [InlineData("cd -", CdProblem.PreviousFolderUnsupported)]
    [InlineData("cd ~user", CdProblem.UserHomeUnsupported)]
    [InlineData("cd ~user/x", CdProblem.UserHomeUnsupported)]
    [InlineData("cd \"my dir", CdProblem.Unterminated)]
    [InlineData("cd 'my dir", CdProblem.Unterminated)]
    [InlineData(@"cd dir\", CdProblem.Unterminated)]
    [InlineData("cd a b", CdProblem.TooManyArguments)]
    [InlineData("cd a 'b c'", CdProblem.TooManyArguments)]
    public void Unsupported_or_malformed_cd_is_a_problem_not_a_target(string input, CdProblem problem)
    {
        var cd = Parse(input);

        Assert.NotNull(cd);
        Assert.Null(cd.Target);
        Assert.Equal(problem, cd.Problem);
    }

    [Fact]
    public void A_problem_keeps_the_argument_as_typed()
    {
        Assert.Equal("~user", Parse("cd   ~user ")!.Argument);
    }

    [Fact]
    public void The_current_folder_at_the_root_resolves_relative_paths()
    {
        Assert.Equal("/x", CdCommandParser.Parse("cd x", "/", Home)!.Target);
        Assert.Equal("/", CdCommandParser.Parse("cd ..", "/", Home)!.Target);
    }
}
