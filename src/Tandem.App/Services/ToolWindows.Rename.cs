using Tandem.App.ViewModels.Tools;
using Tandem.App.Views.Tools;

namespace Tandem.App.Services;

public sealed partial class ToolWindows
{
    partial void AddRename() =>
        Register<MultiRenameViewModel>(model => new MultiRenameWindow { DataContext = model });
}
