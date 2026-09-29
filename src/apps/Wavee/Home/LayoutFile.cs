// ── Home/LayoutFile.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// home-layout.json v2 — a FRESH document, reducer, wire and store for the rebuilt Home customizer. Same folder and
// same atomic-write contract as the OLD store the wavee-home-layout skill and Entities/Home.Host.cs describe
// (`%LOCALAPPDATA%\Wavee\WaveeMusic\home-layout.json`, a .tmp → File.Replace(...,.bak) write, NativeAOT-safe
// System.Text.Json source generation instead of reflection), but the document shape, the zone vocabulary and every
// type are new: no reference to HomeLayoutDoc/HomeModuleSpec/HomeLayoutReducer/HomeLayoutStore/HomeLayoutWire/
// HomePreferences (Entities/Home.Rules.cs §13-14, Entities/Home.Host.cs §1-2) — those were read only to confirm the
// path, the fault taxonomy and the write mechanics. v1 files are an ACCEPTED BREAK (the owner decision in the plan):
// read as version-mismatch and replaced with the v2 default, not preserved.
//
// Role: CORE
// Owner: A5
// Wave: 1
// Spec: docs/plans/wavee/home-redesign-implementation.md — Workstream H "Rebuilt from scratch" (LayoutFile) and
//   "Removals" (home-layout.json v2, zone kinds: daylist, recents, madeForYou, newMusic, releases, becauseYouLike,
//   jumpBackIn, radio, browse); .claude/skills/wavee/home-layout.md (the OLD store's persistence/fail-soft contract,
//   learned not copied).

using System.Text.Json;
using System.Text.Json.Serialization;
using FluentGpu.Hooks;
using FluentGpu.Signals;
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

// ══ 3. THE REDUCER — the only way a LayoutDoc changes ══════════════════════════════════════════════════════════════

/// <summary>Pure Home-layout mutations. <c>Move</c>'s <paramref name="toIndex"/> (below) is interpreted AFTER the
/// removal, the same `Reorderable.OnReorder` contract the sidebar and the old Home customizer both use.</summary>
public static class LayoutCommands
{
    /// <summary>Flip a zone's visibility. A zone the document has no entry for yet is implicitly visible, so toggling
    /// it appends an explicit <c>Visible: false</c> entry; toggling one back on just flips its existing entry.</summary>
    public static LayoutDoc Toggle(LayoutDoc doc, LayoutZone zone)
    {
        var zones = new List<LayoutEntry>(doc.Zones);
        int at = doc.IndexOf(zone);
        if (at < 0)
        {
            zones.Add(new LayoutEntry(LayoutZones.KindName(zone), false));
            return doc with { Zones = zones };
        }
        zones[at] = zones[at] with { Visible = !zones[at].Visible };
        return doc with { Zones = zones };
    }

    /// <summary>Reorder one entry. Out-of-range <paramref name="fromIndex"/> is a no-op; <paramref name="toIndex"/> is
    /// clamped and interpreted AFTER <paramref name="fromIndex"/> is removed.</summary>
    public static LayoutDoc Move(LayoutDoc doc, int fromIndex, int toIndex)
    {
        var zones = new List<LayoutEntry>(doc.Zones);
        if (fromIndex < 0 || fromIndex >= zones.Count) return doc;
        var moving = zones[fromIndex];
        zones.RemoveAt(fromIndex);
        int at = Math.Clamp(toIndex, 0, zones.Count);
        zones.Insert(at, moving);
        return doc with { Zones = zones };
    }

    /// <summary>Back to <see cref="LayoutFile.DefaultDoc"/>.</summary>
    public static LayoutDoc Reset() => LayoutFile.DefaultDoc();
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
    /// the file itself is left untouched (<see cref="LayoutStore.Commit"/> refuses to write over it).</summary>
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

// ══ 5. THE STORE — home-layout.json off the UI thread ══════════════════════════════════════════════════════════════

public enum LayoutSaveFault : byte { None = 0, IoFailure = 1 }

/// <summary>home-layout.json v2: load / fault-classify + an atomic write through a .tmp and a <c>File.Replace</c>
/// that rotates one .bak. Same folder, same mechanics as the old store (0.2.9 → 0.3's <c>HomeLayoutStore</c>):
/// <c>…\WaveeMusic\home-layout.json</c> under <see cref="Platform.LocalFolder"/>, beside sidebar-layout.json and
/// session.json. A sequence number drops a superseded in-flight write so only the newest snapshot ever lands.</summary>
public sealed class LayoutStore
{
    readonly string _path;
    readonly object _writeGate = new();
    long _seq;
    volatile bool _writesBlocked;
    volatile LayoutSaveFault _saveFault;
    Task? _pending;

