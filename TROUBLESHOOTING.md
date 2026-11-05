# Troubleshooting

Every problem in here is one that actually happened while building this, with the real
cause rather than a guess.

**Read the logs first.** Most of these announce themselves clearly in one of:

| log | what it covers |
|---|---|
| `server_console.log` | The server box: master startup, logins, bans, packets. |
| `game_server.log` | Unity's log for the game server. Crashes and exceptions. |
| `log_30000_*.txt` in your **BattleBit folder** | The game server's own gameplay log. Where "Connected to master server" and the player-connection errors appear. Find it by **newest modified**, not by the name - the timestamp in the filename can be stale. |
| `client_log.txt` | The player's client. |

---

## Server sits forever on "Connecting to master server"

The master's port is bound but nothing is accepting on it.

Almost always: the master was started, then stopped, and the listening socket was never
released - so the game server's connection succeeds at the TCP level and then nothing
ever services it. It hangs with nothing in any log, which is what makes it confusing.

**Fix:** stop everything, confirm nothing is still listening, start again.

```
netstat -ano | findstr "29300 29301"
```

If something is listed and the server box is not running, kill that process.

A failed login used to leave the master in exactly this half-dead state. That is fixed,
but the symptom is worth recognising if you ever see it again.

---

## Players cannot see your server in the browser

Work down this list:

1. **Is the game server registered?** `server_console.log` should contain a `hail:` line
   with your server name. No hail means the game server never reached the master.
2. **Build mismatch.** The browser shows `BUILDS DON'T MATCH` and refuses to join if the
   version string differs. Everyone must be on the same BattleBit build.
3. **Ports.** `29300` and `29301` TCP forwarded and allowed through the firewall.
4. **Wrong address.** Players need your **public** IP, not `127.0.0.1`. Check with
   whatismyip. On the same LAN, use the LAN IP.

---

## "EULA is not accepted"

The game server reads `eula.txt` **from its working directory**, which is your BattleBit
folder - not the server box folder. Open it and set:

```
Accept EULA & TOS=True
```

Symptom if you miss it: the server runs, registers, looks completely fine, and silently
refuses every player.

---

## Round never starts / "Waiting for 2 players"

The game server waits for a minimum player count. For solo testing:

- Type `forcestart` in the server window, or
- Set `auto-forcestart=true` in `server.properties`, or
- Add bots: `join bot a 1`

There is no minimum-players setting in the game to lower - `forcestart` is the way.

---

## Player cannot log in

| message | cause |
|---|---|
| `Wrong password for that name on this master server` | That account exists with a different password. Accounts are per cluster. Pick another name, or delete the entry from `accounts.json`. |
| `Password must contain at least one number` | Rules are 8+ characters with a digit. |
| `Banned from this master server` | See below. |
| `requires the launcher's anti-cheat guard` | `anticheat-require-guard=true`. The player ticks the guard box in their launcher. |
| `Could not reach the master server` | Wrong IP, port not open, or the server is not running. |

**Forgot a password?** There is no recovery by design. Delete that account's entry from
`accounts.json` and let them create it again. They lose their progress.

---

## Someone was banned by mistake

Open `bans.json`, delete the entry, restart the server.

Then raise `anticheat-reports-to-ban` or switch off the detector that fired. Each entry
records which detector caused it and what it saw, so you can tell which.

A false positive usually means a legitimate overlay or security tool. Add its DLL to
`IgnoredModules` in the player's `guard.json`, or its process name out of
`FlaggedProcesses`.

**If you develop on the same machine you play on**, prune `FlaggedProcesses` - it ships
flagging debuggers and memory editors like x64dbg and Cheat Engine, which are normal
tools to have installed.

---

## Imported bans did nothing

That is the default. They wait in `bans.pending.json` for you to read them. Set
`anticheat-auto-apply-imported-bans=true` if you trust the source.

Entries below `anticheat-minimum-import-confidence` (default 80) are dropped and never
reach the pending file.

---

## I want to play on a server running on this same PC

Use the **client launcher** with **"Host the master on this PC"** ticked. That hosts the
master inside the launcher and needs no proxy, and it still listens on all addresses, so
other people can join you at the same time.

Do **not** point the client launcher at `127.0.0.1` with that box unticked. The launcher
proxies to a remote master by binding the master's ports on `127.0.0.1` and forwarding
to the address you gave it - so a local address makes it forward to itself, and every
connection it accepts opens another. The launcher now refuses this and says so; older
builds opened thousands of sockets instead.

The **server launcher** is for a server you are not playing on from the same machine: a
box for other people, or a VPS. It and the client launcher cannot both host, because
only one of them can hold port 29300.

---

## My name is blank, or the scoreboard calls me "You"

**Streamer mode.** It is a game setting, not anything to do with the master server: it
hides player names, blanks the name in the main menu, and renders your own scoreboard
row as "You". Turn it off in the game's options.

Worth knowing before you go digging, because it looks exactly like the master failing to
send a username. If you want to rule that out, the server window logs what it sent:

```
seq 12 GetPlayerStats 7656119... -> stats as "Ethan" (289b, 44 progress params)
```

A name in that line means the master did its job and the problem is on the client.

---

## The game crashes when a player joins

Check the **gameplay log** in your BattleBit folder (newest by modified time) for a
`NullReferenceException`. The known one is in `OnClientConnecting`, caused by a null
player name, and is already patched by `tools\patch_auth.ps1`.

If you reinstalled or verified game files, the patches were overwritten. Re-run both,
from the `tools` folder of whichever box you have:

```
tools\patch_auth.ps1
tools\patch_languages.ps1
```

It is safe to run repeatedly - it reports `already patched` for anything still in place.

---

## Tab/scoreboard is very laggy

Usually a player count far higher than the server is actually running - the scoreboard is
rendering hundreds of rows. Check that progression values are sane in `accounts.json`;
absurd stat values can push the UI into rendering a full 127v127 scoreboard.

---

## Player name shows as a long number

A known limitation. If the master cannot deliver a name, the UI falls back to the raw
player id. Check that the player logged in through the launcher - without a session the
master has no name to send.

---

## Starting over

| to reset | delete |
|---|---|
| one player's progress | their entry in `accounts.json` |
| all accounts and progress | `accounts.json` |
| all bans | `bans.json` and `bans.pending.json` |
| all settings | `server.properties`, `anticheat.json`, `progression.json` |

They are all recreated with defaults on the next start. Back up `accounts.json` first -
it is the only file holding anything you cannot regenerate.
