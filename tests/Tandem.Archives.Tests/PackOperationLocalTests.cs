using System.Diagnostics;
using System.Runtime.Versioning;
using Tandem.Core.Operations;
using Tandem.Core.Tests.Operations;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;

namespace Tandem.Archives.Tests;

/// <summary>Packing files of the real disk (a temporary folder), checked by the system's own tools.</summary>
[SupportedOSPlatform("macos")]
public sealed class PackOperationLocalTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempFileStore _temp;
    private readonly ProviderRegistry _providers = new();

    public PackOperationLocalTests()
    {
        _temp = new TempFileStore(_dir.Combine("store"));
        _providers.Register(new LocalFileSystemProvider(new UnrestrictedAccessBroker()));
    }

    public void Dispose()
    {
        _temp.Dispose();
        _dir.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (int ExitCode, string Output) Run(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["LC_ALL"] = "en_US.UTF-8"; // or tar escapes names that are not ASCII
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var errors = process.StandardError.ReadToEndAsync(Ct);
        process.WaitForExit();
        return (process.ExitCode, output.Result + errors.Result);
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.TarGz)]
    public async Task System_tools_accept_packed_zip_and_tar_gz(ArchiveFormat format)
    {
        var source = _dir.Combine("src");
        Directory.CreateDirectory(Path.Combine(source, "папка", "пустая"));
        await File.WriteAllTextAsync(Path.Combine(source, "папка", "файл.txt"), "содержимое", Ct);
        await File.WriteAllTextAsync(Path.Combine(source, "big.txt"),
            string.Concat(Enumerable.Range(0, 20_000).Select(i => $"line {i}\n")), Ct);
        var archive = _dir.Combine("packed." + ArchiveFormats.DefaultExtension(format));
        var op = new PackOperation(_providers,
            [VfsPath.Local(Path.Combine(source, "папка")), VfsPath.Local(Path.Combine(source, "big.txt"))],
            VfsPath.Local(archive), format, new ScriptedInteraction(), _temp);

        var result = await op.RunAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(OperationState.Completed, result.State);
        var (code, output) = format == ArchiveFormat.Zip
            ? Run("/usr/bin/unzip", "-t", archive)
            : Run("/usr/bin/tar", "-tzf", archive);
        Assert.True(code == 0, output);
        if (format == ArchiveFormat.TarGz)
        {
            var names = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(n => n.TrimEnd('/')).ToList();
            Assert.Equal(["big.txt", "папка", "папка/пустая", "папка/файл.txt"],
                names.Select(n => n.Normalize()).Order(StringComparer.Ordinal));
        }
        Assert.Empty(Directory.EnumerateFiles(_dir.Path, "*" + SafeReplace.PartSuffix, SearchOption.AllDirectories));
    }
}
