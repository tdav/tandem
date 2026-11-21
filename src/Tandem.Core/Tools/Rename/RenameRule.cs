using Tandem.Core.Vfs;

namespace Tandem.Core.Tools.Rename;

public enum CaseChange { None, Lower, Upper, FirstUpper, EachWordUpper }

public sealed record RenameRule(string NameTemplate = "[N]", string ExtensionTemplate = "[E]",
    string Search = "", string Replace = "", bool UseRegex = false, bool MatchCase = false,
    CaseChange Case = CaseChange.None, int CounterStart = 1, int CounterStep = 1, int CounterDigits = 1);

public sealed class RenameRuleException(string message, int position) : FormatException(message)
{
    public int Position { get; } = position;
}

public enum RenameStatus { Unchanged, Ok, InvalidName, Duplicate, NameTaken }

public sealed record RenameItem(FileEntry Entry, string NewName, RenameStatus Status);

public sealed record RenamePlan(IReadOnlyList<RenameItem> Items)
{
    public int ChangedCount => Items.Count(item => item.Status == RenameStatus.Ok);
    public bool CanRun => Items.Any(item => item.Status == RenameStatus.Ok)
        && Items.All(item => item.Status is RenameStatus.Unchanged or RenameStatus.Ok);
}
