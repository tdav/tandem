namespace Tandem.Core.Operations;

/// <summary>The operation was refused before it touched anything (e.g. copying a folder into itself).</summary>
public sealed class OperationRejectedException(string reason) : Exception(reason);
