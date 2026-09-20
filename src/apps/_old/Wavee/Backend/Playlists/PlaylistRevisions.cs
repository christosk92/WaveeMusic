using System;
using System.Diagnostics.CodeAnalysis;

namespace Wavee.Backend.Playlists;

/// <summary>Invariant I1 — revision well-formedness. A STORED playlist/rootlist revision is always the 24-byte
/// playlist4 head (4-byte big-endian counter + 20-byte hash). Nothing else may enter those slots: not the 8-byte
/// create base, not the "default"/8-byte permission revision, and — the bug this type exists to make impossible —
/// not the URI bytes of a rootlist <c>PlaylistModificationInfo</c> misparsed as a <c>RootlistModificationInfo</c>.
/// <para>Every writer of a rootlist/playlist revision runs its candidate through <see cref="IsWellFormed"/> first and
/// keeps the stored value (plus a logged <c>RootlistBadRevision</c>) when it fails; a non-well-formed value that was
/// already persisted self-heals to null at sync start, which makes the next hydrate do a full GET.</para></summary>
public static class PlaylistRevisions
{
    /// <summary>The one true length of a playlist4 revision.</summary>
    public const int Length = 24;

    static readonly byte[] CreateBaseBytes = [0x00, 0x00, 0x00, 0x00, 0x72, 0x6f, 0x6f, 0x74];

    /// <summary>The 8-byte base revision a playlist CREATE is written against ("00000000726f6f74" = a zero counter
    /// followed by ASCII "root"). Deliberately NOT 24 bytes: it is a wire value only and never a stored revision, so
    /// the dealer create-echo's 8-byte parent_revision simply fails every equality gate instead of erroring.</summary>
    public static ReadOnlySpan<byte> CreateBase => CreateBaseBytes;

    /// <summary>A fresh mutable copy of <see cref="CreateBase"/> for wire builders that need a byte[].</summary>
    public static byte[] NewCreateBase() => (byte[])CreateBaseBytes.Clone();

    /// <summary>True only for a storable 24-byte revision. Null, empty, the 8-byte create base and URI bytes all fail.</summary>
    public static bool IsWellFormed([NotNullWhen(true)] byte[]? revision) => revision is { Length: Length };

    /// <summary>Byte equality; two nulls are NOT equal (a missing revision never matches a missing revision — that
    /// would turn "we know nothing" into an echo drop).</summary>
    public static bool Equal(byte[]? a, byte[]? b)
    {
        if (a is null || b is null) return false;
        return a.AsSpan().SequenceEqual(b);
    }
}
