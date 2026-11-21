using Tandem.App.ViewModels.Tools;
using Tandem.App.Views.Tools;

namespace Tandem.App.Services;

public sealed partial class ToolWindows
{
    partial void AddSync() => Register<SyncViewModel>(model => new SyncWindow(model));
}
