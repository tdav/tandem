using Tandem.App.Services;
using Tandem.Core.Vfs.Local;

namespace Tandem.App.Tests.Services;

/// <summary>cd as cmd.exe reads it: backslash is a separator, double quotes group, drives and "/d" work.</summary>
public class CdCommandParserWindowsTests
{
    private const string Current = "/C:/Users/me/Docs";
    private const string Home = @"C:\Users\me";

    private static CdCommand? Parse(string text) =>
        CdCommandParser.Parse(text, Current, Home, LocalPathStyle.Windows);

    [Theory]
    [InlineData("cd", "/C:/Users/me")]
    [InlineData("cd ~", "/C:/Users/me")]
    [InlineData(@"cd ~\Music", "/C:/Users/me/Music")]
    [InlineData(@"cd D:\Work", "/D:/Work")]
    [InlineData("cd d:", "/D:")]
    [InlineData(@"cd /d E:\x", "/E:/x")]
    [InlineData(@"cd sub\dir", "/C:/Users/me/Docs/sub/dir")]
    [InlineData(@"cd ..\..", "/C:/Users")]
    [InlineData(@"cd \", "/C:")]
    [InlineData(@"cd \Windows", "/C:/Windows")]
    [InlineData("cd \"My Files\"", "/C:/Users/me/Docs/My Files")]
    [InlineData(@"cd \\srv\share\x", "/UNC/srv/share/x")]
    [InlineData(@"cd C:\..", "/C:")]
    public void Cd_resolves_its_target(string input, string expected) =>
        Assert.Equal(expected, Parse(input)?.Target);

    [Theory]
    [InlineData("cd \"open", CdProblem.Unterminated)]
    [InlineData("cd a b", CdProblem.TooManyArguments)]
    [InlineData("cd ~bob", CdProblem.UserHomeUnsupported)]
    public void Problems(string input, CdProblem problem) => Assert.Equal(problem, Parse(input)?.Problem);

    [Theory]
    [InlineData("dir")]
    [InlineData("cdx")]
    public void Not_a_cd(string input) => Assert.Null(Parse(input));
}
