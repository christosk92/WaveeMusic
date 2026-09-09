using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

/// <summary>Waits for a worker publication without turning passive observation into remote demand.</summary>
static class QueryPublication
{
    public static Task Initial<T>(IQueryHandle<T> handle) => Until(() => handle.Current.Revision > 0);

    public static async Task Until(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
