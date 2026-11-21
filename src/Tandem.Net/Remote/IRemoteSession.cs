using Tandem.Core.Vfs;

namespace Tandem.Net.Remote;

[Flags]
public enum RemoteFeatures
{
    None = 0,

    /// <summary>Link targets can be read (<see cref="RemoteEntry.LinkTarget"/>) and links created. Without it a link is
    /// still reported as a link (<see cref="EntryKind.Symlink"/>), only its target is unknown.</summary>
    Symlinks = 1,

    /// <summary>Unix permissions are reported and can be set.</summary>
    Modes = 2,

    /// <summary>The modified time of a file can be set.</summary>
    SetModified = 4,

    /// <summary>One connection carries any number of calls and streams at the same time.</summary>
    Multiplexed = 8,
}

/// <summary>One entry as the server reports it; <paramref name="Name"/> is a single name, not a path.</summary>
public sealed record RemoteEntry(string Name, EntryKind Kind, long Size, DateTimeOffset Modified, UnixFileMode? Mode,
    string? LinkTarget = null, bool LinkPointsToDirectory = false);

/// <summary>
/// One connection to a server. Paths are absolute, separated by '/'. Errors are core exceptions:
/// <see cref="VfsNotFoundException"/>, <see cref="VfsAccessDeniedException"/>, <see cref="VfsAlreadyExistsException"/>,
/// <see cref="VfsConnectionLostException"/> (the connection), <see cref="NetAuthenticationException"/>,
/// <see cref="NetTrustException"/>; cancellation is <see cref="OperationCanceledException"/>.
/// Without <see cref="RemoteFeatures.Multiplexed"/> the connection runs one call at a time, and an open stream
/// occupies it until the stream is closed.
/// <para><b>Time limits.</b> No call waits forever: connecting (with logging in, not counting the user's answers) is
/// limited to 15 s, and every read or write to 30 s without any data moving; past that, the call throws
/// <see cref="VfsConnectionLostException"/> and the connection counts as closed. Closing a write stream takes no
/// token (<see cref="IAsyncDisposable.DisposeAsync"/>), so the session itself must limit the wait for the server's
/// final reply to the upload in the same way.</para>
/// <para><b>Links.</b> An entry the server marks as a link is always reported as <see cref="EntryKind.Symlink"/>,
/// whatever <see cref="Features"/> say: callers rely on that not to enter links (a recursive delete must not
/// delete what a link points to). <see cref="RemoteFeatures.Symlinks"/> only governs reading targets and creating
/// links. A session that cannot tell reports what the server says.</para>
/// <para><b>Retries.</b> The caller makes a call once more on a fresh connection when the reused one turns out to be
/// closed. If the server had carried out a call that changes something (delete, rename, create) and only its reply was
/// lost, the repeated call may report <see cref="VfsNotFoundException"/> or <see cref="VfsAlreadyExistsException"/>
/// for an action that succeeded. No data is lost that way; a session need not guard against it.</para>
/// </summary>
public interface IRemoteSession : IAsyncDisposable
{
    /// <summary>What the server offers; known after <see cref="ConnectAsync"/>.</summary>
    RemoteFeatures Features { get; }

    /// <summary>False once the connection is known to be closed. True does not promise the server has not closed it
    /// meanwhile: the next call finds out.</summary>
    bool IsConnected { get; }

    /// <summary>Connects and logs in; questions about credentials and trust are asked here.</summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>The direct children, without "." and "..". A missing folder is
    /// <see cref="VfsNotFoundException"/>. A link is <see cref="EntryKind.Symlink"/> (see the class notes).</summary>
    IAsyncEnumerable<RemoteEntry> ListAsync(string directory, CancellationToken ct);

    /// <summary>The entry itself (a link, not its target; <see cref="EntryKind.Symlink"/> whatever the features). Null only when the server says for certain that it does
    /// not exist: an answer that may mean something else (no access, a failed or unclear reply) is an exception,
    /// never null — callers rely on null to decide that a name is free.</summary>
    Task<RemoteEntry?> StatAsync(string path, CancellationToken ct);

    /// <summary>A stream of the file's bytes. Closing it before its end must either leave the connection usable for
    /// the next call (the transfer properly ended) or make <see cref="IsConnected"/> false. The pool of
    /// <see cref="RemoteProvider"/> does not rely on the first: a connection without
    /// <see cref="RemoteFeatures.Multiplexed"/> whose read stream was closed early is closed, not lent again.</summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken ct);

    /// <summary>Creates the file or truncates it. Closing the stream finishes the upload and throws its error (the
    /// server's answer to the transfer, a full disk).</summary>
    Task<Stream> OpenWriteAsync(string path, CancellationToken ct);

    /// <summary>One level: the parent must exist.</summary>
    Task CreateDirectoryAsync(string path, CancellationToken ct);

    /// <summary>A file or a link (the link itself).</summary>
    Task DeleteFileAsync(string path, CancellationToken ct);

    /// <summary>An empty folder only.</summary>
    Task DeleteDirectoryAsync(string path, CancellationToken ct);

    /// <summary>May replace an existing <paramref name="to"/>: the caller checks first.</summary>
    Task RenameAsync(string from, string to, CancellationToken ct);

    Task CreateSymlinkAsync(string path, string target, CancellationToken ct);

    /// <summary>Does nothing without <see cref="RemoteFeatures.SetModified"/>.</summary>
    Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken ct);

    Task SetModeAsync(string path, UnixFileMode mode, CancellationToken ct);
}

/// <summary>Makes connections for a profile (one protocol).</summary>
public interface IRemoteSessionFactory
{
    IRemoteSession Create(ConnectionProfile profile, ICredentialSource credentials, ITrustDecisions trust);
}
