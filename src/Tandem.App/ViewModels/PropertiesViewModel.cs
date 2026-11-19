using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Platform;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels;

/// <summary>
/// The properties dialog (Cmd+I) of one item: name, size, date, owner, the nine permission flags with their octal
/// form, and the tags. Only what the user changed is applied.
/// </summary>
/// <remarks>
/// Shows the entry as the provider reported it; a symbolic link is shown as the link itself and its target is never
/// looked up. The link's mode is not editable: changing it on macOS would change the target's. Tags and owner come
/// from <see cref="IFileMetadataService"/> and only for the local provider; without it they show as unavailable.
/// Call <see cref="LoadAsync"/> before showing it: it reads the owner and tags off the UI thread. UI thread only.
/// </remarks>
public sealed partial class PropertiesViewModel : ObservableObject
{
    private const int PermissionBits = 0x1FF; // 0777

    private readonly FileEntry _entry;
    private readonly IFileSystemProvider _provider;
    private readonly IFileMetadataService? _metadata;
    private UnixFileMode _appliedMode;
    private int _bits; // the nine rwx bits the flags show
    private IReadOnlyList<string> _appliedTags = [];
    private string _octal;
    private string _tagsText = "";
    private bool _canEditTags;

    public PropertiesViewModel(FileEntry entry, IFileSystemProvider provider, IFileMetadataService? metadata)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(provider);
        _entry = entry;
        _provider = provider;
        _metadata = entry.Path.ProviderId == VfsPath.LocalProviderId ? metadata : null;

