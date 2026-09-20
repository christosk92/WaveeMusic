namespace Wavee;

/// <summary>Minimal <c>IObservable.Subscribe(Action&lt;T&gt;)</c> sugar — pairs with Wavee.Core's hand-rolled
/// <c>SimpleSubject</c> so the bridge can subscribe without taking a System.Reactive dependency.</summary>
internal static class Obs
{
    public static IDisposable Subscribe<T>(this IObservable<T> source, Action<T> onNext)
        => source.Subscribe(new Anon<T>(onNext));

    sealed class Anon<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
