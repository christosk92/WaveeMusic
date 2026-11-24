---
kind: changed
issues: [240]
contract: none
contract-note: ""
---
**Switching tabs on On this PC animates in place.** Moving between Home, Albums, Artists, Songs, Folders and Insights
now slides the new tab's content in from the side of its tab while the old one fades out, the header shrinks from Home's
large header to the compact one (or grows back) smoothly, and the tab bar glides with it instead of the whole page being
rebuilt. There is no blank frame between tabs. Home's header now stays at the top while its shelves scroll. (WinUI and
Classic; Zune keeps its own pivot.) (#240)
