using System;
using System.Linq;
using System.Text;
using Google.Protobuf;
using Wavee.Protocol.ExtendedMetadata;

namespace Wavee.UI.WinUI.Services;

internal static class MusicVideoAssociationParser
{
    public static bool HasVideoAssociation(byte[]? data)
        => TryReadVideoAssociationUri(data) is not null;

    public static string? TryReadVideoAssociationUri(byte[]? data)
    {
        if (data is null || data.Length == 0) return null;

        try
        {
            var assoc = Assoc.Parser.ParseFrom(data);
            var uri = assoc.PlainList?.EntityUri.FirstOrDefault(IsSpotifyTrackUri);
            if (!string.IsNullOrWhiteSpace(uri)) return uri;
        }
        catch (InvalidProtocolBufferException)
        {
        }

        try
        {
            var plainList = PlainListAssoc.Parser.ParseFrom(data);
            var uri = plainList.EntityUri.FirstOrDefault(IsSpotifyTrackUri);
            if (!string.IsNullOrWhiteSpace(uri)) return uri;
        }
        catch (InvalidProtocolBufferException)
        {
        }

        var text = Encoding.UTF8.GetString(data);
        var markerIndex = text.IndexOf("spotify:track:", StringComparison.Ordinal);
        if (markerIndex < 0) return null;

        var endIndex = markerIndex;
        while (endIndex < text.Length)
        {
            var ch = text[endIndex];
            if (!IsSpotifyUriChar(ch)) break;
            endIndex++;
        }

        var candidate = text[markerIndex..endIndex];
        return IsSpotifyTrackUri(candidate) ? candidate : null;
    }

    private static bool IsSpotifyTrackUri(string? uri)
        => !string.IsNullOrWhiteSpace(uri)
           && uri.StartsWith("spotify:track:", StringComparison.Ordinal);

    private static bool IsSpotifyUriChar(char ch)
        => (ch >= 'a' && ch <= 'z')
           || (ch >= 'A' && ch <= 'Z')
           || (ch >= '0' && ch <= '9')
           || ch == ':';
}
