using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee.Backend.Playlists;

/// <summary>Engine-free fields for the <c>playlist.snapshot</c> Info line: revision hex, name/cover delta, the
/// membership fingerprint. Tests table-drive these without a fetch or a store.</summary>
public static class PlaylistSnapshotFacts
{
    public static string ShortRev(byte[]? rev)
    {
        if (rev is null || rev.Length == 0) return "-";
        int n = Math.Min(2, rev.Length);
        return Convert.ToHexStringLower(rev.AsSpan(0, n));
    }

    public static string CoverId(Image? image)
    {
        string? url = image?.Url;
        if (string.IsNullOrEmpty(url)) return "-";
        var id = ImageSource.ImageIdSpan(url);
        return id.Length == 0 ? "-" : id.ToString();
    }

    public static bool NameChanged(string? before, string? after)
        => !string.Equals(before ?? "", after ?? "", StringComparison.Ordinal);

    public static string HeadUid(IReadOnlyList<PlaylistMember> members)
        => members is { Count: > 0 } ? members[0].ItemId ?? "" : "";

    /// <summary>Does this playlist's own identity roll over on the server's own clock (a daylist and its future
    /// siblings) — meaning a <c>/diff</c> verdict of "nothing changed" (a 304, an UpToDate body, an APPLIED diff with
    /// no <c>UPDATE_LIST</c> op) can still be wrong about the HEADER: the server names an entirely new edition without
    /// ever emitting an op against the old one. <paramref name="format"/> is <see cref="Playlist.Format"/>
    /// (<c>format_attributes</c>' <c>format_string</c>, e.g. "daylist"); <paramref name="daylistExpiresAtMs"/> is
    /// <see cref="Playlist.DaylistExpiresAtMs"/> — either one being present is enough, so a restored header that kept
    /// the rollover window but lost the format string (or vice versa) still reads as rolling.</summary>
    public static bool IsRollingIdentity(string? format, long daylistExpiresAtMs)
        => string.Equals(format, "daylist", StringComparison.Ordinal) || daylistExpiresAtMs > 0;
}
