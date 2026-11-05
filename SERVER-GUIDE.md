# Running a BattleBit Revival server

This is the server box. It hosts **your own master server** (the thing that lists
servers and holds player accounts) and runs **a game server** alongside it. Players
point their client box at your IP and join.

You do not need the official BattleBit servers for any of this. Your cluster is yours.

---

## Quick start

1. Put this folder anywhere. If it is **inside** your BattleBit install, it will find the
   game by itself. Otherwise set `game-path` (the launcher has a field for it).
2. Run **Server Launcher.bat**.
3. Set a cluster name and a server name, press **Save**, press **Start Server**.
4. Give players your IP address. They enter it in their client box.

That is it. Everything else below is optional tuning.

---

## Does this need the game installed?

Depends which half you are running.

| | needs a BattleBit install? |
|---|---|
| **Master only** (`run-game-server=false`) | **No.** The executable and `server.properties`, nothing else. It hosts accounts, bans, progression and the server browser, none of which touch the game. |
| **Master + game server** (`run-game-server=true`) | **Yes** - a full Windows install, and a **patched** one. |

So a cheap Linux VPS with no game anywhere can run the master for your whole cluster,
and Windows machines run the game servers that register to it. That is the only option
on Linux regardless, since BattleBit has no Linux build.

**A machine running a game server has to be patched**, not just the players. Four of the
patches are on the game-server side of the binary: the server-key check, both ReadString
guards, the player-name read position and the name sanitizer. The game server is what
talks to the master and what reads the player names it sends, so an unpatched one fails
the same ways an unpatched client does. Run `tools\patch_auth.ps1` there too.

---

## The launcher, or the file

Windows has both, and they are the same settings either way:

| | |
|---|---|
| **`Server Launcher.bat`** | A window with every setting as a checkbox, slider or dropdown, a start button, and the server console built in. |
| `Start Server (console).bat` | The same server with no window. What Linux runs, and what you want over SSH. |

The launcher reads and writes `server.properties`, so you can switch between them freely
- it keeps your comments and any key it does not have a control for. It does not host
anything itself: it runs the same `RevivalServer.exe`, so there is one server, not two.

Settings are locked while the server runs. Press **Stop Server** to change them, or use
the console box at the bottom for anything that takes effect live.

Not every key has a control. The ports, `game-path`, imported-ban handling and the
starting class scores are in the file only - open `server.properties` for those.

---

## server.properties

Edit, save, restart. Blank values fall back to the default.

### Cluster

| key | what it does |
|---|---|
| `cluster-name` | The name players see for your cluster. |
| `master-http-port` | Port players connect to. Default `29300`. |
| `master-registry-port` | Port your game servers register on. Default `29301`. |
| `cluster-password` | Blank for open. Set it and players must enter it to join your cluster at all. |
| `require-accounts` | `true` means players must log in through the launcher before they can play. |

### Game server

| key | what it does |
|---|---|
| `game-path` | Your BattleBit folder. Blank = look in the folders above this one. |
| `server-name` | Name of this game server in the browser. |
| `server-port` | Default `30000`. The server also uses `server-port + 1`. |
| `server-hz` | Tick rate, 30-240. Higher is smoother and costs more CPU and bandwidth. |
| `server-size` | `tiny` 8v8, `small` 16v16, `medium` 32v32, `ultra` 127v127. |
| `max-ping` | Reject players above this ping. `999` disables it. |
| `game-anticheat` | `None`, `EAC` or `FaceIt`. Leave `None` for a private cluster. |
| `first-map` / `first-gamemode` | Optional starting map and mode, e.g. `Azagor` / `TDM`. |
| `save-stats` | `false` stops the server asking the master to save player stats. |
| `log-packets` | Log every packet with hex. Leave `false` unless you are debugging the protocol - the registry polls several times a second, and one night with this on wrote 366,342 lines and 24 MB. |

### Rotation

| key | what it does |
|---|---|
| `map-rotation` | Comma separated map names. Blank leaves the game's own rotation alone. |
| `gamemode-rotation` | Comma separated mode names. |

```properties
map-rotation=Azagor, Wakistan, Salhan, Frugis
gamemode-rotation=CONQ, TDM, DOMI
```

Spacing does not matter here - the server normalises the list before sending it, because
the game itself does not trim around the commas and would otherwise look for a map called
` Wakistan`.

All 23 map names and all 21 gamemodes are listed in [COMMANDS.md](COMMANDS.md). A name the
game does not recognise shows up in the server window as
`[game] Unable to find map of X`, and the rest of that list is ignored - so check the
output after changing a rotation.

#### A worked 64v64 rotation

Ten large maps over the three big-team modes:

