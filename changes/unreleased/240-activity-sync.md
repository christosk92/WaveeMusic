---
kind: fixed
issues: [240]
contract: none
contract-note: ""
---
**"Local files" in the sidebar and "On this PC" no longer disagree about a scan.** Opening the page just as a scan
finished could show "Scanning · 2 of 2" under "Local files" while the page's head already said "Up to date · scanned
just now". Both now read the same moment of the scan, so they always say the same thing and leave "Scanning" on the same
frame: after the start-up scan, a rescan, a dropped file or a file arriving in a music folder. (All three themes.) (#240)
