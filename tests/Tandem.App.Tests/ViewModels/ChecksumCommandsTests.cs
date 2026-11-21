using Avalonia.Headless.XUnit;
using Tandem.App.Commands;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels.Tools;
using Tandem.Core.Tools.Checksums;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.ViewModels;

[Collection(LanguageCollection.Name)]
public sealed class ChecksumCommandsTests : LanguageTestBase
{
    [AvaloniaFact]
    public async Task Create_focuses_the_checksum_and_verify_reports_matches()
    {
        await using var app = await TestApp.StartAsync(show: false, scriptedToolWindows: true);
        await app.Left.NavigateToAsync(VfsPath.Local(TestApp.Home), "notes.txt");
        Assert.False(app.Main.Commands.CanExecute(CommandIds.ToolsChecksumVerify));
        app.ScriptedToolWindows.OnDialog = model =>
        {
            var checksum = Assert.IsType<ChecksumCreateViewModel>(model);
            Assert.Equal(ChecksumAlgorithm.Sha256, checksum.Algorithm);
            checksum.Algorithm = ChecksumAlgorithm.Md5;
            Assert.EndsWith(".md5", checksum.Name);
            ChecksumCreateRequest? answer = null;
            checksum.CloseRequested += (_, value) => answer = value;
            checksum.AcceptCommand.Execute(null);
            return answer;
        };

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ToolsChecksumCreate));
        Assert.Equal("notes.txt.md5", app.Left.CursorRow!.Name);
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ToolsChecksumVerify));
        var result = Assert.IsType<ChecksumResultsViewModel>(app.ScriptedToolWindows.Shown.Last());
        Assert.Equal(VerifyStatus.Ok, Assert.Single(result.Rows).Status);
    }
}
