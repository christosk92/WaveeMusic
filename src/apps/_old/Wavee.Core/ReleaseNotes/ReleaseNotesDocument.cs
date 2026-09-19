using System;

namespace Wavee.Core.ReleaseNotes;

/// <summary>The per-release "What's new" document (<c>whatsnew.json</c>, schema 1): authored by hand, completed by
/// <c>Wavee.ReleaseTool validate</c>, shipped both inside the MSIX and as a release asset.
/// <para>Flat by design — no polymorphism, no enums on the wire, unknown members ignored — so one STJ source-generated
/// context (<see cref="ReleaseNotesJsonContext"/>) serves the tool and the app, AOT-clean. Every member is a real
/// <c>{ get; set; }</c> property because STJ source generation ignores fields.</para></summary>
public sealed class ReleaseNotesDocument
{
    public int Schema { get; set; } = 1;
    public string Product { get; set; } = "wavee";
    /// <summary>Semver as tagged (<c>wavee-v0.3.0</c>).</summary>
    public string Version { get; set; } = "";
    /// <summary>MSIX quad; exact match against the package identity version.</summary>
    public string PackageVersion { get; set; } = "";
    /// <summary>The codename (per MINOR).</summary>
    public string Name { get; set; } = "";
    public string Tagline { get; set; } = "";
    /// <summary>yyyy-MM-dd.</summary>
    public string Date { get; set; } = "";
    /// <summary>stable | beta.</summary>
    public string Channel { get; set; } = "stable";
    public string Lang { get; set; } = "en";
    public string MinOs { get; set; } = "10.0.19041.0";
    public string[] Arch { get; set; } = [];
    public ReleaseLinks Links { get; set; } = new();
    public ReleaseHighlight[] Highlights { get; set; } = [];
    public ReleaseSection[] Sections { get; set; } = [];
    public ReleaseNotice[] Notices { get; set; } = [];
    public ReleaseContributor[] Contributors { get; set; } = [];
    /// <summary>Issue-state snapshot time — the page's "as of".</summary>
    public string GeneratedAt { get; set; } = "";
    public ReleaseMedia[] Media { get; set; } = [];
    /// <summary>Commits in range that cite no section item's issue/PR — the "Other changes" appendix.</summary>
    public ReleaseCommit[] UnlinkedCommits { get; set; } = [];

    /// <summary>
    /// Replace every <see langword="null"/> the wire could have put here with the empty value the rest of the code
    /// assumes. The property initializers above only run for members the JSON does not mention — an explicit
    /// <c>"sections": null</c> (or <c>"name": null</c>) overwrites them with null, and every consumer then NREs on the
    /// first enumeration or <c>.Length</c>.
    /// <para>ONE chokepoint rather than a <c>?? []</c> per call site: the document is hand-authored, it is read by the
    /// app (page, dialog, toast) and by the release tool, and a defensive guard that has to be repeated at forty
    /// enumerations is a guard that will be missed at the forty-first. Call it immediately after every deserialize.
    /// Idempotent.</para>
    /// </summary>
    public void Normalize()
    {
        Product ??= "";
        Version ??= "";
        PackageVersion ??= "";
        Name ??= "";
        Tagline ??= "";
        Date ??= "";
        Channel ??= "";
        Lang ??= "";
        MinOs ??= "";
        GeneratedAt ??= "";
        Arch ??= [];
        Links ??= new ReleaseLinks();
        Links.Release ??= "";
        Links.Changelog ??= "";
        Links.Compare ??= "";

        Highlights = Compact(Highlights);
        foreach (var h in Highlights)
        {
            h.Id ??= "";
            h.Title ??= "";
            h.Body ??= "";
            h.Kind ??= "new";
            h.Issues ??= [];
            if (h.Media is { } hm)
            {
                hm.Kind ??= "image";
                hm.Src ??= "";
                hm.Alt ??= "";
            }
        }

        Sections = Compact(Sections);
        foreach (var s in Sections)
        {
            s.Kind ??= "";
            s.Items = Compact(s.Items);
            foreach (var item in s.Items)
            {
                item.Id ??= "";
                item.Text ??= "";
                item.Issues = Compact(item.Issues);
                foreach (var i in item.Issues) { i.Repo ??= ""; i.Title ??= ""; i.State ??= "open"; }
                item.Prs = Compact(item.Prs);
                foreach (var p in item.Prs) { p.Repo ??= ""; p.Title ??= ""; }
                item.Contributors = Compact(item.Contributors);
                foreach (var c in item.Contributors) c.Login ??= "";
                item.Commits = Compact(item.Commits);
                foreach (var c in item.Commits) NormalizeCommit(c);
            }
        }

        Notices = Compact(Notices);
        foreach (var n in Notices) { n.Kind ??= "info"; n.Text ??= ""; }

        Contributors = Compact(Contributors);
        foreach (var c in Contributors) c.Login ??= "";

        Media = Compact(Media);
        foreach (var m in Media) { m.Src ??= ""; m.Sha256 ??= ""; }

        UnlinkedCommits = Compact(UnlinkedCommits);
        foreach (var c in UnlinkedCommits) NormalizeCommit(c);
    }

    /// <summary>Repairs a <see cref="ReleaseCommit"/> read from the wire: null strings become <c>""</c>, null
    /// arrays become <c>[]</c>.</summary>
    static void NormalizeCommit(ReleaseCommit c)
    {
        c.Sha ??= "";
        c.Short ??= "";
        c.Subject ??= "";
        c.Issues ??= [];
        c.Prs ??= [];
    }

