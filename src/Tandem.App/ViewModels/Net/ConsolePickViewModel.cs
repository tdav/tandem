using Tandem.App.Services;
using Tandem.Net;

namespace Tandem.App.ViewModels.Net;

public sealed class ConsolePickViewModel(IReadOnlyList<ConnectionProfile> profiles) : IDialogModel<Guid?>
{
    public IReadOnlyList<ConnectionProfile> Profiles { get; } = profiles;
    public event EventHandler<Guid?>? CloseRequested;
    public void Choose(ConnectionProfile? profile) => CloseRequested?.Invoke(this, profile?.Id);
}
