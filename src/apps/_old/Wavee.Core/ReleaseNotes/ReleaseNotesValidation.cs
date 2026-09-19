using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Wavee.Core.ReleaseNotes;

/// <summary>
/// The pure, engine-free half of the release-notes pipeline: media budget rules, the deep-link rule, the
/// index merge, the GitHub release body, and the store-listing text.
/// </summary>
/// <remarks>
/// This lives in <c>Wavee.Core</c> (not in the release tool) for one reason: it is the part worth unit-testing.
/// <c>Wavee.ReleaseTool</c>'s <c>Validator</c> is a thin shell that reads files, calls the GitHub API and calls
/// into here. Nothing in here touches the network, the clock, or the engine — only the file system, and only for
/// sizes/hashes/copies of media the caller names.
/// </remarks>
public static class ReleaseNotesValidation
{
    /// <summary>Per-file cap for a still (webp/png/jpg).</summary>
    public const long MaxStillBytes = 150_000;

    /// <summary>Per-file cap for motion (mp4, or a webp declared <c>kind: "video"</c>).</summary>
    public const long MaxMotionBytes = 600_000;

    /// <summary>Cap on every media file of one release added together.</summary>
    public const long MaxTotalBytes = 1_500_000;

    /// <summary>How many releases <c>whatsnew-index.json</c> carries (newest first).</summary>
    public const int MaxIndexEntries = 12;

    /// <summary>Hard cap on <c>store-listing.txt</c>.</summary>
    public const int StoreListingMaxChars = 1500;

    /// <summary>Every highlight deep link must be an in-app route, not an arbitrary URI.</summary>
    public const string DeepLinkPrefix = "wavee://open?route=";

    /// <summary>The tag a release is published under; also the folder its assets hang off.</summary>
    public const string TagPrefix = "wavee-v";

    static readonly string[] AllowedExtensions = [".webp", ".png", ".jpg", ".jpeg", ".mp4"];

    /// <summary>One media file referenced by the document, and whether it is billed against the motion budget.</summary>
    public readonly record struct ReleaseMediaEntry(string Src, bool Motion);

    /// <summary>`.mp4`, or anything a highlight declares as <c>kind: "video"</c> (an animated webp).</summary>
    public static bool IsMotionMedia(string? kind, string src)
        => string.Equals(kind, "video", StringComparison.OrdinalIgnoreCase)
           || src.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

    /// <summary>The per-file budget an entry is measured against.</summary>
    public static long MaxBytesFor(bool motion) => motion ? MaxMotionBytes : MaxStillBytes;

    /// <summary>
    /// Every media source the document references, in document order, de-duplicated by path. A poster is always
    /// billed as a still even when its highlight is a video.
    /// </summary>
    public static IReadOnlyList<ReleaseMediaEntry> MediaEntries(ReleaseNotesDocument doc)
    {
        // The document is hand-authored JSON: an explicit "highlights": null (or a null element inside the array) is
        // legal on the wire and would NRE the enumeration below. Normalize is the ONE owner of that repair and is
        // idempotent, so calling it at each public entry point costs nothing on an already-clean document.
        ArgumentNullException.ThrowIfNull(doc);
        doc.Normalize();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ReleaseMediaEntry>();
        foreach (var h in doc.Highlights)
        {
            if (h.Media is not { } m) continue;
            if (!string.IsNullOrWhiteSpace(m.Src) && seen.Add(m.Src))
                list.Add(new ReleaseMediaEntry(m.Src, IsMotionMedia(m.Kind, m.Src)));
            if (!string.IsNullOrWhiteSpace(m.Poster) && seen.Add(m.Poster!))
                list.Add(new ReleaseMediaEntry(m.Poster!, false));
        }
        return list;
    }

    /// <summary>The one folder a media reference may name. <see cref="CopyMedia"/> FLATTENS every reference into
    /// <c>&lt;out&gt;/media/&lt;basename&gt;</c>, and the app resolves a reference verbatim under the notes root
    /// (<c>ReleaseNotesStore.MediaPath</c> → <c>&lt;embeddedRoot&gt;/media/&lt;basename&gt;</c>), so those two only
    /// agree when the document says <c>media/&lt;basename&gt;</c> and nothing else.</summary>
    public const string MediaFolder = "media";

