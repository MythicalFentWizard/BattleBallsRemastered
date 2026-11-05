# What every file here is for

Four categories: **give to players**, **put on a server**, **build from**, and
**developer-only**. If a file is not listed, it is not needed.

---

## Give to players

Everything a player needs is one folder. Zip it and send it.

| | |
|---|---|
| `dist/Client-win-x64/` | The whole player download. Self-contained - no .NET install needed. |

Inside it: `Start Client.bat`, `RevivalLauncher.exe`, `CLIENT-GUIDE.md`,
`TROUBLESHOOTING.md`, and the two patch scripts they run once against their own game.

There is also `Start Client (host and play).bat` in there, for someone who wants to run
a cluster from their gaming PC rather than a separate server.

---

## Put on a server

| | |
|---|---|
| `dist/Server-win-x64/` | Windows host: master **and** game server, with an operator console. |
| `dist/Server-linux-x64/` | Linux host: **master only**. BattleBit has no Linux build, so game servers still run on Windows and register to this. |

Both self-contained. Edit `server.properties`, run `Start Server.bat` (or
`./start-server.sh`), done.

The files a running server creates - and which of them matter:

| file | back it up? |
|---|---|
| `accounts.json` | **Yes.** Password hashes and every player's progress. The only file you cannot regenerate. |
| `bans.json`, `bans.pending.json` | If you care about your ban list. |
| `server.properties` | Only if you have tuned it. Every setting lives here now - there is no separate progression or anti-cheat file. |
| `server_console.log`, `game_server.log` | No. Debugging only. |

---

## Build from

Source. You only need these if you are changing the code; `package.ps1` turns them into
the `dist/` folders above.

| | |
|---|---|
| `MasterServer/` | The master: game-facing HTTP endpoints, the TCP registry channel, accounts, bans, progression, anti-cheat. Also the shared library the other two use. |
| `RevivalServer/` | The headless server box. |
| `Launcher/` | The player box, and the client-side anti-cheat guard. |
| `package.ps1` | Builds all three downloads. `-FrameworkDependent` for small folders that need .NET 10. |
| `README.md`, `LICENSE`, `.gitignore` | Repo housekeeping. |

**Guides** live at the root and are copied into the boxes at package time - edit them
here, not in `dist/`:
`SERVER-GUIDE.md` · `CLIENT-GUIDE.md` · `TROUBLESHOOTING.md`

**Patch scripts** are also copied into the boxes. Both are needed on a fresh install:

| | |
|---|---|
| `patch_auth.ps1` | Removes the dead master-server auth gates, fixes a crash on a missing player name, fixes a string reader that drops everything the master sends, and forces the name read to the right offset. Safe to re-run; keeps a `.orig` backup. |
| `patch_languages.ps1` | Redirects the language download to local files. **Without it every string in the game renders as `MISSING [1277]`.** |

Re-run both after any game update or file verification, or the client will not join.

---

## Developer only - `dev/`

Not needed to run or play anything. Reverse-engineering and debugging tools.

| | |
|---|---|
| `dev/tools/` | Il2CppDumper. |
| `dev/dump/` | Its output - `dump.cs` and `script.json`. ~268 MB, and how every offset in this project was found. Regenerate with `5 - dump symbols.bat`. |
| `dev/*.bat` | Numbered helpers from early development: start the master, client or server by hand, collect logs, dump symbols. Mostly superseded by the `dist/` boxes. |
| `dev/capture_join.ps1` | Captures the join handshake on the wire. |
| `dev/status_probe.ps1` | Probes master endpoints. |
| `dev/stub_master.ps1` | The original PowerShell stand-in master, before the C# one existed. Kept for reference. |
| `dev/patch_gameassembly.py`, `dev/patch_metadata.py` | Byte-patch helpers used while working out the patches. |
| `dev/workflows/` | Scratch notes. |

---

## Useless - `_archive/`

Moved out of the way rather than deleted, so nothing is lost. **Safe to delete entirely.**

| | |
|---|---|
| `_archive/captures/` | `join_capture.txt`, `status_probe_results.txt` - the wire captures the whole protocol was read off. Nothing uses them, but they are the evidence behind it. |
| `_archive/graphify-out/` | Output from an indexing tool, not used by anything. |

The development logs, the stray `eula.txt` / `steam_appid.txt` copies and the assistant
transcript folders were deleted - about 35 MB of regenerable files and duplicates.

---

## Quick answers

**Someone wants to play on my server** → zip `dist/Client-win-x64/`, tell them your IP.

**I want to host** → `dist/Server-win-x64/`, edit `server.properties`, run it.

**I want to host on a VPS** → `dist/Server-linux-x64/`, and run game servers on Windows
boxes pointed at it.

**I changed the code** → `package.ps1`, then redistribute the folders.

**I want to change a setting** → `server.properties`, in the server folder. That is the
only config file. Progression, anti-cheat, rotation, ports and passwords are all in it.

**I'm low on disk** → delete `_archive/` (8 MB) and `dev/dump/` (268 MB). The dump
regenerates from `dev/5 - dump symbols.bat`. The `bin/obj` folders inside each project
are build output and also regenerate.
