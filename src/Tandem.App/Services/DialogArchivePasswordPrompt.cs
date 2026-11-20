using Avalonia.Threading;
using Tandem.Archives;
using Tandem.Core.Vfs;

namespace Tandem.App.Services;

/// <summary>Asks for an archive's password with <see cref="IDialogService.AskPasswordAsync"/>, on the UI thread
/// whatever thread the archive code asks from. The password goes back to the archive code only: it is not logged or
/// kept here.</summary>
public sealed class DialogArchivePasswordPrompt(IDialogService dialogs) : IArchivePasswordPrompt
{
    private readonly IDialogService _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

    public Task<string?> AskAsync(VfsPath archive, bool previousWasWrong, CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(() => _dialogs.AskPasswordAsync(archive.Name, previousWasWrong, ct));
}
