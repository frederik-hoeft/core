namespace Fho.Core.Threading;

internal sealed class AccessTracker<T>(T value) where T : class
{
    private readonly T _value = value;
    private int _accessCount;

    public int AccessCount => Volatile.Read(ref _accessCount);

    public IAccess<T> AcquireAccess()
    {
        Interlocked.Increment(ref _accessCount);
        return new Access(this);
    }

    private sealed class Access(AccessTracker<T> tracker) : IAccess<T>
    {
        private bool _disposedValue;

        public T Value => tracker._value;

        private void DisposeCore()
        {
            if (!_disposedValue)
            {
                Interlocked.Decrement(ref tracker._accessCount);
                _disposedValue = true;
            }
        }

        ~Access()
        {
            // at least ensure eventual lock release
            // Do not change this code. Put cleanup code in 'DisposeCore()' method
            DisposeCore();
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'DisposeCore()' method
            DisposeCore();
            GC.SuppressFinalize(this);
        }
    }
}
