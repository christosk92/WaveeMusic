using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Controls.Lyrics.Helper
{
    public class LatestOnlyTaskRunner
    {
        private CancellationTokenSource? _cts;
        private readonly object _lock = new();

        public async Task RunAsync(Func<CancellationToken, Task> action)
        {
            CancellationTokenSource cts;
            lock (_lock)
            {
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                cts = _cts;
            }

            try
            {
                await action(cts.Token);
            }
            catch (OperationCanceledException) { }
        }
    }
}
