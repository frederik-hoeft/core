namespace Fho.Core.Threading.Exceptions;

/// <summary>
/// The exception that is thrown when an async flow violates <see cref="Fho.Core.Threading.Async.AsyncLock"/> reentrancy requirements.
/// </summary>
public sealed class AsyncLockUsageException : InvalidOperationException
{
    public AsyncLockUsageException() { }

    public AsyncLockUsageException(string? message) : base(message) { }

    public AsyncLockUsageException(string? message, Exception? innerException) : base(message, innerException) { }
}
