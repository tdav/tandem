using Renci.SshNet.Sftp;
using Tandem.Core.Vfs;
using Tandem.Net.Remote;

namespace Tandem.Net.Sftp;

internal static class SftpEntries
{
    public static RemoteEntry? ToEntry(ISftpFile file) =>
        file.Name is "." or ".." ? null : ToEntry(file.Name, file.Attributes);

    public static RemoteEntry ToEntry(string name, SftpFileAttributes attributes)
    {
        var kind = attributes.IsSymbolicLink ? EntryKind.Symlink :
            attributes.IsDirectory ? EntryKind.Directory : EntryKind.File;
        var mode = (attributes.OwnerCanRead ? UnixFileMode.UserRead : 0)
                   | (attributes.OwnerCanWrite ? UnixFileMode.UserWrite : 0)
                   | (attributes.OwnerCanExecute ? UnixFileMode.UserExecute : 0)
                   | (attributes.GroupCanRead ? UnixFileMode.GroupRead : 0)
                   | (attributes.GroupCanWrite ? UnixFileMode.GroupWrite : 0)
                   | (attributes.GroupCanExecute ? UnixFileMode.GroupExecute : 0)
                   | (attributes.OthersCanRead ? UnixFileMode.OtherRead : 0)
                   | (attributes.OthersCanWrite ? UnixFileMode.OtherWrite : 0)
                   | (attributes.OthersCanExecute ? UnixFileMode.OtherExecute : 0);
        return new(name, kind, kind == EntryKind.File ? attributes.Size : 0, attributes.LastWriteTimeUtc, mode);
    }
}
