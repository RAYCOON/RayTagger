namespace RayTagger.Analysis.Bootstrap;

/// <summary>
/// Raised when the bootstrapper can't deliver a working binary — unknown tool, missing platform
/// source, network/IO failure, hash mismatch, or missing executable inside the archive. Treat as
/// recoverable at the host level: log the cause and fall back to "dimension unavailable".
/// </summary>
public sealed class NativeToolBootstrapException : Exception
{
    public NativeToolBootstrapException(string message) : base(message) { }
    public NativeToolBootstrapException(string message, Exception inner) : base(message, inner) { }
    public NativeToolBootstrapException() { }
}
