using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.ReleaseNotes;

namespace Wavee.ReleaseTool;

/// <summary>
/// The <c>validate</c> verb: a thin shell over <see cref="ReleaseNotesValidation"/> plus the two things that are
/// not pure — reading the repo's files and snapshotting issue/PR state from GitHub.
/// </summary>
static class Validator
{
    /// <summary>Reported once per run and returned as the process exit code.</summary>
    public const int ExitOk = 0;
    /// <summary>Usage or I/O failure.</summary>
    public const int ExitUsage = 1;
    /// <summary>The release is not shippable: something in CHANGELOG.md / whatsnew.json / media is wrong.</summary>
    public const int ExitInvalid = 2;

    public static async Task<int> RunAsync(Args a, CancellationToken ct)
    {
        var missing = new List<string>();
        string semver = a.Require("semver", missing);
        string quad = a.Require("quad", missing);
        string codename = a.Require("codename", missing);
        string channel = a.Require("channel", missing);
        string changelogPath = a.Require("changelog", missing);
        string notesDir = a.Require("notes", missing);
        string outDir = a.Require("out", missing);
        string repo = a.Require("repo", missing);
        string? previousIndex = a.Get("previous-index");
        string? previousTag = a.Get("previous-tag");
        string? commitsPath = a.Get("commits");
        // The orchestrator hands the token through the ENVIRONMENT so it never lands in a command line, a
        // transcript or release-state.json; --github-token stays for a hand run. An explicit flag wins.
        string? token = a.Get("github-token") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        bool allowUnresolved = a.Flag("allow-unresolved");
        bool allowUnlinked = a.Flag("allow-unlinked");
        if (missing.Count > 0)
        {
            Console.Error.WriteLine("error: missing required argument(s): " + string.Join(", ", missing));
            Program.PrintUsage();
            return ExitUsage;
        }
        if (channel is not ("stable" or "beta"))
        {
            Console.Error.WriteLine("error: --channel must be 'stable' or 'beta' (got '" + channel + "')");
            return ExitUsage;
        }
        if (previousTag is { Length: > 0 } && commitsPath is null)
        {
            Console.Error.WriteLine("error: --commits <file> is required when --previous-tag is given (the script writes <stage>\\commits.json from git log <previous-tag>..HEAD)");
            return ExitUsage;
        }
        if (commitsPath is not null && !File.Exists(commitsPath))
        {
            Console.Error.WriteLine("error: commits not found: " + commitsPath);
            return ExitUsage;
        }

        string notesJsonPath = Path.Combine(notesDir, "whatsnew.json");
        if (!File.Exists(changelogPath)) { Console.Error.WriteLine("error: changelog not found: " + changelogPath); return ExitUsage; }
        if (!File.Exists(notesJsonPath)) { Console.Error.WriteLine("error: notes not found: " + notesJsonPath); return ExitUsage; }

        var errors = new List<string>();
        var warnings = new List<string>();

        // ── CHANGELOG.md ────────────────────────────────────────────────────────────────────────────────────
        string markdown = File.ReadAllText(changelogPath);
        var release = ChangelogParser.Find(markdown, semver, repo);
        if (release is null)
        {
            Console.Error.WriteLine($"error: {changelogPath} has no '## [{semver}]' entry");
            return ExitInvalid;
        }
        if (release.Date is null or "unreleased")
            errors.Add($"CHANGELOG entry [{semver}] is not dated (still 'unreleased'); the release script dates it before this step");

        // ── whatsnew.json ───────────────────────────────────────────────────────────────────────────────────
        ReleaseNotesDocument? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(File.ReadAllBytes(notesJsonPath), ReleaseNotesJsonContext.Default.ReleaseNotesDocument);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine("error: " + notesJsonPath + " is not valid JSON: " + ex.Message);
            return ExitInvalid;
        }
        if (parsed is null)
        {
            Console.Error.WriteLine("error: " + notesJsonPath + " deserialized to nothing");
            return ExitInvalid;
        }
        var doc = parsed;
        // Every array/string the wire could have sent as null becomes its empty value here, so nothing below
        // has to guard: an explicit "sections": null would otherwise NRE on the first enumeration.
        doc.Normalize();

        if (doc.Version != semver) errors.Add($"whatsnew.json version '{doc.Version}' != --semver '{semver}'");
        if (doc.Name != codename) errors.Add($"whatsnew.json name '{doc.Name}' != --codename '{codename}'");
        if (doc.Highlights.Length == 0) errors.Add("whatsnew.json has no highlights");
        if (doc.Tagline.Length == 0) errors.Add("whatsnew.json has no tagline");

