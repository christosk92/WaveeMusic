using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Wavee.UI.Services.DragDrop;

namespace Wavee.UI.WinUI.DragDrop;

/// <summary>
/// Reads a Windows <see cref="DataPackageView"/> into an <see cref="IDragPayload"/>.
/// Walks every internal format in <see cref="DragFormats.All"/> and asks the
/// registry to deserialize the first match. We never decode external
/// <c>Text</c>/<c>Uri</c> formats — those are drag-out only.
/// </summary>
public static class DragPackageReader
{
    public static async Task<IDragPayload?> ReadAsync(DataPackageView view, IDragDropService service)
    {
        if (view is null || service is null) return null;

        foreach (var format in DragFormats.All)
        {
            if (!view.Contains(format)) continue;
            var raw = await view.GetDataAsync(format) as string;
            if (string.IsNullOrEmpty(raw)) continue;
            if (service.TryDeserialize(format, raw, out var payload) && payload is not null)
                return payload;
        }
        return null;
    }
}