    /// <summary>A null array becomes empty, and a null ELEMENT inside one (<c>"sections": [null]</c> is legal JSON) is
    /// dropped — so a <c>foreach</c> over the result never sees null. The common case (no nulls) returns the input.</summary>
    static T[] Compact<T>(T[]? items) where T : class
    {
        if (items is null || items.Length == 0) return [];
        int nulls = 0;
        foreach (var item in items) if (item is null) nulls++;
        if (nulls == 0) return items;

        var kept = new T[items.Length - nulls];
        int n = 0;
        foreach (var item in items) if (item is not null) kept[n++] = item;
        return kept;
    }
}

public sealed class ReleaseLinks
{
    public string Release { get; set; } = "";
    public string Changelog { get; set; } = "";
    public string Compare { get; set; } = "";
}

public sealed class ReleaseHighlight
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>new | improved | rebuilt.</summary>
    public string Kind { get; set; } = "new";
    public ReleaseMediaRef? Media { get; set; }
    /// <summary>A <c>wavee://open?route=…</c> deep link, or null.</summary>
    public string? DeepLink { get; set; }
    public int[] Issues { get; set; } = [];
}

public sealed class ReleaseMediaRef
{
    /// <summary>image | video.</summary>
    public string Kind { get; set; } = "image";
    public string Src { get; set; } = "";
    public string? Poster { get; set; }
    public string Alt { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public long Bytes { get; set; }
}

public sealed class ReleaseSection
{
    /// <summary>added | changed | fixed | removed | deprecated | security | known.</summary>
    public string Kind { get; set; } = "added";
    public ReleaseItem[] Items { get; set; } = [];
}

public sealed class ReleaseItem
{
    /// <summary>Stable within the release — <c>"{kind}-{index}"</c> from <see cref="ChangelogParser"/>.</summary>
    public string Id { get; set; } = "";
    /// <summary>The optional leading "Player:" scope, rendered as a chip.</summary>
    public string? Scope { get; set; }
    /// <summary>Markdown-lite (see <see cref="MarkdownLite"/>) — bold/code/links/refs survive into the UI.</summary>
    public string Text { get; set; } = "";
    public ReleaseIssue[] Issues { get; set; } = [];
    public ReleasePr[] Prs { get; set; } = [];
    public ReleaseContributor[] Contributors { get; set; } = [];
    /// <summary>Commits (from <c>--commits</c>) whose issues/PRs intersect this item's <see cref="Issues"/>/<see cref="Prs"/>.</summary>
    public ReleaseCommit[] Commits { get; set; } = [];
}

public sealed class ReleaseIssue
{
    public string Repo { get; set; } = "";
    public int Number { get; set; }
    public string Title { get; set; } = "";
    /// <summary>open | closed.</summary>
    public string State { get; set; } = "open";
    /// <summary>completed | reopened | not_planned | duplicate | null.</summary>
    public string? StateReason { get; set; }
    public bool IsPullRequest { get; set; }
}

public sealed class ReleasePr
{
    public string Repo { get; set; } = "";
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public bool Merged { get; set; }
}

public sealed class ReleaseContributor
{
    public string Login { get; set; } = "";
    public bool FirstTime { get; set; }
}

/// <summary>One commit in <c>&lt;prevTag&gt;..HEAD</c>, as written by the release script to <c>commits.json</c>
/// and re-emitted on the wire so the app can link straight to it.</summary>
public sealed class ReleaseCommit
{
    /// <summary>40 hex.</summary>
    public string Sha { get; set; } = "";
    /// <summary>7-12 hex; derived from <see cref="Sha"/> when absent.</summary>
    public string Short { get; set; } = "";
    /// <summary>Verbatim commit subject; escaped at render time.</summary>
    public string Subject { get; set; } = "";
    /// <summary>Issue numbers closed by a closing-keyword trailer (<c>Fixes #n</c>, etc.).</summary>
    public int[] Issues { get; set; } = [];
    /// <summary>PR numbers named by a squash suffix <c>(#N)</c> or a <c>!N</c> reference.</summary>
    public int[] Prs { get; set; } = [];
}

public sealed class ReleaseNotice
{
    /// <summary>breaking | warning | info.</summary>
    public string Kind { get; set; } = "info";
    public string Text { get; set; } = "";
}

public sealed class ReleaseMedia
{
    public string Src { get; set; } = "";
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>The rolling <c>whatsnew-index.json</c> — every published release, newest first, capped by the tool.</summary>
public sealed class ReleaseNotesIndex
{
    public int Schema { get; set; } = 1;
    public string Product { get; set; } = "wavee";
    public ReleaseNotesIndexEntry[] Releases { get; set; } = [];

    /// <summary>Finds the entry for a quad ("0.3.0.17") or a semver ("0.3.0"). Ordinal; null when absent or the key is
    /// empty. The update service passes the FEED's root version (a quad); the UI passes a semver.</summary>
    public ReleaseNotesIndexEntry? Find(string quadOrSemver)
    {
        if (string.IsNullOrEmpty(quadOrSemver)) return null;
        var releases = Releases;
        if (releases is null) return null;
        foreach (var e in releases)
        {
            if (e is null) continue;
            if (string.Equals(e.PackageVersion, quadOrSemver, StringComparison.Ordinal)) return e;
            if (string.Equals(e.Version, quadOrSemver, StringComparison.Ordinal)) return e;
        }
        return null;
    }
}

public sealed class ReleaseNotesIndexEntry
{
    /// <summary>Semver ("0.3.0").</summary>
    public string Version { get; set; } = "";
    /// <summary>MSIX quad ("0.3.0.17").</summary>
    public string PackageVersion { get; set; } = "";
    /// <summary>Codename.</summary>
    public string Name { get; set; } = "";
    public string Date { get; set; } = "";
    /// <summary>stable | beta.</summary>
    public string Channel { get; set; } = "stable";
    /// <summary>Distinct issue numbers this release resolved, ascending.</summary>
    public int[] Issues { get; set; } = [];
}
