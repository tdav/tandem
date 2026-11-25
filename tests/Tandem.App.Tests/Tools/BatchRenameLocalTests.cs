using System.Runtime.Versioning;
using Tandem.Core.Operations;
using Tandem.Core.Tools.Rename;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;
using Tandem.Platform.Mac;

namespace Tandem.App.Tests.Tools;

[SupportedOSPlatform("macos")]
public sealed class BatchRenameLocalTests
{
    [Fact]
    public async Task Thousand_files_can_be_renamed_using_the_counter_and_restored_byte_for_byte()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses the macOS platform layer.");
        const int count = 1000;
        const string suffix = ".txt";
        var ct = TestContext.Current.CancellationToken;
        var folder = Directory.CreateTempSubdirectory("tandem-rename-");
        try
        {
            var originals = Enumerable.Range(1, count).ToDictionary(number => $"f{number:D4}{suffix}", BitConverter.GetBytes);
            foreach (var (name, bytes) in originals)
                await File.WriteAllBytesAsync(Path.Combine(folder.FullName, name), bytes, ct);

            var providers = new ProviderRegistry();
            var local = new LocalFileSystemProvider(new UnrestrictedAccessBroker(), new MacExclusiveRename());
            providers.Register(local);
            var root = VfsPath.Local(folder.FullName);
            var entries = new List<FileEntry>();
            await foreach (var entry in local.EnumerateAsync(root, ct)) entries.Add(entry);
            entries.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            var namesByFolder = new Dictionary<VfsPath, IReadOnlySet<string>> { [root] = originals.Keys.ToHashSet() };
            var plan = RenamePlanner.Build(entries, new RenameRule("[N]_[C]", CounterDigits: 4), namesByFolder, TimeZoneInfo.Utc);
            Assert.True(plan.CanRun);
            Assert.Equal(count, plan.ChangedCount);

            var interaction = new UnexpectedInteraction();
            var operation = new BatchRenameOperation(providers,
                plan.Items.Select(item => new RenameStep(item.Entry.Path, root.Combine(item.NewName))).ToArray(), interaction);
            Assert.Empty((await operation.RunAsync()).Skipped);
            Assert.Equal(count, operation.Completed.Count);
            foreach (var (name, bytes) in originals)
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                var renamed = stem + "_" + stem[1..] + suffix;
                Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(folder.FullName, renamed), ct));
            }

            var journal = operation.Completed;
            var undo = new BatchRenameOperation(providers, journal.Select(item => new RenameStep(item.To, item.From)).ToArray(),
                interaction, journal);
            Assert.Empty((await undo.RunAsync()).Skipped);
            Assert.Equal(originals.Keys.Order(StringComparer.Ordinal),
                Directory.EnumerateFileSystemEntries(folder.FullName).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            foreach (var (name, bytes) in originals)
                Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(folder.FullName, name), ct));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private sealed class UnexpectedInteraction : IOperationInteraction
    {
        public Task<ConflictDecision> ResolveConflictAsync(FileEntry source, FileEntry existing, CancellationToken ct) =>
            throw new InvalidOperationException($"Unexpected conflict: {source.Path}.");
        public Task<ErrorAction> ResolveErrorAsync(VfsPath item, Exception error, CancellationToken ct) =>
            throw new InvalidOperationException($"Unexpected error: {item}.", error);
    }
}
