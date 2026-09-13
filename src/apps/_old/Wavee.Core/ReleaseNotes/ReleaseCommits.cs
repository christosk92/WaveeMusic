using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Wavee.Core.ReleaseNotes;

/// <summary>
/// Cross-checks the CHANGELOG entry's <c>(#n)</c> refs against the commits actually shipped in
/// <c>&lt;prevTag&gt;..HEAD</c> (git closing keywords are the source of truth for "this fixed an issue"; the
/// CHANGELOG is the source of truth for "this shipped in this release"), and wires the two together for
/// rendering (<see cref="ReleaseNotesValidation.RenderBody"/>).
/// </summary>
public static class ReleaseCommits
{
    /// <summary><c>commits.json</c> (the shape <c>ConvertFrom-GitLogRecords</c> / <c>Write-ReleaseCommitsJson</c>
    /// write) → <see cref="ReleaseCommit"/>[]. Repairs null members, derives <see cref="ReleaseCommit.Short"/>
    /// from <see cref="ReleaseCommit.Sha"/> when empty, and drops null array elements.</summary>
    /// <exception cref="JsonException">The document is not a JSON array, or a commit has no <c>sha</c>.</exception>
    public static ReleaseCommit[] Parse(ReadOnlySpan<byte> json)
    {
        var commits = JsonSerializer.Deserialize(json, ReleaseNotesJsonContext.Default.ReleaseCommitArray)
            ?? throw new JsonException("commits.json is not an array");

        var list = new List<ReleaseCommit>(commits.Length);
        foreach (var c in commits)
        {
            if (c is null) continue;
            if (string.IsNullOrEmpty(c.Sha)) throw new JsonException("a commit in commits.json has no sha");
            c.Short = string.IsNullOrEmpty(c.Short) ? c.Sha[..Math.Min(7, c.Sha.Length)] : c.Short;
            c.Subject ??= "";
            c.Issues ??= [];
            c.Prs ??= [];
            list.Add(c);
        }
        return list.ToArray();
    }

    /// <summary>
    /// ALWAYS attaches <paramref name="commits"/> to every section item whose <c>#n</c>/<c>!n</c> refs intersect
    /// the commit's issues∪prs (dedup by sha), fills <see cref="ReleaseNotesDocument.UnlinkedCommits"/> (commits
    /// matching no item, in input order), and returns the mismatches between the CHANGELOG entry's refs and the
    /// commits' closing keywords — one line per issue number, ascending:
    /// <code>
    /// issue #{n} is fixed by {short} "{subject}" but the CHANGELOG [{doc.Version}] entry does not cite it
    /// CHANGELOG cites #{n} but no commit in {range} carries "Fixes #{n}"
    /// </code>
    /// Only closing-keyword issues (<see cref="ReleaseCommit.Issues"/>) can be "missing in the changelog"; a
    /// CHANGELOG <c>#n</c> is satisfied by a commit whose <see cref="ReleaseCommit.Issues"/> OR
    /// <see cref="ReleaseCommit.Prs"/> contain <c>n</c>. Issue refs of another repo
    /// (<see cref="ReleaseIssue.Repo"/> set and different from <paramref name="repo"/>, case-insensitive) are
    /// ignored. Calls <see cref="ReleaseNotesDocument.Normalize"/> first. Errors are returned even when attaching
    /// (so <c>--allow-unlinked</c> can ship what it has).
    /// </summary>
    public static IReadOnlyList<string> Link(
        ReleaseNotesDocument doc, IReadOnlyList<ReleaseCommit> commits, string repo, string range)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(commits);
        doc.Normalize();

        // Every item's own ref numbers (issues, repo-filtered, ∪ PRs), and which items own a given ref number.
        var itemsByRef = new Dictionary<int, List<ReleaseItem>>();
        var changelogIssues = new SortedSet<int>();
        foreach (var section in doc.Sections)
        {
            foreach (var item in section.Items)
            {
                var refs = new HashSet<int>();
                foreach (var i in item.Issues)
                {
                    if (i.Repo.Length > 0 && !string.Equals(i.Repo, repo, StringComparison.OrdinalIgnoreCase))
                        continue;
                    refs.Add(i.Number);
                    changelogIssues.Add(i.Number);
                }
                foreach (var p in item.Prs) refs.Add(p.Number);

                foreach (var n in refs)
                {
                    if (!itemsByRef.TryGetValue(n, out var list)) itemsByRef[n] = list = [];
                    list.Add(item);
                }
            }
        }

        // Attach: for each commit, find every item whose refs intersect this commit's issues∪prs.
        var itemCommits = new Dictionary<ReleaseItem, (HashSet<string> Shas, List<ReleaseCommit> Commits)>();
        var unlinked = new List<ReleaseCommit>();
        var refSatisfied = new HashSet<int>();
        var fixedBy = new Dictionary<int, List<ReleaseCommit>>();

        foreach (var c in commits)
        {
            var commitRefs = new HashSet<int>();
            foreach (var n in c.Issues)
            {
                commitRefs.Add(n);
                refSatisfied.Add(n);
                if (!fixedBy.TryGetValue(n, out var fixers)) fixedBy[n] = fixers = [];
                fixers.Add(c);
            }
            foreach (var n in c.Prs)
            {
                commitRefs.Add(n);
                refSatisfied.Add(n);
            }

            var touched = new HashSet<ReleaseItem>();
            foreach (var n in commitRefs)
                if (itemsByRef.TryGetValue(n, out var list))
                    foreach (var item in list) touched.Add(item);

            foreach (var item in touched)
            {
                if (!itemCommits.TryGetValue(item, out var bucket))
                    itemCommits[item] = bucket = ([], []);
                if (bucket.Shas.Add(c.Sha)) bucket.Commits.Add(c);
            }
            if (touched.Count == 0) unlinked.Add(c);
        }

        foreach (var section in doc.Sections)
            foreach (var item in section.Items)
                item.Commits = itemCommits.TryGetValue(item, out var bucket) ? [.. bucket.Commits] : [];
        doc.UnlinkedCommits = [.. unlinked];

        var mismatches = new List<(int Issue, string Message)>();
        foreach (var (n, fixers) in fixedBy)
        {
            if (changelogIssues.Contains(n)) continue;
            var first = fixers[0];
            mismatches.Add((n,
                $"issue #{n} is fixed by {first.Short} \"{first.Subject}\" but the CHANGELOG [{doc.Version}] entry does not cite it"));
        }
        foreach (var n in changelogIssues)
        {
            if (refSatisfied.Contains(n)) continue;
            mismatches.Add((n, $"CHANGELOG cites #{n} but no commit in {range} carries \"Fixes #{n}\""));
        }
        mismatches.Sort((a, b) => a.Issue.CompareTo(b.Issue));

        var errors = new List<string>(mismatches.Count);
        foreach (var m in mismatches) errors.Add(m.Message);
        return errors;
    }
}
