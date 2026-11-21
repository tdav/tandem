using Renci.SshNet;
using Renci.SshNet.Common;

namespace Tandem.Net.Sftp;

internal static class SftpAuth
{
    public static ConnectionInfo Build(ConnectionProfile profile, NetCredentials credentials, TimeSpan connectTimeout)
    {
        AuthenticationMethod[] methods = profile.Auth switch
        {
            NetAuth.PrivateKey => [KeyMethod(profile, credentials)],
            NetAuth.Password => PasswordMethods(profile.User, credentials.Password ?? ""),
            _ => throw new NetAuthenticationException($"Unsupported SSH authentication for {profile.Address}."),
        };
        return new ConnectionInfo(profile.Host, profile.Port, profile.User, methods) { Timeout = connectTimeout };
    }

    public static bool NeedsPassphrase(byte[] privateKey)
    {
        try
        {
            using var bytes = new MemoryStream(privateKey, writable: false);
            using var key = new PrivateKeyFile(bytes);
            return false;
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            return true;
        }
    }

    public static HostKeyInfo Describe(string hostKeyName, string fingerprintSha256) =>
        new(hostKeyName, "SHA256:" + fingerprintSha256.TrimEnd('='));

    public static void DisposeMethods(ConnectionInfo info)
    {
        foreach (var method in info.AuthenticationMethods.OfType<IDisposable>())
        {
            if (method is PrivateKeyAuthenticationMethod keyMethod)
                foreach (var key in keyMethod.KeyFiles.OfType<IDisposable>()) key.Dispose();
            method.Dispose();
        }
    }

    private static AuthenticationMethod[] PasswordMethods(string user, string password)
    {
        var keyboard = new KeyboardInteractiveAuthenticationMethod(user);
        keyboard.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts.Where(p => !p.IsEchoed))
                prompt.Response = password;
        };
        return [new PasswordAuthenticationMethod(user, password), keyboard];
    }

    private static PrivateKeyAuthenticationMethod KeyMethod(ConnectionProfile profile, NetCredentials credentials)
    {
        if (credentials.PrivateKey is not { Length: > 0 } bytes)
            throw new NetAuthenticationException($"No private key was supplied for {profile.Address}.");
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return new PrivateKeyAuthenticationMethod(profile.User, new PrivateKeyFile(stream, credentials.Passphrase));
        }
        catch (Exception e) when (e is SshException or ArgumentException or FormatException or IOException)
        {
            throw new NetAuthenticationException($"Could not read the private key '{profile.PrivateKeyPath}'.");
        }
    }
}
