# Server console commands

Type these into the server window while it is running. Anything not recognised by the
server box is passed straight to the game's own console.

The game has its own `help` command that prints the list below — this file is that list,
plus the few commands the server box adds itself.

---

## Added by the server box

| command | what it does |
|---|---|
| `help` | Prints the server box commands, then asks the game for its own list. |
| `status` | Is the game server process alive, and its pid. |
| `stop` | Shuts down the game server **and** the master. |

`stop` is also a game command; the server box intercepts it so the master shuts down too.

---

## Admin commands

| command | what it does |
|---|---|
| `stop` | Stops the server. |
| `end game` | Ends the round. |
| `force start` / `forcestart` | Starts the round even if the server is still waiting for players. |
| `say [text]` | Says something in chat. |
| `sayto [steamid] [text]` | Says something to one player only. |
| `announce [text]` (`an`, `ann`) | Announcement to all players. |
| `ban [steamid]` | Bans a player. |
| `kick [steamid]` | Kicks a player. |
| `kick all` | Kicks everyone. |
| `notifyupdate` | Tells players the server will be updated. |
| `notifyend` | Tells players the server is going to close. |
| `scale [8vs8\|16vs16\|32vs32\|64vs64\|127vs127]` | Starts a new round at that size, if the server is waiting for players. |
| `setsize [tiny\|small\|medium\|ultra]` | Sets the size directly. `tiny` 8v8 · `small` 16v16 · `medium` 32v32 · `ultra` 127v127. |
| `squadkick [steamid]` | Kicks a player out of their squad. |
| `squaddisband [steamid]` | Disbands the squad that player is in. |
| `squadpromote [steamid]` | Promotes a player to squad leader. |
| `kill [steamid]` | Kills a player. |
| `changeteam [steamid]` | Force-changes a player's team. |
| `changeteam [steamid] a` / `b` | Force-changes to a specific team. |
| `warn [steamid] [msg]` | Warns a player. |
| `msg [steamid] [msg]` | Private message to a player. |
| `serverlog [msg] [length]` | Announces a log line from the server. |
| `setpass [password]` | Sets the server password. Empty removes it. |
| `setmaxping [ping]` | Sets the ping limit. |
| `setratelimit [true\|false]` | Rate limiting on or off. |
| `setdebug [true\|false]` | Debug mode on or off. |
| `setspeedhackdetection [true\|false]` | The game's own speed-hack detection. |
| `useroulettemapvote [true\|false]` | Roulette map voting on or off. |
| `setapitoken [token]` | Sets the Community API token. Max 512 characters. |

### Ending a round a specific way

| command | result |
|---|---|
| `endgame` | Ends the round. |
| `endgame a` | Team A wins. |
| `endgame b` | Team B wins. |
| `endgame draw` | Draw. |

---

## Debug commands

Useful for testing a server on your own.

> **Turn debug mode on first, or none of these do anything:**
>
> ```
> setdebug true
> ```
>
> The console accepts a debug command with debug mode off, prints nothing, and ignores
> it - identical to how a misspelled command behaves. `bots-per-team` in
> `server.properties` sends `setdebug true` for you.

| command | what it does |
|---|---|
| `join bot [n]` | Adds bots. |
| `join bot a [n]` / `join bot b [n]` | Adds bots to a specific team. |
| `bot fire` | Makes bots shoot. |
| `remove bot` | Removes bots. |
| `set fps [n]` | Target server tick rate. |
| `set random fps [min] [max]` | Randomises tick rate between two values — for testing under varying load. |
| `set bullet speed [float]` | Projectile speed. |
| `set time scale [1.0]` | Game speed. |
| `start sintime [speed],[offset],[scale]` | Oscillating time scale. |
| `stop sintime [speed],[offset],[scale]` | Stops it. |
| `reset socket` | Resets the sockets. |
| `clear cache` | Clears all cached user data. |
| `transfer players` | Transfers players off this server. |

---

## Testing a server by yourself

The round will not start with one player. Either:

```
forcestart
```

or add bots so it starts on its own:

```
setdebug true
join bot a 5
join bot b 5
```

The `setdebug true` is not optional - bot commands are debug commands and are silently
dropped without it.

You can also set `auto-forcestart=true` or `bots-per-team=5` in `server.properties` so it
happens every time the server starts, without typing anything.

---

## Rotation

