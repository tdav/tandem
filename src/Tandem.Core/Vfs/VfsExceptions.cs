namespace Tandem.Core.Vfs;

/// <summary>A file-system provider failure.</summary>
public class VfsException : IOException
{
    public VfsException(string message) : base(message) { }
    public VfsException(string message, Exception? inner) : base(message, inner) { }
}

public class VfsNotFoundException : VfsException
{
    public VfsNotFoundException(string message) : base(message) { }
    public VfsNotFoundException(string message, Exception? inner) : base(message, inner) { }
}

public class VfsAccessDeniedException : VfsException
{
    public VfsAccessDeniedException(string message) : base(message) { }
    public VfsAccessDeniedException(string message, Exception? inner) : base(message, inner) { }
}

public class VfsAlreadyExistsException : VfsException
{
    public VfsAlreadyExistsException(string message) : base(message) { }
    public VfsAlreadyExistsException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>The connection to the source was lost or could not be made. Repeating the same call tries to connect
/// again.</summary>
public class VfsConnectionLostException : VfsException
{
    public VfsConnectionLostException(string message) : base(message) { }
    public VfsConnectionLostException(string message, Exception? inner) : base(message, inner) { }
}
