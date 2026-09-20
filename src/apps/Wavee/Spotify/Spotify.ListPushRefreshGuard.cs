namespace Wavee;

/// <summary>Repeated head-only pushes must not re-read the same rolling-list edition indefinitely.</summary>
public sealed class ListPushRefreshGuard
{
    public const int RetryWindowMs = 30_000;
    const int Capacity = 512;
    readonly Dictionary<string, (string? Head, int At)> _asked = new(StringComparer.Ordinal);
    public bool Accept(string uri, string? head, int nowMs)
    {
        if (_asked.TryGetValue(uri, out var prior) && prior.Head == head
            && unchecked((uint)(nowMs - prior.At)) < RetryWindowMs) return false;
        if (_asked.Count >= Capacity && !_asked.ContainsKey(uri)) _asked.Clear();
        _asked[uri] = (head, nowMs);
        return true;
    }
}
