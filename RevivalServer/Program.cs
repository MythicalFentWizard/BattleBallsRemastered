using System.Diagnostics;
using MasterServer;

namespace RevivalServer;

/// Headless server box: reads server.properties, hosts the master, starts the game
/// server, and gives the operator a console to type commands into. Everything a server
/// owner needs and nothing a player does - no UI, no client launching, runs fine on a
/// box with no desktop.
static class Program
{
    static readonly string Dir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
    static readonly string LogPath = Path.Combine(Dir, "server_console.log");
    static Process? _game;

    static async Task<int> Main(string[] args)
    {
        Console.Title = "BattleBit Revival Server";
        var propsPath = Path.Combine(Dir, "server.properties");

        // --init just writes the settings file and exits, so the packaged box ships with
        // one to edit rather than only creating it on first run.
        if (args.Any(a => a.Equals("--init", StringComparison.OrdinalIgnoreCase)))
        {
            File.WriteAllText(propsPath, ServerProperties.Template);
            Console.WriteLine($"Wrote {propsPath}");
            return 0;
        }

        var props = ServerProperties.LoadOrCreate(propsPath);

        Log("BattleBit Revival - server");
        Log($"Settings: {props.FilePath}");

        // The game is Windows-only, so elsewhere this box hosts the master alone and
        // Windows machines run the game servers that register to it.
        var runGame = props.GetBool("run-game-server", OperatingSystem.IsWindows());
        string? gameRoot = null;

        if (runGame)
        {
            gameRoot = ResolveGameRoot(props);
            if (gameRoot is null)
            {
                Log("");
                Log("Could not find BattleBit.exe.");
                Log("Set game-path in server.properties to your BattleBit folder, e.g.");
                Log(@"  game-path=C:\SteamLibrary\steamapps\common\BattleBit Remastered");
                Log("");
                Log("Or set run-game-server=false to host only the master here.");
                Log("See TROUBLESHOOTING.md. Press Enter to close.");
                Console.ReadLine();
                return 1;
            }
            Log($"Game: {gameRoot}");

            // The game server reads eula.txt from its working directory and refuses to
            // accept players until it says True, silently except in its own log.
            EnsureEula(gameRoot);
        }
        else
        {
            Log("Master-only mode: hosting accounts, bans, progression and the browser.");
            Log("Game servers run on Windows and register to this master.");
        }

        var cfg = props.ToMasterConfig();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var master = Task.Run(async () =>
        {
            try { await MasterServerHost.RunAsync(cfg, Log, cts.Token, props); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"Master server stopped: {ex.Message}"); }
        });

        if (gameRoot is not null)
        {
            // Give the master its ports before the game server tries to register.
            await Task.Delay(1500);
            if (!StartGameServer(props, gameRoot)) { cts.Cancel(); return 1; }
            TailGameLog(gameRoot, cts.Token);
            await SendStartupCommands(props, cts.Token);
        }

        Log("");
        Log(gameRoot is null
            ? "Master is up. Point your game servers at this machine."
            : "Server is up. Type console commands here, or 'help' for the common ones.");
        Log("Ctrl+C or 'stop' shuts everything down.");
        Log("");

        await ConsoleLoop(cts);

