using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Localization;
using Wavee.Core.Sidebar;

namespace Wavee;

/// <summary>
/// Static AOT-clean command-palette table + keystroke filter. Builtins are nav / playback / settings; registry
/// actions that accept NowPlaying, ActiveRoute, or None are merged at palette-open. Filter is an array scan over
/// pre-lowercased labels — no LINQ, no per-keystroke closures. A <c>&gt;</c> prefix (VS Code) restricts the scan to
/// commands; typing without it also offers a "Search for X" row that navigates to the Search page (catalog search is
/// a page pipeline, not a palette API).
/// </summary>
static class WaveeCommands
{
    public const int MaxResults = 8;

    public enum Kind : byte { Navigate, Playback, Settings, Registry, CatalogSearch, Library }
    public enum PlaybackVerb : byte { PlayPause, Next, Previous, Shuffle, Repeat }
    public enum SettingsVerb : byte { ToggleTheme, ToggleCrossfade }
    /// <summary>Library-structure verbs. "New folder" lives here because the only other way to reach it is a right-click
    /// on a sidebar row — and on a pane with no folders yet there is no such row to right-click.</summary>
    public enum LibraryVerb : byte { NewPlaylist, NewFolder }

    public sealed class Entry
    {
        public required string Id;
        public required string Label;
        public required string LabelLower;
        public required string Glyph;
        public Kind Kind;
        public string? RouteKey;
        public PlaybackVerb Playback;
        public SettingsVerb Settings;
        public LibraryVerb Library;
        public string? RegistryProvider;
        public string? RegistryAction;
        public SidebarActionTargetMode RegistryTarget;
        public string? CatalogQuery;
    }

    public readonly struct Host
    {
        public required Action<string, string?> Go { get; init; }
        public required ActionServices Actions { get; init; }
        public required IAppSettings Settings { get; init; }
        public required Action ToggleTheme { get; init; }
    }

    /// <summary>Builtin table size (nav + playback + settings). Registry rows are appended by <see cref="BuildIndex"/>.</summary>
    public const int BuiltinCount = 14;

    /// <summary>Allocate a fresh index for one palette-open. Labels are localized at this edge (not per keystroke).</summary>
    public static Entry[] BuildIndex(WaveeExtensionRegistry? registry)
    {
        var builtins = CreateBuiltins();
        if (registry is null) return builtins;

        var extra = registry.Actions;
        int extraN = 0;
        for (int i = 0; i < extra.Count; i++)
            if (PaletteTargetOf(extra[i], out _)) extraN++;

        var all = new Entry[builtins.Length + extraN];
        Array.Copy(builtins, all, builtins.Length);
        int w = builtins.Length;
        for (int i = 0; i < extra.Count; i++)
        {
            var d = extra[i];
            if (!PaletteTargetOf(d, out var target)) continue;
            string label = d.Label();
            all[w++] = new Entry
            {
                Id = d.Key,
                Label = label,
                LabelLower = label.ToLowerInvariant(),
                Glyph = d.Icon().Glyph ?? Icons.More,
                Kind = Kind.Registry,
                RegistryProvider = BuiltInExtensionTable.ExtensionId,
                RegistryAction = ActionIdOf(d.Key),
                RegistryTarget = target,
            };
        }
        return all;
    }

