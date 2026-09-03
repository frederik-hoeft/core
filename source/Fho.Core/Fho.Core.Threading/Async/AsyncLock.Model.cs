using Fho.Core.Threading.Optimistic;

namespace Fho.Core.Threading.Async;

public sealed partial class AsyncLock
{
    private enum LifecycleState
    {
        Active,
        Canceling,
        Quiescing,
        Disposed
    }

    private sealed class OwnershipContext
    {
        internal OwnershipFrame? Top;
        internal AtomicBoolean Poisoned;
        internal AtomicBoolean RootReleased;
    }

    private sealed class OwnershipFrame(OwnershipContext context, OwnershipFrame? parent, int depth)
    {
        internal OwnershipContext Context { get; } = context;

        internal OwnershipFrame? Parent { get; } = parent;

        internal int Depth { get; } = depth;

        internal AtomicBoolean ExitRequested;
    }

    private readonly record struct ExecutionResult<TResult>(TResult? Result, bool TaskExecuted)
    {
        internal static ExecutionResult<TResult> Executed(TResult result) => new(result, TaskExecuted: true);

        internal static ExecutionResult<TResult> Skipped() => new(default, TaskExecuted: false);
    }
}
