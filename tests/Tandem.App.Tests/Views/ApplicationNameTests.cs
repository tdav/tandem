using Avalonia;
using Avalonia.Headless.XUnit;

namespace Tandem.App.Tests.Views;

/// <summary>The application's name: the title of the macOS application menu (Avalonia's default is "Avalonia
/// Application").</summary>
public class ApplicationNameTests
{
    [AvaloniaFact]
    public void The_application_is_named_Tandem() => Assert.Equal("Tandem", Application.Current!.Name);
}
