using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using static Wavee.ReleaseNotes;

namespace Wavee.ReleaseTool;

/// <summary>
/// Fills each highlight's <c>community</c> from the CHANGELOG: an item carrying the sentence <c>changelog assemble</c>
/// writes for a change file's <c>by:</c> ("A community contribution from @login") credits that login on every highlight
/// that shares an issue number with it. Run by <c>validate</c> and <c>render</c>, so both see the same document.
/// </summary>
static partial class CommunityCredits
{
    [GeneratedRegex(@"A community contribution from @(?<login>[A-Za-z0-9][A-Za-z0-9-]{0,38})(?![A-Za-z0-9-])")]
    private static partial Regex CreditRegex();

    /// <summary>Adds the credited logins after the ones the author wrote, without repeats. A highlight with no issues
    /// stays as authored. Returns one line per login added: <c>community: @0dm added to highlight solid-controls (#228)</c>.</summary>
    public static List<string> Fill(ReleaseNotesDocument doc, string repo)
    {
        var lines = new List<string>();
        foreach (var h in doc.Highlights)
        {
            if (h.Issues.Length == 0) continue;
            var logins = new List<string>(h.Community);
            foreach (var s in doc.Sections)
                foreach (var item in s.Items)
                {
                    var shared = new List<int>();
                    foreach (var i in item.Issues)
                        if ((i.Repo.Length == 0 || i.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase))
                            && Array.IndexOf(h.Issues, i.Number) >= 0 && !shared.Contains(i.Number))
                            shared.Add(i.Number);
                    if (shared.Count == 0) continue;
                    foreach (Match m in CreditRegex().Matches(item.Text))
                    {
                        string login = m.Groups["login"].Value;
                        if (logins.Exists(x => x.Equals(login, StringComparison.OrdinalIgnoreCase))) continue;
                        logins.Add(login);
                        var refs = new string[shared.Count];
                        for (int k = 0; k < shared.Count; k++) refs[k] = "#" + shared[k].ToString(CultureInfo.InvariantCulture);
                        lines.Add($"community: @{login} added to highlight {h.Id} ({string.Join(", ", refs)})");
                    }
                }
            if (logins.Count > h.Community.Length) h.Community = [.. logins];
        }
        return lines;
    }
}
