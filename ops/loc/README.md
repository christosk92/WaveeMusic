# Wavee loc packets

Nested JSON in `src/apps/Wavee/assets/loc/` is the source of truth. Reviewers never edit those files.

## Operator loop

From the repo root:

```text
dotnet run --project ops/loc/Wavee.LocPack -- pack --culture ko-KR --packet settings-general --prefix settings.language --prefix settings.tabs --prefix settings.links --prefix settings.general
dotnet run --project ops/loc/Wavee.LocPack -- draft ops/loc/work/ko-KR/settings-general.json --from ops/loc/work/ko-KR/settings-general.drafts.json
dotnet run --project ops/loc/Wavee.LocPack -- xlsx ops/loc/work/ko-KR/settings-general.json
```

Send the reviewer:

- `ops/loc/review/index.html`
- the packet JSON (after `draft`, if you pre-filled `ai_draft`)
- a short HOW-TO in their language: open the HTML, choose the JSON, Approve / edit / Skip. Do not change words in `{curly braces}`. Send the downloaded JSON back.

Then:

```text
dotnet run --project ops/loc/Wavee.LocPack -- qa ops/loc/work/ko-KR/settings-general.json
dotnet run --project ops/loc/Wavee.LocPack -- import --culture ko-KR ops/loc/work/ko-KR/settings-general.json
```

`xlsx` writes a UTF-8 CSV next to the packet for Google Sheets. Re-export from JSON if a sheet round-trip looks wrong.

Do not put API keys in this repo. `draft --from` merges a sidecar map of `key → translation` into empty `ai_draft` fields and never overwrites `yours`.
