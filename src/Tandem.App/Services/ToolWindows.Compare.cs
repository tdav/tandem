using Tandem.App.ViewModels.Tools;
using Tandem.App.Views.Tools;

namespace Tandem.App.Services;

public sealed partial class ToolWindows
{
    partial void AddCompare() => Register<FileCompareViewModel>(model => new FileCompareWindow(model));
}
