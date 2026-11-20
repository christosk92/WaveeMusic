# Change files

Every user-facing change adds one file to `changes/unreleased/`. Nobody edits `CHANGELOG.md` by hand: at release
time the release runner assembles the files into the version's CHANGELOG section (the owner curates it there), feeds
the What's new page and the issue comments from them, and deletes them in the release commit.

One file per change means two PRs never conflict on the changelog, and an entry can never end up under a version that
shipped while its PR was open.

## Format

Name the file after the issue or the branch: `changes/unreleased/226-heart-follows-title.md`.

```markdown
---
kind: fixed              # added | changed | fixed | removed
issues: [225, 226]       # issues this change closes; each commit that fixes one carries "Fixes #n" in its body
by: 0dm                  # a community contributor's GitHub login (credited in the CHANGELOG and What's new); omit otherwise
contract: none           # none | additive | breaking: did Theming/Contracts change?
contract-note: ""        # one line when contract is not none: what was added, removed or changed
---
**The title links only its text in Classic's player bar.** The space beside a short title no longer lights it up,
and in a wide window the heart sits right after the words.
```

The body is the CHANGELOG line: a bold, user-facing lead phrase, then plain detail, the way the existing entries in
`CHANGELOG.md` read. Say which themes it applies to when it is not all of them.

## Which changes need one

Needs one: anything a listener notices, and any change to the theme contract.

Needs none: refactors, documentation, tooling, and a fix for a regression that never shipped.

`contract: breaking` (a removed or changed member, or a new `required` one) needs the owner's agreement first; the
release runner raises the contract version from these fields, so never edit `ContractVersion.cs` by hand.
