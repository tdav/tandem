using Tandem.Core.Tools.Rename;
using Tandem.Core.Vfs;

namespace Tandem.Core.Tests.Tools.Rename;

public sealed class RenamePlannerTests
{
    [Theory]
    [InlineData("[N]_old", "report.pdf", "report_old.pdf")]
    [InlineData("[N2-4]", "abcdef.txt", "bcd.txt")]
    [InlineData("[N3-]", "abcdef.txt", "cdef.txt")]
    [InlineData("[N1-1]", "и\u0306а.txt", "и\u0306.txt")]
    [InlineData("[N1-1]", "😀a.txt", "😀.txt")]
    [InlineData("[[]x]", "a.txt", "[x].txt")]
    public void Tokens_keep_text_elements_intact(string template, string name, string expected)
    {
        var item = Assert.Single(Build(new RenameRule(template), name).Items);
        Assert.Equal(expected, item.NewName);
        Assert.Equal(RenameStatus.Ok, item.Status);
    }

    [Theory]
    [InlineData("[X]", 0)]
    [InlineData("ab[N", 2)]
    [InlineData("[N5-2]", 0)]
    public void Invalid_template_reports_position(string template, int position) =>
        Assert.Equal(position, Assert.Throws<RenameRuleException>(() => Build(new RenameRule(template), "a.txt")).Position);

    [Fact]
    public void Empty_base_is_invalid() =>
        Assert.Equal(RenameStatus.InvalidName, Assert.Single(Build(new RenameRule("[N9-]"), "abc.txt").Items).Status);

    [Fact]
    public void Counter_date_and_extension_can_be_formatted()
    {
        var plan = Build(new RenameRule("[C]-[Y]-[M]-[D]", "txt", CounterStart: 5, CounterStep: 5, CounterDigits: 3),
            "a.pdf", "b.pdf");
        Assert.Equal(["005-2026-03-02.txt", "010-2026-03-02.txt"], plan.Items.Select(row => row.NewName));
    }

    [Fact]
    public void Regex_groups_and_case_leave_extension_alone() =>
        Assert.Equal("B-A.TXT", Assert.Single(Build(new RenameRule(Search: "(a)-(b)", Replace: "$2-$1",
            UseRegex: true, Case: CaseChange.Upper), "a-b.TXT").Items).NewName);

    [Fact]
    public void Swaps_and_chains_are_allowed()
    {
        var swap = Build(new RenameRule(Search: "^(a)(b)$|^(b)(a)$", Replace: "$2$1$4$3", UseRegex: true), "ab", "ba");
        Assert.True(swap.CanRun);
        Assert.Equal(["ba", "ab"], swap.Items.Select(item => item.NewName));
        var chain = Build(new RenameRule("[C]"), "0", "1");
        Assert.True(chain.CanRun);
        Assert.All(chain.Items, item => Assert.Equal(RenameStatus.Ok, item.Status));
    }

    [Fact]
    public void Duplicates_and_bystanders_use_mac_name_equality()
    {
        var names = new Dictionary<VfsPath, IReadOnlySet<string>>
        {
            [new("mem", "/")] = new HashSet<string> { "a.txt", "b.txt", "Й.txt" },
        };
        var plan = RenamePlanner.Build([Entry("a.txt"), Entry("b.txt")], new RenameRule("и\u0306"), names, TimeZoneInfo.Utc);
        Assert.False(plan.CanRun);
        Assert.All(plan.Items, item => Assert.Equal(RenameStatus.Duplicate, item.Status));
        plan = RenamePlanner.Build([Entry("a.txt")], new RenameRule("и\u0306"), names, TimeZoneInfo.Utc);
        Assert.Equal(RenameStatus.NameTaken, Assert.Single(plan.Items).Status);
    }

    private static RenamePlan Build(RenameRule rule, params string[] names) =>
        RenamePlanner.Build(names.Select(Entry).ToArray(), rule,
            new Dictionary<VfsPath, IReadOnlySet<string>> { [new("mem", "/")] = names.ToHashSet() },
            TimeZoneInfo.CreateCustomTimeZone("UTC+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3"));

    private static FileEntry Entry(string name) => new(new("mem", "/" + name), EntryKind.File, 0,
        new DateTimeOffset(2026, 3, 1, 23, 30, 0, TimeSpan.Zero), default, null, false);
}
