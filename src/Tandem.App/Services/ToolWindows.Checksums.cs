using Tandem.App.ViewModels.Tools;
using Tandem.App.Views.Tools;

namespace Tandem.App.Services;

public sealed partial class ToolWindows
{
    partial void AddChecksums()
    {
        Register<ChecksumCreateViewModel>(model => new ChecksumCreateDialog(model));
        Register<ChecksumResultsViewModel>(model => new ChecksumResultsWindow(model));
    }
}