    /// <summary>Is <paramref name="src"/> exactly <c>media/&lt;basename&gt;</c> — the one shape that survives the
    /// publish? A bare basename, a second folder, a nested path or a backslash separator all fail.</summary>
    public static bool IsPublishableMediaPath(string? src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        int slash = src.IndexOf('/');
        if (slash <= 0 || slash == src.Length - 1) return false;               // no folder, or nothing after it
        if (!src.AsSpan(0, slash).SequenceEqual(MediaFolder)) return false;    // some other folder
        var name = src.AsSpan(slash + 1);
        return name.IndexOf('/') < 0 && name.IndexOf('\\') < 0;               // exactly one level deep
    }

    /// <summary>
    /// The media rules: every reference is exactly <c>media/&lt;basename&gt;</c> and resolves inside
    /// <paramref name="notesDir"/>, no GIF, only webp/png/jpg/mp4, ≤150 KB per still, ≤600 KB per motion file,
    /// ≤1.5 MB in total, and no two distinct sources sharing a basename (release assets are flat, so a shared
    /// basename would collide).
    /// </summary>
    /// <returns>Human-readable errors; empty when the document passes.</returns>
    public static IReadOnlyList<string> ValidateMedia(ReleaseNotesDocument doc, string notesDir)
    {
        var errors = new List<string>();
        var basenames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;

        foreach (var (src, motion) in MediaEntries(doc))
        {
            if (!IsSafeRelativePath(src))
            {
                errors.Add($"media path must be relative and stay inside the notes folder: {src}");
                continue;
            }

            // Not a `continue`: a document that puts its files in the wrong folder should still hear about the size,
            // type and basename problems in the same run rather than one rule per fix-and-rerun cycle.
            if (!IsPublishableMediaPath(src))
                errors.Add($"media must be referenced as '{MediaFolder}/{Path.GetFileName(src)}', not '{src}' — "
                    + $"publishing FLATTENS every reference into <release>/{MediaFolder}/<basename> and the app resolves "
                    + $"it back as <notes root>/{MediaFolder}/<basename>, so any other folder (or a bare file name) is a "
                    + "poster that validates here and then renders as an empty band on the user's machine");

            string ext = Path.GetExtension(src);
            if (ext.Equals(".gif", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"GIF is not allowed (use webp or mp4): {src}");
                continue;
            }
            if (Array.FindIndex(AllowedExtensions, e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)) < 0)
            {
                errors.Add($"unsupported media type '{ext}' (allowed: {string.Join(", ", AllowedExtensions)}): {src}");
                continue;
            }

            string full = Path.Combine(notesDir, src);
            if (!File.Exists(full))
            {
                errors.Add($"missing media file: {src}");
                continue;
            }

            string basename = Path.GetFileName(src);
            if (basenames.TryGetValue(basename, out string? other))
                errors.Add($"duplicate media basename '{basename}': {other} and {src}");
            else
                basenames[basename] = src;

            long len = new FileInfo(full).Length;
            total += len;
            long cap = MaxBytesFor(motion);
            if (len > cap)
                errors.Add($"media too large: {src} is {len} bytes, cap is {cap} ({(motion ? "motion" : "still")})");
        }

