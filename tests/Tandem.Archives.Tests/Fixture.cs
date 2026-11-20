namespace Tandem.Archives.Tests;

/// <summary>The archive fixtures copied next to the tests: the generated ones from <c>Fixtures/</c> (see
/// <c>Fixtures/README.md</c>), and the external ones from <c>tests/fixtures/external/</c> when someone has put them
/// there. A test that asks for a missing external fixture is skipped, its reason naming the file.</summary>
internal static class Fixture
{
    /// <summary>Fixtures that cannot be made on a Mac and are not in the repository.</summary>
    public static readonly IReadOnlySet<string> ExternalNames =
        new HashSet<string>(StringComparer.Ordinal) { "sample.rar", "solid.rar", "encrypted.7z" };

    /// <summary>The password of <c>encrypted.7z</c> (see <c>tests/fixtures/external/README.md</c>).</summary>
    public const string External7zPassword = "testpassword";

    private static readonly string Root = System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static byte[] Bytes(string name) => File.ReadAllBytes(Path(name));

    public static string Path(string name)
    {
        if (!ExternalNames.Contains(name))
        {
            var path = System.IO.Path.Combine(Root, name);
            return File.Exists(path) ? path : throw new FileNotFoundException($"No fixture '{name}'.", path);
        }
        var external = System.IO.Path.Combine(Root, "external", name);
        if (!File.Exists(external))
            Assert.Skip($"External fixture '{name}' is absent: put it in tests/fixtures/external " +
                        "(see the README there).");
        return external;
    }
}