        Name = entry.Name;
        SizeText = entry.Kind == EntryKind.Directory ? Loc.Get("Panel_Folder") : SizeFormatter.Format(entry.Size);
        ModifiedText = entry.Modified.ToLocalTime().ToString("g", Loc.Culture);
        LinkTarget = entry.Kind == EntryKind.Symlink ? entry.LinkTarget : null;
        CanEditMode = entry.Mode is not null && entry.Kind != EntryKind.Symlink &&
                      provider.Capabilities.HasFlag(ProviderCapabilities.UnixPermissions);
        _appliedMode = entry.Mode ?? 0;
        _bits = (int)_appliedMode & PermissionBits;
        _octal = entry.Mode is null ? "" : FormatOctal(_bits);
        OwnerText = Loc.Get("Properties_Unavailable");
        if (_metadata is null) _tagsText = Loc.Get("Properties_Unavailable");
    }

    public string Name { get; }

    public string SizeText { get; }

    public string ModifiedText { get; }

    /// <summary>Where a symbolic link points, as stored in the link; null for anything else.</summary>
    public string? LinkTarget { get; }

    /// <summary>"user:group", or "unavailable"; read only.</summary>
    [ObservableProperty]
    public partial string OwnerText { get; private set; }

    /// <summary>Whether the permissions can be changed: the provider supports Unix permissions, the entry has a mode,
    /// and it is not a symbolic link.</summary>
    public bool CanEditMode { get; }

    /// <summary>Whether the tags can be changed: a local item whose tags could be read.</summary>
    public bool CanEditTags
    {
        get => _canEditTags;
        private set => SetProperty(ref _canEditTags, value);
    }

    /// <summary>Why the last <see cref="ApplyAsync"/> failed, in the user's language; null otherwise.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    public bool OwnerRead { get => Bit(0x100); set => SetBit(0x100, value); }
    public bool OwnerWrite { get => Bit(0x080); set => SetBit(0x080, value); }
    public bool OwnerExecute { get => Bit(0x040); set => SetBit(0x040, value); }
    public bool GroupRead { get => Bit(0x020); set => SetBit(0x020, value); }
    public bool GroupWrite { get => Bit(0x010); set => SetBit(0x010, value); }
    public bool GroupExecute { get => Bit(0x008); set => SetBit(0x008, value); }
    public bool OtherRead { get => Bit(0x004); set => SetBit(0x004, value); }
    public bool OtherWrite { get => Bit(0x002); set => SetBit(0x002, value); }
    public bool OtherExecute { get => Bit(0x001); set => SetBit(0x001, value); }

    /// <summary>The permissions as three octal digits ("755"); empty when the entry has no mode. Typing three octal
    /// digits sets the flags; anything else leaves them as they are (and <see cref="ApplyAsync"/> refuses it).</summary>
    public string Octal
    {
        get => _octal;
        set
        {
            value ??= "";
            if (!SetProperty(ref _octal, value)) return;
            if (ParseOctal(value) is { } bits && bits != _bits)
            {
                _bits = bits;
                RaiseFlagsChanged();
            }
        }
    }

    /// <summary>The tags separated by commas.</summary>
    public string TagsText
    {
        get => _tagsText;
        set => SetProperty(ref _tagsText, value ?? "");
    }

    /// <summary>Reads the owner and the tags off the UI thread. A failure leaves them unavailable.</summary>
    public async Task LoadAsync()
    {
        if (_metadata is not { } metadata) return;
        var path = _entry.Path.Path;
        try
        {
            var owner = await Task.Run(() => metadata.GetOwner(path));
            OwnerText = $"{owner.User}:{owner.Group}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // stays "unavailable"
        }
        try
        {
            var tags = await Task.Run(() => metadata.GetTags(path));
            _appliedTags = tags.ToArray();
            TagsText = string.Join(", ", tags);
            CanEditTags = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TagsText = Loc.Get("Properties_Unavailable");
        }
    }

    /// <summary>Applies the permissions if they changed (special bits beyond rwx kept as they were) and the tags if
    /// they changed. False with <see cref="Error"/> set when the octal text is invalid or a change failed; what was
    /// applied before the failure stays applied.</summary>
    public async Task<bool> ApplyAsync()
    {
        Error = null;
        if (CanEditMode && ParseOctal(_octal) is null)
        {
            Error = Loc.Get("Properties_InvalidOctal");
            return false;
        }
        try
        {
            var mode = (UnixFileMode)(((int)_appliedMode & ~PermissionBits) | _bits);
            if (CanEditMode && mode != _appliedMode)
            {
                var (provider, path) = (_provider, _entry.Path);
                await Task.Run(() => provider.SetModeAsync(path, mode, CancellationToken.None));
                _appliedMode = mode;
            }

            if (CanEditTags && _metadata is { } metadata)
            {
                var tags = ParseTags(_tagsText);
                if (!tags.SequenceEqual(_appliedTags, StringComparer.Ordinal))
                {
                    var path = _entry.Path.Path;
                    await Task.Run(() => metadata.SetTags(path, tags));
                    _appliedTags = tags;
                }
            }
            return true;
        }
        catch (Exception e)
        {
            Error = Loc.Format("Properties_ApplyFailed", OperationTexts.ForFailure(e));
            return false;
        }
    }

    /// <summary>The tags of a comma-separated text: trimmed, empty ones dropped, and duplicates (ignoring case)
    /// removed, keeping the first spelling.</summary>
    public static IReadOnlyList<string> ParseTags(string text)
    {
        var tags = new List<string>();
        foreach (var part in (text ?? "").Split(','))
        {
            var tag = part.Trim();
            if (tag.Length > 0 && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
        }
        return tags;
    }

    [GeneratedRegex("^[0-7]{3}$")]
    private static partial Regex OctalPattern();

    private static int? ParseOctal(string text) => OctalPattern().IsMatch(text) ? Convert.ToInt32(text, 8) : null;

    private static string FormatOctal(int bits) => Convert.ToString(bits, 8).PadLeft(3, '0');

    private bool Bit(int bit) => (_bits & bit) != 0;

    private void SetBit(int bit, bool on)
    {
        var bits = on ? _bits | bit : _bits & ~bit;
        if (bits == _bits) return;
        _bits = bits;
        RaiseFlagsChanged();
        SetProperty(ref _octal, FormatOctal(bits), nameof(Octal));
    }

    private void RaiseFlagsChanged()
    {
        foreach (var name in new[]
                 {
                     nameof(OwnerRead), nameof(OwnerWrite), nameof(OwnerExecute), nameof(GroupRead),
                     nameof(GroupWrite), nameof(GroupExecute), nameof(OtherRead), nameof(OtherWrite),
                     nameof(OtherExecute),
                 })
            OnPropertyChanged(name);
    }
}
