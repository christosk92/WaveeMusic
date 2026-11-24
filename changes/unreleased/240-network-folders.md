---
kind: added
issues: []
contract: none
---
**Music folders on a NAS or a network share.** A folder picked on a mapped drive is kept by its network path, so it
keeps working when the letter changes, and Settings shows it the way you know it ("Z:\Music (\\nas\share\Music)").
Each network folder says what its share is doing: checked and online, waking up while its disks spin up, slow, needs
you to sign in (one click opens the Windows sign-in; Wavee never sees the password) or offline with its songs kept and
a Retry. Songs on a share are read ahead of playback, so a slow or sleeping NAS buffers instead of stalling the song,
and a song on a share that is offline is refused at once instead of freezing playback. In Classic, WinUI and Zune.
(#240)
