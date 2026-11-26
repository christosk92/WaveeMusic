// ── Shell/Lyrics.Spoken.cs ───────────────────────────────────────────────────────────────────────────────────────────
// AI DJ (plan-dj §2.8): the DJ's words as the lyric lanes read them. While the DJ talks (here or on the device that owns playback,
// `Playback.Dj.CaptionNow`), the item on the deck has no lyrics to ask for (`Store.IdOf` answers "" for a narration clip, and an
// owner's `spotify:media:` item is no track), so every lane that would say "No lyrics available" or show nothing shows the words.
//
// Role: CORE (pure documents over the caption text; the lanes stay the themes' pictures)
// Spec: docs/plans/wavee/dj-implementation.md §2.8 ("SSML → plain text … shown timed (the whole text, fading in with the voice) in the
// stage caption and the now-playing hero")
//
// TWO SHAPES OF ONE TEXT, each built once per caption (a new narration is a new document, so every keyed lane re-keys and fades):
//   Line()    the whole text as ONE line from 0 with no end: the timed lanes (the stage caption over the face, the now-playing hero's
//             reel, the mini player's ticker and lines) hold it as their active line for the whole clip. The published position stands
//             at 0 while the DJ talks (its progress is suppressed), so the line is active from the first frame to the last.
//   Sheet()   the text as a reading block, a sentence a row: the lyrics view's untimed form (the stage's Lyrics face, the side pane, the
//             mini player's lyrics window) in each theme's own unsynced picture.
// Nothing here fetches, caches to disk or enters `Store`: the words are the session's, and they go when the clip does.

namespace Wavee;

public static partial class Lyrics
{
    /// <summary>The DJ's words as lyric documents (see the file header).</summary>
    public static class Spoken
    {
        /// <summary>The provider id the documents carry (the inspector names it).</summary>
        public const string Provider = "dj";

        /// <summary>The track-id prefix of a spoken document: a scroll key, never a store key.</summary>
        public const string IdPrefix = "dj:";

        static string s_lineText = "", s_sheetText = "";
        static Doc? s_line, s_sheet;
        static int s_generation;

        /// <summary>The words the DJ says now (<see cref="Playback.Dj.CaptionNow"/>), "" while it is quiet. Reactive.</summary>
        public static string Now() => Playback.Dj.CaptionNow();

        /// <summary>The whole text as one line from 0 (a timed document), or null for no text. Built once per text.</summary>
        public static Doc? Line(string text)
        {
            if (text.Length == 0) return null;
            if (s_line is { } held && ReferenceEquals(text, s_lineText)) return held;
            s_lineText = text;
            return s_line = new Doc(IdOf(text), IsSynced: true, [new Line(0, text, [])], SyncKind.Line, Provider);
        }

        /// <summary>The text as a reading block, one sentence a row (an untimed document), or null for no text. Built once per text.</summary>
        public static Doc? Sheet(string text)
        {
            if (text.Length == 0) return null;
            if (s_sheet is { } held && ReferenceEquals(text, s_sheetText)) return held;
            s_sheetText = text;
            var lines = new List<Line>(4);
            foreach (string sentence in Sentences(text)) lines.Add(new Line(0, sentence, []));
            return s_sheet = new Doc(IdOf(text), IsSynced: false, lines, SyncKind.Unsynced, Provider);
        }

        /// <summary>A key per caption: the same words keep it, the next narration gets a new one (a lane keyed by it fades the change).</summary>
        public static string KeyOf(string text) => IdOf(text);

        static string IdOf(string text)
        {
            if (!ReferenceEquals(text, s_keyText)) { s_keyText = text; s_key = IdPrefix + (++s_generation).ToString(System.Globalization.CultureInfo.InvariantCulture); }
            return s_key;
        }

        static string s_keyText = "", s_key = IdPrefix + "0";

        /// <summary>The text cut after each sentence end (<c>.</c>, <c>!</c>, <c>?</c>, <c>…</c>, closing quotes kept) that a space follows;
        /// the pieces trimmed, none empty. PURE.</summary>
        public static IEnumerable<string> Sentences(string text)
        {
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] is not ('.' or '!' or '?' or '…')) continue;
                int end = i + 1;
                while (end < text.Length && text[end] is '"' or '\'' or '”' or '’' or ')' or '.' or '!' or '?') end++;
                if (end < text.Length && !char.IsWhiteSpace(text[end])) continue;
                string piece = text[start..end].Trim();
                if (piece.Length > 0) yield return piece;
                start = end;
                i = end - 1;
            }
            if (start < text.Length && text[start..].Trim() is { Length: > 0 } rest) yield return rest;
        }
    }
}
