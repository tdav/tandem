using System.Net.Sockets;
using Renci.SshNet.Common;
using Tandem.Core.Vfs;

namespace Tandem.Net.Sftp;

internal static class SftpErrors
{
    public static Exception Map(Exception error, string path) => error switch
    {
        OperationCanceledException or VfsException => error,
        SftpPathNotFoundException => new VfsNotFoundException(path, error),
        SftpPermissionDeniedException => new VfsAccessDeniedException(path, error),
        SshAuthenticationException => new NetAuthenticationException($"SSH authentication failed for {path}."),
        SshConnectionException or SshOperationTimeoutException or SocketException or TimeoutException
            or ObjectDisposedException => new VfsConnectionLostException(path, error),
        SshException => new VfsException($"{path}: {error.Message}", error),
        _ => error,
    };
}
