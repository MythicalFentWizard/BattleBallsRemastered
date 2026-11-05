# Joining a BattleBit Revival cluster

This is the client box. It logs you in to someone's cluster and starts the game.

---

## First time

1. Run **Start Client.bat**.
2. Under **Account**, fill in:
   - **Player name** - what other players see. 2-20 characters.
   - **Password** - at least 8 characters, must contain a number.
3. Under **Cluster**, fill in:
   - **Address** - the IP the server owner gave you.
   - **Cluster password** - only if they set one. Otherwise leave blank.
4. Press **Start**, then **Launch Game**.
5. Pick the server in the browser and join.

The **Log** button at the bottom opens the connection log. You do not need it unless
something goes wrong, which is why it starts closed - but it is the first thing to look
at if a join fails, and the same text is written to `launcher_log.txt` beside the exe.

Your account is created the first time you log in. **Remember the password** - there is
no reset. If you lose it the owner has to delete your account, and your progress goes
with it.

---

## Your account is per cluster

Each server owner runs their own master, with its own accounts and its own progression.
The same name on a different cluster is a different account with separate progress. Two
people on the same cluster cannot share a name.

---

## The anti-cheat guard

The **Enable anti-cheat guard** checkbox is off unless you tick it.

With it on, the launcher watches your machine while you play and reports to the server
if it finds things cheats need: memory editors running, DLLs injected into the game, a
debugger attached. It also sends a hashed id of your PC so a ban can cover the machine
rather than just the name.

Worth knowing:

- Only a **hash** is sent. The raw details never leave your PC and are never stored.
- The hash is deliberately **not** tied to one server, so it is the same value on every
  cluster that sees it.
- The guard never blocks or bans anything on your machine. It only reports; the server
  decides.
- A single detection never bans you. It takes several of the same thing.

Some servers set `anticheat-require-guard`, in which case you cannot log in without
ticking it. That is the owner's choice, and it is shown in the error if you are refused.

If you have debugging or memory tools installed for unrelated reasons, say so to the
owner before enabling it - those are exactly what the guard looks for.

---

## Common problems

| message | what to do |
|---|---|
| `Wrong password for that name on this master server` | That name is taken on this cluster with a different password. Pick another, or ask the owner to clear it. |
| `Password must contain at least one number` | 8+ characters with a digit. |
| `Banned from this master server` | Talk to the owner. Bans are per cluster. |
| `requires the launcher's anti-cheat guard` | Tick the guard box. |
| `Could not reach the master server` | Wrong address, or their server is down. Check the IP. |
| `BUILDS DON'T MATCH` in the browser | You and the server are on different BattleBit versions. |

**Stuck on "Connecting to server and synchronizing"** - usually the server's problem, not
yours. Tell the owner and point them at their `TROUBLESHOOTING.md`.

**The game verified or updated its files** - re-run `tools\patch_auth.ps1`, or the client will
not be able to join.
