using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wavee.Core.ReleaseNotes;
using Xunit;

namespace Wavee.Tests.ReleaseNotes;

/// <summary>
/// The pure half of the release-notes pipeline — the rules `Wavee.ReleaseTool validate` enforces and the two
/// texts it emits. Media rules need real files, so each test builds its own fixture folder under the temp
/// directory and deletes it again; nothing here touches the network or the repo's own release folders.
/// </summary>
public sealed class ReleaseNotesValidationTests
{
    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────────────

    sealed class NotesDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "wavee-notes-" + Guid.NewGuid().ToString("N"));

        public NotesDir() => Directory.CreateDirectory(System.IO.Path.Combine(Path, "media"));

        /// <summary>Writes <paramref name="bytes"/> bytes of filler at <paramref name="relative"/> and returns that path.</summary>
        public string File(string relative, int bytes)
        {
            string full = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllBytes(full, new byte[bytes]);
            return relative;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static ReleaseNotesDocument Doc(params ReleaseHighlight[] highlights) => new()
    {
        Version = "0.2.0",
        PackageVersion = "0.2.0.17",
        Name = "Breaker",
        Tagline = "Updates that install themselves.",
        Date = "2026-08-29",
        Channel = "stable",
        Arch = ["x64", "arm64"],
        Highlights = highlights,
    };

    static ReleaseHighlight Highlight(string title, string? src = null, string? poster = null,
                                      string kind = "image", string? deepLink = null) => new()
    {
        Id = title.ToLowerInvariant().Replace(' ', '-'),
        Title = title,
        Body = "Body.",
        Kind = "new",
        DeepLink = deepLink,
        Media = src is null ? null : new ReleaseMediaRef { Kind = kind, Src = src, Poster = poster, Alt = title },
    };

    // ── media rules ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Media_that_exists_and_fits_passes()
    {
        using var dir = new NotesDir();
        var doc = Doc(Highlight("Hero", dir.File("media/hero.webp", 100_000)));
        Assert.Empty(ReleaseNotesValidation.ValidateMedia(doc, dir.Path));
    }

    [Fact]
    public void Missing_media_file_is_an_error()
    {
        using var dir = new NotesDir();
        var doc = Doc(Highlight("Hero", "media/absent.webp"));
        Assert.Contains(ReleaseNotesValidation.ValidateMedia(doc, dir.Path), e => e.Contains("missing media"));
    }

    [Fact]
    public void Gif_is_rejected_outright()
    {
        using var dir = new NotesDir();
        var doc = Doc(Highlight("Hero", dir.File("media/hero.gif", 1_000)));
        Assert.Contains(ReleaseNotesValidation.ValidateMedia(doc, dir.Path), e => e.Contains("GIF is not allowed"));
    }

    [Fact]
    public void Unknown_extension_is_rejected()
    {
        using var dir = new NotesDir();
        var doc = Doc(Highlight("Hero", dir.File("media/hero.bmp", 1_000)));
        Assert.Contains(ReleaseNotesValidation.ValidateMedia(doc, dir.Path), e => e.Contains("unsupported media type"));
    }

    [Fact]
    public void A_still_over_150_KB_is_rejected_but_the_same_size_mp4_is_not()
    {
        using var still = new NotesDir();
        var overWeight = Doc(Highlight("Hero", still.File("media/hero.webp", (int)ReleaseNotesValidation.MaxStillBytes + 1)));
        Assert.Contains(ReleaseNotesValidation.ValidateMedia(overWeight, still.Path), e => e.Contains("media too large"));

        using var video = new NotesDir();
        var motion = Doc(Highlight("Hero", video.File("media/hero.mp4", (int)ReleaseNotesValidation.MaxStillBytes + 1), kind: "video"));
        Assert.Empty(ReleaseNotesValidation.ValidateMedia(motion, video.Path));
    }

    [Fact]
    public void An_mp4_over_600_KB_is_rejected()
    {
        using var dir = new NotesDir();
        var doc = Doc(Highlight("Hero", dir.File("media/hero.mp4", (int)ReleaseNotesValidation.MaxMotionBytes + 1), kind: "video"));
        Assert.Contains(ReleaseNotesValidation.ValidateMedia(doc, dir.Path), e => e.Contains("media too large"));
    }

    [Fact]
    public void A_webp_declared_as_video_gets_the_motion_cap_but_its_poster_does_not()
    {
        using var dir = new NotesDir();
        string animated = dir.File("media/animated.webp", 400_000);          // > still cap, < motion cap
        string poster = dir.File("media/poster.webp", (int)ReleaseNotesValidation.MaxStillBytes + 1);
        var doc = Doc(Highlight("Hero", animated, poster, kind: "video"));
        var errors = ReleaseNotesValidation.ValidateMedia(doc, dir.Path);
        Assert.Single(errors);
        Assert.Contains("poster.webp", errors[0]);
    }

    // ── the media/<basename> rule ───────────────────────────────────────────────────────────────────────────
    // CopyMedia flattens every reference into <release>/media/<basename> and the app resolves it back as
    // <notes root>/media/<basename>. Anything else validates as "the file is there" at authoring time and then
    // renders as an empty band on the user's machine — the exact failure this rule exists to make loud.

    [Theory]
    [InlineData("hero.jpg")]                 // bare basename: publish puts it in media/, the app looks in the root
    [InlineData("images/hero.jpg")]          // some other folder
    [InlineData("media/shots/hero.jpg")]     // nested under media/
    [InlineData("assets/media/hero.jpg")]    // media/ present, but not as the FIRST segment
    [InlineData("media\\hero.jpg")]          // backslash is not the wire separator
    public void A_media_reference_outside_media_slash_basename_is_rejected(string src)
    {
        using var dir = new NotesDir();
        var doc = Doc(Highlight("Hero", dir.File(src, 1_000)));
        Assert.Contains(ReleaseNotesValidation.ValidateMedia(doc, dir.Path),
                        e => e.Contains("must be referenced as", StringComparison.Ordinal));
    }

    [Fact]
    public void A_poster_is_held_to_the_same_folder_rule_as_its_source()
    {
        using var dir = new NotesDir();
        var doc = Doc(Highlight("Hero", dir.File("media/clip.mp4", 1_000), dir.File("stills/clip.jpg", 1_000), kind: "video"));
        var errors = ReleaseNotesValidation.ValidateMedia(doc, dir.Path);
        Assert.Single(errors);
        Assert.Contains("stills/clip.jpg", errors[0], StringComparison.Ordinal);
        Assert.Contains("media/clip.jpg", errors[0], StringComparison.Ordinal);   // the error names the fix
    }

    [Theory]
    [InlineData("media/hero.jpg", true)]
    [InlineData("media/a.b.c.webp", true)]
    [InlineData("hero.jpg", false)]
    [InlineData("media/", false)]
    [InlineData("/media/hero.jpg", false)]
    [InlineData("Media/hero.jpg", false)]    // the folder name is written verbatim by CopyMedia
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPublishableMediaPath_accepts_exactly_one_shape(string? src, bool ok)
        => Assert.Equal(ok, ReleaseNotesValidation.IsPublishableMediaPath(src));

    [Fact]
    // Both files are also outside media/<basename> now (two distinct files CANNOT share a basename while both obey
    // that rule), so this document reports both problems; the basename rule is the one under test.
    public void Two_different_files_sharing_a_basename_are_rejected()
    {
        using var dir = new NotesDir();
        var doc = Doc(
            Highlight("One", dir.File("media/a/hero.webp", 1_000)),
            Highlight("Two", dir.File("media/b/hero.webp", 1_000)));
        Assert.Contains(ReleaseNotesValidation.ValidateMedia(doc, dir.Path), e => e.Contains("duplicate media basename"));
    }

    [Fact]
    public void The_same_file_referenced_twice_is_not_a_duplicate()
    {
        using var dir = new NotesDir();
        string shared = dir.File("media/hero.webp", 1_000);
        var doc = Doc(Highlight("One", shared), Highlight("Two", shared));
        Assert.Empty(ReleaseNotesValidation.ValidateMedia(doc, dir.Path));
        Assert.Single(ReleaseNotesValidation.MediaEntries(doc));
    }

    [Fact]
    public void The_total_media_budget_is_enforced_even_when_every_file_fits()
    {
        using var dir = new NotesDir();
        var doc = Doc(
            Highlight("One", dir.File("media/one.mp4", 550_000), kind: "video"),
            Highlight("Two", dir.File("media/two.mp4", 550_000), kind: "video"),
            Highlight("Three", dir.File("media/three.mp4", 550_000), kind: "video"));
        var errors = ReleaseNotesValidation.ValidateMedia(doc, dir.Path);
        Assert.Single(errors);
        Assert.Contains("byte budget", errors[0]);
    }

    [Fact]
    public void Media_paths_may_not_escape_the_notes_folder()
    {
        using var dir = new NotesDir();
        var doc = Doc(Highlight("Hero", "../secrets/hero.webp"));
        Assert.Contains(ReleaseNotesValidation.ValidateMedia(doc, dir.Path), e => e.Contains("must be relative"));
    }

    [Fact]
    public void Media_hashes_and_the_copy_cover_every_referenced_file()
    {
        using var dir = new NotesDir();
        using var outDir = new NotesDir();
        var doc = Doc(Highlight("Hero", dir.File("media/hero.mp4", 2_048), dir.File("media/hero.webp", 1_024), kind: "video"));

        var hashes = ReleaseNotesValidation.MediaHashes(doc, dir.Path);
        Assert.Equal(2, hashes.Length);
        Assert.Equal(2_048, hashes[0].Bytes);
        Assert.Equal(64, hashes[0].Sha256.Length);
        Assert.All(hashes, h => Assert.Equal(h.Sha256.ToLowerInvariant(), h.Sha256));

        Assert.Equal(2, ReleaseNotesValidation.CopyMedia(doc, dir.Path, outDir.Path));
        Assert.True(File.Exists(Path.Combine(outDir.Path, "media", "hero.mp4")));
        Assert.True(File.Exists(Path.Combine(outDir.Path, "media", "hero.webp")));
    }

    // ── deep links ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("wavee://open?route=settings")]
    [InlineData("wavee://open?route=whatsnew")]
    [InlineData("wavee://open?route=pl&arg=spotify%3Aplaylist%3A1")]
    public void In_app_routes_are_accepted(string link)
        => Assert.Empty(ReleaseNotesValidation.ValidateDeepLinks(Doc(Highlight("H", deepLink: link))));

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("wavee://now-playing?lyrics=1")]
    [InlineData("wavee://open?arg=settings")]
    [InlineData("wavee://open?route=")]
    public void Anything_that_is_not_an_in_app_route_is_rejected(string link)
        => Assert.NotEmpty(ReleaseNotesValidation.ValidateDeepLinks(Doc(Highlight("H", deepLink: link))));

    [Fact]
    public void A_highlight_without_a_deep_link_is_fine()
        => Assert.Empty(ReleaseNotesValidation.ValidateDeepLinks(Doc(Highlight("H"))));

    // ── index merge ─────────────────────────────────────────────────────────────────────────────────────────

    static ReleaseNotesIndex Index(params string[] versions) => new()
    {
        Releases = versions.Select(v => new ReleaseNotesIndexEntry { Version = v, Name = "N" + v, Date = "2026-01-01" }).ToArray(),
    };

    [Fact]
    public void The_new_release_goes_to_the_front_of_the_index()
    {
        var merged = ReleaseNotesValidation.MergeIndex(Index("0.1.2", "0.1.1"), Doc());
        Assert.Equal(new[] { "0.2.0", "0.1.2", "0.1.1" }, merged.Releases.Select(r => r.Version).ToArray());
        Assert.Equal("Breaker", merged.Releases[0].Name);
        Assert.Equal("0.2.0.17", merged.Releases[0].PackageVersion);
        Assert.Equal("2026-08-29", merged.Releases[0].Date);
        Assert.Equal("stable", merged.Releases[0].Channel);
    }

    [Fact]
    public void Re_validating_the_same_version_replaces_its_entry_instead_of_duplicating_it()
    {
        var merged = ReleaseNotesValidation.MergeIndex(Index("0.2.0", "0.1.2"), Doc());
        Assert.Equal(new[] { "0.2.0", "0.1.2" }, merged.Releases.Select(r => r.Version).ToArray());
        Assert.Equal("Breaker", merged.Releases[0].Name);
    }

    [Fact]
    public void The_index_is_capped_at_twelve_newest_first()
    {
        var previous = Index(Enumerable.Range(1, 20).Select(i => "0.0." + i).ToArray());
        var merged = ReleaseNotesValidation.MergeIndex(previous, Doc());
        Assert.Equal(ReleaseNotesValidation.MaxIndexEntries, merged.Releases.Length);
        Assert.Equal("0.2.0", merged.Releases[0].Version);
        Assert.Equal("0.0.1", merged.Releases[1].Version);
        Assert.Equal("0.0.11", merged.Releases[^1].Version);
    }

    [Fact]
    public void A_first_release_produces_a_one_entry_index()
    {
        var merged = ReleaseNotesValidation.MergeIndex(null, Doc());
        Assert.Equal("wavee", merged.Product);
        Assert.Equal(1, merged.Schema);
        Assert.Single(merged.Releases);
    }

    // ── release body ────────────────────────────────────────────────────────────────────────────────────────

    static ReleaseNotesDocument BodyDoc()
    {
        var doc = Doc(Highlight("Docked video", deepLink: "wavee://open?route=settings"));
        doc.Notices =
        [
            new ReleaseNotice { Kind = "breaking", Text = "Environment-variable switches are gone." },
            new ReleaseNotice { Kind = "info", Text = "The ms-appinstaller hand-off is gone." },
        ];
        doc.Sections =
        [
            new ReleaseSection
            {
                Kind = "added",
                Items =
                [
                    new ReleaseItem
                    {
                        Scope = "Updates",
                        Text = "**What's new** page.",
                        Issues = [new ReleaseIssue { Repo = "christosk92/WaveeMusic", Number = 412, State = "closed" }],
                        Prs = [new ReleasePr { Repo = "christosk92/WaveeMusic", Number = 430, Merged = true }],
                    },
                ],
            },
            new ReleaseSection { Kind = "known", Items = [new ReleaseItem { Text = "No system tray." }] },
        ];
        doc.Links = new ReleaseLinks { Changelog = "https://example.invalid/CHANGELOG.md" };
        return doc;
    }

    [Fact]
    public void The_body_leads_with_the_version_and_codename()
    {
        string body = ReleaseNotesValidation.RenderBody(BodyDoc(), "christosk92/WaveeMusic", null);
        Assert.StartsWith("# Wavee 0.2.0 — Breaker", body);
        Assert.Contains("Updates that install themselves.", body);
    }

    [Fact]
    public void Notices_become_GitHub_alerts_and_a_breaking_one_says_so()
    {
        string body = ReleaseNotesValidation.RenderBody(BodyDoc(), "christosk92/WaveeMusic", null);
        Assert.Contains("> [!WARNING]\n> **Breaking:** Environment-variable switches are gone.", body);
        Assert.Contains("> [!NOTE]\n> The ms-appinstaller hand-off is gone.", body);
    }

    [Fact]
    public void Changelog_items_carry_their_scope_and_autolinked_refs()
    {
        string body = ReleaseNotesValidation.RenderBody(BodyDoc(), "christosk92/WaveeMusic", null);
        Assert.Contains("## Added", body);
        Assert.Contains("## Known limitations", body);
        Assert.Contains("- Updates: **What's new** page. ([#412](https://github.com/christosk92/WaveeMusic/issues/412), "
                        + "[#430](https://github.com/christosk92/WaveeMusic/pull/430))", body);
    }

    [Fact]
    public void An_empty_section_is_left_out()
    {
        var doc = BodyDoc();
        doc.Sections = [new ReleaseSection { Kind = "fixed", Items = [] }];
        Assert.DoesNotContain("## Fixed", ReleaseNotesValidation.RenderBody(doc, "christosk92/WaveeMusic", null));
    }

    [Fact]
    public void Generated_notes_are_folded_into_a_details_block_and_omitted_when_absent()
    {
        var doc = BodyDoc();
        Assert.DoesNotContain("<details>", ReleaseNotesValidation.RenderBody(doc, "christosk92/WaveeMusic", null));
        string withNotes = ReleaseNotesValidation.RenderBody(doc, "christosk92/WaveeMusic", "* feat: thing by @someone");
        Assert.Contains("<details><summary>Commits &amp; contributors</summary>", withNotes);
        Assert.Contains("* feat: thing by @someone", withNotes);
        Assert.Contains("</details>", withNotes);
    }

    [Fact]
    public void Highlight_media_points_at_this_releases_assets()
    {
        var doc = Doc(Highlight("Docked video", "media/nested/hero.mp4", "media/nested/hero.webp", kind: "video"));
        string body = ReleaseNotesValidation.RenderBody(doc, "christosk92/WaveeMusic", null);
        Assert.Contains("[![Docked video](https://github.com/christosk92/WaveeMusic/releases/download/wavee-v0.2.0/hero.webp)]"
                        + "(https://github.com/christosk92/WaveeMusic/releases/download/wavee-v0.2.0/hero.mp4)", body);
    }

    [Fact]
    public void Known_limitations_is_the_heading_for_the_known_kind()
    {
        Assert.Equal("Known limitations", ReleaseNotesValidation.SectionTitle("known"));
        Assert.Equal("Added", ReleaseNotesValidation.SectionTitle("added"));
        Assert.Equal("Security", ReleaseNotesValidation.SectionTitle("security"));
    }

    // ── resolved issues / other changes (issue ⇄ commit linkage) ──────────────────────────────────────────────
    // BodyDoc() above stays commit-free on purpose (Generated_notes_are_folded_into_a_details_block_and_omitted_when_absent
    // asserts DoesNotContain("<details>") against it) — every test in this block builds its own document instead.

    const string LinkSha1 = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0"; // short: a1b2c3d
    const string LinkSha2 = "9f8e7d6c5b4a392817069f5a4b3c2d1e0f9e8d76"; // short: 9f8e7d6

    /// <summary>A <see cref="BodyDoc"/> whose only section item cites issue #412 + PR #52 and carries the two
    /// commits that fixed it — the shape <c>ReleaseCommits.Link</c> leaves behind on a healthy release.</summary>
    static ReleaseNotesDocument LinkedBodyDoc()
    {
        var doc = BodyDoc();
        doc.Sections =
        [
            new ReleaseSection
            {
                Kind = "fixed",
                Items =
                [
                    new ReleaseItem
                    {
                        Text = "Add mix feature.",
                        Issues =
                        [
                            new ReleaseIssue { Repo = "christosk92/WaveeMusic", Number = 412, Title = "Please add *mix* feature", State = "closed" },
                        ],
                        Prs = [new ReleasePr { Repo = "christosk92/WaveeMusic", Number = 52, Merged = true }],
                        Commits =
                        [
                            new ReleaseCommit { Sha = LinkSha1, Short = "a1b2c3d", Subject = "fix: add mix feature", Issues = [412], Prs = [52] },
                            new ReleaseCommit { Sha = LinkSha2, Short = "9f8e7d6", Subject = "fix: polish mix feature", Issues = [412], Prs = [52] },
                        ],
                    },
                ],
            },
        ];
        return doc;
    }

    [Fact]
    public void Resolved_issues_lists_each_issue_once_with_its_commits_and_pr()
    {
        string body = ReleaseNotesValidation.RenderBody(LinkedBodyDoc(), "christosk92/WaveeMusic", null);
        Assert.Contains("## Resolved issues", body);

        const string Golden = "- [#412](https://github.com/christosk92/WaveeMusic/issues/412) Please add \\*mix\\* feature — "
            + "[a1b2c3d](https://github.com/christosk92/WaveeMusic/commit/a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0), "
            + "[9f8e7d6](https://github.com/christosk92/WaveeMusic/commit/9f8e7d6c5b4a392817069f5a4b3c2d1e0f9e8d76) "
            + "(PR [#52](https://github.com/christosk92/WaveeMusic/pull/52))\n";
        Assert.Contains(Golden, body);

        int issueIdx = body.IndexOf(Golden, StringComparison.Ordinal);
        int footerIdx = body.IndexOf("---\n\n", StringComparison.Ordinal);
        Assert.True(issueIdx >= 0, "golden Resolved-issues line not found");
        Assert.True(footerIdx >= 0, "footer not found");
        Assert.True(issueIdx < footerIdx, "Resolved issues must precede the footer");
    }

    [Fact]
    public void Other_changes_folds_unlinked_commits_and_is_omitted_when_empty()
    {
        var doc = LinkedBodyDoc();
        doc.UnlinkedCommits = [new ReleaseCommit { Sha = LinkSha1, Short = "a1b2c3d", Subject = "chore: bump dependencies" }];

        string body = ReleaseNotesValidation.RenderBody(doc, "christosk92/WaveeMusic", null);
        Assert.Contains("<details><summary>Other changes</summary>", body);
        Assert.Contains(
            "- [a1b2c3d](https://github.com/christosk92/WaveeMusic/commit/a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0) chore: bump dependencies",
            body);

        doc.UnlinkedCommits = [];
        Assert.DoesNotContain("Other changes", ReleaseNotesValidation.RenderBody(doc, "christosk92/WaveeMusic", null));
    }

    [Fact]
    public void Resolved_issues_still_lists_an_issue_with_no_commit_when_allow_unlinked_shipped_it()
    {
        var doc = BodyDoc();
        doc.Sections =
        [
            new ReleaseSection
            {
                Kind = "fixed",
                Items =
                [
                    new ReleaseItem
                    {
                        Text = "Two issues, one commit.",
                        Issues =
                        [
                            new ReleaseIssue { Repo = "christosk92/WaveeMusic", Number = 412, Title = "Has a commit", State = "closed" },
                            new ReleaseIssue { Repo = "christosk92/WaveeMusic", Number = 413, Title = "Shipped unlinked", State = "closed" },
                        ],
                        Commits = [new ReleaseCommit { Sha = LinkSha1, Short = "a1b2c3d", Subject = "fix: 412", Issues = [412] }],
                    },
                ],
            },
        ];

        string body = ReleaseNotesValidation.RenderBody(doc, "christosk92/WaveeMusic", null);
        Assert.Contains(
            "[#412](https://github.com/christosk92/WaveeMusic/issues/412) Has a commit — [a1b2c3d](https://github.com/christosk92/WaveeMusic/commit/"
            + LinkSha1 + ")", body);
        Assert.Contains(
            "[#413](https://github.com/christosk92/WaveeMusic/issues/413) Shipped unlinked — no linked commit", body);
    }

    [Fact]
    public void EscapeMarkdown_escapes_punctuation_but_keeps_hash_autolinks()
    {
        Assert.Equal(@"\\ \* \_ \` \[ \] \< \> \~ \|", ReleaseNotesValidation.EscapeMarkdown(@"\ * _ ` [ ] < > ~ |"));
        Assert.Equal("(#52) still autolinks", ReleaseNotesValidation.EscapeMarkdown("(#52) still autolinks"));
        Assert.Equal("Please add \\*mix\\* feature", ReleaseNotesValidation.EscapeMarkdown("Please add *mix* feature"));
        Assert.Equal("plain text is untouched", ReleaseNotesValidation.EscapeMarkdown("plain text is untouched"));
    }

    [Fact]
    public void The_index_entry_carries_the_distinct_issue_numbers_ascending()
    {
        var doc = BodyDoc();
        doc.Sections =
        [
            new ReleaseSection
            {
                Kind = "fixed",
                Items =
                [
                    new ReleaseItem { Text = "a", Issues = [new ReleaseIssue { Repo = "christosk92/WaveeMusic", Number = 90 }] },
                    new ReleaseItem
                    {
                        Text = "b",
                        Issues =
                        [
                            new ReleaseIssue { Repo = "christosk92/WaveeMusic", Number = 41 },
                            new ReleaseIssue { Repo = "christosk92/WaveeMusic", Number = 90 }, // duplicate, distinct in the result
                        ],
                    },
                ],
            },
        ];

        Assert.Equal(new[] { 41, 90 }, ReleaseNotesValidation.IssueNumbers(doc));

        var merged = ReleaseNotesValidation.MergeIndex(null, doc);
        Assert.Equal(new[] { 41, 90 }, merged.Releases[0].Issues);
    }

    // ── store listing ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_store_listing_is_the_tagline_plus_the_highlight_titles()
    {
        var doc = Doc(Highlight("One"), Highlight("Two"));
        string text = ReleaseNotesValidation.RenderStoreListing(doc);
        Assert.StartsWith("Updates that install themselves.", text);
        Assert.Contains("New in Wavee 0.2.0 “Breaker”:", text);
        Assert.Contains("- One", text);
        Assert.Contains("- Two", text);
        Assert.True(text.Length <= ReleaseNotesValidation.StoreListingMaxChars);
    }

    [Fact]
    public void Too_many_highlights_drop_trailing_bullets_rather_than_cutting_words()
    {
        var doc = Doc(Enumerable.Range(0, 60).Select(i => Highlight(new string('T', 40) + i)).ToArray());
        string text = ReleaseNotesValidation.RenderStoreListing(doc);
        Assert.True(text.Length <= ReleaseNotesValidation.StoreListingMaxChars);
        Assert.StartsWith("Updates that install themselves.", text);
        Assert.DoesNotContain("…", text);                                  // bullets are dropped, never cut

        // The last line is still a whole bullet, matching one of the authored titles.
        string last = text.Split('\n')[^1];
        Assert.StartsWith("- ", last);
        Assert.Contains(last[2..], doc.Highlights.Select(h => h.Title));
    }

    [Fact]
    public void A_tagline_longer_than_the_cap_is_truncated_with_an_ellipsis()
    {
        var doc = Doc();
        doc.Tagline = string.Join(' ', Enumerable.Repeat("word", 600));
        string text = ReleaseNotesValidation.RenderStoreListing(doc);
        Assert.True(text.Length <= ReleaseNotesValidation.StoreListingMaxChars);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void A_release_with_no_codename_still_renders_a_listing()
    {
        var doc = Doc(Highlight("One"));
        doc.Name = "";
        Assert.Contains("New in Wavee 0.2.0:", ReleaseNotesValidation.RenderStoreListing(doc));
    }

    // ── misc ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_generated_at_stamp_is_second_precision_UTC()
        => Assert.Equal("2026-08-29T14:03:11Z",
            ReleaseNotesValidation.Stamp(new DateTimeOffset(2026, 8, 29, 16, 3, 11, TimeSpan.FromHours(2))));
}
