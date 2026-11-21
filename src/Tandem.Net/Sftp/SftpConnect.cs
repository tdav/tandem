using System.Runtime.ExceptionServices;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Tandem.Net.Sftp;

internal static class SftpConnect
{
    public static async Task WithTrustAsync(BaseClient client, ConnectionProfile profile, ITrustDecisions trust,
        CancellationToken ct)
    {
        var refused = false;
        var connected = false;
        HostKeyInfo? accepted = null;
        ExceptionDispatchInfo? promptError = null;
        void CheckHost(object? sender, HostKeyEventArgs args)
        {
            args.CanTrust = false;
            try
            {
                var key = SftpAuth.Describe(args.HostKeyName, args.FingerPrintSHA256);
                // The first accepted key also pins subsequent key exchanges on this connection.
                args.CanTrust = accepted is { } pinned ? pinned == key :
                    trust.TrustHostKeyAsync(profile, key, ct).GetAwaiter().GetResult();
                if (args.CanTrust) accepted = key;
                refused = !args.CanTrust;
            }
            catch (Exception e) { promptError = ExceptionDispatchInfo.Capture(e); }
        }

        client.HostKeyReceived += CheckHost;
        try
        {
            await Task.Run(() => client.ConnectAsync(ct), ct).ConfigureAwait(false);
            connected = true;
        }
        catch (Exception e)
        {
            ct.ThrowIfCancellationRequested();
            promptError?.Throw();
            if (refused) throw new NetTrustException($"SSH host key refused for {profile.Address}.");
            ExceptionDispatchInfo.Throw(SftpErrors.Map(e, profile.Address));
        }
        finally
        {
            if (!connected) client.HostKeyReceived -= CheckHost;
        }
    }
}
