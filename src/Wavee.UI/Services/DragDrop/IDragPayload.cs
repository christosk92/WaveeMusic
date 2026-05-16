using System.Collections.Generic;

namespace Wavee.UI.Services.DragDrop;

/// <summary>
/// Framework-neutral drag payload contract. Each draggable item kind has its own
/// concrete implementation under <c>Payloads/</c>. The WinUI adapter
/// (<c>DragPackageWriter</c>) projects this into a Windows <c>DataPackage</c>
/// with both the custom <see cref="InternalFormat"/> and external
/// <c>Text</c>/<c>Uri</c> fallbacks derived from <see cref="HttpsUrls"/>.
/// </summary>
public interface IDragPayload
{
    DragPayloadKind Kind { get; }

    /// <summary>Custom data format string from <see cref="DragFormats"/>.</summary>
    string InternalFormat { get; }

    /// <summary>How many items are being dragged (for badge / drop UI captions).</summary>
    int ItemCount { get; }

    /// <summary>
    /// Public <c>https://open.spotify.com/...</c> URLs for every item in this
    /// payload. Used by <c>DragPackageWriter</c> to fill <c>StandardDataFormats.Text</c>
    /// and (when single-item) <c>WebLink</c>, so external apps (chat, browser,
    /// text editors) receive a clickable link without knowing about the
    /// internal format. May be empty when no public URL exists (sidebar folders).
    /// </summary>
    IReadOnlyList<string> HttpsUrls { get; }

    /// <summary>Serialize this payload to a string the registry can later deserialize back.</summary>
    string Serialize();
}