    /// <summary>Scan <paramref name="index"/> into a caller-owned <paramref name="dest"/> (length ≥ <see cref="MaxResults"/>).
    /// Returns the number of hits written. <paramref name="catalogScratch"/> is filled when a "Search for X" row is
    /// appended (the same object is reused across keystrokes).</summary>
    public static int Filter(Entry[] index, string query, Entry[] dest, Entry catalogScratch)
    {
        bool commandsOnly = false;
        var q = query;
        if (q.Length > 0 && q[0] == '>')
        {
            commandsOnly = true;
            int start = 1;
            while (start < q.Length && q[start] == ' ') start++;
            q = start == 0 ? q : q.Substring(start);
        }
        else if (q.Length > 0)
        {
            q = q.Trim();
        }

        string qLower = q.Length == 0 ? "" : q.ToLowerInvariant();
        int written = 0;
        // Insertion by score (lower = better), then original order. Tiny N (MaxResults) — linear insert, no Sort/LINQ.
        Span<int> scores = stackalloc int[MaxResults];

        for (int i = 0; i < index.Length; i++)
        {
            var e = index[i];
            int s = qLower.Length == 0 ? 0 : ScoreOf(e.LabelLower, qLower);
            if (s < 0) continue;
            InsertScored(dest, scores, ref written, e, s);
        }

        if (!commandsOnly && q.Length > 0 && written < MaxResults)
        {
            catalogScratch.Id = "search.query";
            catalogScratch.Kind = Kind.CatalogSearch;
            catalogScratch.Glyph = Icons.Search;
            catalogScratch.CatalogQuery = q;
            catalogScratch.Label = "Search for “" + q + "”";
            catalogScratch.LabelLower = catalogScratch.Label.ToLowerInvariant();
            dest[written++] = catalogScratch;
        }
        return written;
    }

    public static void Invoke(Entry cmd, in Host host)
    {
        switch (cmd.Kind)
        {
            case Kind.Navigate:
                if (cmd.RouteKey is { Length: > 0 } key) host.Go(key, null);
                break;
            case Kind.Playback:
                if (host.Actions.Playback is not { } pb) return;
                switch (cmd.Playback)
                {
                    case PlaybackVerb.PlayPause: PlayerBarContent.TogglePlayPause(pb); break;
                    case PlaybackVerb.Next: _ = pb.Player.NextAsync(); break;
                    case PlaybackVerb.Previous: _ = pb.Player.PreviousAsync(); break;
                    case PlaybackVerb.Shuffle: PlayerBarContent.ToggleShuffle(pb); break;
                    case PlaybackVerb.Repeat: PlayerBarContent.CycleRepeat(pb); break;
                }
                break;
            case Kind.Settings:
                switch (cmd.Settings)
                {
                    case SettingsVerb.ToggleTheme:
                        host.ToggleTheme();
                        break;
                    case SettingsVerb.ToggleCrossfade:
                        bool on = host.Settings.Get(WaveeSettings.CrossfadeEnabled);
                        host.Settings.Set(WaveeSettings.CrossfadeEnabled, !on);
                        break;
                }
                break;
            case Kind.Registry:
                if (cmd.RegistryProvider is not { Length: > 0 } p || cmd.RegistryAction is not { Length: > 0 } a) return;
                if (host.Actions.Extensions is not { } registry) return;
                var binding = new SidebarActionBinding(p, a, cmd.RegistryTarget, null, null);
                registry.Execute(host.Actions, in binding);
                break;
            case Kind.Library:
                switch (cmd.Library)
                {
                    // Both ride the ONE create path / the ONE folder command set, so the palette can never disagree with
                    // the sidebar row menu about what these two verbs do.
                    case LibraryVerb.NewPlaylist: PlaylistCreateFlow.Create(host.Actions, default, navigate: true); break;
                    case LibraryVerb.NewFolder: FolderActions.NewFolder(host.Actions, null); break;
                }
                break;
            case Kind.CatalogSearch:
                host.Go("search", cmd.CatalogQuery ?? "");
                break;
        }
    }

