<!-- Base branch: the open release branch (the newest release/*, e.g. release/0.3.12), not main. -->

## Summary

<!-- One paragraph: what does this PR do and why. -->

## Linked issue

Fixes #

## How I verified it

- <!-- e.g. rendered and traced screen X in the headless inspector (`--inspect`) -->
- <!-- e.g. ran the app with `dotnet run --project src/apps/Wavee -- --fake` and checked Y -->

## Checklist

- [ ] `dotnet build Wavee.slnx` (Debug) builds clean, no warnings
- [ ] No tests were added (test projects and files are banned)
- [ ] No new environment-variable switches for behaviour or verification
- [ ] No legacy paths kept alongside the new code — replaced outright
- [ ] Engine-touching changes were made and verified in the sibling `..\fluent-gpu` repo
- [ ] The base branch is the open `release/*` branch, not `main`
- [ ] A change file in `changes/unreleased/` (user-facing changes; `contract:` set if `Theming/Contracts` changed), and `CHANGELOG.md` / `ContractVersion.cs` untouched
- [ ] The description covers every change in the diff (unrelated changes: separate PRs)
- [ ] Nothing under the private PlayPlay paths is included in this diff
- [ ] Screenshots / recording attached (UI changes)