```properties
server-size=64vs64
map-rotation=Eduardovo, TensaTown, MultuIslands, Frugis, Valley, SandySunset, Isle, Lonovo, Azagor, Wakistan
gamemode-rotation=CONQ, DOMI, FRONTLINE
useroulettemapvote=false
```

Ten maps against three modes is deliberate. The lists advance independently, one step per
round, so if their lengths share a common factor every map ends up locked to one mode
forever. 10 and 3 are coprime: the pairing only repeats after 30 rounds, every map is
eventually played in all three modes, and each mode gets exactly a third of the rounds.

**If you change the mode list, keep its length coprime with your map count.** With ten
maps that means avoiding any even length and any multiple of five - 6 modes would give you
five permanent pairs, 5 would give you two. 3, 7 and 9 all work. Repeating an entry is the
easy way to both fix a length and weight a mode: `CONQ, DOMI, FRONTLINE, CONQ, DOMI,
FRONTLINE, CONQ` is 7 entries, still coprime with 10, and makes CONQ three sevenths.

The order above alternates open, vehicle-heavy maps with dense urban ones so consecutive
rounds do not feel the same. Rearrange freely - order changes the sequence, not the
coverage.

Map choices come from community tier lists rather than a formal poll. Eduardovo, Isle and
TensaTown sit at or near the top of most of them; MultuIslands is consistently praised as
a large, varied, vehicle-friendly Conquest map; SandySunset is the one map sources
explicitly single out for Frontline, because its segmented layout gives each push
somewhere worth fighting over; Azagor is a broad favourite that also suits Frontline.
Valley, Lonovo, Wakistan and Frugis are the divisive ones - top tier in some lists, bottom
in others - but they add real variety, and taste is yours to have.

> **Watch the server window the first time through.** Not every map has spawn layouts for
> every mode at every size. When a combination has none the game reports
> `Coulnd't find any FRONTLINE with size of ...` and falls back to something else. Frugis
> and Wakistan are the tighter maps here, so they are the likeliest to do it at 64v64. A
> fallback is harmless - it just means that round is not the one you asked for.

### Startup

| key | what it does |
|---|---|
| `auto-forcestart` | Start the round without waiting for the minimum player count. |
| `bots-per-team` | Bots added to each team on startup. Good for testing alone. |
| `startup-commands` | Extra console commands, separated by `;`. |
| `startup-command-delay` | Seconds to wait before sending them. Default 25, raise it on a slow box. |

### Progression

| key | what it does |
|---|---|
| `progression-starting-rank` | What a brand new player starts at. |
| `progression-starting-prestige` / `-exp` | Same, for prestige and XP. |
| `progression-starting-class-scores` | Head start per class, as `Assault:5000, Recon:2000`. Blank means all six start at zero. |
| `progression-xp-multiplier` | `2.0` is double XP. |
| `progression-score-multiplier` | Same, for score. |

Multipliers apply to what a player **earns**, so changing them never retroactively
inflates or wipes progress someone already has.

The game works out class level from class score, so `progression-starting-class-scores`
is how you hand players a head start on specific classes rather than overall rank.

**This is the only place progression is configured.** Earlier builds also kept a
`progression.json`, and `server.properties` quietly overwrote every setting the two
shared - so whichever file you edited, the other one might win. If you still have that
file the server will say so on startup; delete it. Same for `anticheat.json`.

---

## Console commands

Type these into the server window while it is running.

| command | effect |
|---|---|
| `forcestart` | Start the round immediately. |
| `join bot a 5` | Add 5 bots to team A (`b` for team B). |
| `remove bot` | Remove bots. |
| `setsize tiny` | `tiny` / `small` / `medium` / `ultra`. |
| `set fps 60` | Change tick rate live. |
| `kick all` | Kick everyone. |
| `announce <text>` | Message every player. |
| `status` | Is the game server alive. |
| `stop` | Shut down the game server and the master. |

Anything not in that list is passed straight through to the game's own console, and
`help` prints both this box's commands and the game's own full list.

**[COMMANDS.md](COMMANDS.md) is the complete reference** - admin commands (ban, kick,
squad management, force team changes, private messages), round control (`endgame a`,
`endgame draw`), and debug commands (`set bullet speed`, `set random fps`, `bot fire`).

---

## Accounts and progression

Players create an account the first time they log in to your cluster. Name and password,
at least 8 characters with a number in it.

Accounts are **per cluster**. The same name on someone else's server is a different
account with different progress. Passwords are stored as PBKDF2-SHA256 hashes, never in
plain text.

Progress lives in `accounts.json`. To wipe a player, delete their entry. To wipe the
cluster, delete the file.

---

## Anti-cheat

