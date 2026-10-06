# DeathMessage

Says what killed a player, to everyone: "Astrid was killed by Troll ★1." instead of "Astrid has died."

One DLL, installed in the players' games and on the server.

## What it does

**In a player's game**

- Remembers the last hits your own character takes and, when you die, turns the killing one into
  a cause: who or what, the damage, and what was left of your health.
- Shows the cause on screen and keeps the last 30 deaths in
  `BepInEx/config/liekos47.deathmessage.deaths.txt`. The console command `deaths` lists the last
  10, `deaths hits` adds the hits before each.
- Reports the cause to the server.
- Shows other players' deaths as a chat line (or in the top-left feed).

**On the server**

- Puts the player's name in front of the reported cause and tells the other players. Players
  without the mod get the line in their top-left feed.
- When [DiscordConnector](https://thunderstore.io/c/valheim/p/nwesterhausen/DiscordConnector/)
  is installed, swaps the cause into the death message it posts. Whether deaths are posted, the
  position, the first-death message, the stats and the webhook stay DiscordConnector's settings.

Only the dying player's game knows what hit them, so a death by a player without the mod is
announced by nobody and goes to Discord as DiscordConnector's usual message.

## Settings

`BepInEx/config/liekos47.deathmessage.cfg`. The server re-reads it within 30 seconds of a change.

| Section | Setting | Default | |
|---|---|---|---|
| Client | LocalLog | true | On-screen cause and history file |
| Client | SendToServer | true | Report your deaths to the server |
| Client | OthersInChat | true | Other players' deaths as a chat line |
| Client | OthersTopLeft | false | Other players' deaths in the top-left feed |
| Server | Announce | true | Tell the other players |
| Server | AnnounceFormat | `{player} {cause}.` | Also `{biome}` |
| Server | AnnounceToUnmodded | true | Top-left line for players without the mod |
| Server | Discord | true | Put the cause into DiscordConnector's death message |
| Server | DiscordMessage | `%PLAYER_NAME% {cause}.` | Also `{biome}` and DiscordConnector's own variables |
| Server | DiscordFirstDeathMessage | `%PLAYER_NAME% {cause}. That was their first death.` | |

With Valheim Vanilla Plus or Valheim God Mode installed, their death log already shows the cause
on screen, so this mod leaves the on-screen part to them and only keeps its file and reports.

## Building

```
dotnet build DeathMessage.csproj -c Release
```

`refs\` (not committed) holds the assemblies to compile against, in a dedicated-server layout:
`valheim_server_Data\Managed\` with `assembly_valheim.dll`, `assembly_utils.dll`,
`assembly_guiutils.dll`, `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, and `BepInEx\core\`
with `BepInEx.dll` and `0Harmony.dll`.
