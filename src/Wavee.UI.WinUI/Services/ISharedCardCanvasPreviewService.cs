using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Services;

public interface ISharedCardCanvasPreviewService
{
    Task EnsureInitializedAsync(CancellationToken ct = default);
    Task<CanvasPreviewLease?> AcquireAsync(Panel host, string canvasUrl, CancellationToken ct = default);
    Task ReleaseAsync(CanvasPreviewLease? lease, CancellationToken ct = default);
    Task ReleaseHostAsync(Panel host, CancellationToken ct = default);
}

public sealed class CanvasPreviewLease
{
    internal CanvasPreviewLease(long id, Panel host, string canvasUrl)
    {
        Id = id;
        Host = host;
        CanvasUrl = canvasUrl;
    }

    public long Id { get; }
    public Panel Host { get; }
    public string CanvasUrl { get; }
}
