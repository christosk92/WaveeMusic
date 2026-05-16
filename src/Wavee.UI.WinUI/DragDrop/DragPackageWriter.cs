using System;
using Windows.ApplicationModel.DataTransfer;
using Wavee.UI.Services.DragDrop;
using Wavee.UI.Services.DragDrop.Payloads;

namespace Wavee.UI.WinUI.DragDrop;

/// <summary>
/// Writes an <see cref="IDragPayload"/> into a Windows <see cref="DataPackage"/>.
/// Always emits the custom internal format AND external <c>Text</c>/<c>WebLink</c>
/// fallbacks derived from <see cref="IDragPayload.HttpsUrls"/> so the same
/// drag works for internal targets (which prefer the custom format) and for
/// external apps (which receive a usable link).
/// </summary>
public static class DragPackageWriter
{
    public static void Write(DataPackage data, IDragPayload payload)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(payload);

        // 1. Custom internal format — the preferred path for in-app drops.
        data.SetData(payload.InternalFormat, payload.Serialize());

        // 2. Legacy back-compat: anyone still keyed off the old WaveeTrackIds
        //    pipe-joined-ids format keeps reading for one release.
        if (payload is TrackDragPayload track)
            data.SetData(DragFormats.LegacyTrackIds, track.LegacyPipeJoinedIds);

        // 3. External text — newline-joined https URLs so chat/notepad get a readable list.
        //    Skip when the payload has no public URLs (e.g. sidebar folders).
        var urls = payload.HttpsUrls;
        if (urls.Count > 0)
        {
            // Use Environment.NewLine so paste targets render line breaks natively.
            data.SetText(string.Join(Environment.NewLine, urls));

            // 4. WebLink — only valid for a single Uri. Browsers receive a click target.
            if (urls.Count == 1 && Uri.TryCreate(urls[0], UriKind.Absolute, out var first))
                data.SetWebLink(first);
        }
    }
}
