using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MasterServer;

public static class MasterServerHost
{
    /// Prelude a proxy sends on the registry port before relaying game bytes.
    public const string AuthMagic = "REVIVAL1";

    public static readonly ConcurrentDictionary<string, GameServerInfo> Servers = new();

    static Action<string> _log = _ => { };
    static MasterConfig _cfg = new();

    /// Established by sweeping all 256 values: only 0x01 keeps the connection alive.
    /// (The Community API's Accepted=2 belongs to a different protocol.)
    public const byte Accepted = 0x01;

    /// Diagnostic: fill the Accepted body with its own byte offsets, so any value the
    /// game echoes into its log reveals which offset that field lives at.
    public static bool ProbeOffsets;

    /// Accounts and saved progress for THIS master server. Each master keeps its own
    /// file, so the same player name on two masters is two unrelated accounts with
    /// separate progress.
    static AccountStore _accounts = null!;

    /// Session of the account logged in on this machine. When the launcher hosts the
    /// master itself there is no proxy to attach a session header - the master owns
    /// those ports already - so loopback requests without one are attributed to this
    /// account instead.
    public static string? LocalSession { get; set; }

    /// Builds this master server's account store and progression rules, once. Exposed
    /// so a launcher hosting the master can check credentials BEFORE starting it:
    /// starting the server and tearing it down on a bad password left the ports bound
    /// but no longer accepting, which hung the next game server forever on
    /// "Connecting to master server".
    public static AccountStore EnsureAccounts()
    {
        if (_accounts != null) return _accounts;
        var dataDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";

        // Neutral defaults; server.properties is the only thing that changes them.
        // progression.json and anticheat.json used to hold these, and server.properties
        // then overwrote every key the two shared - so whichever file the owner edited,
        // the other one might win. See LegacyConfigFiles below.
        _accounts = new AccountStore(Path.Combine(dataDir, "accounts.json"),
                                     new ProgressionConfig());

        AntiCheat = new AntiCheatConfig();
        Bans = new BanStore(Path.Combine(dataDir, "bans.json"), _cfg.ClusterName, AntiCheat);
        Detections = new DetectionTracker(AntiCheat);
        return _accounts;
    }

    /// Config files an older build wrote and this one ignores. Named rather than
    /// deleted: they are the owner's files, and one of them silently losing its
    /// settings is the whole reason they stopped being read.
    static IEnumerable<string> LegacyConfigFiles(string dataDir)
    {
        foreach (var name in new[] { "progression.json", "anticheat.json" })
        {
            var path = Path.Combine(dataDir, name);
            if (File.Exists(path)) yield return path;
        }
    }

    /// Collapses a message that repeats. The game polls for a token about once a
    /// second and keeps polling while it is refused, so the interesting fact is "this
    /// is still happening, N times now" rather than N copies of the same line.
    static readonly Dictionary<string, (DateTime When, int Count)> _repeats = new();

    static bool ShouldSay(string key, int seconds = 30)
    {
        lock (_repeats)
        {
            var now = DateTime.UtcNow;
            if (_repeats.TryGetValue(key, out var last) && (now - last.When).TotalSeconds < seconds)
            {
                _repeats[key] = (last.When, last.Count + 1);
                return false;
            }
            _repeats[key] = (now, 0);
            return true;
        }
    }

    static int Suppressed(string key)
    {
        lock (_repeats) return _repeats.TryGetValue(key, out var v) ? v.Count : 0;
    }

    /// Per-packet tracing. Off by default: the registry polls several times a second
    /// and each packet was three lines including a hex dump, which put 366,342 lines
    /// and 24 MB into one night's console log. Everything that actually happened -
    /// logins, stats requests, saves, bans, errors - is logged either way.
    public static bool LogPackets;

    /// The save payload is dumped once per run, not per packet. See the save handler.
    static bool LogSaveLayoutOnce = true;

    /// Anti-cheat is per master server and entirely opt-in; with it off, none of the
    /// reporting or banning below does anything.
    public static AntiCheatConfig AntiCheat { get; private set; } = new();
    public static BanStore Bans { get; private set; } = null!;
    static DetectionTracker Detections = null!;

