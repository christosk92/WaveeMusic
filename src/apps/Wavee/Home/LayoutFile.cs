// ── Home/LayoutFile.cs: home-layout.json v2, READ ONLY ────────────────────────────────────────────────────────────────
// The pre-board Home layout (a zone order and per-zone visibility). The pinned Home replaced it (Home/Board/HomeDoc*.cs);
// what is left is the v2 vocabulary, document and reader that HomeMigration reads ONCE per account to carry hidden zones
// into the v3 document. Nothing writes this file any more; it is left in place and never read again after the migration.

using System.Text.Json;
using System.Text.Json.Serialization;
using Wavee;   // Platform.LocalFolder, Log (Platform/Platform.Settings.cs, Platform/Platform.Host.cs)

namespace Wavee.HomeUi;

// ══ 1. THE ZONE VOCABULARY — what home-layout.json v2 can toggle/reorder ═══════════════════════════════════════════

/// <summary>A customizable Home zone IDENTITY, persisted by name — append only, never rename, never renumber.
/// Distinct from <c>ZoneKind</c> (Home/Model.cs): several <see cref="LayoutZone"/> values can render as the same
/// <c>ZoneKind</c> (e.g. <c>MadeForYou</c> and <c>NewMusic</c> can both be a <c>ZoneKind.CoverShelf</c>
/// / <c>WideTiles</c> shape) — this enum is what the user's document toggles and reorders, not how a zone paints.</summary>
public enum LayoutZone : byte
{
    Daylist, Recents, MadeForYou, NewMusic, Releases, BecauseYouLike, JumpBackIn, Radio, Browse,
}

/// <summary>Wire kind strings (persisted — never rename one) and the default order, in one place so the reducer, the
/// wire and the customizer UI cannot drift apart.</summary>
public static class LayoutZones
{
    /// <summary>The v2 default order — every zone visible, first-run and Reset land here.</summary>
    public static readonly LayoutZone[] DefaultOrder =
    [
        LayoutZone.Daylist, LayoutZone.Recents, LayoutZone.MadeForYou, LayoutZone.NewMusic,
        LayoutZone.Releases, LayoutZone.BecauseYouLike, LayoutZone.JumpBackIn, LayoutZone.Radio, LayoutZone.Browse,
    ];

    public static string KindName(LayoutZone zone) => zone switch
    {
        LayoutZone.Daylist => "daylist",
        LayoutZone.Recents => "recents",
        LayoutZone.MadeForYou => "madeForYou",
        LayoutZone.NewMusic => "newMusic",
        LayoutZone.Releases => "releases",
        LayoutZone.BecauseYouLike => "becauseYouLike",
        LayoutZone.JumpBackIn => "jumpBackIn",
        LayoutZone.Radio => "radio",
        LayoutZone.Browse => "browse",
        _ => "browse",
    };

    /// <summary>Parse a wire kind string. False for anything this build does not know — the caller DROPS it (v2 has
    /// no forward-compat carry; a future zone this build predates simply does not show in the customizer).</summary>
    public static bool TryParse(string? s, out LayoutZone zone)
    {
        switch (s)
        {
            case "daylist": zone = LayoutZone.Daylist; return true;
            case "recents": zone = LayoutZone.Recents; return true;
            case "madeForYou": zone = LayoutZone.MadeForYou; return true;
            case "newMusic": zone = LayoutZone.NewMusic; return true;
            case "releases": zone = LayoutZone.Releases; return true;
            case "becauseYouLike": zone = LayoutZone.BecauseYouLike; return true;
            case "jumpBackIn": zone = LayoutZone.JumpBackIn; return true;
            case "radio": zone = LayoutZone.Radio; return true;
            case "browse": zone = LayoutZone.Browse; return true;
            default: zone = default; return false;
        }
    }
}

// ══ 2. THE DOCUMENT ═════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>One zone's authored state: its wire kind name and whether it is shown. <paramref name="Visible"/> (not
/// "Hidden") is the v2 polarity on purpose — a document with no entry for a zone means visible, so
/// <c>LayoutEntry(Kind, Visible: false)</c> is the only shape that ever needs to be written for a default-visible
/// zone the user turned off.</summary>
public sealed record LayoutEntry(string Kind, bool Visible);

/// <summary>What home-layout.json v2 carries: the zones in user order, each visible or not.</summary>
public sealed record LayoutDoc(int Version, IReadOnlyList<LayoutEntry> Zones, long UpdatedAtMs)
{
    public const int CurrentVersion = 2;

    /// <summary>True when <paramref name="zone"/> is authored hidden. A zone the document never mentions is visible
    /// — used by the planner (Home/ZonePlanner.cs, A1) to drop a zone before layout, same as the old
    /// <c>HomeLayoutDoc.IsHidden</c>.</summary>
    public bool IsHidden(LayoutZone zone)
    {
        string name = LayoutZones.KindName(zone);
        for (int i = 0; i < Zones.Count; i++)
            if (string.Equals(Zones[i].Kind, name, StringComparison.Ordinal)) return !Zones[i].Visible;
        return false;
    }

