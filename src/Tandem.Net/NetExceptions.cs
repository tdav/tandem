using Tandem.Core.Vfs;

namespace Tandem.Net;

/// <summary>The server did not accept the user name, password or key. <see cref="Declined"/>: the user chose not to
/// give them (no message is needed). The message never contains a secret.</summary>
public sealed class NetAuthenticationException : VfsAccessDeniedException
{
    public NetAuthenticationException(string message, Exception? inner = null) : base(message, inner) { }

    public NetAuthenticationException(string message, bool declined, Exception? inner = null) : base(message, inner) =>
        Declined = declined;

    /// <summary>The user declined to give credentials; nothing was refused by the server.</summary>
    public bool Declined { get; }
}

/// <summary>The server's host key or certificate was not confirmed.</summary>
public sealed class NetTrustException : VfsAccessDeniedException
{
    public NetTrustException(string message, Exception? inner = null) : base(message, inner) { }
}
