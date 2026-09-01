## Summary

<!-- One paragraph: what does this PR do and why. -->

## Linked issue

Fixes #

## How I verified it

- <!-- e.g. added/updated tests for X -->
- <!-- e.g. ran the app with `dotnet run --project src/apps/Wavee -- --fake` and checked Y -->

## Checklist

- [ ] `dotnet build Wavee.slnx` (Debug) builds clean, no warnings
- [ ] `dotnet build Wavee.slnx -c Release` builds clean, no warnings
- [ ] `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj` is green
- [ ] No source-text tests were added (tests don't read/grep production source)
- [ ] No new environment-variable switches for behaviour or verification
- [ ] No legacy paths kept alongside the new code — replaced outright
- [ ] Engine-touching changes were made and verified in the sibling `..\fluent-gpu` repo
- [ ] `CHANGELOG.md` has an entry under a `## [x.y.z] - unreleased` heading
- [ ] Nothing under the private PlayPlay paths is included in this diff
- [ ] Screenshots / recording attached (UI changes)
