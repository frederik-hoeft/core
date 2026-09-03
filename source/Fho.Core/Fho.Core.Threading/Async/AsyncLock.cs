using Fho.Core.Threading.Exceptions;
using System.Diagnostics;

namespace Fho.Core.Threading.Async;

/// <summary>
/// An asynchronous mutual-exclusion lock that can be held across await boundaries.
/// </summary>
/// <remarks>
/// Reentrancy is supported for a serialized async call stack. Concurrent branching of inherited ownership is invalid usage.
/// </remarks>
[DebuggerDisplay("IsHeld = {IsHeld}")]
// nobody wants to step through the internals of a lock when debugging business logic,
// so we extensively use DebuggerStepThrough to allow the debugger to skip over the internals of the lock
// and quickly get to the delegate execution of the user code.
[method: DebuggerStepThrough]
public sealed partial class AsyncLock()
{
    /// <summary>
    /// Asynchronously acquires the lock and executes the specified synchronous action, releasing the lock when the action completes.
    /// </summary>
    /// <typeparam name="TResult">The type of the result returned by the action.</typeparam>
    /// <param name="synchronizedAction">The action to execute while holding the lock.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="LockDisposedException">Thrown if the lock has been disposed before the action can begin.</exception>
    /// <exception cref="AsyncLockUsageException">Thrown when inherited reentrant ownership violates the serialized call-stack contract.</exception>
    [DebuggerStepThrough]
    public Task<TResult> RunAsync<TResult>(Func<TResult> synchronizedAction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedAction);
        return RunCoreAsync(Wrapper, cancellationToken);

