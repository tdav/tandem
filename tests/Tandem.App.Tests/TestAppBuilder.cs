using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Tandem.App.Tests.TestAppBuilder))]

namespace Tandem.App.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