    public int IndexOf(LayoutZone zone)
    {
        string name = LayoutZones.KindName(zone);
        for (int i = 0; i < Zones.Count; i++)
            if (string.Equals(Zones[i].Kind, name, StringComparison.Ordinal)) return i;
        return -1;
    }
}

public static class LayoutFile
{
    /// <summary>Every zone visible, in <see cref="LayoutZones.DefaultOrder"/>. First run, a v1 file, and Reset all
    /// land here.</summary>
    public static LayoutDoc DefaultDoc()
    {
        var zones = new LayoutEntry[LayoutZones.DefaultOrder.Length];
        for (int i = 0; i < zones.Length; i++) zones[i] = new LayoutEntry(LayoutZones.KindName(LayoutZones.DefaultOrder[i]), true);
        return new LayoutDoc(LayoutDoc.CurrentVersion, zones, 0);
    }
}

// ══ 4. THE WIRE ═════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Why <see cref="LayoutWire.Read"/> did not return the file's own document. <see cref="None"/> covers both
/// "no file" and "a good v2 file" — see <see cref="LayoutReadResult"/>.</summary>
public enum LayoutReadFault : byte
{
    None = 0,
    /// <summary>v1, or any version below <see cref="LayoutDoc.CurrentVersion"/> — the owner-accepted break: read as
    /// the v2 default, no recovery attempted, the file is simply overwritten on the next save.</summary>
    LegacyVersion = 1,
    /// <summary>A version this build does not understand yet (v3+) — fail-soft: the default is used IN MEMORY but
    /// the file itself is left untouched.</summary>
    TooNew = 2,
    /// <summary>Unparsable JSON / no document — fail-soft, same as <see cref="TooNew"/>.</summary>
    Malformed = 3,
}

public readonly record struct LayoutReadResult(LayoutDoc Doc, LayoutReadFault Fault);

sealed class LayoutEntryDto
{
    public string? Kind { get; set; }
    public bool? Visible { get; set; }
}

sealed class LayoutDocDto
{
    public int Version { get; set; }
    public long UpdatedAtMs { get; set; }
    public LayoutEntryDto[]? Zones { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(LayoutDocDto))]
sealed partial class LayoutJsonCtx : JsonSerializerContext { }

public static class LayoutWire
{
    /// <summary>Parse home-layout.json. Missing/empty text, v1-and-below, too-new and malformed all resolve to the
    /// v2 default doc (in memory) plus the fault that explains why — see <see cref="LayoutReadFault"/> for which
    /// faults the store then refuses to overwrite the file for. A known v2 zone kind the document never mentions is
    /// appended visible (preserve-don't-destroy for a zone added to this build after the file was written); a zone
    /// kind this build does not recognize is silently dropped (v2 has no forward-compat carry).</summary>
    public static LayoutReadResult Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new LayoutReadResult(LayoutFile.DefaultDoc(), LayoutReadFault.None);

        LayoutDocDto? dto;
        try { dto = JsonSerializer.Deserialize(json, LayoutJsonCtx.Default.LayoutDocDto); }
        catch (Exception) { return new LayoutReadResult(LayoutFile.DefaultDoc(), LayoutReadFault.Malformed); }
        if (dto is null) return new LayoutReadResult(LayoutFile.DefaultDoc(), LayoutReadFault.Malformed);

        if (dto.Version < LayoutDoc.CurrentVersion)
            return new LayoutReadResult(LayoutFile.DefaultDoc(), dto.Version <= 0 ? LayoutReadFault.Malformed : LayoutReadFault.LegacyVersion);
        if (dto.Version > LayoutDoc.CurrentVersion)
            return new LayoutReadResult(LayoutFile.DefaultDoc(), LayoutReadFault.TooNew);

        var zones = new List<LayoutEntry>(dto.Zones?.Length ?? 0);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (dto.Zones is { } raw)
            for (int i = 0; i < raw.Length; i++)
            {
                var z = raw[i];
                if (z?.Kind is not { Length: > 0 } name) continue;
                if (!LayoutZones.TryParse(name, out _)) continue;   // unknown kind: dropped, not carried
                if (!seen.Add(name)) continue;                       // duplicate: first wins
                zones.Add(new LayoutEntry(name, z.Visible ?? true));
            }

        var defaults = LayoutZones.DefaultOrder;
        for (int i = 0; i < defaults.Length; i++)
        {
            string name = LayoutZones.KindName(defaults[i]);
            if (seen.Add(name)) zones.Add(new LayoutEntry(name, true));
        }

        return new LayoutReadResult(new LayoutDoc(LayoutDoc.CurrentVersion, zones, dto.UpdatedAtMs), LayoutReadFault.None);
    }

    public static string Write(LayoutDoc doc)
    {
        var zones = new LayoutEntryDto[doc.Zones.Count];
        for (int i = 0; i < zones.Length; i++)
            zones[i] = new LayoutEntryDto { Kind = doc.Zones[i].Kind, Visible = doc.Zones[i].Visible ? null : false };

        var dto = new LayoutDocDto { Version = LayoutDoc.CurrentVersion, UpdatedAtMs = doc.UpdatedAtMs, Zones = zones };
        return JsonSerializer.Serialize(dto, LayoutJsonCtx.Default.LayoutDocDto);
    }
}
