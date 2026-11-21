using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentFTP;

namespace Tandem.Net.Ftp;

internal static class FtpSettings
{
    public static FtpConfig ToConfig(ConnectionProfile profile, TimeSpan connectTimeout, TimeSpan readTimeout) => new()
    {
        EncryptionMode = profile.Protocol switch
        {
            NetProtocol.Ftp => FtpEncryptionMode.None,
            NetProtocol.FtpsExplicit => FtpEncryptionMode.Explicit,
            NetProtocol.FtpsImplicit => FtpEncryptionMode.Implicit,
            _ => throw new ArgumentException(nameof(profile)),
        },
        SslProtocols = SslProtocols.None,
        DataConnectionEncryption = true,
        DataConnectionType = profile.PassiveMode ? FtpDataConnectionType.AutoPassive : FtpDataConnectionType.AutoActive,
        ConnectTimeout = Milliseconds(connectTimeout),
        ReadTimeout = Milliseconds(readTimeout),
        WriteTimeout = Milliseconds(readTimeout),
        DataConnectionConnectTimeout = Milliseconds(connectTimeout),
        DataConnectionReadTimeout = Milliseconds(readTimeout),
        DataConnectionWriteTimeout = Milliseconds(readTimeout),
        RetryAttempts = 1,
        SelfConnectMode = FtpSelfConnectMode.Never,
        TimeConversion = FtpDate.UTC,
    };

    public static Encoding EncodingOf(ConnectionProfile profile)
    {
        if (profile.NameEncoding is not { } name) return Encoding.UTF8;
        return CodePagesEncodingProvider.Instance.GetEncoding(name) ?? Encoding.GetEncoding(name);
    }

    public static CertificateInfo Describe(X509Certificate certificate, SslPolicyErrors errors)
    {
        using var detail = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return new(detail.Subject, detail.Issuer, Convert.ToHexString(detail.GetCertHash(HashAlgorithmName.SHA256)),
            detail.NotBefore, detail.NotAfter, errors.ToString());
    }

    private static int Milliseconds(TimeSpan timeout) => checked((int)timeout.TotalMilliseconds);
}
