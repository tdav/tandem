using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.Core.Tests.Vfs;

public class ProviderRegistryTests
{
    [Fact]
    public void Get_and_For_find_registered_provider()
    {
        var registry = new ProviderRegistry();
        var mem = new InMemoryFileSystem();
        var other = new InMemoryFileSystem("other");

        registry.Register(mem);
        registry.Register(other);

        Assert.Same(mem, registry.Get("mem"));
        Assert.Same(other, registry.For(new VfsPath("other", "/a")));
    }

    [Fact]
    public void Get_unknown_throws() =>
        Assert.Throws<KeyNotFoundException>(() => new ProviderRegistry().Get("nope"));

    [Fact]
    public void Register_duplicate_id_throws()
    {
        var registry = new ProviderRegistry();
        registry.Register(new InMemoryFileSystem());

        Assert.Throws<InvalidOperationException>(() => registry.Register(new InMemoryFileSystem()));
    }
}