Off by default. Turn it on with `anticheat-enabled=true`.

It has two halves:

- **Server side** - always the authority. Holds the ban list, counts detections, decides
  who gets banned. Players cannot influence it.
- **Client side (the guard)** - optional, opt-in by each player in their launcher. It
  reports things only visible on their machine: memory editors running, DLLs injected
  into the game, a debugger attached.

A single detection never bans anyone. It takes `anticheat-reports-to-ban` reports of the
same thing inside `anticheat-window-minutes`, because overlays and security software do
produce false positives.

| key | what it does |
|---|---|
| `anticheat-enabled` | Master switch. |
| `anticheat-require-guard` | Refuse logins from players who have not enabled the guard. |
| `anticheat-reports-to-ban` | Default 3. |
| `anticheat-window-minutes` | Default 30. |
| `anticheat-ban-by-hardware-id` | Ban the machine as well as the account. |
| `anticheat-detectors-off` | Detectors to disable, comma separated. Blank leaves all four on. |

The four detectors are `known-cheat-process`, `injected-module`, `debugger-attached` and
`game-file-modified`. The last ships **off**, because `patch_auth.ps1` edits
`GameAssembly.dll` by design - leaving it on would flag every correctly set up player.
Only enable it if you hand out an already-patched install and nobody runs the script.

A misspelled detector name is reported on startup rather than ignored, because a typo
would otherwise leave the detector quietly **on** while you believed you had turned it
off.

### Sharing ban lists

Export your bans:

    http://localhost:29300/_revival/bans/export

Import someone else's by POSTing that JSON to `/_revival/bans/import`.

Both are **owner-only**: they answer requests from the server machine itself, or from
anywhere once `cluster-password` is set. Without that, a blank password would leave your
whole ban list - account names and hardware ids - readable by anyone who can reach the
port, and the import endpoint writable by them. Use the `localhost` URLs above on the
server, or set a password and send `X-Revival-Auth`.

By default imported bans land in `bans.pending.json` for you to read **before** they take
effect. Set `anticheat-auto-apply-imported-bans=true` only for sources you genuinely
trust: a shared list is a false-positive amplifier, and one bad detector on someone
else's cluster should not silently ban your players.

Entries below `anticheat-minimum-import-confidence` are dropped outright.

### What it will not do

The guard runs in user space. Anyone able to patch it can remove it, and hardware ids can
be spoofed. `anticheat-require-guard` means *no guard, no token, no join*, which raises
the bar - it does not close the door. Catching a determined cheater needs kernel-level
anti-cheat, which this is not.

---

## Networking

| port | protocol | forward it? | who connects |
|---|---|---|---|
| `29300` | TCP | **yes** | Players. Login, accounts, the server browser. |
| `30000` | UDP | **yes** | Players. The actual gameplay traffic. |
| `30001` | UDP | **yes** | Players. The game binds `server-port + 1` as well. |
| `29301` | TCP | **only if** | Game servers registering to your master. A game server on the **same machine** reaches it over `127.0.0.1` and needs no forward. |

So hosting both halves on one home box, which is the normal case, forward **three**:
`29300/TCP`, `29300/UDP` is not needed, and `30000-30001/UDP`.

Leave `29301` closed unless someone else's Windows machine runs a game server that
registers to your master. It is the channel that trusts whatever connects to it, so
there is no reason to expose it to the internet for a setup that never uses it.

Those are the defaults; if you change `master-http-port` or `server-port`, forward what
you changed them to - and remember `server-port + 1` moves with it.

Allow the same ports through Windows Firewall. Forwarding alone is not enough: the
router sends the traffic to the machine and the firewall then drops it, which looks
exactly like a broken forward.

**Bandwidth is the real limit, not CPU.** At 60Hz each player needs roughly
0.7 Mbps **upstream** from your server. A 32v32 server wants ~45 Mbps up; a full 127v127
wants well over 100 Mbps. Home connections rarely have that, which is why big servers
live on a VPS. Your own 8v8 testing is fine at home.

CPU wants **single-thread speed** rather than core count - the simulation loop is largely
single-threaded. Allow 6-8 GB RAM for a big server.

---

## Files this creates

| file | what it is |
|---|---|
| `server.properties` | Your settings. The only file you normally edit. |
| `accounts.json` | Accounts, password hashes and progression. **Back this up.** |
| `bans.json` | Your ban list. |
| `bans.pending.json` | Imported bans waiting for your review. |
| `server_console.log` | What scrolled past in the server window. |
| `game_server.log` | The game server's own log. |

`accounts.json` is the one that matters. Everything else regenerates.

---

Stuck? See **TROUBLESHOOTING.md**.
