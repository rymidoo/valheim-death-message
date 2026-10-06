# DeathMessage

[![Support me on Ko-fi](https://img.shields.io/badge/Ko--fi-Support%20the%20mod-FF5E5B?logo=ko-fi&logoColor=white)](https://ko-fi.com/liekos47)

Says what killed a player, and where - in everyone's chat, on everyone's screen and in Discord.

Vanilla Valheim tells nobody when a player dies, and a Discord bridge can only say "Astrid has
died." DeathMessage says **"Astrid was killed by Troll ★1 at (120, 34, -560)"**: fell, drowned,
burned, poisoned by a Blob, crushed by a tree, hit by another player - whatever it was.

One DLL, for the players' games and the server.

## Screenshots

In the chat box of every player, with the mod or without:

![Deaths in the in-game chat](https://raw.githubusercontent.com/liekos47/valheim-death-message/main/Screenshots/ingame-chat-broadcast.png)

In Discord, through DiscordConnector, with the cause and the place of death:

![Deaths in Discord](https://raw.githubusercontent.com/liekos47/valheim-death-message/main/Screenshots/discord-death-broadcast.png)

## What happens when you die

1. Your game works out the cause from the last hits your character took: who or what, the
   damage by type, and what was left of your health.
2. You see it in the middle of the screen: **"Killed by Troll ★1 — last hit: 42 blunt"**.
3. Your character shouts it, so it is an ordinary chat line for every player on the server:
   **"Astrid: was killed by Troll ★1 at (120, 34, -560)"**.
4. The server shows every other player **"Astrid was killed by Troll ★1 at (120, 34, -560)."**
   in the top-left message feed, which is also kept in the Compendium's message log.
5. With [DiscordConnector](https://thunderstore.io/c/valheim/p/nwesterhausen/DiscordConnector/)
   on the server, Discord gets **"Astrid was killed by Troll ★1. Coords: (120, 34, -560)"** at
   once, in place of "Astrid has died."
6. The death is added to your own history: `/deaths` in chat lists the last 10, `/deaths hits`
   adds the hits before each. The last 30 are kept in
   `BepInEx/config/liekos47.deathmessage.deaths.txt`.

Also in the mod, both off until you turn them on:

- **A shorter respawn.** The game waits a fixed 10 seconds on the death view and then at least
  8 more at your bed. Two settings in your own game shorten both.
- **`/grave`.** When the server allows it, pay coins to be taken back to where you last died.

## Who installs it

| Installed in | What you get |
|---|---|
| The dying player's game and the server | Everything above |
| The dying player's game only | The on-screen cause, the history, the shout in everyone's chat, the shorter respawn |
| The server only | Nothing yet: only a player's own game knows what hit them |

Players without the mod still read other players' deaths in chat and in the top-left feed.
Their own deaths are announced by nobody and go to Discord as DiscordConnector's usual message.

## Install

Needs [BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/).
Install it with a mod manager, or put `DeathMessage.dll` in `BepInEx/plugins/` by hand. It goes
in the game of each player who wants it, and in `BepInEx/plugins/` on the dedicated server. DiscordConnector is optional and only belongs on the server.

## Configuration

`BepInEx/config/liekos47.deathmessage.cfg`, written on the first start. The `[Client]` settings
are read in a player's game, the `[Server]` settings on the server (or in the game of a player
hosting the world). Changes to the file are picked up within 30 seconds, without a restart.

### In a player's game: `[Client]`

| Setting | Default | |
|---|---|---|
| `LocalLog` | `true` | Show the cause in the middle of the screen and keep the history |
| `ShoutDeath` | `true` | Shout the cause when you die, so it is in every player's chat |
| `ShoutFormat` | `{cause} at {pos}` | What is shouted. The game puts your name in front |
| `SendToServer` | `true` | Report your deaths to the server, for its announcement and Discord |
| `DeathViewSeconds` | `10` | Seconds on the death view before the respawn starts. The game's own is 10, the least is 1 |
| `RespawnLoadSeconds` | `8` | Least seconds waited at your spawn point. The game's own is 8, the least is 3 |

### On the server: `[Server]`

| Setting | Default | |
|---|---|---|
| `Announce` | `true` | Show the other players a message when someone dies |
| `AnnounceFormat` | `{player} {cause} at {pos}.` | The message |
| `AnnounceWhere` | `TopLeft` | `TopLeft` = the message feed. `Center` = across the middle of the screen |
| `Discord` | `true` | Put the cause and the place into DiscordConnector's death message |
| `DiscordMessage` | `%PLAYER_NAME% {cause}. Coords: {pos}` | Replaces DiscordConnector's "Player Death Message" |
| `DiscordFirstDeathMessage` | `%PLAYER_NAME% {cause}. That was their first death. Coords: {pos}` | Replaces its "Player First Death Message" |
| `GraveTeleport` | `false` | Allow `/grave` |
| `GraveTeleportItem` | `Coins` | What a trip costs, by prefab name |
| `GraveTeleportCost` | `100` | How many. 0 = free |

`[General]` has `DebugLog` (`false`), which logs every report and what is done with it.

### What goes in the messages

| | |
|---|---|
| `{cause}` | "was killed by Troll ★1", "drowned", "died from poison (from Blob)" |
| `{pos}` | Where they died: x, height, z |
| `{biome}` | "Black Forest" |
| `{player}` | The player's name. `AnnounceFormat` only |
| `%PLAYER_NAME%` and the like | DiscordConnector's own variables. The two Discord messages only |

### Discord

The mod has no webhook of its own; it works through DiscordConnector. Whether deaths are
posted at all, the first-death event, the death counts and the webhook stay DiscordConnector's
settings. What changes when a cause is known:

- The text is this mod's `DiscordMessage`, not DiscordConnector's "Player Death Message".
  Nothing is written to DiscordConnector's config files.
- The message goes out the moment the player dies. By itself DiscordConnector only notices a
  death when the player has respawned, ten or more seconds later.
- The coordinates are where the player died. DiscordConnector's own are read after the
  respawn, so they are the player's bed. They are left off these messages.
- The shouted copy of the cause is not passed on to Discord as a shout.

Turn `Discord` off, or leave `DiscordMessage` empty, to get DiscordConnector's own message back.

### A shorter respawn

`DeathViewSeconds` and `RespawnLoadSeconds` shorten the game's two fixed waits after a death,
in your own game only; 3 and 5 take the usual 18 seconds down to about 8. Loading is not
skipped: the game still holds you until the area around your spawn point is ready.

Do not set `RespawnLoadSeconds` very low on a slow connection. If you are put down before the
server has sent your base, the game finds no bed, resets your spawn point and sends you to the
start of the world. Back at 8 it behaves as vanilla.

### `/grave`

With `GraveTeleport` on, a player with the mod types `/grave` in chat to be taken to where
they last died, once per death.

- The price is taken from what the player carries. Someone who has just died carries nothing,
  as their coins are in the grave, so they fetch the price from a chest first.
- The usual portal rule applies: no trip while carrying ore and the like.
- A change to these settings reaches the players' games within two minutes.
- As with a portal, the trip and the payment happen in the player's own game.

## Other mods

- **Valheim Vanilla Plus, Valheim God Mode**: their death log already shows the cause in the
  middle of the screen, so this mod leaves that message to them.
- **DiscordConnector**: made for 3.1.3. If a later version moves things, the mod says so in
  the server log and deaths go to Discord as DiscordConnector sends them.

## Known limits

- A death by a player without the mod has no cause, in the game or in Discord.
- The shout is a real shout: the text also appears over the player's head, and a player who
  has blocked chat from someone on their platform does not see that player's line.
- `/die` and other deaths without a hit are reported as "died of unknown causes".

## Building

```
dotnet build DeathMessage.csproj -c Release
```

`refs\` (not committed) holds the assemblies to compile against, in a dedicated-server layout:
`valheim_server_Data\Managed\` with `assembly_valheim.dll`, `assembly_utils.dll`,
`assembly_guiutils.dll`, `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, and `BepInEx\core\`
with `BepInEx.dll` and `0Harmony.dll`.
