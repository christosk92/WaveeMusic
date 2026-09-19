namespace Wavee;

/// <summary>Elapsed presentation time for the decorative stage drift. Pausing holds the last sampled pose; resuming
/// continues that phase without spending the paused wall time. The caller supplies monotonic seconds.</summary>
public sealed class StageDriftClock
{
    double _elapsedSeconds;
    double _lastSampleSeconds;
    bool _sampled;
    public bool Running { get; private set; }

    public void SetRunning(bool running, double nowSeconds)
    {
        if (Running == running) return;
        Running = running;
        _lastSampleSeconds = nowSeconds;
    }

    public double Sample(double nowSeconds)
    {
        if (!Running) return _elapsedSeconds;
        if (!_sampled)
        {
            _sampled = true;
            _lastSampleSeconds = nowSeconds;
            return _elapsedSeconds;
        }
        if (nowSeconds > _lastSampleSeconds)
        {
            _elapsedSeconds += nowSeconds - _lastSampleSeconds;
            _lastSampleSeconds = nowSeconds;
        }
        return _elapsedSeconds;
    }

    public void Reset()
    {
        Running = false;
        _sampled = false;
        _elapsedSeconds = _lastSampleSeconds = 0d;
    }
}
