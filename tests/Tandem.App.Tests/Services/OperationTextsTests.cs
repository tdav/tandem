using System.Runtime.Versioning;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Archives;
using Tandem.Core.Vfs;
using Tandem.Platform.Mac;

namespace Tandem.App.Tests.Services;

/// <summary>The words chosen for the errors the platform's file copy raises.</summary>
[Collection(LanguageCollection.Name)]
[SupportedOSPlatform("macos")]
public class OperationTextsTests : LanguageTestBase
{
    private static readonly VfsPath Item = VfsPath.Local("/dst/a.txt");

    [Theory]
    [InlineData(SystemErrors.ENOSPC, "DiskFull")]
    [InlineData(SystemErrors.EACCES, "AccessDenied")]
    [InlineData(SystemErrors.EPERM, "AccessDenied")]
    [InlineData(SystemErrors.ENOENT, "NotFound")]
    [InlineData(SystemErrors.EEXIST, "AlreadyExists")]
    [InlineData(5 /* EIO */, "IO")]
    public void Native_copy_errors_get_their_own_words(int errno, string kind)
    {
        var error = SystemErrors.FromErrno(errno);

        Assert.Equal(Loc.Format("OperationError_" + kind, Item.Path), OperationTexts.ForError(Item, error));
        Assert.Equal(Loc.Get("OperationFailed_" + kind), OperationTexts.ForFailure(error));
    }

    [Fact]
    public void Disk_full_and_access_denied_read_as_such_in_both_languages()
    {
        Assert.Equal("There is not enough space on the disk for /dst/a.txt.",
            OperationTexts.ForError(Item, SystemErrors.FromErrno(SystemErrors.ENOSPC)));
        Assert.Equal("Access to /dst/a.txt was denied.",
            OperationTexts.ForError(Item, SystemErrors.FromErrno(SystemErrors.EACCES)));
        Loc.SetLanguage("ru");
        Assert.Equal(Loc.Format("OperationError_DiskFull", Item.Path),
            OperationTexts.ForError(Item, SystemErrors.FromErrno(SystemErrors.ENOSPC)));
        Assert.NotEqual(Loc.Format("OperationError_IO", Item.Path),
            OperationTexts.ForError(Item, SystemErrors.FromErrno(SystemErrors.EACCES)));
    }

    [Fact]
    public void Archive_errors_have_their_own_words()
    {
        var password = new ArchivePasswordRequiredException(VfsPath.Local("/a.zip"), wrongPassword: true);
        var damaged = new ArchiveDamagedException("damaged");

        Assert.Equal("/dst/a.txt needs the archive's password.", OperationTexts.ForError(Item, password));
        Assert.Equal("/dst/a.txt is damaged or is not an archive.", OperationTexts.ForError(Item, damaged));
        Assert.Equal(Loc.Get("OperationFailed_PasswordRequired"), OperationTexts.ForFailure(password));
        Assert.Equal(Loc.Get("OperationFailed_ArchiveDamaged"), OperationTexts.ForFailure(damaged));
        Assert.NotEqual(Loc.Get("OperationFailed_IO"), OperationTexts.ForFailure(damaged));
        Loc.SetLanguage("ru");
        Assert.Equal("Для /dst/a.txt нужен пароль архива.", OperationTexts.ForError(Item, password));
        Assert.Equal("архив повреждён или не является архивом.", OperationTexts.ForFailure(damaged));
    }

    [Fact]
    public void Name_an_archive_cannot_store_has_its_own_words()
    {
        var error = new ArchiveEntryNameException("a:b.txt", "x");

        Assert.Equal("/dst/a.txt has a name an archive cannot store (it contains “:” or “\\”).",
            OperationTexts.ForError(Item, error));
        Assert.Equal(Loc.Get("OperationFailed_InvalidName"), OperationTexts.ForFailure(error));
        Assert.Equal("Skipped: an archive cannot store this name (it contains “:” or “\\”)",
            Loc.Enum(Tandem.Core.Operations.SkipReason.NameNotStorable));
        Loc.SetLanguage("ru");
        Assert.Equal("У /dst/a.txt имя, которое архив не может сохранить (в нём есть «:» или «\\»).",
            OperationTexts.ForError(Item, error));
    }
}
