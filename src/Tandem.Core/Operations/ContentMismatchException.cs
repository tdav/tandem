namespace Tandem.Core.Operations;

/// <summary>A copied file, read back for verification, differs from its source. It is an item error: the user is
/// asked whether to retry or skip, and the unverified copy is removed.</summary>
public sealed class ContentMismatchException(string message) : IOException(message);