        if (total > MaxTotalBytes)
            errors.Add($"media total {total} bytes exceeds the {MaxTotalBytes} byte budget");
        return errors;
    }

    /// <summary>Highlight deep links must open an in-app route: <c>wavee://open?route=…</c>.</summary>
    public static IReadOnlyList<string> ValidateDeepLinks(ReleaseNotesDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        doc.Normalize();
        var errors = new List<string>();
        foreach (var h in doc.Highlights)
        {
            if (h.DeepLink is not { Length: > 0 } dl) continue;
            if (!dl.StartsWith(DeepLinkPrefix, StringComparison.Ordinal))
                errors.Add($"deep link must start with '{DeepLinkPrefix}': {dl}");
            else if (dl.Length == DeepLinkPrefix.Length)
                errors.Add($"deep link names no route: {dl}");
        }
        return errors;
    }

    /// <summary>Size + SHA-256 for every media file, in document order. Missing files are skipped (validation reports them).</summary>
    public static ReleaseMedia[] MediaHashes(ReleaseNotesDocument doc, string notesDir)
    {
        var entries = MediaEntries(doc);
        var list = new List<ReleaseMedia>(entries.Count);
        foreach (var (src, _) in entries)
        {
            if (!IsSafeRelativePath(src)) continue;
            string full = Path.Combine(notesDir, src);
            if (!File.Exists(full)) continue;
            using var fs = File.OpenRead(full);
            byte[] hash = SHA256.HashData(fs);
            list.Add(new ReleaseMedia { Src = src, Bytes = new FileInfo(full).Length, Sha256 = Convert.ToHexStringLower(hash) });
        }
        return list.ToArray();
    }

    /// <summary>Copies every referenced media file into <c>&lt;outDir&gt;/media/</c> (flat, by basename).</summary>
    /// <returns>The number of files copied.</returns>
    public static int CopyMedia(ReleaseNotesDocument doc, string notesDir, string outDir)
    {
        int copied = 0;
        foreach (var (src, _) in MediaEntries(doc))
        {
            if (!IsSafeRelativePath(src)) continue;
            string full = Path.Combine(notesDir, src);
            if (!File.Exists(full)) continue;
            string dest = Path.Combine(outDir, "media", Path.GetFileName(src));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(full, dest, overwrite: true);
            copied++;
        }
        return copied;
    }

    /// <summary>
    /// This release prepended to the previous index, newest first, capped at <see cref="MaxIndexEntries"/>.
    /// An existing entry for the same version is replaced, not duplicated.
    /// </summary>
    public static ReleaseNotesIndex MergeIndex(ReleaseNotesIndex? previous, ReleaseNotesDocument doc)
    {
        var head = new ReleaseNotesIndexEntry
        {
            Version = doc.Version,
            PackageVersion = doc.PackageVersion,
            Name = doc.Name,
            Date = doc.Date,
            Channel = doc.Channel,
            Issues = IssueNumbers(doc),
        };
        var list = new List<ReleaseNotesIndexEntry>(MaxIndexEntries) { head };
        if (previous?.Releases is { } prev)
        {
            foreach (var e in prev)
            {
                if (list.Count >= MaxIndexEntries) break;
                if (string.Equals(e.Version, doc.Version, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(e);
            }
        }
        return new ReleaseNotesIndex
        {
            Schema = 1,
            Product = string.IsNullOrWhiteSpace(doc.Product) ? "wavee" : doc.Product,
            Releases = list.ToArray(),
        };
    }

    /// <summary>Section heading for a Keep-a-Changelog kind (<c>known</c> → "Known limitations").</summary>
    public static string SectionTitle(string kind) => kind switch
    {
        "added" => "Added",
        "changed" => "Changed",
        "deprecated" => "Deprecated",
        "removed" => "Removed",
        "fixed" => "Fixed",
        "security" => "Security",
        "known" => "Known limitations",
        _ => kind.Length == 0 ? "Other" : char.ToUpperInvariant(kind[0]) + kind[1..],
    };

    /// <summary>Badge word for a highlight kind (<c>new|improved|rebuilt</c>).</summary>
    public static string HighlightKindLabel(string kind) => kind switch
    {
        "new" => "New",
        "improved" => "Improved",
        "rebuilt" => "Rebuilt",
        _ => kind.Length == 0 ? "New" : char.ToUpperInvariant(kind[0]) + kind[1..],
    };

    /// <summary>The public URL of a media file once it is a release asset of this version's tag.</summary>
    public static string MediaAssetUrl(string repo, string version, string src)
        => $"https://github.com/{repo}/releases/download/{TagPrefix}{version}/{Path.GetFileName(src)}";

    /// <summary>
    /// The GitHub release body: title, tagline, notices as GitHub alerts, highlights (with media linked to this
    /// release's assets), the changelog sections with <c>#n</c> autolinks, a footer, and — when
    /// <paramref name="generatedNotes"/> is supplied (<c>POST /releases/generate-notes</c>) — a folded appendix.
    /// </summary>
    public static string RenderBody(ReleaseNotesDocument doc, string repo, string? generatedNotes)
    {
        ArgumentNullException.ThrowIfNull(doc);
        doc.Normalize();
        var sb = new StringBuilder(4096);
        sb.Append("# Wavee ").Append(doc.Version);
        if (doc.Name.Length > 0) sb.Append(" — ").Append(doc.Name);
        sb.Append("\n\n");

        if (doc.Tagline.Length > 0) sb.Append(doc.Tagline).Append("\n\n");

        foreach (var n in doc.Notices)
        {
            if (n.Text.Length == 0) continue;
            string alert = n.Kind switch { "breaking" => "WARNING", "warning" => "WARNING", _ => "NOTE" };
            sb.Append("> [!").Append(alert).Append("]\n> ");
            if (n.Kind == "breaking") sb.Append("**Breaking:** ");
            sb.Append(n.Text.Replace("\n", "\n> ", StringComparison.Ordinal)).Append("\n\n");
        }

        if (doc.Highlights.Length > 0)
        {
            sb.Append("## Highlights\n\n");
            foreach (var h in doc.Highlights)
            {
                sb.Append("### ").Append(h.Title);
                if (h.Kind.Length > 0) sb.Append(" · ").Append(HighlightKindLabel(h.Kind));
                sb.Append("\n\n");
                if (h.Body.Length > 0) sb.Append(h.Body).Append("\n\n");
                if (h.Media is { } m)
                {
                    string alt = m.Alt.Length > 0 ? m.Alt : h.Title;
                    if (m.Poster is { Length: > 0 } poster)
                        sb.Append("[![").Append(alt).Append("](").Append(MediaAssetUrl(repo, doc.Version, poster))
                          .Append(")](").Append(MediaAssetUrl(repo, doc.Version, m.Src)).Append(")\n\n");
                    else if (IsMotionMedia(m.Kind, m.Src))
                        sb.Append('[').Append(alt).Append("](").Append(MediaAssetUrl(repo, doc.Version, m.Src)).Append(")\n\n");
                    else
                        sb.Append("![").Append(alt).Append("](").Append(MediaAssetUrl(repo, doc.Version, m.Src)).Append(")\n\n");
                }
                if (h.DeepLink is { Length: > 0 } dl) sb.Append('`').Append(dl).Append("`\n\n");
            }
        }

        foreach (var s in doc.Sections)
        {
            if (s.Items.Length == 0) continue;
            sb.Append("## ").Append(SectionTitle(s.Kind)).Append("\n\n");
            foreach (var item in s.Items)
            {
                sb.Append("- ");
                if (item.Scope is { Length: > 0 } scope) sb.Append(scope).Append(": ");
                sb.Append(item.Text);
                bool first = true;
                foreach (var i in item.Issues)
                {
                    sb.Append(first ? " (" : ", ");
                    first = false;
                    sb.Append('[').Append('#').Append(i.Number).Append("](https://github.com/")
                      .Append(i.Repo.Length > 0 ? i.Repo : repo).Append("/issues/").Append(i.Number).Append(')');
                }
                foreach (var p in item.Prs)
                {
                    sb.Append(first ? " (" : ", ");
                    first = false;
                    sb.Append('[').Append('#').Append(p.Number).Append("](https://github.com/")
                      .Append(p.Repo.Length > 0 ? p.Repo : repo).Append("/pull/").Append(p.Number).Append(')');
                }
                if (!first) sb.Append(')');
                sb.Append('\n');
            }
            sb.Append('\n');
        }

        AppendResolvedIssues(sb, doc, repo);
        AppendOtherChanges(sb, doc, repo);

        sb.Append("---\n\n");
        var facts = new List<string>(5);
        if (doc.Date.Length > 0) facts.Add("Released " + doc.Date);
        if (doc.PackageVersion.Length > 0) facts.Add("build " + doc.PackageVersion);
        if (doc.Channel.Length > 0) facts.Add(doc.Channel);
        if (doc.Arch.Length > 0) facts.Add(string.Join(", ", doc.Arch));
        if (doc.MinOs.Length > 0) facts.Add("requires Windows " + doc.MinOs);
        sb.Append(string.Join(" · ", facts)).Append("\n\n");
        if (doc.Links.Changelog.Length > 0)
            sb.Append("Full changelog: ").Append(doc.Links.Changelog).Append('\n');
        if (doc.Links.Compare.Length > 0)
            sb.Append("Compare: ").Append(doc.Links.Compare).Append('\n');
        if (doc.Links.Changelog.Length > 0 || doc.Links.Compare.Length > 0) sb.Append('\n');

        if (!string.IsNullOrWhiteSpace(generatedNotes))
        {
            sb.Append("<details><summary>Commits &amp; contributors</summary>\n\n")
              .Append(generatedNotes.Trim()).Append("\n\n</details>\n");
        }

        return sb.ToString();
    }

    /// <summary>The <c>## Resolved issues</c> block: one line per distinct issue number (ascending) the document
    /// cites, its title, the commits that fix it and any PR that shipped it. Omitted entirely when no section item
    /// carries a commit (<see cref="ReleaseItem.Commits"/>).</summary>
    static void AppendResolvedIssues(StringBuilder sb, ReleaseNotesDocument doc, string repo)
    {
        bool anyCommits = false;
        foreach (var s in doc.Sections)
        {
            foreach (var item in s.Items)
            {
                if (item.Commits.Length == 0) continue;
                anyCommits = true;
                break;
            }
            if (anyCommits) break;
        }
        if (!anyCommits) return;

        sb.Append("## Resolved issues\n\n");
        foreach (var n in IssueNumbers(doc))
        {
            ReleaseIssue? issue = null;
            var commits = new List<ReleaseCommit>();
            var seenSha = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in doc.Sections)
            {
                foreach (var item in s.Items)
                {
                    bool cites = false;
                    foreach (var i in item.Issues)
                    {
                        if (i.Number != n) continue;
                        cites = true;
                        issue ??= i;
                    }
                    if (!cites) continue;
                    // An item can cite several issues; only the commits that name THIS one (closing keyword or
                    // squash suffix) belong on its line.
                    foreach (var c in item.Commits)
                        if ((Array.IndexOf(c.Issues, n) >= 0 || Array.IndexOf(c.Prs, n) >= 0) && seenSha.Add(c.Sha))
                            commits.Add(c);
                }
            }

            sb.Append("- [#").Append(n).Append("](https://github.com/").Append(repo)
              .Append(issue is { IsPullRequest: true } ? "/pull/" : "/issues/").Append(n).Append(')');
            if (issue is { Title.Length: > 0 }) sb.Append(' ').Append(EscapeMarkdown(issue.Title));
            sb.Append(" — ");
            if (commits.Count == 0)
            {
                sb.Append("no linked commit");
            }
            else
            {
                for (int i = 0; i < commits.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append('[').Append(commits[i].Short).Append("](https://github.com/").Append(repo)
                      .Append("/commit/").Append(commits[i].Sha).Append(')');
                }
                var prs = new SortedSet<int>();
                foreach (var c in commits)
                    foreach (var p in c.Prs)
                        if (p != n) prs.Add(p);
                if (prs.Count > 0)
                {
                    sb.Append(" (PR ");
                    bool first = true;
                    foreach (var p in prs)
                    {
                        if (!first) sb.Append(", ");
                        first = false;
                        sb.Append('[').Append('#').Append(p).Append("](https://github.com/").Append(repo)
                          .Append("/pull/").Append(p).Append(')');
                    }
                    sb.Append(')');
                }
            }
            sb.Append('\n');
        }
        sb.Append('\n');
    }

    /// <summary>The folded <c>&lt;details&gt;Other changes&lt;/details&gt;</c> appendix — every commit in range
    /// that cites no section item (<see cref="ReleaseNotesDocument.UnlinkedCommits"/>). Omitted when empty.</summary>
    static void AppendOtherChanges(StringBuilder sb, ReleaseNotesDocument doc, string repo)
    {
        if (doc.UnlinkedCommits.Length == 0) return;
        sb.Append("<details><summary>Other changes</summary>\n\n");
        foreach (var c in doc.UnlinkedCommits)
            sb.Append("- [").Append(c.Short).Append("](https://github.com/").Append(repo).Append("/commit/")
              .Append(c.Sha).Append(") ").Append(EscapeMarkdown(c.Subject)).Append('\n');
        sb.Append("\n</details>\n\n");
    }

    /// <summary>Backslash-escapes GitHub Flavored Markdown's special characters (<c>\ * _ ` [ ] &lt; &gt; ~ |</c>)
    /// so a commit subject or issue title cannot break the release body's formatting. Deliberately leaves
    /// <c>#</c> alone — GitHub autolinks <c>#52</c> in a subject to the issue/PR, and escaping it would break
    /// that.</summary>
    public static string EscapeMarkdown(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': case '*': case '_': case '`': case '[': case ']':
                case '<': case '>': case '~': case '|':
                    sb.Append('\\');
                    break;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Distinct issue numbers (&gt; 0) cited by any section item, ascending.</summary>
    public static int[] IssueNumbers(ReleaseNotesDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var set = new SortedSet<int>();
        foreach (var s in doc.Sections)
            foreach (var item in s.Items)
                foreach (var i in item.Issues)
                    if (i.Number > 0) set.Add(i.Number);
        var result = new int[set.Count];
        set.CopyTo(result);
        return result;
    }

    /// <summary>
    /// The store blurb: tagline plus highlight titles, never longer than <see cref="StoreListingMaxChars"/>.
    /// Trailing bullets are dropped before any text is cut; only a tagline that is itself over the cap gets
    /// hard-truncated (on a word boundary, with an ellipsis).
    /// </summary>
    public static string RenderStoreListing(ReleaseNotesDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        doc.Normalize();
        string head = doc.Tagline.Trim();
        string title = doc.Name.Length > 0
            ? $"New in Wavee {doc.Version} “{doc.Name}”:"
            : $"New in Wavee {doc.Version}:";

        var bullets = new List<string>();
        foreach (var h in doc.Highlights)
            if (h.Title.Length > 0)
                bullets.Add("- " + h.Title.Trim());

        while (true)
        {
            var sb = new StringBuilder(StoreListingMaxChars);
            if (head.Length > 0) sb.Append(head).Append("\n\n");
            if (bullets.Count > 0)
            {
                sb.Append(title).Append('\n');
                for (int i = 0; i < bullets.Count; i++) sb.Append(bullets[i]).Append('\n');
            }
            string text = sb.ToString().TrimEnd();
            if (text.Length <= StoreListingMaxChars) return text;
            if (bullets.Count > 0) { bullets.RemoveAt(bullets.Count - 1); continue; }
            return Ellipsize(text, StoreListingMaxChars);
        }
    }

    static string Ellipsize(string text, int max)
    {
        if (text.Length <= max) return text;
        int cut = max - 1;
        int space = text.LastIndexOf(' ', Math.Min(cut, text.Length - 1));
        if (space > max / 2) cut = space;
        return text[..cut].TrimEnd() + "…";
    }

    static bool IsSafeRelativePath(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        if (Path.IsPathRooted(src)) return false;
        if (src.Contains(':', StringComparison.Ordinal)) return false;
        foreach (var seg in src.Split('/', '\\'))
            if (seg == "..") return false;
        return true;
    }

    /// <summary>UTC stamp in the form the schema uses for <c>generatedAt</c>.</summary>
    public static string Stamp(DateTimeOffset when)
        => when.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
