// ── Playback/LocalHome.cs ──────────────────────────────────────────────────────────────────────────────────────────
// which shelves "On this PC"'s Home shows, in which order ("Edit home", #240 P2, concept §3 and board 18)
//
// Role: CORE (UI thread). One setting, `local.page.home`: the shelves in the user's order, joined by ','; a hidden one is prefixed '!'
// ("recent,!lossless,decades"). Unknown names are dropped; a shelf the setting does not name (one added in a later version) is appended,
// shown. Empty = the default order, every shelf shown. Read and written ONLY here.

using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>The Home shelves' order and visibility (see the file header).</summary>
public static class LocalHome
{
    static readonly SettingKey<string> s_key = new("local.page.home", "");
    static Signal<IReadOnlyList<LocalShelfSlot>>? s_shelves;

    /// <summary>The shelves, in the user's order, each with whether it shows. UI thread.</summary>
    public static IReadSignal<IReadOnlyList<LocalShelfSlot>> Shelves => s_shelves ??= new(Parse(Platform.Settings.Get(s_key)));

    /// <summary>Show or hide one shelf.</summary>
    public static void Show(LocalShelf shelf, bool shown)
    {
        var list = new List<LocalShelfSlot>(Shelves.Peek());
        int i = list.FindIndex(s => s.Shelf == shelf);
        if (i < 0 || list[i].Shown == shown) return;
        list[i] = list[i] with { Shown = shown };
        Commit(list);
    }

    /// <summary>Move one shelf up (<paramref name="delta"/> -1) or down (+1); at an end, nothing.</summary>
    public static void Move(LocalShelf shelf, int delta)
    {
        var list = new List<LocalShelfSlot>(Shelves.Peek());
        int i = list.FindIndex(s => s.Shelf == shelf), j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        Commit(list);
    }

    static void Commit(List<LocalShelfSlot> list)
    {
        s_shelves!.Value = list.ToArray();
        Platform.Settings.Set(s_key, Format(list));
    }

    static string Format(List<LocalShelfSlot> list)
    {
        var parts = new string[list.Count];
        for (int i = 0; i < parts.Length; i++) parts[i] = (list[i].Shown ? "" : "!") + list[i].Shelf.ToString().ToLowerInvariant();
        return string.Join(',', parts);
    }

    /// <summary>The setting's text as slots (PURE).</summary>
    public static LocalShelfSlot[] Parse(string text)
    {
        var list = new List<LocalShelfSlot>(4);
        foreach (string raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            bool hidden = raw.StartsWith('!');
            if (!Enum.TryParse(hidden ? raw[1..] : raw, ignoreCase: true, out LocalShelf shelf) || !Enum.IsDefined(shelf)) continue;
            if (list.Exists(s => s.Shelf == shelf)) continue;
            list.Add(new LocalShelfSlot(shelf, !hidden));
        }
        foreach (LocalShelf shelf in Enum.GetValues<LocalShelf>())
            if (!list.Exists(s => s.Shelf == shelf)) list.Add(new LocalShelfSlot(shelf, true));
        return list.ToArray();
    }
}