    static Entry[] CreateBuiltins() =>
    [
        Nav("nav.home", Loc.Get(Strings.Nav.Home), Icons.Home, "home"),
        Nav("nav.search", Loc.Get(Strings.Nav.Search), Icons.Search, "search"),
        Nav("nav.library", Loc.Get(Strings.Nav.YourLibrary), Icons.MusicNote, "liked"),
        Nav("nav.recents", Loc.Get(Strings.Nav.Recents), Icons.Headphones, "recents"),
        Nav("nav.settings", "Settings", Icons.Settings, "settings"),
        Play("playback.playPause", Loc.Get(Strings.Detail.Play), Icons.Play, PlaybackVerb.PlayPause),
        Play("playback.next", Loc.Get(Strings.Player.Next), Icons.Next, PlaybackVerb.Next),
        Play("playback.previous", Loc.Get(Strings.Player.Previous), Icons.Previous, PlaybackVerb.Previous),
        Play("playback.shuffle", Loc.Get(Strings.Player.Shuffle), Icons.Shuffle, PlaybackVerb.Shuffle),
        Play("playback.repeat", Loc.Get(Strings.Player.Repeat), Icons.RepeatAll, PlaybackVerb.Repeat),
        Set("settings.theme", Loc.Get(Strings.Settings.Appearance.Theme), Icons.Brush, SettingsVerb.ToggleTheme),
        Set("settings.crossfade", Loc.Get(Strings.Settings.Sound.Crossfade), Icons.MusicNote, SettingsVerb.ToggleCrossfade),
        Lib("library.newPlaylist", Loc.Get(Strings.Detail.NewPlaylist), Icons.Add, LibraryVerb.NewPlaylist),
        Lib("library.newFolder", Loc.Get(Strings.Sidebar.CreateFolder), Icons.Folder, LibraryVerb.NewFolder),
    ];

    static Entry Nav(string id, string label, string glyph, string route) => new()
    {
        Id = id, Label = label, LabelLower = label.ToLowerInvariant(), Glyph = glyph,
        Kind = Kind.Navigate, RouteKey = route,
    };

    static Entry Play(string id, string label, string glyph, PlaybackVerb verb) => new()
    {
        Id = id, Label = label, LabelLower = label.ToLowerInvariant(), Glyph = glyph,
        Kind = Kind.Playback, Playback = verb,
    };

    static Entry Set(string id, string label, string glyph, SettingsVerb verb) => new()
    {
        Id = id, Label = label, LabelLower = label.ToLowerInvariant(), Glyph = glyph,
        Kind = Kind.Settings, Settings = verb,
    };

    static Entry Lib(string id, string label, string glyph, LibraryVerb verb) => new()
    {
        Id = id, Label = label, LabelLower = label.ToLowerInvariant(), Glyph = glyph,
        Kind = Kind.Library, Library = verb,
    };

    static bool PaletteTargetOf(WaveeActionDescriptor d, out SidebarActionTargetMode target)
    {
        var accepted = d.AcceptedTargets;
        if ((accepted & WaveeActionTargetModes.NowPlaying) != 0) { target = SidebarActionTargetMode.NowPlaying; return true; }
        if ((accepted & WaveeActionTargetModes.ActiveRoute) != 0) { target = SidebarActionTargetMode.ActiveRoute; return true; }
        if ((accepted & WaveeActionTargetModes.None) != 0) { target = SidebarActionTargetMode.None; return true; }
        target = SidebarActionTargetMode.None;
        return false;
    }

    static string ActionIdOf(string key)
    {
        int dot = key.IndexOf('.');
        return dot < 0 || dot == key.Length - 1 ? key : key.Substring(dot + 1);
    }

    static void InsertScored(Entry[] dest, Span<int> scores, ref int written, Entry e, int score)
    {
        if (written == MaxResults && score >= scores[written - 1]) return;
        int cap = written < MaxResults ? written : MaxResults - 1;
        int at = cap;
        while (at > 0 && scores[at - 1] > score) at--;
        int last = written < MaxResults ? written : MaxResults - 1;
        for (int i = last; i > at; i--)
        {
            dest[i] = dest[i - 1];
            scores[i] = scores[i - 1];
        }
        dest[at] = e;
        scores[at] = score;
        if (written < MaxResults) written++;
    }

    // Lower score = better. -1 = no match. Pre-lowercased haystack + needle.
    static int ScoreOf(string labelLower, string qLower)
    {
        int idx = labelLower.IndexOf(qLower, StringComparison.Ordinal);
        if (idx == 0) return 0;
        if (idx > 0) return 1;
        return Subsequence(labelLower, qLower) ? 2 : -1;
    }

    static bool Subsequence(string labelLower, string qLower)
    {
        int j = 0;
        for (int i = 0; i < labelLower.Length && j < qLower.Length; i++)
            if (labelLower[i] == qLower[j]) j++;
        return j == qLower.Length;
    }
}