        // The script owns these — the author never hand-maintains them.
        doc.PackageVersion = quad;
        doc.Channel = channel;
        doc.Date = release.Date ?? "";
        doc.Sections = release.Sections;

        errors.AddRange(ReleaseNotesValidation.ValidateMedia(doc, notesDir));
        errors.AddRange(ReleaseNotesValidation.ValidateDeepLinks(doc));

        // ── commits.json cross-check ────────────────────────────────────────────────────────────────────────
        DefaultRepos(doc, repo);
        if (commitsPath is not null)
        {
            ReleaseCommit[] commits;
            try
            {
                commits = ReleaseCommits.Parse(File.ReadAllBytes(commitsPath));
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"error: {commitsPath} is not a valid commits.json: {ex.Message}");
                return ExitUsage;
            }
            string range = previousTag is { Length: > 0 } pt ? pt + "..HEAD" : "the release range";
            if (commits.Length == 0)
                warnings.Add($"{commitsPath} lists no commits ({range} is empty)");
            var mismatches = ReleaseCommits.Link(doc, commits, repo, range);
            foreach (var m in mismatches)
                (allowUnlinked ? warnings : errors).Add(allowUnlinked ? m + " (shipping anyway: --allow-unlinked)" : m);
        }
        else
        {
            warnings.Add("no --commits: the CHANGELOG/commit cross-check is skipped and RELEASE_BODY.md gets no Resolved issues section");
        }

        // ── GitHub snapshots ────────────────────────────────────────────────────────────────────────────────
        using var gh = new GitHubApi(token, semver);
        if (!gh.Authenticated)
            warnings.Add("no token (--github-token / GITHUB_TOKEN): issue lookups are unauthenticated (60/hour) and generate-notes is skipped");
        int unresolved = await SnapshotAsync(gh, doc, repo, errors, warnings, ct).ConfigureAwait(false);
        if (unresolved > 0 && !allowUnresolved)
        {
            // Throttled/failed lookups leave the AUTHORED title and state in the document. Shipping that quietly
            // is how a release page ends up with a blank title, or an issue shown "open" that closed weeks ago,
            // under a generatedAt stamp claiming it was checked. Refuse; --allow-unresolved is the deliberate
            // escape hatch for "GitHub is down and this release has to go out anyway".
            errors.Add($"{unresolved} issue(s) unresolved: GitHub could not be read for them, so the authored title/state would ship as if it had been verified. Retry with a token, or pass --allow-unresolved to publish the authored values.");
        }

        // ── derived fields ──────────────────────────────────────────────────────────────────────────────────
        string tag = ReleaseNotesValidation.TagPrefix + semver;
        doc.Links = new ReleaseLinks
        {
            Release = $"https://github.com/{repo}/releases/tag/{tag}",
            Changelog = $"https://github.com/{repo}/blob/{tag}/CHANGELOG.md",
            Compare = previousTag is { Length: > 0 } p ? $"https://github.com/{repo}/compare/{p}...{tag}" : "",
        };
        doc.GeneratedAt = ReleaseNotesValidation.Stamp(DateTimeOffset.UtcNow);
        doc.Media = ReleaseNotesValidation.MediaHashes(doc, notesDir);

        foreach (var w in warnings) Console.Error.WriteLine("warning: " + w);
        if (errors.Count > 0)
        {
            foreach (var e in errors) Console.Error.WriteLine("error: " + e);
            Console.Error.WriteLine($"error: {errors.Count} problem(s); nothing was written");
            return ExitInvalid;
        }

        // ── outputs ─────────────────────────────────────────────────────────────────────────────────────────
        string? generated = await gh.GenerateNotesAsync(repo, tag, previousTag, ct).ConfigureAwait(false);
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, "whatsnew.json"),
            JsonSerializer.SerializeToUtf8Bytes(doc, ReleaseNotesJsonContext.Default.ReleaseNotesDocument));
        IndexWriter.Write(outDir, previousIndex, doc);
        BodyWriter.Write(outDir, doc, repo, generated);
        int copied = ReleaseNotesValidation.CopyMedia(doc, notesDir, outDir);

        Console.WriteLine($"ok: Wavee {semver} \"{codename}\" ({quad}, {channel}) -> {Path.GetFullPath(outDir)}");
        Console.WriteLine($"    whatsnew.json + whatsnew-index.json + RELEASE_BODY.md + store-listing.txt + {copied} media file(s)");
        int items = 0;
        int linked = 0;
        foreach (var s in doc.Sections)
            foreach (var item in s.Items)
            {
                items++;
                linked += item.Commits.Length;
            }
        Console.WriteLine($"    {doc.Highlights.Length} highlight(s), {doc.Sections.Length} section(s), {items} item(s); as of {doc.GeneratedAt}" +
                           $"; {linked} linked commit(s), {doc.UnlinkedCommits.Length} other");
        return ExitOk;
    }

    /// <summary>A bare <c>#123</c> in CHANGELOG.md means "this repo".</summary>
    static void DefaultRepos(ReleaseNotesDocument doc, string repo)
    {
        foreach (var s in doc.Sections)
            foreach (var item in s.Items)
            {
                foreach (var i in item.Issues) if (i.Repo.Length == 0) i.Repo = repo;
                foreach (var p in item.Prs) if (p.Repo.Length == 0) p.Repo = repo;
            }
    }

    /// <summary>
    /// Fetches every referenced issue/PR once and writes the live state into the document. A 404 is an error
    /// (the changelog references a number that does not exist); throttling or a transport failure keeps the
    /// authored values and is COUNTED — the caller decides whether an unverified snapshot may ship.
    /// </summary>
    /// <returns>How many referenced issues/PRs could not be resolved (throttled, forbidden, transport failure).</returns>
    static async Task<int> SnapshotAsync(GitHubApi gh, ReleaseNotesDocument doc, string repo,
                                         List<string> errors, List<string> warnings, CancellationToken ct)
    {
        var cache = new Dictionary<string, GitHubIssueResult>(StringComparer.Ordinal);
        var contributors = new List<ReleaseContributor>();
        var seenLogins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool stopped = false;
        int unresolved = 0;

        async Task<GitHubIssueResult> LookupAsync(string r, int n)
        {
            string key = r + "#" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (cache.TryGetValue(key, out var hit)) return hit;
            if (stopped) return new GitHubIssueResult(GitHubOutcome.Throttled, null, "skipped after an earlier 403");
            var res = await gh.GetIssueAsync(r, n, ct).ConfigureAwait(false);
            if (res.Outcome == GitHubOutcome.Throttled) stopped = true;
            cache[key] = res;
            return res;
        }

        void Note(string login)
        {
            if (login.Length > 0 && seenLogins.Add(login))
                contributors.Add(new ReleaseContributor { Login = login, FirstTime = false });
        }

        foreach (var s in doc.Sections)
            foreach (var item in s.Items)
            {
                foreach (var i in item.Issues)
                {
                    var res = await LookupAsync(i.Repo, i.Number).ConfigureAwait(false);
                    switch (res.Outcome)
                    {
                        case GitHubOutcome.Ok when res.Issue is { } gi:
                            i.Title = gi.Title ?? i.Title;
                            i.State = gi.State ?? i.State;
                            i.StateReason = gi.StateReason;
                            i.IsPullRequest = gi.IsPullRequest;
                            Note(gi.User?.Login ?? "");
                            break;
                        case GitHubOutcome.Missing:
                            errors.Add($"{i.Repo}#{i.Number} does not exist ({res.Detail})");
                            break;
                        default:
                            unresolved++;
                            warnings.Add($"{i.Repo}#{i.Number}: {res.Detail} — keeping the authored state '{i.State}'");
                            break;
                    }
                }
                foreach (var p in item.Prs)
                {
                    var res = await LookupAsync(p.Repo, p.Number).ConfigureAwait(false);
                    switch (res.Outcome)
                    {
                        case GitHubOutcome.Ok when res.Issue is { } gi:
                            p.Title = gi.Title ?? p.Title;
                            p.Merged = gi.Merged;
                            if (!gi.IsPullRequest)
                                errors.Add($"{p.Repo}!{p.Number} is an issue, not a pull request");
                            Note(gi.User?.Login ?? "");
                            break;
                        case GitHubOutcome.Missing:
                            errors.Add($"{p.Repo}!{p.Number} does not exist ({res.Detail})");
                            break;
                        default:
                            unresolved++;
                            warnings.Add($"{p.Repo}!{p.Number}: {res.Detail} — keeping the authored values");
                            break;
                    }
                }
            }

        if (doc.Contributors.Length == 0 && contributors.Count > 0)
            doc.Contributors = contributors.ToArray();
        return unresolved;
    }
}
