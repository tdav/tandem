using Tandem.Core.Platform;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;

namespace Tandem.App.Services;

/// <summary>
/// Carries the quarantine mark (<see cref="IQuarantine"/>) of an archive that came from outside onto what leaves it,
/// as the system's own unarchiver does, so Gatekeeper still checks an app or script taken out of a downloaded
/// archive. The archive is the outermost file the items lie in (through archives inside archives) on the local disk;
/// when it has a mark, every file and folder written out of it to the local disk gets the same value, and so does a
/// temporary copy of a file inside it. Best effort: a mark that cannot be read or written is logged and never fails
/// the copy. Items that are on the local disk already, or that lie in no file (search results), are left alone.
/// </summary>
public sealed class QuarantineCarrier(ProviderRegistry providers, IQuarantine quarantine, ErrorLog log)
{
    private const int MaxDepth = 64;

    private readonly ProviderRegistry _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    private readonly IQuarantine _quarantine = quarantine ?? throw new ArgumentNullException(nameof(quarantine));
    private readonly ErrorLog _log = log ?? throw new ArgumentNullException(nameof(log));

    /// <summary>The file on the local disk that <paramref name="path"/> lies in, through any number of providers
    /// hosted in files; null for a path on the local disk itself or one that lies in no file. Does no I/O.</summary>
    public VfsPath? OutermostFile(VfsPath path)
    {
        var current = path;
        for (var depth = 0; current.ProviderId != VfsPath.LocalProviderId; depth++)
        {
            if (depth == MaxDepth || !_providers.TryGet(current.ProviderId, out _) ||
                _providers.HostLocationOf(current) is not { FocusName: { } name } host)
                return null;
            current = host.Directory.Combine(name);
        }
        return current == path ? null : current;
    }

    /// <summary>What a copy or move of <paramref name="sources"/> hears for each item it wrote
    /// (<see cref="Core.Operations.CopyOperation.ItemWritten"/>): marks the items written to the local disk with the
    /// mark of the file the sources lie in. The mark is read once, at the first item, on the operation's flow. Null
    /// when the sources lie in no file (nothing to carry).</summary>
    public Action<VfsPath>? ForSources(IReadOnlyList<VfsPath> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0 || OutermostFile(sources[0]) is not { } file) return null;
        var read = false;
        byte[]? value = null;
        return written =>
        {
            if (!read)
            {
                read = true;
                value = Read(file.ToOsPath());
            }
            if (value is not null && written.ProviderId == VfsPath.LocalProviderId) Write(written.ToOsPath(), value);
        };
    }

    /// <summary>Gives <paramref name="localCopy"/>, a copy of <paramref name="source"/>, the mark of the file the
    /// source lies in, if it has one. Does I/O: call it off the UI thread.</summary>
    public void MarkCopy(VfsPath source, string localCopy)
    {
        if (OutermostFile(source) is { } file && Read(file.ToOsPath()) is { } value) Write(localCopy, value);
    }

    private byte[]? Read(string path)
    {
        try
        {
            return _quarantine.Get(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Write(e, $"quarantine: reading the mark of {path}");
            return null;
        }
    }

    private void Write(string path, byte[] value)
    {
        try
        {
            _quarantine.Set(path, value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Write(e, $"quarantine: marking {path}");
        }
    }
}
