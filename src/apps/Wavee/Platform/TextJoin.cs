// ── Platform/TextJoin.cs ───────────────────────────────────────────────────────────────────────────────────────────────
// The ONE subtitle separator. Every composed meta line (a search row's "Song · A, B", the top result's eyebrow, a
// browse card's caption) joins its parts with this, so the app never words one line two ways.
//
// Role: UI
// Owner: L
// Wave: interaction-consistency (WP7)
// Budget: 20 lines

namespace Wavee;

public static class TextJoin
{
    /// <summary>Between the parts of a subtitle: kind word, then detail.</summary>
    public const string Sep = " · ";
}