    public static async Task RunAsync(MasterConfig cfg, Action<string> log,
                                      CancellationToken ct = default, ServerProperties? props = null)
    {
        _cfg = cfg;
        _log = log;

        var bind = cfg.HostMaster ? IPAddress.Any : IPAddress.Loopback;
        log($"Master server starting on {bind}:{cfg.HttpPort} (HTTP) and :{cfg.RegistryPort} (registry)");
        log(cfg.Password.Length > 0 ? "Password protection: ON" : "Password protection: OFF (open cluster)");

        var accounts = EnsureAccounts();

        // server.properties is the owner-facing file, so it wins over the json files
        // it overlaps with - those keep the settings it does not expose.
        if (props != null)
        {
            LogPackets = props.GetBool("log-packets", false);
            foreach (var problem in props.ApplyTo(AntiCheat)) log($"  WARNING  {problem}");
            foreach (var problem in props.ApplyTo(accounts.Progression)) log($"  WARNING  {problem}");
            log($"Settings loaded from {props.FilePath}");
        }
        foreach (var stale in LegacyConfigFiles(Path.GetDirectoryName(Environment.ProcessPath) ?? "."))
            log($"  NOTE  {Path.GetFileName(stale)} is no longer read - its settings live in " +
                $"server.properties now. Delete it.");
        log($"Progression: {accounts.Progression.Describe()} (edit server.properties to change it)");
        log($"Accounts: {accounts.Count} known, stored in {accounts.FilePath}");
        log(cfg.RequireAccounts
            ? "Account login: REQUIRED (players log in through the launcher)"
            : "Account login: optional");
        log($"Anti-cheat: {AntiCheat.Describe()}");
        log($"Bans: {Bans.Count} on this master, stored in {Bans.FilePath}");

        // ASP.NET logs "Request starting" and "Request finished" for every request at
        // Information level. The game polls several endpoints on a timer, so that is
        // two lines a second of framework chatter on top of anything this master says -
        // and it goes to stdout, which the server launcher shows in its console pane.
        // Warnings and errors still come through; log-packets brings the detail back.
        var builder = WebApplication.CreateBuilder();
        builder.Logging.AddFilter("Microsoft.AspNetCore",
            LogPackets ? LogLevel.Information : LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
        var httpApp = builder.Build();
        httpApp.Urls.Clear();
        httpApp.Urls.Add($"http://{bind}:{cfg.HttpPort}");
        httpApp.Use(AuthMiddleware);
        httpApp.MapGet("/_revival/cluster", GetClusterListing);
        // The default fallback pattern is {*path:nonfile}, which skips anything with a file
        // extension — that 404s the .txt/.lang localization fetches.
        httpApp.MapFallback("{*path}", HandleGameRequest);

        await Task.WhenAll(
            httpApp.RunAsync(ct),
            RunRegistryListener(bind, cfg.RegistryPort, ct));
    }

    // ==================== Auth ====================

    static async Task AuthMiddleware(HttpContext ctx, RequestDelegate next)
    {
        // Traffic from this machine is the host's own game talking directly to the
        // master it is hosting; it has no way to attach the password.
        if (_cfg.Password.Length > 0 && !IsLoopback(ctx.Connection.RemoteIpAddress))
        {
            var supplied = ctx.Request.Headers["X-Revival-Auth"].ToString();
            if (!FixedTimeEquals(supplied, _cfg.Password))
            {
                _log($"HTTP {ctx.Request.Path} REJECTED (bad password) from {ctx.Connection.RemoteIpAddress}");
                ctx.Response.StatusCode = 401;
                return;
            }
        }
        await next(ctx);
    }

    /// True when a request can be trusted as the owner's rather than a player's: it
    /// came from the server machine itself, or the cluster has a password, in which case
    /// AuthMiddleware has already checked it before this point.
    static bool OwnerRequest(HttpContext ctx) =>
        IsLoopback(ctx.Connection.RemoteIpAddress) || _cfg.Password.Length > 0;

    static bool IsLoopback(IPAddress? addr) =>
        addr != null && (IPAddress.IsLoopback(addr) ||
                         (addr.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(addr.MapToIPv4())));

    /// Compares without leaking length or content through timing.
    static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(ba), SHA256.HashData(bb));
    }

    // ==================== HTTP ====================

    static Task GetClusterListing(HttpContext ctx)
    {
        var rows = Servers.Values.Select(s => new
        {
            s.EndPoint, s.ServerName, s.MapName, s.GameMode,
            s.Port, s.CurrentPlayers, s.MaxPlayers, s.Version,
            UptimeSeconds = (int)(DateTime.UtcNow - s.ConnectedAt).TotalSeconds,
        });
        // A SteamID64 resolves straight to a Steam profile, so the roster only carries
        // them for the owner. Remote callers get the names, which they would see on the
        // scoreboard anyway, and nothing that deanonymises anyone.
        var owner = OwnerRequest(ctx);
        var players = PlayerNames.Select(kv => new
        {
            SteamId = owner ? kv.Key.ToString() : null,
            Name = kv.Value,
        });
        return ctx.Response.WriteAsJsonAsync(new { _cfg.ClusterName, Servers = rows, Players = players });
    }

    /// The master's own API, kept apart from the game's request protocol.
    static async Task HandleRevivalApi(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        var post = HttpMethods.IsPost(ctx.Request.Method);

        if (path.EndsWith("/login", StringComparison.OrdinalIgnoreCase) && post)
        {
            await HandleLogin(ctx);
            return;
        }
        if (path.EndsWith("/guard/report", StringComparison.OrdinalIgnoreCase) && post)
        {
            await HandleGuardReport(ctx);
            return;
        }
        // The ban endpoints are the owner's, not a player's. AuthMiddleware only asks
        // for a password when the owner set one, so on a default open cluster these
        // would otherwise answer the whole internet: export hands out every banned
        // account name and hardware id, and import writes to the ban store. Both are
        // documented as localhost URLs, so requiring that costs the owner nothing and
        // closes the hole for anyone who left cluster-password blank.
        if (path.Contains("/bans/", StringComparison.OrdinalIgnoreCase) && !OwnerRequest(ctx))
        {
            _log($"HTTP {path} REJECTED (owner-only) from {ctx.Connection.RemoteIpAddress}");
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new
            {
                ok = false,
                error = "Owner-only endpoint. Run it on the server machine, or set cluster-password.",
            });
            return;
        }
        if (path.EndsWith("/bans/export", StringComparison.OrdinalIgnoreCase))
        {
            await ctx.Response.WriteAsJsonAsync(new
            {
                cluster = _cfg.ClusterName,
                exportedAt = DateTime.UtcNow,
                bans = Bans.All(),
            });
            return;
        }
        if (path.EndsWith("/bans/import", StringComparison.OrdinalIgnoreCase) && post)
        {
            await HandleBanImport(ctx);
            return;
        }
        ctx.Response.StatusCode = 404;
    }

    record LoginRequest(string? Name, string? Password, string? HardwareId, bool Guard = false);
    record GuardReport(string? Session, string? Signature, string? Detail, int Confidence = 100);
    record BanImport(List<BanEntry>? Bans);

    /// Last hardware id seen for an account, so a ban can cover the machine as well as
    /// the name. Only the hash ever reaches here; the launcher never sends raw
    /// identifiers.
    static readonly ConcurrentDictionary<string, string> HardwareIds = new();

    /// The launcher's guard reporting something it found on the player's machine.
    /// Reports accumulate: one detection never bans anyone, because overlays and
    /// security products do produce false positives.
    static async Task HandleGuardReport(HttpContext ctx)
    {
        if (!AntiCheat.Enabled) { ctx.Response.StatusCode = 404; return; }

        GuardReport? req;
        try { req = await ctx.Request.ReadFromJsonAsync<GuardReport>(); }
        catch { req = null; }

        if (req?.Signature is not { Length: > 0 })
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "Malformed report." });
            return;
        }

        var account = _accounts.FromSession(req.Session);
        if (account is null)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "Not logged in." });
            return;
        }

        // A detector the owner switched off is still logged, but never counts toward a
        // ban - that way turning one off is a policy change, not a blind spot.
        if (!AntiCheat.DetectorEnabled(req.Signature))
        {
            _log($"guard: {account.Name} reported {req.Signature} (detector off, not counted)");
            await ctx.Response.WriteAsJsonAsync(new { ok = true, counted = false });
            return;
        }

        var crossed = Detections.Record(account.Name, req.Signature, out var count);
        _log($"guard: {account.Name} {req.Signature} [{count}/{AntiCheat.ReportsToBan}] {req.Detail}");

        if (crossed)
        {
            Bans.Ban(new BanEntry
            {
                HardwareId = HardwareIds.GetValueOrDefault(account.Name),
                Account = account.Name,
                Signature = req.Signature,
                Reason = $"{req.Signature} detected {count} times in {AntiCheat.ReportWindowMinutes}m",
                Evidence = Printable(req.Detail),
                // Not req.Confidence. That arrives from the reporting client, and
                // confidence is what decides whether another owner's import keeps the
                // entry - so trusting it lets a cheater self-report at zero and make
                // their own ban unshareable. The threshold was crossed on this master,
                // which is the only evidence this field should be describing.
                Confidence = 100,
            });
            Detections.Clear(account.Name);
            _log($"BAN issued: \"{account.Name}\" for {req.Signature}");
        }

        await ctx.Response.WriteAsJsonAsync(new { ok = true, counted = true, count, banned = crossed });
    }

    /// Merge a ban list exported by another server owner. By default entries land in a
    /// pending file rather than taking effect - see AntiCheatConfig.AutoApplyImportedBans.
    static async Task HandleBanImport(HttpContext ctx)
    {
        BanImport? req;
        try { req = await ctx.Request.ReadFromJsonAsync<BanImport>(); }
        catch { req = null; }

        if (req?.Bans is null)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "Malformed ban list." });
            return;
        }

        var (applied, pending, skipped) = Bans.Import(req.Bans);
        _log($"ban import: {applied} applied, {pending} pending review, {skipped} skipped");
        await ctx.Response.WriteAsJsonAsync(new
        {
            ok = true, applied, pending, skipped,
            pendingFile = pending > 0 ? Bans.PendingPath : null,
        });
    }

    /// Called by the launcher before it starts the game: creates the account on first
    /// use, or checks the password against the existing one. Hands back a session token
    /// the launcher passes on every game request, so a name cannot simply be claimed by
    /// setting a header.
    static async Task HandleLogin(HttpContext ctx)
    {
        LoginRequest? req;
        try { req = await ctx.Request.ReadFromJsonAsync<LoginRequest>(); }
        catch { req = null; }

        if (req is null)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "Malformed login request." });
            return;
        }

        // A ban is checked before the password, so a banned machine cannot probe for
        // valid account names.
        if (AntiCheat.Enabled && Bans.Find(req.HardwareId, req.Name) is { } ban)
        {
            _log($"login refused for \"{req.Name}\": banned ({ban.Signature}, by {ban.IssuedBy})");
            await ctx.Response.WriteAsJsonAsync(new
            {
                ok = false,
                error = $"Banned from this master server: {ban.Reason}",
            });
            return;
        }

        if (AntiCheat.Enabled && AntiCheat.RequireGuard && !req.Guard)
        {
            await ctx.Response.WriteAsJsonAsync(new
            {
                ok = false,
                error = "This master server requires the launcher's anti-cheat guard to be enabled.",
            });
            return;
        }

        var error = _accounts.Login(req.Name ?? "", req.Password ?? "", out var session, out var account);
        if (error != null || account is null)
        {
            // Logs that a login was refused, never which half was wrong or the password.
            _log($"login refused for \"{req.Name}\": {error}");
            await ctx.Response.WriteAsJsonAsync(new { ok = false, error });
            return;
        }

        if (req.HardwareId is { Length: > 0 })
            HardwareIds[account.Name] = req.HardwareId;

        _log($"login ok: \"{account.Name}\" ({_accounts.Count} accounts on this master)");
        await ctx.Response.WriteAsJsonAsync(new
        {
            ok = true,
            name = account.Name,
            session,
            cluster = _cfg.ClusterName,
        });
    }

    static async Task HandleGameRequest(HttpContext ctx)
    {
        // Our own control endpoints are dispatched here rather than with MapPost: the
        // catch-all fallback below wins over an explicit POST route, so a MapPost for
        // /_revival/login never ran and every login came back as a game reply. This runs
        // before the body is read, since HandleLogin parses the body itself.
        if (ctx.Request.Path.StartsWithSegments("/_revival"))
        {
            await HandleRevivalApi(ctx);
            return;
        }

        var body = await ReadBody(ctx);
        var q = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : "";
        // The client polls a handful of endpoints on a timer, so logging every request
        // buries the ones that matter. Each path is announced, then collapsed until it
        // has been quiet for a while. Turn log-packets on to see every one.
        if (LogPackets || ShouldSay($"http:{ctx.Request.Path}"))
        {
            var also = Suppressed($"http:{ctx.Request.Path}");
            _log($"HTTP {ctx.Request.Method} {ctx.Request.Path}{q} body={body.Length}b" +
                 (also > 0 ? $" [+{also} more since]" : ""));
        }

        // Requests begin with the caller's steamId, so a request carrying the launcher's
        // session token ties that Steam account to a Revival account.
        ulong? caller = body.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(body) : null;
        var session = ctx.Request.Headers["X-Revival-Session"].ToString();
        if (session.Length == 0 && IsLoopback(ctx.Connection.RemoteIpAddress))
            session = LocalSession ?? "";
        var loggedIn = _accounts.FromSession(session);
        if (caller is ulong id)
        {
            if (loggedIn != null)
                BindPlayer(id, loggedIn);
            else if (!_cfg.RequireAccounts)
            {
                // Open master: fall back to whatever name the launcher asked for.
                var chosen = ctx.Request.Headers["X-Revival-Player"].ToString();
                if (chosen.Length > 0) RegisterPlayerName(id, chosen);
            }
        }
        if (LogPackets && body.Length > 0)
        {
            _log($"  hex:   {Hex(body, 192)}{(body.Length > 192 ? "..." : "")}");
            _log($"  ascii: {Ascii(body, 192)}");
        }
        ctx.Response.ContentType = "application/octet-stream";

        // Localization is fetched as plain files, not as the binary request protocol.
        var path = ctx.Request.Path.Value ?? "";
        if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".lang", StringComparison.OrdinalIgnoreCase))
        {
            await ServeLanguageFile(ctx, path);
            return;
        }

        // Every response starts with the int32 result code. Leaving it zero is Fail, which
        // is what made the client retry each endpoint forever.
        byte[] reply;
        if (ctx.Request.Path.StartsWithSegments("/Tokens"))
        {
            // The token is the client's way in, so a missing account login has to be
            // refused here - everything downstream assumes a valid token.
            if (_cfg.RequireAccounts && loggedIn is null)
            {
                if (ShouldSay("badtoken"))
                {
                    var also = Suppressed("badtoken");
                    _log("  no account session on the token request -> BadToken " +
                         "(launch through the Revival launcher and log in)" +
                         (also > 0 ? $" [and {also} more since the last of these]" : ""));
                }
                reply = BuildTokenReply(BadToken);
            }
            else
            {
                reply = BuildTokenReply();
            }
        }
        else if (ctx.Request.Path.Value?.Contains("GetServerList", StringComparison.OrdinalIgnoreCase) == true)
        {
            reply = BuildServerListReply();
        }
        else if (ctx.Request.Path.Value?.Contains("GetPingServers", StringComparison.OrdinalIgnoreCase) == true)
        {
            reply = BuildPingServersReply();
        }
        else if (ctx.Request.Path.Value?.Contains("GetServerHandshakeToken", StringComparison.OrdinalIgnoreCase) == true)
        {
            reply = BuildServerKeyReply(body);
        }
        else
        {
            reply = new byte[ReplyBytes];
            BinaryPrimitives.WriteInt32LittleEndian(reply, Success);
        }

        await ctx.Response.Body.WriteAsync(reply);
        if (LogPackets) _log($"  <- replied {reply.Length}b: {Hex(reply, 160)}");
    }

    /// Response codes are shared with the request enum; Fail=0 was what made the client
    /// report "Database is unavailable now."
    public const byte Fail = 0;
    public const byte Success = 1;
    public const byte BadToken = 3;

    /// int32 result code, then mServerToken, mServerTokenAliveUntilMs, mServerTokenBuild.
    /// Measured, not deduced: a 25-byte reply with a 1-byte code sends the client back to
    /// looping on GetToken, while this 28-byte form carries it through the whole flow.
    static byte[] BuildTokenReply(byte result = Success)
    {
        // Proven from the client's own log: it printed the token as 0x4956414C01000000,
        // which is this reply read as a ulong at offset 1 — so the result code is ONE
        // byte. Writing it as int32 shifts aliveUntil and build into garbage, which is
        // why ServerBuildID came back changing between requests.
        // Offsets read back out of the client's own log: it printed our token verbatim
        // from offset 1, printed "alive for" as a denormal float matching bytes 10-13,
        // and printed ServerBuildID=417 matching bytes 14+. So lifetime is a FLOAT at 10
        // (not the int64 the field name suggests) and build is a ulong at 14.
        var p = new byte[64];
        p[0] = result;
        if (result != Success)
            return p;   // no token to hand out; the client reads the code and stops
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(1), IssuedToken);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(9), TokenLifetimeSeconds);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(13), ServerBuild);
        return p;
    }

    /// Reported both as the token's build and on every server list entry, so the two
    /// always agree.
    public const ulong ServerBuild = 0;

    /// How long the client should consider its token valid. A near-zero value here is
    /// what produced "Our token expired" immediately after every fetch.
    public const float TokenLifetimeSeconds = 86400f;

    public const ulong IssuedToken = 0x5245564956414C01;

    /// The game ships English.lang next to the executable but still fetches localization
    /// over HTTP, so serve the local file rather than inventing one.
    static async Task ServeLanguageFile(HttpContext ctx, string path)
    {
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        var name = Path.GetFileName(path);

        if (name.Equals("Languages.txt", StringComparison.OrdinalIgnoreCase))
        {
            var names = Directory.Exists(GameRoot)
                ? Directory.GetFiles(GameRoot, "*.lang").Select(Path.GetFileNameWithoutExtension).ToList()
                : ["English"];
            var body = string.Join("\n", names!);
            _log($"  <- Languages.txt: {body.Replace("\n", ", ")}");
            await ctx.Response.WriteAsync(body);
            return;
        }

        var file = Path.Combine(GameRoot ?? ".", name);
        if (File.Exists(file))
        {
            _log($"  <- {name} ({new FileInfo(file).Length}b from disk)");
            await ctx.Response.SendFileAsync(file);
        }
        else
        {
            _log($"  <- {name} NOT FOUND at {file}");
            ctx.Response.StatusCode = 404;
        }
    }

    /// Walks up from the running executable to the folder holding BattleBit.exe.
    static readonly string? GameRoot = FindGameRoot();

    static string? FindGameRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "BattleBit.exe"))) return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    /// The client takes this as (DateTime, List&lt;entry&gt;).
    static byte[] BuildServerListReply()
    {
        var rows = Servers.Values.ToList();
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        // The code is a single byte: the client reads the token at offset 1, so writing it
        // as int32 shifts the whole body by three bytes.
        w.Write(Success);               // byte result
        w.Write(DateTime.UtcNow.Ticks); // DateTime is serialised as ticks
        w.Write(rows.Count);            // entry count
        foreach (var s in rows)
            WriteServerEntry(w, s);
        return ms.ToArray();
    }

    /// The client calls this as GetPublicKeyForGameServer(IPEndPoint, ...) before joining;
    /// the request carries steamId, token, then the server's IP and port. The reply is the
    /// key struct: two ulongs plus two ints.
    static byte[] BuildServerKeyReply(byte[] request)
    {
        ulong lo = 0, hi = 0;
        if (request.Length >= 22)
        {
            var ip = new IPAddress(BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(16)));
            ushort port = BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(20));
            // Fall back to the sole registered server: the advertised port may be the
            // capture port rather than the server's real one.
            var match = Servers.Values.FirstOrDefault(s => s.Port == port)
                        ?? (Servers.Count == 1 ? Servers.Values.First() : null);
            if (match != null) { lo = match.KeyLow; hi = match.KeyHigh; }
            _log($"  key request for {ip}:{port} -> {(match != null ? "matched" : "NO SERVER")}");
        }

        // Layout taken from the client's parser (<GetPublicKeyForGameServer>b__0):
        // it checks the outer result code == 1, then reads a SECOND status byte off the
        // stream which must also be 1, and only then reads the two key ulongs plus a
        // trailing byte. Omitting that status byte makes it read the first key byte
        // instead, fail the == 1 test, hand back a zeroed key, and never cache it —
        // which surfaces as "NoResponse" because the join then silently times out.
        var p = new byte[64];
        p[0] = Success;   // outer result code
        p[1] = 1;         // inner status the callback requires
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(2), lo);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(10), hi);
        p[18] = 0;        // trailing byte read into the struct's last field
        return p;
    }

    /// RequestPingServers hands the client a Dictionary&lt;Region, List&lt;IPAddress&gt;&gt;.
    /// An empty one makes it throw IndexOutOfRange picking the first address for its own
    /// region, and that exception lands mid-startup, taking the localization fetch with
    /// it — so every region gets an entry.
    static byte[] BuildPingServersReply()
    {
        // Format from the client's parser (<RequestPingServers>b__0): after the result
        // code it loops WHILE at least one byte remains, reading
        //   byte region, byte count, then count x uint32 addresses.
        // There is no outer entry count, so the body must END exactly after the last
        // entry: any trailing padding is re-read as region 0 / count 0 and produces
        // "in ping servers: Developer_Server added twice", then IndexOutOfRange.
        // Only one entry: the client also indexes ping servers BY ADDRESS, so repeating
        // 127.0.0.1 across regions throws "An item with the same key has already been
        // added. Key: 127.0.0.1".
        var region = (byte)(Servers.Values.FirstOrDefault()?.Region ?? DefaultRegion);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Success);
        w.Write(region);                                         // key
        w.Write((byte)1);                                        // one address
        w.Write(BinaryPrimitives.ReadUInt32LittleEndian(IPAddress.Loopback.GetAddressBytes()));
        return ms.ToArray();
    }

    /// Europe_Central, from the region enum in the dump.
    const byte DefaultRegion = 10;

    // ==================== Player names ====================

    /// steamId -> the name this player is listed under on the cluster.
    static readonly ConcurrentDictionary<ulong, string> PlayerNames = new();

    /// steamId -> the Revival account that logged in from it. Account names are unique
    /// per master server, so these never need the numeric-prefix treatment below: a
    /// second player cannot take a name without the first player's password.
    static readonly ConcurrentDictionary<ulong, string> PlayerAccounts = new();

    static void BindPlayer(ulong steamId, Account account)
    {
        if (PlayerAccounts.TryGetValue(steamId, out var current) && current == account.Name)
            return;
        PlayerAccounts[steamId] = account.Name;
        PlayerNames[steamId] = account.Name;
        _log($"account bound: {steamId} -> \"{account.Name}\"");
    }

    static Account? AccountFor(ulong steamId) =>
        PlayerAccounts.TryGetValue(steamId, out var name) ? _accounts.ByName(name) : null;

    /// Registers the launcher-chosen name for a player. Two people using the same name
    /// are told apart by a numeric prefix ("2ethan", "3ethan") rather than being
    /// rejected, so nobody is blocked from joining by someone else's choice.
    public static string RegisterPlayerName(ulong steamId, string requested)
    {
        // Sanitized here rather than at the call site: on an open master this value
        // arrives in an X-Revival-Player request header, so it is attacker-controlled
        // and ends up as the name every other player sees.
        var raw = requested;
        requested = AccountRules.SanitizeName(requested);
        if (requested != raw.Trim())
            _log($"player name sanitized: {steamId} \"{Printable(raw)}\" -> \"{requested}\"");
        if (requested.Length == 0) return PlayerNames.TryGetValue(steamId, out var cur) ? cur : "";

        if (PlayerNames.TryGetValue(steamId, out var existing) &&
            StripPrefix(existing) == requested)
            return existing;   // same player reconnecting, keep their established name

        var name = requested;
        for (int n = 2; PlayerNames.Any(kv => kv.Key != steamId && kv.Value == name); n++)
            name = $"{n}{requested}";

        PlayerNames[steamId] = name;
        _log($"player name: {steamId} -> \"{name}\"");
        return name;
    }

    /// Renders an untrusted string safe to put in a log line. The server console is a
    /// terminal, so a name carrying escape sequences could otherwise move the cursor,
    /// recolour output or overwrite earlier lines - the log is read by the owner to
    /// decide who to ban, so it is worth keeping honest. Long values are cut, since the
    /// point of logging a rejected name is to recognise it, not to reproduce it.
    static string Printable(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var clean = new string(s.Select(c => char.IsControl(c) ? '?' : c).Take(64).ToArray());
        return s.Length > 64 ? clean + $"... ({s.Length} chars)" : clean;
    }

    static string StripPrefix(string name)
    {
        int i = 0;
        while (i < name.Length && char.IsDigit(name[i])) i++;
        return name[i..];
    }

    /// Field order follows the entry class's offsets (TypeDefIndex 7354); enums sit on
    /// 4-byte boundaries there, so they are written as int32.
    static void WriteServerEntry(BinaryWriter w, GameServerInfo s)
    {
        var ip = s.Address is { } a && !a.IsIPv4MappedToIPv6 ? a : IPAddress.Loopback;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        // Probe mode labels every field so a screenshot of the browser row shows which
        // slot each one actually feeds.
        bool p = ProbeOffsets;

        w.Write(p ? 111111 : 0);                                  // 0x10 int
        // IPAddress(uint) takes the LOW byte as the first octet, so 127.0.0.1 must go out
        // as 0x0100007F. Writing it big-endian sends 1.0.0.127 and the client's join
        // attempt dies with "NoResponse".
        w.Write(BinaryPrimitives.ReadUInt32LittleEndian(ip.GetAddressBytes())); // 0x14 uint ip
        // Advertising a port we listen on ourselves makes the client send its real
        // Game_Hail to us, which is the only way to see the true join packet layout.
        w.Write((ushort)(CapturePort > 0 ? CapturePort : s.Port)); // 0x18 ushort port
        // Probe: the version goes in every string slot at once. If "BUILDS DON'T MATCH"
        // clears, one of these three is the build/version field the client compares.
        var ver = s.Version ?? "Production 2.2.5 Hotfix";
        // 0x20 is the build/version string the browser compares. Found by elimination:
        // putting the version in all three slots cleared "BUILDS DON'T MATCH", while
        // 0x50 alone and 0x40 alone did not.
        WriteString(w, ver);                                      // 0x20 string version
        w.Write((byte)(p ? 11 : s.CurrentPlayers));               // 0x28 byte
        w.Write((byte)(p ? 22 : 0));                              // 0x29 byte
        w.Write((byte)(p ? 33 : (s.MaxPlayers > 0 ? s.MaxPlayers : 254))); // 0x2A byte
        w.Write((byte)(p ? 44 : s.MapSize));                      // 0x2B byte map size
        w.Write(p ? 1 : 0);                                       // 0x2C None/Restarting/NewServer
        w.Write(p ? 2 : 0);                                       // 0x30 enum
        w.Write(p ? 3 : GameModeId(s.GameMode));                  // 0x34 game mode
        w.Write(p ? 4 : s.Hz);                                    // 0x38 tickrate
        WriteString(w, p ? "NAME40" : s.ServerName ?? "Revival"); // 0x40 string
        w.Write(p ? 5 : 0);                                       // 0x48 Casual=0 / Hardcore=1
        w.Write(p ? 6 : 0);                                       // 0x4C Day=0 / Night=1
        WriteString(w, p ? "MAP50" : s.MapName ?? "Azagor");      // 0x50 string
        w.Write((byte)(p ? 55 : 0));                              // 0x58 byte
        w.Write(p ? 7 : s.Region);                                // 0x5C region (Europe_Central=10)
        w.Write((byte)(p ? 66 : 0));                              // 0x60 byte
        w.Write((byte)(p ? 77 : 0));                              // 0x61 byte
        w.Write((byte)(p ? 88 : 0));                              // 0x62 byte
        w.Write((byte)(p ? 99 : 0));                              // 0x63 flags: None=0, IsOfficial=1
        w.Write(p ? 222222 : 0);                                  // 0x64 anti-cheat: NoAntiCheat=0
        w.Write(true);                                            // 0x68 bool
        w.Write(p ? 123f : 0f);                                   // 0x6C float
    }

    /// Game mode enum from the dump: Null=0, TDM=1, AAS=2, RUSH=3, CONQ=4, DOMI=5,
    /// ELI=6, INFCONQ=7, FRONTLINE=8. The Hail carries the name, not the id.
    static int GameModeId(string? mode) => mode?.ToUpperInvariant() switch
    {
        "TDM" => 1,
        "AAS" => 2,
        "RUSH" => 3,
        "CONQ" => 4,
        "DOMI" => 5,
        "ELI" => 6,
        "INFCONQ" => 7,
        "FRONTLINE" => 8,
        _ => 1,
    };

    static void WriteString(BinaryWriter w, string value)
    {
        var b = Encoding.UTF8.GetBytes(value);
        w.Write((ushort)b.Length);
        w.Write(b);
    }

    /// Fallback size for endpoints whose layout is not known yet.
    public static int ReplyBytes = 256;

    /// Diagnostic: advertise this port instead of the server's real one, so the client's
    /// join packet arrives at a socket we control and can be dumped.
    public static int CapturePort;

    // ==================== Registry port (game servers) ====================

    static async Task RunRegistryListener(IPAddress bind, int port, CancellationToken ct)
    {
        var listener = new TcpListener(bind, port);
        listener.Start();
        _log($"Registry listener up on {bind}:{port}");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleRegistryClient(client);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Releasing the socket matters: left bound, a game server's connect still
            // succeeds and then nothing ever accepts it, so it hangs indefinitely.
            listener.Stop();
            _log("Registry listener stopped");
        }
    }

    static async Task HandleRegistryClient(TcpClient client)
    {
        var ep = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var id = $"gs_{ep}";
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();

            var head = await ReadHeader(stream);
            if (head == null) return;

            // A proxy announces itself with the auth prelude; the game sends an HTTP upgrade.
            if (head.StartsWith(AuthMagic, StringComparison.Ordinal))
            {
                var parts = head.Split('\n');
                var supplied = parts.Length > 1 ? parts[1] : "";
                if (_cfg.Password.Length > 0 && !FixedTimeEquals(supplied, _cfg.Password))
                {
                    _log($"REGISTRY {ep} REJECTED (bad password)");
                    await stream.WriteAsync("DENIED\n"u8.ToArray());
                    return;
                }
                await stream.WriteAsync("OK\n"u8.ToArray());
                await stream.FlushAsync();
                _log($"REGISTRY {ep} proxy authenticated");

                head = await ReadHeader(stream);
                if (head == null) return;
            }
            else if (_cfg.Password.Length > 0 &&
                     !IsLoopback((client.Client.RemoteEndPoint as IPEndPoint)?.Address))
            {
                _log($"REGISTRY {ep} REJECTED (no auth prelude on a protected cluster)");
                return;
            }

            if (!head.StartsWith("GET ", StringComparison.Ordinal))
            {
                _log($"REGISTRY {ep} unexpected opening bytes, closing");
                return;
            }

            var wsKey = ExtractHeader(head, "Sec-WebSocket-Key");
            if (wsKey == null) { _log($"REGISTRY {ep} no Sec-WebSocket-Key"); return; }

            var accept = Convert.ToBase64String(SHA1.HashData(
                Encoding.UTF8.GetBytes(wsKey + "258EAFA5-E914-47DA-95CA-5AB0DC85B11B")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n" +
                $"Connection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
            await stream.FlushAsync();

            var info = new GameServerInfo
            {
                EndPoint = ep,
                ConnectedAt = DateTime.UtcNow,
                Address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address,
            };
            Servers[id] = info;
            _log($"REGISTRY {ep} upgraded; awaiting Hail");

            await PumpPackets(stream, info, ep);
        }
        catch (Exception ex)
        {
            _log($"REGISTRY {ep} error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Servers.TryRemove(id, out _);
            _log($"REGISTRY {ep} disconnected. Cluster size: {Servers.Count}");
            client.Close();
        }
    }

    /// After the upgrade the game speaks raw uint32-LE length-prefixed packets.
    static async Task PumpPackets(NetworkStream stream, GameServerInfo info, string ep)
    {
        var lenBuf = new byte[4];
        while (true)
        {
            if (await ReadExact(stream, lenBuf, 4) < 4) { _log($"REGISTRY {ep} EOF"); return; }
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);

            // The game sends an empty packet every 5s as a keepalive. Treating that as
            // an error is what was tearing down every accepted connection.
            if (len == 0)
            {
                info.LastHeartbeat = DateTime.UtcNow;
                _log($"REGISTRY {ep} heartbeat");
                await SendPacket(stream, []);
                continue;
            }
            if (len > 1 << 20) { _log($"REGISTRY {ep} bad packet length {len}"); return; }

            var data = new byte[len];
            if (await ReadExact(stream, data, (int)len) < len) { _log($"REGISTRY {ep} truncated packet"); return; }

            if (LogPackets)
            {
                _log($"REGISTRY {ep} packet type=0x{data[0]:X2} len={len}");
                if (len <= 192) _log($"  hex: {Hex(data, 192)}");
            }

            await Dispatch(stream, info, ep, data);
        }
    }

    /// 24 bytes: Accepted, then the server's address as the master sees it (its "NAT IP",
    /// offset found by probing). The remaining 19 bytes are not yet identified; zeros work.
    static byte[] BuildAccepted(GameServerInfo info)
    {
        var p = new byte[24];
        p[0] = Accepted;
        var ip = info.Address ?? IPAddress.Loopback;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ip.GetAddressBytes().CopyTo(p, 1);

        // Clients ask the master for this server's public key before joining
        // (GetPublicKeyForGameServer), and the key is two ulongs. The remaining 19 bytes
        // here are the only channel the master has to hand the server a key, so mint one
        // per session and give the same pair out to joining clients.
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(5), info.KeyLow);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(13), info.KeyHigh);
        return p;
    }

    /// After the Hail handshake the server switches to a sequenced message stream:
    ///     uint64 sequence, byte type, payload
    /// These are the game's own master-protocol names and values (TypeDefIndex 7347).
    /// The master's reply echoes both the sequence and the request's type byte, which is
    /// why the answer to Hail is 0x01 and not any separate "accepted" code.
    enum MasterPacket : byte
    {
        Ping = 0,
        Hail = 1,
        DisconnectRequest = 2,
        GetIntervalStats = 3,
        InsertLog = 4,
        GetPlayerStats = 5,
        JoinPlayerToServer = 6,
        LeavePlayerFromServer = 7,
        SaveStatsAndLeavePlayerFromServer = 8,
        SaveStatsAndLeavePlayerFromServerBatched = 9,
        FlagPlayerByAntiCheat = 10,
        UpdateGameInfo = 11,
        UpdatePlayerInfo = 12,
        UpdateServerPhysicalStateInfo = 13,
        UpdateServerBrowserStateInfo = 14,
        UpdatePlayingModeratorCount = 15,
        UpdatePlayingContentCreatorCount = 16,
        UploadGameStatistics = 17,
        GetClanInfo = 18,
        LogError = 19,
        RequestToTransfer = 20,
        GetBlackwhiteListedWords = 21,
        RecordChatFilterDetection = 22,
        GetCommandQueue = 23,
        LogFast = 24,
        GetWeeklyChallenge = 25,
    }

    /// Dispatch modelled on BattleBitAPI's ServerListener.mExecutePackage: one branch per
    /// packet type, each reading its own fields.
    static async Task Dispatch(NetworkStream stream, GameServerInfo info, string ep, byte[] data)
    {
        // Hail is the only packet outside the sequenced stream, and it is always first.
        if (!info.Hailed)
        {
            info.Hailed = true;
            await HandleHail(stream, info, data);
            return;
        }

        if (data.Length < 9)
        {
            _log($"  runt packet ({data.Length}b): {Hex(data)}");
            return;
        }

        ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(data);
        var type = (MasterPacket)data[8];
        var payload = data.AsSpan(9);

        switch (type)
        {
            // The server sits in WaitingStatsFromMasterServer and drops the joining
            // player after 6000ms if this goes unanswered.
            case MasterPacket.GetPlayerStats when payload.Length >= 8:
            {
                var steamId = BinaryPrimitives.ReadUInt64LittleEndian(payload);
                var account = AccountFor(steamId);
                var who = account?.Name ?? DisplayName(steamId);
                var progress = account?.Progress ?? _accounts.Progression.NewPlayerProgress();
                var reply = BuildStatsReply(sequence, steamId, who, progress,
                                            account?.IsBanned ?? false);
                await SendPacket(stream, reply);
                _log($"  seq {sequence} GetPlayerStats {steamId} -> stats as \"{who}\" " +
                     $"({reply.Length}b, {progress.Params.Length} progress params" +
                     $"{(account is null ? ", no account yet" : "")})");
                break;
            }

            // Progress coming back when a player leaves is NOT the layout we send.
            // A captured save payload is 325 bytes against our 278, it begins with a
            // byte we do not account for, and the player's steamId does not appear in
            // it anywhere - not at any offset, not in either byte order. Reading it as
            // the reply layout yields a nonsense steamId, so the progress is dropped.
            //
            // Decoding it properly means reading the game's own writer, which delegates
            // to a chain of per-member writers rather than laying the fields out inline
            // the way the reply does. Until that is done, the save is acknowledged and
            // discarded rather than parsed wrongly.
            case MasterPacket.SaveStatsAndLeavePlayerFromServer:
            {
                var record = PlayerStats.TryRead(new PacketReader(payload.ToArray()), out var sid);
                var name = record is null ? null : PlayerAccounts.GetValueOrDefault(sid);

                if (name is not null)
                {
                    _accounts.StoreProgress(name, record!);
                    _log($"  seq {sequence} {type}: saved progress for \"{name}\" " +
                         $"({record!.Params.Length} params)");
                }
                else if (LogSaveLayoutOnce)
                {
                    // Once, not on every retry: this is a 650-character line and the
                    // game server repeats the packet until it is acknowledged.
                    LogSaveLayoutOnce = false;
                    _log($"  seq {sequence} {type}: cannot parse this layout yet, " +
                         $"progress not saved ({payload.Length}b). Logged once:");
                    _log($"    {Hex(payload.ToArray(), 4096)}");
                }

                // Acknowledge either way. Without a reply the game server treats the
                // save as lost and resends it every five seconds for the rest of the
                // round, which is what filled the console with identical dumps.
                var ack = new PacketWriter();
                ack.Write(sequence);
                ack.Write((byte)type);
                await SendPacket(stream, ack.ToArray());
                break;
            }

            default:
                if (LogPackets)
                    _log($"  seq {sequence} {type} (0x{data[8]:X2}) {payload.Length}b: {Hex(payload.ToArray())}");
                break;
        }
    }

    static async Task HandleHail(NetworkStream stream, GameServerInfo info, byte[] data)
    {
        ParseHail(data, info);
        _log($"  hail: port={info.Port} hz={info.Hz} region={info.Region} " +
             $"version=\"{info.Version}\" map=\"{info.MapName}\" name=\"{info.ServerName}\"");

        var accepted = BuildAccepted(info);
        if (ProbeOffsets)
            for (int i = 1; i < accepted.Length; i++) accepted[i] = (byte)i;

        await SendPacket(stream, accepted);
        _log($"  -> sent Accepted (NAT IP {info.Address}): {Hex(accepted)}");
    }

    /// The name the game server will show for this player. Registered by the launcher
    /// when the client authenticates, so it is the name the player typed rather than
    /// anything Steam supplied.
    static string DisplayName(ulong steamId) =>
        PlayerNames.TryGetValue(steamId, out var n) ? n : "Player";

    /// A reply to a sequenced request echoes the sequence and the request's type byte.
    /// This is load-bearing: dropping the sequence makes the server ignore the reply and
    /// re-send GetPlayerStats every 10.7s until it gives up at WaitingStatsFromMasterServer,
    /// so the join fails outright. That 10.7s is the retry interval, NOT a timeout before
    /// the server continues - do not read a gap of that length as "the reply was ignored".
    ///
    /// There is no envelope around the stats themselves: the handler at RVA 0x24479E0
    /// allocates a PlayerStats and reads straight into it, so the payload is the blob
    /// and nothing else.
    static byte[] BuildStatsReply(
        ulong sequence, ulong steamId, string username, ProgressRecord progress, bool isBanned)
    {
        var w = new PacketWriter();
        w.Write(sequence);
        w.Write((byte)MasterPacket.GetPlayerStats);
        PlayerStats.Write(w, steamId, username, progress, isBanned);

        // The username is the last field, and the game's string reader returns NULL
        // rather than throwing when fewer bytes remain than the length prefix claims
        // (RVA 0x236BC70: "cmp eax,2 / jl ret-null" then "cmp edx,ebp / jl ret-null").
        // An exactly-sized body therefore loses the name silently, which is what left
        // the player unnamed in game while everything else parsed. The same trap already
        // bit the token and key replies, so pad the tail.
        w.WriteRaw(new byte[64]);
        return w.ToArray();
    }

    static void ParseHail(byte[] d, GameServerInfo info)
    {
        if (d.Length < 23) return;
        info.Port = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(5));
        info.MapSize = d[9];
        info.Hz = d[17];
        info.Region = d[18];

        int off = 23;
        if (TryReadString(d, ref off, out var version)) info.Version = version;
        if (TryReadString(d, ref off, out var map)) info.MapName = map;
        if (TryReadString(d, ref off, out var name)) info.ServerName = name;
    }

    static async Task SendPacket(NetworkStream stream, byte[] payload)
    {
        var wire = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(wire, (uint)payload.Length);
        payload.CopyTo(wire, 4);
        await stream.WriteAsync(wire);
        await stream.FlushAsync();
    }

    // ==================== Helpers ====================

    static async Task<string?> ReadHeader(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (sb.Length < 8192)
        {
            if (await stream.ReadAsync(one, 0, 1) == 0) return sb.Length > 0 ? sb.ToString() : null;
            sb.Append((char)one[0]);
            var s = sb.ToString();
            if (s.EndsWith("\r\n\r\n", StringComparison.Ordinal)) return s;
            // The auth prelude is two newline-terminated lines.
            if (s.StartsWith(AuthMagic, StringComparison.Ordinal) && s.Count(c => c == '\n') >= 2) return s;
        }
        return sb.ToString();
    }

    static string? ExtractHeader(string request, string name)
    {
        foreach (var line in request.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
                return t[(name.Length + 1)..].Trim();
        }
        return null;
    }

    static bool TryReadString(byte[] data, ref int offset, out string result)
    {
        result = "";
        if (offset + 2 > data.Length) return false;
        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
        offset += 2;
        if (len == 0 || offset + len > data.Length) return false;
        result = Encoding.UTF8.GetString(data, offset, len);
        offset += len;
        return true;
    }

    static async Task<int> ReadExact(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = await stream.ReadAsync(buffer, total, count - total);
            if (read == 0) return total;
            total += read;
        }
        return total;
    }

    static async Task<byte[]> ReadBody(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        return ms.ToArray();
    }

    static string Hex(byte[] data, int max = int.MaxValue) =>
        BitConverter.ToString(data, 0, Math.Min(data.Length, max)).Replace("-", "");

    static string Ascii(byte[] data, int max)
    {
        var sb = new StringBuilder();
        foreach (var b in data.AsSpan(0, Math.Min(data.Length, max)))
            sb.Append(b >= 32 && b <= 126 ? (char)b : '.');
        return sb.ToString();
    }
}

public class GameServerInfo
{
    public string EndPoint { get; set; } = "";
    public IPAddress? Address { get; set; }
    public DateTime ConnectedAt { get; set; }
    public DateTime LastHeartbeat { get; set; }

    /// Set once the Hail has been answered; every later packet on this connection
    /// carries the uint64 sequence prefix.
    public bool Hailed { get; set; }

    /// Session key handed to the server in its Accepted packet and to clients that ask
    /// for this server's public key before joining.
    public ulong KeyLow { get; } = (ulong)Random.Shared.NextInt64();
    public ulong KeyHigh { get; } = (ulong)Random.Shared.NextInt64();
    public string? Version { get; set; }
    public string? ServerName { get; set; }
    public string? MapName { get; set; }
    public string? GameMode { get; set; }
    public int Port { get; set; }
    public int MapSize { get; set; }
    public int Hz { get; set; }
    public int Region { get; set; }
    public int CurrentPlayers { get; set; }
    public int MaxPlayers { get; set; }
}
