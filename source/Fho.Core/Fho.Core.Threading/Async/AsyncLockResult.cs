namespace Fho.Core.Threading.Async;

public readonly record struct AsyncLockResult(bool TaskExecuted)
{
    internal static AsyncLockResult Skipped() => new(false);

    internal static AsyncLockResult<TResult> Skipped<TResult>() => new(default, TaskExecuted: false);
}
