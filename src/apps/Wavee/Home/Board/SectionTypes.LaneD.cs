// ── Home/Board/SectionTypes.LaneD.cs: a BRIDGE, deleted when lane A's model types merge ─────────────────────────────
//
// SectionRef and SectionTemplate are lane A's (Home/Board/HomeModel.cs), SeenRef too (Home/Board/HomeDoc.cs): the same
// declarations, word for word, so the data lane's identity, chooser and log compile before that merge. When the
// integration brings HomeModel.cs and HomeDoc.cs in, this file is deleted (a duplicate type is CS0101).

namespace Wavee;

/// <summary>A Spotify section's identity (design 2.2).</summary>
public readonly record struct SectionRef(string Key, ulong Fp, string Family);

/// <summary>When a section was last in an answer (design 1.2.1).</summary>
public readonly record struct SeenRef(SectionRef Ref, long LastSeenMs);

/// <summary>Which body draws a Spotify section (design 2.3, spec 3.1). Chosen from structure only.</summary>
public enum SectionTemplate : byte
{
    None, ShortcutFill, RecentsData, DaylistHero, CoverShelf, EntityShelf, MoodPlates, WideTiles, ReleaseList,
    ShowGrid, VideoTiles, EpisodeRows, BookShelf, PreviewCard, PreviewGrid, PlainShelf,
}
