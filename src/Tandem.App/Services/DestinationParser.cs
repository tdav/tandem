using Tandem.App.Resources;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;

namespace Tandem.App.Services;

/// <summary>What exists at a path, as far as choosing a destination cares.</summary>
public enum DestinationKind { Missing, Directory, Other }

/// <summary>Where a copy or move goes: into <paramref name="Directory"/>, under <paramref name="NewName"/> (null:
/// the items keep their names). <paramref name="CreateDirectory"/>: the folder does not exist yet and the user asked
/// for it (a path ending in "/").</summary>
public sealed record DestinationTarget(VfsPath Directory, string? NewName, bool CreateDirectory);

/// <summary>Either a <see cref="Target"/> or a localized <see cref="Error"/> to show in the dialog.</summary>
public sealed record DestinationParseResult(DestinationTarget? Target, string? Error)
{
    public static DestinationParseResult Fail(string error) => new(null, error);
}

/// <summary>
/// Turns the text typed in the copy/move dialog into a destination folder and an optional new name. The text always
/// refers to the source (provider) of the destination: the passive panel for F5/F6, the panel dropped on for a drop.
/// <list type="bullet">
/// <item>Empty or only spaces: an error. Other spaces are kept: they are legal in names.</item>
/// <item>"~" and "~/…" mean the home folder, on the local disk only.</item>
/// <item>An absolute path is taken as it is; a relative one starts at the items' folder (the active panel's for F5/F6;
/// only when it is on the destination's source). "." and ".." are resolved; going above the root is an error.</item>
/// <item>Ending in "/" (or the root): that folder, created if it does not exist.</item>
/// <item>An existing folder: that folder, the items keep their names. With one item whose own name is the last
/// segment, the item goes into the parent under its own name instead (so "/dst/docs" for the folder "docs" merges
/// into /dst/docs, never makes /dst/docs/docs).</item>
/// <item>Otherwise the last segment is a new name and the parent must be an existing folder; a new name for several
/// items is an error.</item>
/// </list>
/// </summary>
public static class DestinationParser
{
    /// <param name="baseDirectory">Where a relative path starts: the folder of the items.</param>
    /// <param name="destinationDirectory">The proposed destination; its provider is the one the text refers to.</param>
    /// <param name="probe">What exists at a path in the destination's source; called off the UI thread is not
    /// required. Its failures become an error result.</param>
    public static async Task<DestinationParseResult> ParseAsync(string text, VfsPath baseDirectory,
        VfsPath destinationDirectory, int itemCount, string? singleName, string homeDirectory,
        Func<VfsPath, CancellationToken, Task<DestinationKind>> probe, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(probe);
        if (string.IsNullOrWhiteSpace(text)) return DestinationParseResult.Fail(Loc.Get("Destination_Empty"));

        var providerId = destinationDirectory.ProviderId;
        var local = providerId == VfsPath.LocalProviderId;
        var style = local ? LocalPath.Current : LocalPathStyle.Unix;
        var absolute = local ? LocalPath.ExpandTyped(text, homeDirectory, style)
            : text.StartsWith('/') ? text : null;

        string raw;
        if (absolute is not null)
        {
            raw = absolute;
        }
        else
        {
            if (baseDirectory.ProviderId != providerId)
                return DestinationParseResult.Fail(Loc.Get("Destination_RelativeOtherSource"));
            raw = baseDirectory.Path.TrimEnd('/') + "/" + LocalPath.RelativeToVfs(text, style);
        }

        VfsPath path;
        try
        {
            path = new VfsPath(providerId, raw);
        }
        catch (ArgumentException)
        {
            return DestinationParseResult.Fail(Loc.Format("Destination_Invalid", text));
        }

        try
        {
            var kind = await probe(path, ct).ConfigureAwait(false);
            if (LocalPath.EndsWithSeparator(text, style) || path.IsRoot)
            {
                return kind switch
                {
                    DestinationKind.Directory => new(new DestinationTarget(path, null, false), null),
                    DestinationKind.Missing => new(new DestinationTarget(path, null, true), null),
                    _ => DestinationParseResult.Fail(Loc.Format("Destination_NotAFolder", path.Display())),
                };
            }

            var name = path.Name;
            var parent = path.Parent!.Value;
            // Compared exactly (ordinal). Edge case on a case-insensitive volume: "/dst/DOCS" typed for the folder
            // "docs", with /dst/docs existing, is not taken as the item's own name but as the existing folder to
            // copy into, giving /dst/docs/docs. The default text uses the item's exact name, so only a retyped
            // name in other case reaches this.
            var ownName = itemCount == 1 && string.Equals(name, singleName, StringComparison.Ordinal);
            if (kind == DestinationKind.Directory && !ownName)
                return new(new DestinationTarget(path, null, false), null);

            if (itemCount > 1) return DestinationParseResult.Fail(Loc.Format("Destination_NewNameManyItems", name));
            if (await probe(parent, ct).ConfigureAwait(false) != DestinationKind.Directory)
                return DestinationParseResult.Fail(Loc.Format("Destination_FolderMissing", parent.Display()));
            return new(new DestinationTarget(parent, ownName ? null : name, false), null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return DestinationParseResult.Fail(Loc.Format("Destination_Unreadable", path.Display(), e.Message));
        }
    }

    /// <summary>A probe over a provider: what <see cref="IFileSystemProvider.GetEntryAsync"/> finds, links to
    /// folders counting as folders.</summary>
    public static Func<VfsPath, CancellationToken, Task<DestinationKind>> ProbeOf(ProviderRegistry providers) =>
        async (path, ct) =>
        {
            var entry = await providers.For(path).GetEntryAsync(path, ct).ConfigureAwait(false);
            return entry is null ? DestinationKind.Missing
                : entry.IsDirectoryLike ? DestinationKind.Directory
                : DestinationKind.Other;
        };
}