        cts.Cancel();
        StopGameServer();
        await Task.WhenAny(master, Task.Delay(5000));
        Log("Stopped.");
        return 0;
    }

    // ==================== Game server ====================

    static string? ResolveGameRoot(ServerProperties props)
    {
        var configured = props.Get("game-path");
        if (configured.Length > 0)
            return File.Exists(Path.Combine(configured, "BattleBit.exe")) ? configured : null;

        var dir = Dir;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "BattleBit.exe"))) return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    static void EnsureEula(string gameRoot)
    {
        var eula = Path.Combine(gameRoot, "eula.txt");
        try
        {
            if (File.Exists(eula)
                && File.ReadAllText(eula).Contains("=false", StringComparison.OrdinalIgnoreCase))
            {
                Log("eula.txt is not accepted - the server would refuse every player.");
                Log("Read https://agreements.battlebit.cloud/GameServerTos.pdf, then set");
                Log("  Accept EULA & TOS=True   in eula.txt");
            }
        }
        catch { }
    }

    /// Follows the game's GAMEPLAY log and echoes the interesting lines into our
    /// console, so replies like "New map rotation set", "Size set to 8v8" and
    /// "Unable to find map of X" are actually seen.
    ///
    /// Three places were candidates and only one is right: the process's stdout is
    /// empty (Unity writes to -logFile instead), the -logFile holds only engine noise
    /// like shader warnings, and the replies to console commands go to a
    /// log_<port>_<timestamp>.txt in the GAME folder. Its name carries a timestamp that
    /// can be stale, so it is found by newest modified time rather than by name.
    static void TailGameLog(string gameRoot, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            long position = 0;
            string? path = null;
            var interesting = new[] { "rotation", "Unable to find", "Unable to convert",
                                      "Size set to", "password", "Ping limit",
                                      "kicked", "banned", "EULA" };
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Re-check every pass, not just when the file vanishes: the game
                    // writes a NEW log on each start, so latching onto whichever was
                    // newest at launch means following the previous run's file.
                    var newest = new DirectoryInfo(gameRoot)
                        .GetFiles("log_*.txt")
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .FirstOrDefault()?.FullName;

                    if (newest is not null && newest != path)
                    {
                        path = newest;
                        position = 0;
                    }

                    if (path is not null && File.Exists(path))
                    {
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                      FileShare.ReadWrite);
                        if (fs.Length < position) position = 0;      // log was rotated
                        fs.Seek(position, SeekOrigin.Begin);
                        using var reader = new StreamReader(fs);
                        while (await reader.ReadLineAsync() is { } line)
                            if (interesting.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)))
                                Log("[game] " + line.Trim());
                        position = fs.Position;
                    }
                }
                catch { }
                await Task.Delay(1000, ct);
            }
        }, ct);
    }

    static bool StartGameServer(ServerProperties props, string gameRoot)
    {
        var logFile = Path.Combine(Dir, "game_server.log");
        var args = props.GameServerArguments(logFile);
        Log($"Starting game server: {args}");
        try
        {
            _game = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(gameRoot, "BattleBit.exe"),
                Arguments = args,
                WorkingDirectory = gameRoot,
                UseShellExecute = false,
                RedirectStandardInput = true,   // UnityConsole reads commands from here
                // Capture the game's replies too. Without this, acknowledgements like
                // "New map rotation set" and errors like "Unable to find map of X" went
                // nowhere, so there was no way to tell whether a command was accepted.
            });
            return _game != null;
        }
        catch (Exception ex)
        {
            Log($"Could not start the game server: {ex.Message}");
            return false;
        }
    }

    static void StopGameServer()
    {
        try
        {
            if (_game is { HasExited: false })
            {
                Send("stop");
                if (!_game.WaitForExit(5000)) _game.Kill(entireProcessTree: true);
            }
        }
        catch { }
    }

    /// Writes a line to the game server's stdin. UnityConsole picks it up with
    /// Console.ReadLine and hands it to ServerHandler.OnConsoleCommand.
    static bool Send(string command)
    {
        if (_game is null || _game.HasExited) return false;
        try
        {
            _game.StandardInput.WriteLine(command);
            _game.StandardInput.Flush();
            return true;
        }
        catch { return false; }
    }

    static async Task SendStartupCommands(ServerProperties props, CancellationToken ct)
    {
        var commands = props.StartupCommands().ToList();
        if (commands.Count == 0) return;

        // The game has to finish loading its map before it will accept commands.
        Log("Waiting for the game server to come up before sending startup commands...");
        await Task.Delay(TimeSpan.FromSeconds(props.GetInt("startup-command-delay", 25)), ct);

        foreach (var command in commands)
        {
            if (ct.IsCancellationRequested) return;
            Log($"> {command}");
            Send(command);
            await Task.Delay(500, ct);
        }
    }

    // ==================== Operator console ====================

    static async Task ConsoleLoop(CancellationTokenSource cts)
    {
        while (!cts.IsCancellationRequested)
        {
            var line = await Task.Run(Console.ReadLine);
            if (line is null) { await Task.Delay(500); continue; }

            var command = line.Trim();
            if (command.Length == 0) continue;

            switch (command.ToLowerInvariant())
            {
                case "stop":
                case "quit":
                case "exit":
                    return;

                case "help":
                    PrintHelp();
                    // The game has its own help with the full admin and debug command
                    // list. Intercepting "help" outright hid it, so pass it along too.
                    Send("help");
                    continue;

                case "status":
                    Log(_game is { HasExited: false }
                        ? $"Game server running (pid {_game.Id}). Master on the configured ports."
                        : "Game server is NOT running.");
                    continue;
            }

            if (!Send(command))
                Log(_game is null
                    ? "Master-only mode - there is no game server here to send that to."
                    : "Game server is not running - cannot send that.");
        }
    }

    static void PrintHelp()
    {
        Log("");
        Log("  Added by this server box:");
        Log("    status              is the game server alive");
        Log("    stop                shut down the game server AND the master");
        Log("");
        Log("  Most used:");
        Log("    forcestart          start the round without waiting for players");
        Log("    join bot a 5        add 5 bots to team A (b for team B)");
        Log("    remove bot          remove bots");
        Log("    setsize tiny        tiny | small | medium | ultra");
        Log("    set fps 60          server tick rate");
        Log("    kick all            kick everyone");
        Log("    announce <text>     message to all players");
        Log("");
        Log("  Full reference: COMMANDS.md. The game's own list follows.");
        Log("");
    }

    static void Log(string message)
    {
        var line = message.Length == 0 ? "" : $"{DateTime.Now:HH:mm:ss} {message}";
        Console.WriteLine(line);
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }
}