| command | what it does |
|---|---|
| `setmaprotation [maps]` | Sets the map rotation, comma separated. |
| `setgamemoderotation [modes]` | Sets the gamemode rotation, comma separated. |

```
setmaprotation Azagor, Wakistan, Salhan, Frugis
setgamemoderotation CONQ, TDM, DOMI
```

A name the game does not recognise is reported as `Unable to find map of ...` or
`Unable to convert gamemode of ...` in the server window, and the rest of the list is
ignored — so check the spelling against the tables below.

Set them once in `server.properties` instead and the server applies them on every start:

```properties
map-rotation=Azagor, Wakistan, Salhan
gamemode-rotation=CONQ, TDM
```

### Keeping map and mode from pairing up permanently

The two lists advance independently, one step per round. If their lengths share a common
factor, every map ends up locked to the same mode forever:

```
6 maps + 3 modes  ->  map1 always CONQ, map2 always DOMI, map3 always FRONTLINE ...
```

Make the lengths **coprime** and every map eventually meets every mode, after
`maps x modes` rounds. Ten maps against three modes works as-is:

```properties
map-rotation=A, B, C, D, E, F, G, H, I, J   # 10
gamemode-rotation=CONQ, DOMI, FRONTLINE     # 3
```

Where the lengths do share a factor, repeating a mode to change the list length is enough:

```properties
map-rotation=A, B, C, D, E, F                                          # 6
gamemode-rotation=CONQ, DOMI, FRONTLINE, CONQ, DOMI, FRONTLINE, CONQ   # 7
```

6 and 7 are coprime, so the pairing repeats after 42 rounds, and the repeated entries
also weight the modes - here CONQ 3/7, the other two 2/7 each.

Also set `useroulettemapvote false` if you want the rotation respected — with map voting
on, players choose instead.

### Maps

| | | | |
|---|---|---|---|
| Azagor | Basra | Construction | District |
| Dustydew | Eduardovo | Frugis | Isle |
| Kodiak | Lonovo | MultuIslands | Namak |
| OilDunes | Outskirts | Polygon | River |
| Salhan | SandySunset | TensaTown | Valley |
| Wakistan | WineParadise | ZalfiBay | |

### Gamemodes

| name | mode | | name | mode |
|---|---|---|---|---|
| `TDM` | Team Deathmatch | | `GunGameFFA` | Gun game, free for all |
| `AAS` | Advance and Secure | | `FFA` | Free for all |
| `RUSH` | Rush | | `GunGameTeam` | Gun game, team |
| `CONQ` | Conquest | | `SuicideRush` | Suicide rush |
| `DOMI` | Domination | | `CatchGame` | Catch |
| `ELI` | Elimination | | `Infected` | Infected |
| `INFCONQ` | Infantry Conquest | | `CashRun` | Cash run |
| `FRONTLINE` | Frontline | | `VoxelFortify` | Voxel fortify |
| `CaptureTheFlag` | Capture the flag | | `VoxelTrench` | Voxel trench |
| `Invasion` | Invasion | | `OneLife` | One life |

Not every mode works on every map or at every size — the game reports
`Coulnd't find any TDM/InfConq with size of ...` when a combination has no valid spawns,
and falls back. Stick to the common modes (`CONQ`, `TDM`, `DOMI`, `INFCONQ`, `RUSH`,
`FRONTLINE`) unless you are testing.

---

## Also accepted

These appear in the game's command table but are mostly situational:

`setsquadpoints` · `setsquad` · `sethp` · `givedamage` · `heal` · `teleport` ·
`setrole` · `setxp` · `set time` · `set test value` · `update words` ·
`setloadingscreentext` · `setrulesscreentext` · `msgf` · `msgforce` ·
`force transfer players` · `pingpong`

---

## Notes

- **Steam IDs**, not player names, for anything taking `[steamid]`.
- `ban` here is the **game's** ban, which lasts as long as the server runs. The master
  server's ban list is separate, survives restarts, and is what
  `anticheat-enabled` writes to — see [SERVER-GUIDE.md](SERVER-GUIDE.md).
- `setpass` changes the **game server's** password, not your cluster password. The
  cluster password lives in `server.properties`.
- `setspeedhackdetection` is the game's own check, unrelated to the optional anti-cheat
  in this project.
- Commands sent before the map has finished loading are ignored. The server box waits
  `startup-command-delay` seconds (default 25) before sending the ones from
  `server.properties`.
