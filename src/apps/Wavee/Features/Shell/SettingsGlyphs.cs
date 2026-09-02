using System;
using FluentGpu.Controls;

namespace Wavee;

/// <summary>Resolves a <see cref="SettingsCatalog"/> glyph NAME (a glyphs.json key, e.g. "Globe") to the matching
/// <see cref="Icons"/> constant. Split out of <see cref="SettingsCatalog"/> because <c>Icons</c> lives in
/// <c>FluentGpu.Controls</c>, which <c>Wavee.Tests</c> does not reference — the catalog itself must stay engine-free so
/// <c>SettingsCatalogTests</c> can assert the no-repeat invariant on plain data.
///
/// <para>Every arm is a name actually used by <see cref="SettingsCatalog"/>. A name the catalog adds without a matching
/// arm here THROWS on first render — in a Debug build that is exactly the tab painting the offending row, not a silent
/// blank glyph — which is the whole point of resolving by name instead of letting the page reference <c>Icons.*</c>
/// directly: the catalog's uniqueness test is only "real" if the page actually renders what the catalog says.</para></summary>
static class SettingsGlyphs
{
    public static string Resolve(string name) => name switch
    {
        "Globe" => Icons.Globe,
        "Link" => Icons.Link,
        "Devices" => Icons.Devices,
        "Device" => Icons.Device,
        "Code" => Icons.Code,
        "LocaleLanguage" => Icons.LocaleLanguage,
        "OpenInNewWindow" => Icons.OpenInNewWindow,
        "Settings" => Icons.Settings,
        "Clock" => Icons.Clock,
        "Document" => Icons.Document,
        "Refresh" => Icons.Refresh,
        "Brush" => Icons.Brush,
        "Sun" => Icons.Sun,
        "Zoom" => Icons.Zoom,
        "Font" => Icons.Font,
        "Design" => Icons.Design,
        "List" => Icons.List,
        "RowSize" => Icons.RowSize,
        "Picture" => Icons.Picture,
        "ViewList" => Icons.ViewList,
        "DockLeft" => Icons.DockLeft,
        "SplitView" => Icons.SplitView,
        "Edit" => Icons.Edit,
        "Microphone" => Icons.Microphone,
        "RefineSparkle" => Icons.RefineSparkle,
        "MusicNote" => Icons.MusicNote,
        "Headphones" => Icons.Headphones,
        "RadioTower" => Icons.RadioTower,
        "Volume" => Icons.Volume,
        "Play" => Icons.Play,
        "Speakers" => Icons.Speakers,
        "Equalizer" => Icons.Equalizer,
        "Audio" => Icons.Audio,
        "Movie" => Icons.Movie,
        "TvMonitor" => Icons.TvMonitor,
        "Pin" => Icons.Pin,
        "ThisPc" => Icons.ThisPc,
        "Album" => Icons.Album,
        "Folder" => Icons.Folder,
        "FolderOpen" => Icons.FolderOpen,
        "Tag" => Icons.Tag,
        "Delete" => Icons.Delete,
        "Attention" => Icons.Attention,
        "Download" => Icons.Download,
        _ => Fallback(name),
    };

    /// <summary>An unmapped catalog name must never take the app down (it did: a Release build crashed on the Storage
    /// tab over a missing "Download" arm). Debug builds fail loudly at the offending row; Release logs once per name
    /// and paints the generic settings glyph so the page still renders.</summary>
    static string Fallback(string name)
    {
        System.Diagnostics.Debug.Fail($"SettingsGlyphs: no Icons.* mapping for glyph name '{name}'.");
        if (s_warned.Add(name))
            WaveeLog.Instance.Warn("settings", "settings.glyph.unmapped", "Settings glyph name has no Icons mapping; using the default glyph",
                WaveeLogField.Of("name", name));
        return Icons.Settings;
    }

    static readonly System.Collections.Generic.HashSet<string> s_warned = new(StringComparer.Ordinal);

    /// <summary>The section-header glyph for one <see cref="SettingsCatalog"/> section — <c>SettingsGlyphs.Section</c>
    /// reads better at the call site than <c>Resolve(SettingsCatalog.SectionGlyph(...))</c> repeated forty times.</summary>
    public static string Section(SettingsTab tab, string title) => Resolve(SettingsCatalog.SectionGlyph(tab, title));

    /// <summary>The row glyph for one <see cref="SettingsCatalog"/> row, by its tab-unique <c>RowId</c>.</summary>
    public static string Row(SettingsTab tab, string rowId) => Resolve(SettingsCatalog.RowGlyph(tab, rowId));
}
