using Avalonia.Headless.XUnit;

namespace Tandem.App.Tests;

public class SmokeTests
{
    [AvaloniaFact]
    public void MainWindow_opens()
    {
        var w = new MainWindow();
        w.Show();
        Assert.Equal("Tandem", w.Title);
    }
}
