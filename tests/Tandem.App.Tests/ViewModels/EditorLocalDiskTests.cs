using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia.Headless.XUnit;
using Tandem.App.ViewModels.Editing;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;
using Tandem.Platform.Mac;

namespace Tandem.App.Tests.ViewModels;

/// <summary>The editor saving on the real local disk, in a temporary folder: the file keeps what makes it the same
/// file besides its text.</summary>
[SupportedOSPlatform("macos")]
public sealed class EditorLocalDiskTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "tandem-editor-tests", Guid.NewGuid().ToString("N"));

    public EditorLocalDiskTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static string Run(string tool, params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(tool, arguments) { RedirectStandardOutput = true })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output.Trim();
    }

    [AvaloniaFact]
    public async Task Save_keeps_finder_tags_extended_attributes_and_mode()
    {
        var file = Path.Combine(_folder, "notes.txt");
        await File.WriteAllTextAsync(file, "line one\r\n", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(file, (UnixFileMode)0b111_101_101); // 0o755
        var broker = new UnrestrictedAccessBroker();
        var metadata = new MacFileMetadataService(broker);
        metadata.SetTags(file, ["Red", "Работа"]);
        Run("/usr/bin/xattr", "-w", "user.tandem.test", "kept", file);
        var providers = new ProviderRegistry();
        providers.Register(new LocalFileSystemProvider(broker, new MacExclusiveRename()));
        var editor = await EditorViewModel.OpenAsync(providers, VfsPath.Local(file), CancellationToken.None,
            new MacNativeCopy(broker));

        editor.Document.Insert(0, "edited ");
        Assert.True(await editor.SaveAsync(), editor.Error);

        Assert.Equal("edited line one\r\n", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        Assert.Equal(["Red", "Работа"], metadata.GetTags(file).Order(StringComparer.Ordinal));
        Assert.Equal("kept", Run("/usr/bin/xattr", "-p", "user.tandem.test", file));
        Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(file));
        Assert.Equal(["notes.txt"], Directory.EnumerateFileSystemEntries(_folder).Select(Path.GetFileName));
    }

    [AvaloniaFact]
    public async Task Save_through_a_relative_symlink_edits_the_target_and_keeps_the_link()
    {
        var target = Path.Combine(_folder, "real.txt");
        await File.WriteAllTextAsync(target, "real", TestContext.Current.CancellationToken);
        var link = Path.Combine(_folder, "link.txt");
        File.CreateSymbolicLink(link, "real.txt");
        var broker = new UnrestrictedAccessBroker();
        var providers = new ProviderRegistry();
        providers.Register(new LocalFileSystemProvider(broker, new MacExclusiveRename()));
        var editor = await EditorViewModel.OpenAsync(providers, VfsPath.Local(link), CancellationToken.None,
            new MacNativeCopy(broker));

        editor.Document.Insert(0, "edited ");
        Assert.True(await editor.SaveAsync(), editor.Error);

        Assert.Equal("edited real", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        Assert.Equal("real.txt", new FileInfo(link).LinkTarget);
    }
}