    public LayoutStore(string path) => _path = path;

    public static LayoutStore ForApp() => new(DefaultPath());

    /// <summary><c>…\WaveeMusic\home-layout.json</c> under <see cref="Platform.LocalFolder"/>. Reading
    /// <c>LocalFolder</c> creates the profile folder, which is why a test uses <see cref="PathUnder"/> instead.</summary>
    public static string DefaultPath() => PathUnder(Platform.LocalFolder);

    /// <summary>The document's path under a profile root — the pure half of <see cref="DefaultPath"/>.</summary>
    public static string PathUnder(string profileRoot) => Path.Combine(profileRoot, "WaveeMusic", "home-layout.json");

    public string FilePath => _path;
    public string BakPath => _path + ".bak";
    public string TmpPath => _path + ".tmp";
    public bool WritesBlocked => _writesBlocked;
    public LayoutSaveFault SaveFault => _saveFault;

    /// <summary>No file ⇒ the v2 default with no fault (first run is not a fault). A file that reads with
    /// <see cref="LayoutReadFault.TooNew"/> or <see cref="LayoutReadFault.Malformed"/> blocks every write until a
    /// version this build understands lands on disk another way; <see cref="LayoutReadFault.LegacyVersion"/> (v1) does
    /// NOT block writes — it is the owner-accepted break, so the very next <see cref="Commit"/> just overwrites it.</summary>
    public LayoutReadResult Load()
    {
        if (!File.Exists(_path)) return new LayoutReadResult(LayoutFile.DefaultDoc(), LayoutReadFault.None);

        string json;
        try { json = File.ReadAllText(_path); }
        catch (Exception) { _writesBlocked = true; return new LayoutReadResult(LayoutFile.DefaultDoc(), LayoutReadFault.Malformed); }

        var result = LayoutWire.Read(json);
        if (result.Fault is LayoutReadFault.TooNew or LayoutReadFault.Malformed) _writesBlocked = true;
        return result;
    }

    /// <summary>Stamp <c>UpdatedAtMs</c> and write on the pool. A newer commit supersedes an older one that has not
    /// written yet; nothing writes while a load fault blocks writes.</summary>
    public void Commit(LayoutDoc doc)
    {
        if (doc is null || _writesBlocked) return;
        var snapshot = doc with { Version = LayoutDoc.CurrentVersion, UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };

        long mine = Interlocked.Increment(ref _seq);
        var task = Task.Run(() => WriteOnPool(snapshot, mine));
        lock (_writeGate) _pending = task;
    }

    void WriteOnPool(LayoutDoc snapshot, long mine)
    {
        if (Interlocked.Read(ref _seq) != mine) return;
        lock (_writeGate)
        {
            if (Interlocked.Read(ref _seq) != mine) return;
            if (_writesBlocked) return;
            try
            {
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(LayoutWire.Write(snapshot));

                using (var fs = new FileStream(TmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes);
                    fs.Flush(flushToDisk: true);
                }

                if (File.Exists(_path))
                {
                    try { File.Replace(TmpPath, _path, BakPath, ignoreMetadataErrors: true); }
                    catch (Exception)
                    {
                        try { File.Copy(_path, BakPath, overwrite: true); } catch (Exception) { }
                        File.Move(TmpPath, _path, overwrite: true);
                    }
                }
                else
                {
                    File.Move(TmpPath, _path, overwrite: true);
                }

                _saveFault = LayoutSaveFault.None;
            }
            catch (Exception ex)
            {
                _saveFault = LayoutSaveFault.IoFailure;
                Log.Warn("home", "home.layout.save_failed fault=io_failure exception_type=" + ex.GetType().Name);
                try { if (File.Exists(TmpPath)) File.Delete(TmpPath); } catch (Exception) { }
            }
        }
    }

