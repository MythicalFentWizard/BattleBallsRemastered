using System.Globalization;
using System.Text;

namespace MasterServer;

/// Everything a server owner sets, in one plain key=value file next to the server
/// executable. Deliberately not JSON: this is the file people edit by hand, so it keeps
/// its comments and tolerates being half-filled-in.
///
/// Unknown keys are preserved on save, and anything missing falls back to the default,
/// so a properties file from an older build still loads.
public sealed class ServerProperties
{
    readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    readonly string _path;

    ServerProperties(string path) => _path = path;

    public string FilePath => _path;

    public static ServerProperties LoadOrCreate(string path)
    {
        var props = new ServerProperties(path);
        if (!File.Exists(path))
        {
            try { File.WriteAllText(path, Template); } catch { }
        }

        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or '!') continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                props._values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        catch { }
        return props;
    }

    public string Get(string key, string fallback = "") =>
        _values.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

    public void Set(string key, string value) => _values[key] = value ?? "";
    public void Set(string key, bool value) => Set(key, value ? "true" : "false");
    public void Set(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));
    public void Set(string key, double value) => Set(key, value.ToString("0.##", CultureInfo.InvariantCulture));

    /// Writes the current values back over the existing file, line by line, so the
    /// comments survive. This file is the one a server owner edits by hand and its
    /// comments are most of its documentation - regenerating it from the template would
    /// throw away anything they added, and serialising the dictionary would throw away
    /// all of it.
    ///
    /// A key that is not already in the file is appended, so a setting added in a later
    /// build still lands somewhere sensible rather than being silently dropped.
    public void Save()
    {
        var lines = File.Exists(_path)
            ? File.ReadAllLines(_path).ToList()
            : Template.Replace("\r\n", "\n").Split('\n').ToList();

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is '#' or '!') continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            var key = trimmed[..eq].Trim();
            if (!_values.TryGetValue(key, out var value)) continue;
            lines[i] = $"{key}={value}";
            written.Add(key);
        }

        foreach (var pair in _values)
            if (!written.Contains(pair.Key))
                lines.Add($"{pair.Key}={pair.Value}");

        // Same tmp-then-move as the account store: a half-written settings file would
        // lose the owner's whole configuration.
        var tmp = _path + ".tmp";
        File.WriteAllLines(tmp, lines);
        File.Move(tmp, _path, overwrite: true);
    }

    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public double GetDouble(string key, double fallback) =>
        double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public bool GetBool(string key, bool fallback) =>
        bool.TryParse(Get(key), out var v) ? v : fallback;

    // ==================== Mapping onto the running config ====================

    public MasterConfig ToMasterConfig() => new()
    {
        MasterHost = "127.0.0.1",
        ClusterName = Get("cluster-name", "Revival Cluster"),
        HttpPort = GetInt("master-http-port", 29300),
        RegistryPort = GetInt("master-registry-port", 29301),
        Password = Get("cluster-password"),
        RequireAccounts = GetBool("require-accounts", true),
        HostMaster = true,
    };

    /// Returns a warning for every value it could not make sense of, so the caller can
    /// put them in front of the owner instead of starting up as if the file were clean.
    public IEnumerable<string> ApplyTo(AntiCheatConfig ac)
    {
        ac.Enabled = GetBool("anticheat-enabled", ac.Enabled);
        ac.RequireGuard = GetBool("anticheat-require-guard", ac.RequireGuard);
        ac.ReportsToBan = GetInt("anticheat-reports-to-ban", ac.ReportsToBan);
        ac.ReportWindowMinutes = GetInt("anticheat-window-minutes", ac.ReportWindowMinutes);
        ac.BanByHardwareId = GetBool("anticheat-ban-by-hardware-id", ac.BanByHardwareId);
        ac.AutoApplyImportedBans = GetBool("anticheat-auto-apply-imported-bans", ac.AutoApplyImportedBans);
        ac.MinimumImportConfidence = GetInt("anticheat-minimum-import-confidence", ac.MinimumImportConfidence);

        foreach (var problem in ac.ApplyDetectorsOff(Get("anticheat-detectors-off")))
            yield return $"anticheat-detectors-off: {problem}";
    }

    /// As above: the caller shows the warnings rather than swallowing them.
    public IEnumerable<string> ApplyTo(ProgressionConfig p)
    {
        p.StartingRank = (uint)Math.Max(0, GetInt("progression-starting-rank", (int)p.StartingRank));
        p.StartingPrestige = (uint)Math.Max(0, GetInt("progression-starting-prestige", (int)p.StartingPrestige));
        p.StartingExp = (uint)Math.Max(0, GetInt("progression-starting-exp", (int)p.StartingExp));
        p.XpMultiplier = GetDouble("progression-xp-multiplier", p.XpMultiplier);
        p.ScoreMultiplier = GetDouble("progression-score-multiplier", p.ScoreMultiplier);

        foreach (var problem in p.ApplyClassScores(Get("progression-starting-class-scores")))
            yield return $"progression-starting-class-scores: {problem}";
    }

    /// The command line for BattleBit.exe in dedicated-server mode. Only flags the
    /// binary actually parses are emitted - anything else is silently ignored by the
    /// game, which makes typos hard to spot.
    public string GameServerArguments(string logFile)
    {
        var sb = new StringBuilder("-batchmode -nographics");
        sb.Append($" -Port={GetInt("server-port", 30000)}");
        sb.Append($" -Hz={GetInt("server-hz", 60)}");
        sb.Append($" -AntiCheat={Get("game-anticheat", "None")}");
        sb.Append($" -MaxPing={GetInt("max-ping", 999)}");
        sb.Append($" -VoxelMode={GetBool("voxel-mode", false).ToString().ToLowerInvariant()}");
        sb.Append($" -Name={Get("server-name", "RevivalServer")}");

        var firstMap = Get("first-map");
        if (firstMap.Length > 0) sb.Append($" -FirstMap={firstMap}");
        var firstGamemode = Get("first-gamemode");
        if (firstGamemode.Length > 0) sb.Append($" -FirstGamemode={firstGamemode}");
        var size = Get("fixed-size");
        if (size.Length > 0) sb.Append($" -FixedSize={size}");
        if (!GetBool("save-stats", true)) sb.Append(" -SaveStats=false");

        sb.Append($" -logFile \"{logFile}\"");
        return sb.ToString();
    }

    /// Console commands to send once the server is up, newest-first in the file order.
    public IEnumerable<string> StartupCommands()
    {
        // setsize only knows tiny/small/medium/ultra, which covers 8v8, 16v16, 32v32
        // and 127v127 - there is NO setsize name for 64v64. The scale command is the
        // only route to it, so an explicit NvN value is sent that way instead.
        var size = Get("server-size");
        if (size.Length > 0)
            yield return size.Contains("vs", StringComparison.OrdinalIgnoreCase)
                      || size.Contains("v8", StringComparison.OrdinalIgnoreCase)
                ? $"scale {size}"
                : $"setsize {size}";

        // Rotation first, so the size change does not start a round on the old one.
        if (Clean(Get("map-rotation")) is { Length: > 0 } maps)
            yield return $"setmaprotation {maps}";
        if (Clean(Get("gamemode-rotation")) is { Length: > 0 } modes)
            yield return $"setgamemoderotation {modes}";

        var bots = GetInt("bots-per-team", 0);
        if (bots > 0)
        {
            // "join bot" is under Debug Commands in the game's own help, and debug
            // commands are ignored unless debug mode is on. Without this the bot
            // commands are accepted by the console and silently do nothing - no error,
            // no bots, which is exactly how it looks when the syntax is wrong.
            yield return "setdebug true";
            yield return $"join bot a {bots}";
            yield return $"join bot b {bots}";
        }

        if (GetBool("auto-forcestart", false)) yield return "forcestart";

        foreach (var extra in Get("startup-commands").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (extra.Trim().Length > 0) yield return extra.Trim();
    }

    /// Normalises a comma separated list: trims each entry, drops blanks, and rejoins
    /// with "," and NO space.
    ///
    /// The game splits these lists on the comma but does not trim what it gets, so
    /// "Azagor, Wakistan" makes it look for a map called " Wakistan" and report
    /// "Unable to find map of  Wakistan" - leaving a silently truncated rotation.
    static string Clean(string list) =>
        string.Join(",", list.Split(',', StringSplitOptions.RemoveEmptyEntries)
                              .Select(x => x.Trim())
                              .Where(x => x.Length > 0));

    public const string Template = """
        # ============================================================
        #  BattleBit Revival - server.properties
        #
        #  Edit, save, restart the server. Lines starting with # are
        #  comments. Anything left blank falls back to the default.
        #  See SERVER-GUIDE.md for what each of these does.
        # ============================================================

        # ---------- Cluster (your master server) ----------
        # The name players see for your cluster.
        cluster-name=Revival Cluster
        # Ports the master listens on. Players connect to master-http-port.
        master-http-port=29300
        master-registry-port=29301
        # Leave blank for an open cluster, or set a password players must enter.
        cluster-password=
        # Require players to log in with an account before they can get a token.
        require-accounts=true
        # Where players over the internet are sent to join a game server running on
        # this machine: your public IP or a DNS name. Leave blank on a LAN - players
        # there are sent to whatever address they reached the master on.
        public-address=

        # ---------- Game server ----------
        # BattleBit ships no Linux dedicated server, so on Linux this box runs the
        # master only: accounts, bans, progression and the server browser. Windows
        # machines then run game servers that register to it. Leave blank to decide
        # automatically (on = Windows, off = anything else).
        run-game-server=
        # Where BattleBit.exe lives. Blank means "look in the folders above this one",
        # which is right if you unzipped this inside your BattleBit install.
        game-path=
        server-name=RevivalServer
        server-port=30000
        # Tick rate: 30-240. Higher is smoother and costs more CPU and bandwidth.
        server-hz=60
        # tiny=8v8, small=16v16, medium=32v32, ultra=127v127
        # For 64v64 use the explicit form: server-size=64vs64
        # (there is no setsize name for it, so it is sent via the scale command, which
        #  only takes effect while the server is waiting for players)
        server-size=tiny
        # Reject players above this ping. 999 disables it.
        max-ping=999
        # The game's own anti-cheat: None, EAC or FaceIt. Leave None for a private cluster.
        game-anticheat=None
        voxel-mode=false
        # Optional starting map and mode, e.g. Azagor / TDM. Blank lets the game choose.
        first-map=
        first-gamemode=
        # Set false to stop the server asking the master to save player stats.
        save-stats=true

        # ---------- Rotation ----------
        # Comma separated. Leave blank to let the game use its own rotation.
        # Valid names are listed in COMMANDS.md - a name the game does not recognise is
        # reported as "Unable to find map of ..." in the server window.
        #   maps: Azagor Basra Construction District Dustydew Eduardovo Frugis Isle
        #         Kodiak Lonovo MultuIslands Namak OilDunes Outskirts Polygon River
        #         Salhan SandySunset TensaTown Valley Wakistan WineParadise ZalfiBay
        map-rotation=
        #   modes: TDM AAS RUSH CONQ DOMI ELI INFCONQ FRONTLINE GunGameFFA FFA
        #          GunGameTeam SuicideRush CatchGame Infected CashRun VoxelFortify
        #          VoxelTrench CaptureTheFlag Invasion OneLife
        gamemode-rotation=

        # Log every packet, with hex. Leave false unless you are debugging the
        # protocol: the registry polls several times a second and this turns that into
        # megabytes of console an hour.
        log-packets=false

        # ---------- Startup ----------
        # Start the round without waiting for the minimum player count. Handy for testing.
        auto-forcestart=false
        # Bots added to each team on startup. 0 for none.
        bots-per-team=0
        # Extra console commands, separated by semicolons. e.g. set fps 60;kick all
        startup-commands=

        # ---------- Anti-cheat (optional, off by default) ----------
        # See SERVER-GUIDE.md. This is the server-side system; the client-side guard
        # is opt-in by each player unless anticheat-require-guard is true.
        anticheat-enabled=false
        # Refuse logins from players who have not enabled the guard.
        anticheat-require-guard=false
        # Reports of the same detection, inside the window, before a ban is issued.
        anticheat-reports-to-ban=3
        anticheat-window-minutes=30
        # Ban the machine as well as the account. Hardware ids are spoofable.
        anticheat-ban-by-hardware-id=true
        # Detectors to switch off, comma separated. Blank leaves them all on. A detector
        # that is off is still logged, it just never counts toward a ban.
        #   known-cheat-process  injected-module  debugger-attached  game-file-modified
        # game-file-modified ships off because patch_auth.ps1 edits GameAssembly.dll by
        # design, so it would flag every correctly set up player. Only turn it on if you
        # distribute an already-patched install and nobody needs to run the script.
        anticheat-detectors-off=game-file-modified
        # Imported ban lists wait in bans.pending.json for review unless this is true.
        anticheat-auto-apply-imported-bans=false
        anticheat-minimum-import-confidence=80

        # ---------- Progression ----------
        # What a brand new player starts with on this cluster.
        progression-starting-rank=0
        progression-starting-prestige=0
        progression-starting-exp=0
        # Head start per class, as class:score pairs. Blank means all six start at zero.
        # The game derives class level from score, so this is how you hand out levels.
        #   Leader  Assault  Medic  Engineer  Support  Recon
        progression-starting-class-scores=
        # Multipliers applied to what a player EARNS, so changing them never
        # retroactively inflates or wipes progress someone already has.
        progression-xp-multiplier=1.0
        progression-score-multiplier=1.0

        """;
}
