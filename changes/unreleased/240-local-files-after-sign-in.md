---
kind: fixed
issues: [240]
contract: none
contract-note: ""
---
**On this PC lists your songs again after Wavee starts.** With a music folder already set up, the Songs tab could say
"Nothing here yet" while the header counted the songs: the files were listed a moment before sign-in finished, and
signing in replaced the app's catalog, taking the list with it. Wavee now lists the files again in the signed-in
catalog (nothing is rescanned and nothing is announced as new), so the Songs tab, playing a local file and the Local
Files playlist work from the first start. (#240)
