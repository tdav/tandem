using System.Diagnostics;
using Tandem.Core.Vfs;

namespace Tandem.Core.Tools;

public sealed record FolderSize(long Bytes, long Files, long Folders, int Unreadable);

public static class FolderSizeCalculator
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    public static async Task<FolderSize> ComputeAsync(IFileSystemProvider provider, VfsPath folder,
        IProgress<FolderSize>? progress, CancellationToken ct)
    {
        var stack = new Stack<VfsPath>();
        stack.Push(folder);
        long bytes = 0, files = 0, folders = 0;
        var unreadable = 0;
        var lastReport = Stopwatch.GetTimestamp();
        while (stack.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await foreach (var entry in provider.EnumerateAsync(directory, ct).ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.Kind == EntryKind.Directory)
                    {
                        folders++;
                        stack.Push(entry.Path);
                    }
                    else
                    {
                        files++;
                        bytes += entry.Size;
                    }
                    if (progress is not null && Stopwatch.GetElapsedTime(lastReport) >= ProgressInterval)
                    {
                        progress.Report(new(bytes, files, folders, unreadable));
                        lastReport = Stopwatch.GetTimestamp();
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                unreadable++;
            }
        }
        ct.ThrowIfCancellationRequested();
        return new(bytes, files, folders, unreadable);
    }
}
