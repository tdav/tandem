using Tandem.Core.Vfs;

namespace Tandem.Archives;

/// <summary>Asks the user for an archive's password.</summary>
public interface IArchivePasswordPrompt
{
    /// <summary>The password for <paramref name="archive"/>, or null if the user declined. May be called from a
    /// background thread.</summary>
    /// <param name="archive">The archive file, in its host provider.</param>
    /// <param name="previousWasWrong">The password tried last for this archive did not fit.</param>
    Task<string?> AskAsync(VfsPath archive, bool previousWasWrong, CancellationToken ct);
}

/// <summary>Lets a flow read archives without being asked for passwords: inside <see cref="Suppress"/> (and in the
/// work it starts), a read that needs a password the archive does not have fails with
/// <see cref="ArchivePasswordRequiredException"/> instead of asking. For a caller that reads on its own (a preview
/// following the cursor) and has already heard the user decline.</summary>
public static class ArchivePasswordQuestions
{
    private static readonly AsyncLocal<bool> Quiet = new();

    /// <summary>Whether the calling flow is inside <see cref="Suppress"/>.</summary>
    public static bool AreSuppressed => Quiet.Value;

    /// <summary>No questions in the calling flow until the result is disposed.</summary>
    public static IDisposable Suppress()
    {
        var before = Quiet.Value;
        Quiet.Value = true;
        return new Restore(before);
    }

    private sealed class Restore(bool before) : IDisposable
    {
        public void Dispose() => Quiet.Value = before;
    }
}