    /// <summary>Block until the newest pending write finished, or the timeout. True when nothing is pending.</summary>
    public bool WaitForWrites(int timeoutMs = 5000)
    {
        Task? t;
        lock (_writeGate) t = _pending;
        if (t is null) return true;
        try { return t.Wait(timeoutMs); }
        catch (Exception) { return false; }
    }
}

// ══ 6. THE SERVICE — one live document, read by every screen (F29) ═════════════════════════════════════════════════

/// <summary>The one app-wide Home layout instance: a live <see cref="Signal{T}"/> of <see cref="LayoutDoc"/> plus the
/// single mutation path (<see cref="Dispatch"/>), backed by <see cref="LayoutStore"/>. Constructed once
/// (<see cref="Instance"/>, lazy — the same seam as <c>Sidebar.Store</c>) and read through <see cref="Slot"/> by both
/// <c>HomeScreen</c> (<c>Home/Screen.UI.cs</c>) and <c>CustomizeScreen</c> (<c>Home/Customize.UI.cs</c>), so a
/// customize edit is visible on Home the same frame it commits: no second private copy, no file I/O in
/// <c>Render()</c> (F29 — <c>LayoutStore.ForApp().Load()</c> used to run inside <c>HomeScreen.Render()</c> AND inside
/// the customizer's constructor, and the two never synced).</summary>
public sealed class HomeLayout
{
    readonly LayoutStore _store;

    /// <summary>The live document. Read <c>.Value</c> in <c>Render()</c> to subscribe to every future
    /// <see cref="Dispatch"/>; <c>.Peek()</c> from a mutator that must not itself subscribe.</summary>
    public readonly Signal<LayoutDoc> Doc;

    public HomeLayout(LayoutStore store)
    {
        _store = store;
        Doc = new Signal<LayoutDoc>(store.Load().Doc);
    }

    /// <summary>THE one mutation path: reduce the current document, publish the result, and commit it to disk
    /// (atomic .tmp → <see cref="LayoutStore.Commit"/>, off the UI thread). Every customizer action
    /// (<see cref="LayoutCommands.Toggle"/>/<see cref="LayoutCommands.Move"/>/<see cref="LayoutCommands.Reset"/>)
    /// goes through this — nothing ever writes <see cref="Doc"/> directly.</summary>
    public void Dispatch(Func<LayoutDoc, LayoutDoc> reduce)
    {
        var next = reduce(Doc.Peek());
        Doc.Value = next;
        _store.Commit(next);
    }

    /// <summary>The context slot both screens read via <c>UseRequiredContext(HomeLayout.Slot)</c>. No default value:
    /// a screen mounted without a provider is a wiring bug (unlike <c>FacetCtx</c>'s null-default soft-fail — Home
    /// cannot render without a layout document, so this fails loud instead of silently).</summary>
    public static readonly Context<HomeLayout> Slot = new(null!);

    /// <summary>The one process-lifetime instance, lazily constructed on first touch — the same seam as
    /// <c>Sidebar.Store</c> (resolving <see cref="LayoutStore.DefaultPath"/> reads <see cref="Platform.LocalFolder"/>,
    /// whose getter CREATES the profile folder, so an eager static field would do that the moment any Home static is
    /// touched — every headless test included — and would ignore a <c>--profile</c> set later in <c>Main</c>).</summary>
    public static HomeLayout Instance => s_instance ??= new HomeLayout(LayoutStore.ForApp());
    static HomeLayout? s_instance;

    /// <summary>Force <see cref="Instance"/> to materialize now — called once from <c>App.cs</c>, beside
    /// <c>Sidebar.Boot()</c>, so home-layout.json is read before the first Home render rather than on it.</summary>
    public static void Boot() => _ = Instance;

    /// <summary>Point the service at another instance — a test (a temp-file-backed store) or a host with its own
    /// profile. Call before anything reads <see cref="Instance"/> or <see cref="Slot"/>: the document already loaded
    /// is not re-read.</summary>
    public static void UseInstance(HomeLayout instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        s_instance = instance;
    }
}
