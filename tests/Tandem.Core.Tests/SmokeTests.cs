using System.Reflection;
using System.Runtime.Versioning;

namespace Tandem.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void Core_targets_net11() =>
        Assert.StartsWith(".NETCoreApp,Version=v11", typeof(Tandem.Core.AssemblyMarker).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName);
}
