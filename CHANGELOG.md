## 0.1.3

First release.

- Works out what killed you from the last hits your character took, shows it in the middle of
  the screen and keeps a history (`/deaths`, `/deaths hits`).
- Shouts the cause and the place, so it is a chat line for every player, with the mod or without.
- The server shows the other players a message in the top-left feed (or the centre).
- With DiscordConnector on the server, the Discord death message carries the cause and where the
  player died, and goes out at the moment of death instead of after the respawn.
- Optional, off by default: a shorter wait between dying and respawning (`DeathViewSeconds`,
  `RespawnLoadSeconds`), and `/grave` to pay coins to return to where you last died
  (`GraveTeleport`, set by the server).
