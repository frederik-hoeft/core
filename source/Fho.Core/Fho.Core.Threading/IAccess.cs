namespace Fho.Core.Threading;

internal interface IAccess<out T> : IDisposable where T : class
{
    T Value { get; }
}
