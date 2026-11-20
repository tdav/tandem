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
