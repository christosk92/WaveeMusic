// ── Home/Board/HomeIdentLog.cs: Phase 0's section-identity log (pinned-home design 2.6 step 5) ─────────────────────
//
// Every fresh Home answer upserts one line per section, (day, facet, index, key, fp, family, template, reason), into
// `home-ident.json` in the profile: 14 days, capped, local, never sent. Passive, so it runs in a verify instance and in
// the owner's own Wavee with no probe. The reading (`Readings`) says, per section family, how often the KEY of a day's
// section was in the previous day's answer for that facet and how often its FINGERPRINT was: the numbers behind
// `SectionIdentity.KeyFirst`.
//
// A section's fingerprint folds its dominant card kind, and the cards land a moment after the section row: the line of
// (day, facet, key) is therefore UPDATED by later reads of the same answer, never duplicated. A `--fake` run without a
// profile of its own (the default demo, which would otherwise write into the owner's folder) logs nothing.

using System.Text.Json;
using System.Text.Json.Serialization;
using Wavee.HomeUi;

namespace Wavee;

public static class HomeIdentLog
{
    public const string FileName = "home-ident.json";
    const int KeepDays = 14, MaxEntries = 6000;

    static readonly object s_gate = new();
    static List<IdentEntryDto>? s_entries;
    static bool s_dirty, s_writeQueued;

    /// <summary>Where the log lives; null when this run logs nothing.</summary>
    public static string? PathOrNull
        => Platform.Args.Fake && Platform.ProfileRoot.Length == 0 ? null : System.IO.Path.Combine(Platform.LocalFolder, FileName);

    /// <summary>One answer of <paramref name="facet"/> ("" = All), in server order. Cheap when nothing changed.</summary>
    public static void Note(string facet, IReadOnlyList<SectionInput> answer, long nowMs)
    {
        if (answer.Count == 0 || PathOrNull is not { } path) return;
        string day = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var plan = SectionChooser.Plan(answer, nowMs);
        lock (s_gate)
        {
            var entries = Load(path);
            bool changed = false;
            foreach (var p in plan)
            {
                string fp = p.Fp.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
                string tpl = p.Template.ToString();
                string reason = p.Reason + " [cards " + p.Usable + "/" + p.Cards + ", unsupported " + p.Unsupported + ", duplicates " + p.Duplicates + "]";
                int at = entries.FindIndex(e => e.Day == day && e.Facet == facet && e.Key == p.Key);
                if (at >= 0)
                {
                    var e = entries[at];
                    if (e.Fp == fp && e.Index == p.Index && e.Template == tpl && e.Reason == reason) continue;
                    e.Fp = fp; e.Index = p.Index; e.Template = tpl; e.Family = p.Family; e.Reason = reason;
                }
                else entries.Add(new IdentEntryDto { Day = day, Facet = facet, Index = p.Index, Key = p.Key, Fp = fp, Family = p.Family, Template = tpl, Reason = reason });
                changed = true;
            }
            if (!changed) return;
            Trim(entries, day);
            s_dirty = true;
            if (s_writeQueued) return;
            s_writeQueued = true;
        }
        // One write per burst of reads (a feed's cards and previews land over a few frames), off the UI thread.
        _ = Task.Delay(1000).ContinueWith(_ => Flush(path), TaskScheduler.Default);
    }

    /// <summary>Per family: answers seen, and of the sections of a day that had a previous day for the same facet, how
    /// many kept their key and how many kept their fingerprint.</summary>
    public sealed record FamilyReading(string Family, int Lines, int Pairs, int KeyHeld, int FpHeld);

    public static IReadOnlyList<FamilyReading> Readings()
    {
        if (PathOrNull is not { } path) return [];
        List<IdentEntryDto> entries;
        lock (s_gate) entries = [.. Load(path)];
        var byFamily = new Dictionary<string, (int Lines, int Pairs, int Key, int Fp)>(StringComparer.Ordinal);
        var days = entries.Select(e => (e.Facet, e.Day)).Distinct().ToList();
        foreach (var e in entries)
        {
            var r = byFamily.GetValueOrDefault(e.Family);
            r.Lines++;
            string? prev = days.Where(d => d.Facet == e.Facet && string.CompareOrdinal(d.Day, e.Day) < 0)
                .Select(d => d.Day).OrderByDescending(d => d, StringComparer.Ordinal).FirstOrDefault();
            if (prev is not null)
            {
                r.Pairs++;
                if (entries.Any(x => x.Day == prev && x.Facet == e.Facet && x.Key == e.Key)) r.Key++;
                if (entries.Any(x => x.Day == prev && x.Facet == e.Facet && x.Fp == e.Fp)) r.Fp++;
            }
            byFamily[e.Family] = r;
        }
        return byFamily.Select(kv => new FamilyReading(kv.Key, kv.Value.Lines, kv.Value.Pairs, kv.Value.Key, kv.Value.Fp))
            .OrderBy(r => r.Family, StringComparer.Ordinal).ToList();
    }

    /// <summary>The log's lines as they stand (the planner dump prints them).</summary>
    public static IReadOnlyList<IdentEntryDto> Lines()
    {
        if (PathOrNull is not { } path) return [];
        lock (s_gate) return [.. Load(path)];
    }

    static List<IdentEntryDto> Load(string path)
    {
        if (s_entries is not null) return s_entries;
        s_entries = [];
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), IdentJsonCtx.Default.IdentFileDto) is { Entries: { } read })
                s_entries.AddRange(read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn("home", "home-ident.json unreadable, starting a new log: " + ex.Message);
        }
        return s_entries;
    }

    static void Trim(List<IdentEntryDto> entries, string today)
    {
        string oldest = DateTime.ParseExact(today, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            .AddDays(-(KeepDays - 1)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        entries.RemoveAll(e => string.CompareOrdinal(e.Day, oldest) < 0);
        if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
    }

    static void Flush(string path)
    {
        string json;
        lock (s_gate)
        {
            s_writeQueued = false;
            if (!s_dirty || s_entries is null) return;
            s_dirty = false;
            json = JsonSerializer.Serialize(new IdentFileDto { Version = 1, Entries = [.. s_entries] }, IdentJsonCtx.Default.IdentFileDto);
        }
        try
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("home", "home-ident.json not written: " + ex.Message);
        }
    }
}

public sealed class IdentEntryDto
{
    public string Day { get; set; } = "";
    public string Facet { get; set; } = "";
    public int Index { get; set; }
    public string Key { get; set; } = "";
    public string Fp { get; set; } = "";
    public string Family { get; set; } = "";
    public string Template { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class IdentFileDto
{
    public int Version { get; set; }
    public IdentEntryDto[]? Entries { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(IdentFileDto))]
sealed partial class IdentJsonCtx : JsonSerializerContext { }
