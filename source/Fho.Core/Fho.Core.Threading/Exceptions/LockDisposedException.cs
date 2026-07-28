using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Fho.Core.Threading.Exceptions;

public sealed class LockDisposedException : ObjectDisposedException
{
    internal LockDisposedException() : base(objectName: null) { }

    public LockDisposedException(string? objectName) : base(objectName) { }

    public LockDisposedException(string? objectName, string? message) : base(objectName, message) { }

    public LockDisposedException(string message, Exception innerException) : base(message, innerException) { }

    [StackTraceHidden]
    public static new void ThrowIf([DoesNotReturnIf(true)] bool condition, object instance)
    {
        if (condition)
        {
            ThrowObjectDisposedException(instance);
        }
    }

    [StackTraceHidden]
    [DoesNotReturn]
    public static void Rethrow(ObjectDisposedException inner) => throw new LockDisposedException("The lock was disposed.", inner);

    [StackTraceHidden]
    [DoesNotReturn]
    private static void ThrowObjectDisposedException(object? instance) => throw new LockDisposedException(instance?.GetType().FullName);
}
