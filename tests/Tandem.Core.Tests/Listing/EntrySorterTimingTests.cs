using System.Diagnostics;
using Tandem.Core.Listing;
using Tandem.Core.Vfs;

namespace Tandem.Core.Tests.Listing;

/// <summary>How fast a large folder sorts. Code and collation data are loaded by a warm-up sort outside the timed
/// part; the bound stays one second.</summary>
[Collection(TimingCollection.Name)]
public class EntrySorterTimingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static List<FileEntry> RandomEntries(int count, int seed)
    {
        var random = new Random(seed);
        var entries = new List<FileEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var name = random.Next(4) switch
            {
                0 => $"File{random.Next(1_000_000)}.txt",
                1 => $"photo_{random.Next(10_000):D5}.JPG",
                2 => $"документ {random.Next(100_000)}.docx",
                _ => $"й{random.Next(100_000)}",
            };
            var kind = random.Next(10) == 0 ? EntryKind.Directory : EntryKind.File;
            entries.Add(new FileEntry(VfsPath.Local("/dir").Combine(name + "-" + i), kind, random.Next(), T0, T0,
                Mode: null, IsHidden: false, LinkTarget: null, LinkPointsToDirectory: false));
        }
        return entries;
    }

    [Fact]
    public void Sorts_100000_entries_by_name_in_under_a_second()
    {
        var warmUp = RandomEntries(5_000, 7);
        warmUp.Sort(EntrySorter.Create(new SortSpec(SortColumn.Name)));
        var entries = RandomEntries(100_000, 42);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var stopwatch = Stopwatch.StartNew();
        entries.Sort(EntrySorter.Create(new SortSpec(SortColumn.Name)));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Sorting took {stopwatch.Elapsed}");
        var comparer = EntrySorter.Create(new SortSpec(SortColumn.Name));
        for (var i = 1; i < entries.Count; i++)
            Assert.True(comparer.Compare(entries[i - 1], entries[i]) <= 0);
    }
}