        [DebuggerStepThrough]
        Task<TResult> Wrapper(CancellationToken _) => Task.FromResult(synchronizedAction());
    }

    /// <summary>
    /// Asynchronously acquires the lock and executes the specified synchronous action, releasing the lock when the action completes.
    /// </summary>
    /// <param name="synchronizedAction">The action to execute while holding the lock.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="LockDisposedException">Thrown if the lock has been disposed before the action can begin.</exception>
    /// <exception cref="AsyncLockUsageException">Thrown when inherited reentrant ownership violates the serialized call-stack contract.</exception>
    [DebuggerStepThrough]
    public Task RunAsync(Action synchronizedAction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedAction);
        return RunCoreAsync(Wrapper, cancellationToken);

        [DebuggerStepThrough]
        Task<bool> Wrapper(CancellationToken _)
        {
            synchronizedAction();
            return s_completedDummyTask;
        }
    }

    /// <summary>
    /// Attempts to asynchronously acquire the lock and execute the specified synchronous action, releasing the lock when the action completes.
    /// </summary>
    /// <typeparam name="TResult">The type of the result returned by the action.</typeparam>
    /// <param name="synchronizedAction">The action to execute while holding the lock.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task whose result indicates whether the action was executed before disposal.</returns>
    /// <exception cref="AsyncLockUsageException">Thrown when inherited reentrant ownership violates the serialized call-stack contract.</exception>
    [DebuggerStepThrough]
    public Task<AsyncLockResult<TResult>> TryRunAsync<TResult>(Func<TResult> synchronizedAction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedAction);
        return TryRunCoreAsync(Wrapper, cancellationToken);

        [DebuggerStepThrough]
        Task<TResult> Wrapper(CancellationToken _) => Task.FromResult(synchronizedAction());
    }

    /// <summary>
    /// Attempts to asynchronously acquire the lock and execute the specified synchronous action, releasing the lock when the action completes.
    /// </summary>
    /// <param name="synchronizedAction">The action to execute while holding the lock.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task whose result indicates whether the action was executed before disposal.</returns>
    /// <exception cref="AsyncLockUsageException">Thrown when inherited reentrant ownership violates the serialized call-stack contract.</exception>
    [DebuggerStepThrough]
    public Task<AsyncLockResult> TryRunAsync(Action synchronizedAction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedAction);
        return TryRunCoreWithoutResultAsync(Wrapper, cancellationToken);

        [DebuggerStepThrough]
        Task<bool> Wrapper(CancellationToken _)
        {
            synchronizedAction();
            return s_completedDummyTask;
        }
    }

    /// <summary>
    /// Asynchronously acquires the lock and executes the specified asynchronous task, releasing the lock when the task completes.
    /// </summary>
    /// <param name="synchronizedTask">The asynchronous task to execute while holding the lock.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="LockDisposedException">Thrown if the lock has been disposed before the task can begin.</exception>
    /// <exception cref="AsyncLockUsageException">Thrown when inherited reentrant ownership violates the serialized call-stack contract.</exception>
    [DebuggerStepThrough]
    public Task RunTaskAsync(Func<CancellationToken, Task> synchronizedTask, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedTask);
        return RunCoreAsync(Wrapper, cancellationToken);

        [DebuggerStepThrough]
        async Task<bool> Wrapper(CancellationToken ct)
        {
            await synchronizedTask(ct);
            return true;
        }
    }

    /// <summary>
    /// Asynchronously acquires the lock and executes the specified asynchronous task, releasing the lock when the task completes.
    /// </summary>
    /// <typeparam name="TResult">The type of the result returned by the task.</typeparam>
    /// <param name="synchronizedTask">The asynchronous task to execute while holding the lock.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="LockDisposedException">Thrown if the lock has been disposed before the task can begin.</exception>
    /// <exception cref="AsyncLockUsageException">Thrown when inherited reentrant ownership violates the serialized call-stack contract.</exception>
    [DebuggerStepThrough]
    public Task<TResult> RunTaskAsync<TResult>(Func<CancellationToken, Task<TResult>> synchronizedTask, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedTask);
        return RunCoreAsync(synchronizedTask, cancellationToken);
    }

    /// <summary>
    /// Attempts to asynchronously acquire the lock and execute the specified asynchronous task, releasing the lock when the task completes.
    /// </summary>
    /// <param name="synchronizedTask">The asynchronous task to execute while holding the lock.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task whose result indicates whether the task was executed before disposal.</returns>
    /// <exception cref="AsyncLockUsageException">Thrown when inherited reentrant ownership violates the serialized call-stack contract.</exception>
    [DebuggerStepThrough]
    public Task<AsyncLockResult> TryRunTaskAsync(Func<CancellationToken, Task> synchronizedTask, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedTask);
        return TryRunCoreWithoutResultAsync(Wrapper, cancellationToken);

        [DebuggerStepThrough]
        async Task<bool> Wrapper(CancellationToken ct)
        {
            await synchronizedTask(ct);
            return true;
        }
    }

    /// <summary>
    /// Attempts to asynchronously acquire the lock and execute the specified asynchronous task, releasing the lock when the task completes.
    /// </summary>
    /// <typeparam name="TResult">The type of the result returned by the task.</typeparam>
    /// <param name="synchronizedTask">The asynchronous task to execute while holding the lock.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task whose result indicates whether the task was executed before disposal.</returns>
    /// <exception cref="AsyncLockUsageException">Thrown when inherited reentrant ownership violates the serialized call-stack contract.</exception>
    [DebuggerStepThrough]
    public Task<AsyncLockResult<TResult>> TryRunTaskAsync<TResult>(Func<CancellationToken, Task<TResult>> synchronizedTask, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronizedTask);
        return TryRunCoreAsync(synchronizedTask, cancellationToken);
    }

    private async Task<TResult> RunCoreAsync<TResult>(Func<CancellationToken, Task<TResult>> synchronizedTask, CancellationToken cancellationToken)
    {
        ExecutionResult<TResult> execution = await ExecuteAsync(synchronizedTask, cancellationToken);
        if (!execution.TaskExecuted)
        {
            throw new LockDisposedException(GetType().FullName);
        }

        return execution.Result!;
    }

    private async Task<AsyncLockResult> TryRunCoreWithoutResultAsync(Func<CancellationToken, Task<bool>> synchronizedTask, CancellationToken cancellationToken)
    {
        ExecutionResult<bool> execution = await ExecuteAsync(synchronizedTask, cancellationToken);
        return execution.TaskExecuted ? new AsyncLockResult(TaskExecuted: true) : AsyncLockResult.Skipped();
    }

    private async Task<AsyncLockResult<TResult>> TryRunCoreAsync<TResult>(Func<CancellationToken, Task<TResult>> synchronizedTask, CancellationToken cancellationToken)
    {
        ExecutionResult<TResult> execution = await ExecuteAsync(synchronizedTask, cancellationToken);
        return execution.TaskExecuted
            ? new AsyncLockResult<TResult>(execution.Result, TaskExecuted: true)
            : AsyncLockResult.Skipped<TResult>();
    }
}
