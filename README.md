# BattleBit Revival

Self-hosted master server infrastructure for BattleBit Remastered.

The official master servers are gone. This replaces them: you run your own master, it
lists your servers, holds player accounts and progression, and players join through a
launcher that points at your IP. No central service, no dependency on anything that can
be switched off.

> **Educational / personal project.** It doesnt really talk to a game you already own; you can even pirate it. it does not
> contain, redistribute or circumvent payment for any part of it. Fuck the Devs Btw. motherfuckers killed my favorite game.

---

## What's here

| | |
|---|---|
| **MasterServer** | The master itself: HTTP endpoints the game client calls, the TCP registry channel game servers connect to, accounts, bans, progression. Also the library the other two use. |
| **RevivalServer** | Headless server box. Hosts the master, starts a game server, gives you an operator console. |
| **Launcher** | Player box. Logs in, proxies to a remote cluster, launches the game. Doubles as a host-and-play box. |
| **patch_auth.ps1** | Patches your own game install. See below. |

Guides: [SERVER-GUIDE.md](SERVER-GUIDE.md) · [CLIENT-GUIDE.md](CLIENT-GUIDE.md) ·
[TROUBLESHOOTING.md](TROUBLESHOOTING.md) · [COMMANDS.md](COMMANDS.md) · [FILES.md](FILES.md)

راهنمای فارسی: [RAHNAMA-FA.md](RAHNAMA-FA.md)

---

## Game files are not in this repo

`GameAssembly.dll` and `global-metadata.dat` are BattleBit's, and a patched copy is still
a derivative of them. They are gitignored on purpose.

Instead, **`patch_auth.ps1` patches your own install**. It is safe to run repeatedly -
anything already done reports `already patched` - and it keeps a `GameAssembly.dll.orig`
backup. Re-run it after any game update or file verification.

Both scripts find your game folder on their own, or take `-GamePath "C:\path	o\BattleBit"`.

> **The patch offsets are specific to game build `Production 2.2.5 Hotfix`.** On any
> other build the byte checks will not match and the script reports `SKIPPED` rather than
> corrupting anything. Finding the new offsets means re-dumping with Il2CppDumper - see
> `dev/5 - dump symbols.bat`.

Every patch is documented in that script with the address, the original bytes and the
reason. They remove dead master-server auth gates, fix a crash on a missing player name,
and fix a string reader that drops every string the master sends.

---

## Building

Requires the .NET 10 SDK.

```
dotnet build _revival/RevivalServer -c Release     # server box
dotnet build _revival/Launcher      -c Release     # player box
```

Or build the downloadable packages:

```
powershell -ExecutionPolicy Bypass -File package.ps1
```

That produces, in `dist/`:

| folder | |
|---|---|
| `Server-win-x64` | master + game server + console |
| `Server-linux-x64` | **master only** - the game has no Linux build, so game servers run on Windows and register to it |
| `Client-win-x64` | player box |

Each box is **one executable** with the .NET runtime bundled inside it, plus
`server.properties`, a `docs/` folder and a `tools/` folder holding the patch scripts -
seven to nine files, rather than the ~340 runtime DLLs an ordinary publish leaves beside
the exe.

Those DLLs cannot just be moved into a subfolder: a self-contained executable loads its
runtime from its own directory, and the code locates `server.properties`,
`accounts.json` and `bans.json` next to the executable through
`Environment.ProcessPath` - so moving the exe would drag the config along with it.
Bundling keeps that path correct without touching the code.

Self-contained by default. `-FrameworkDependent` makes them far smaller but needs
.NET 10 installed.

---

## Quick start

**Hosting:** edit `server.properties`, run `Start Server.bat`, give players your IP.

**Playing:** run `Start Client.bat`, enter the host's address, pick a name and password,
press Start then Launch Client.

---

## Features

- **Your own cluster** - optional cluster password, your own server browser listing.
- **Accounts per master** - name + password, PBKDF2-SHA256 hashed, never stored in plain
  text. The same name on another cluster is a different account.
- **Progression you control** - starting rank, prestige and XP, plus XP and score
  multipliers. Multipliers apply to what is *earned*, so changing them never
  retroactively alters existing progress.
- **Optional anti-cheat** - server-side authority with an opt-in client guard, threshold
  based so one detection never bans, hardware-id bans, and export/import so owners can
  share lists (imports wait for review by default).
- **Server console** - `forcestart`, bots, size, tick rate, kick, announce.

---

## Protocol notes

The wire protocol was reverse-engineered from the shipped binary. A few things that cost
real time and are worth knowing if you work on this:

- The registry channel on 29301 does an HTTP WebSocket upgrade handshake and then speaks
  **raw `uint32`-LE length-prefixed binary** - no WebSocket framing.
- After the Hail, every server to master message is `uint64 sequence` + `byte type` +
  payload. Replies echo both. Dropping the sequence breaks the join entirely.
- The master's reply to a request echoes the **request's type byte**; there is no
  separate "accepted" code.
- The game's stream buffer includes the 9-byte packet header that this master strips
  before parsing, so absolute offsets differ by 9 between the two sides.

---

## Credits

Built with [Claude Code](https://claude.com/claude-code). Reverse engineering done with
Il2CppDumper and Capstone. ik i used ai. im not ashamed of it. i would have even sold my soul to stick it to those motherfuckers who killed the game and Fucked it up. i hope they die in a fire. i hate them. I FUCKING HATE THEM.
