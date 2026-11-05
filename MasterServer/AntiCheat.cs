using System.Collections.Concurrent;
using System.Text.Json;

namespace MasterServer;

/// Anti-cheat settings for THIS master server, in anticheat.json beside the account
/// store. Off by default: a server owner opts in, and can turn individual detectors off
/// without disabling the rest.
public sealed class AntiCheatConfig
{
    public bool Enabled { get; set; }

    /// Refuse logins from launchers that are not running the guard. Only meaningful
    /// with Enabled; a determined cheater can patch the launcher, so this raises the
    /// bar rather than closing the door.
    public bool RequireGuard { get; set; }

    /// Per-detector toggles. A detector reporting a signature that is switched off here
    /// is recorded but never counts toward a ban.
    public Dictionary<string, bool> Detectors { get; set; } = new()
    {
        ["known-cheat-process"] = true,
        ["injected-module"] = true,
        ["debugger-attached"] = true,
        // On here, and switched off in the shipped server.properties. It used to default
        // to false, which meant clearing anticheat-detectors-off still left it off and
        // the owner could not tell why. Everything is on until the properties file says
        // otherwise, so that file is the whole answer.
        ["game-file-modified"] = true,
    };

    /// How many reports of the SAME signature, from the same player, inside the window
    /// before a ban is issued. One report is never enough - a single detection can be a
    /// false positive from an overlay or a security product.
    public int ReportsToBan { get; set; } = 3;
    public int ReportWindowMinutes { get; set; } = 30;

    /// Ban the hardware id as well as the account. Hardware ids are spoofable, so this
    /// slows down ban evasion rather than preventing it.
    public bool BanByHardwareId { get; set; } = true;

    /// Imported ban lists land in a pending file for review instead of taking effect.
    /// Leave this false: a shared list is a false-positive amplifier, and one bad
    /// detector on someone else's cluster should not silently ban your players.
    public bool AutoApplyImportedBans { get; set; }

    /// Minimum confidence an imported entry must carry to be considered at all.
    public int MinimumImportConfidence { get; set; } = 80;

    /// Switches off the named detectors, from one server.properties value written as
    /// "known-cheat-process, debugger-attached". Anything unrecognised is reported
    /// rather than ignored, since a misspelled detector silently stays ON - the failure
    /// that matters, because the owner believes they turned it off.
    ///
    /// These were in anticheat.json, which server.properties then silently overwrote
    /// for every key the two shared. One file, one place.
    public IEnumerable<string> ApplyDetectorsOff(string list)
    {
        if (string.IsNullOrWhiteSpace(list)) yield break;

        foreach (var raw in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw.Trim();
            var key = Detectors.Keys.FirstOrDefault(
                k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (key is null)
            {
                yield return $"\"{name}\" is not a detector " +
                             $"({string.Join(", ", Detectors.Keys)})";
                continue;
            }
            Detectors[key] = false;
        }
    }

    public bool DetectorEnabled(string signature) =>
        Detectors.TryGetValue(signature, out var on) && on;

    public string Describe() => Enabled
        ? $"ON, {Detectors.Count(d => d.Value)}/{Detectors.Count} detectors, " +
          $"{ReportsToBan} reports in {ReportWindowMinutes}m to ban"
        : "OFF";
}

/// One ban. Carries its evidence so that a server owner importing someone else's list
/// can judge it rather than trust it blindly.
public sealed class BanEntry
{
    /// SHA-256 of the machine's hardware fingerprint, deliberately UNSALTED so that an
    /// exported list is meaningful on another cluster. The raw components never leave
    /// the player's machine and are never stored.
    public string? HardwareId { get; set; }

    public string? Account { get; set; }
    public ulong SteamId { get; set; }

    public string Signature { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Evidence { get; set; } = "";
    public int Confidence { get; set; } = 100;

    /// Cluster that issued it, so an imported entry can be traced back.
    public string IssuedBy { get; set; } = "";
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;

    public bool Matches(string? hardwareId, string? account, ulong steamId) =>
        (HardwareId is { Length: > 0 } h && h == hardwareId)
        || (Account is { Length: > 0 } a && account is { Length: > 0 } b
            && string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        || (SteamId != 0 && SteamId == steamId);
}

/// Per-master-server ban list. Like accounts and progression, it lives beside the
/// executable, so every master keeps its own bans - nothing is shared unless the owner
/// explicitly exports and someone else explicitly imports.
public sealed class BanStore
{
    readonly string _path;
    readonly string _pendingPath;
    readonly string _cluster;
    readonly AntiCheatConfig _config;
    readonly object _gate = new();
    readonly List<BanEntry> _bans = [];

    public BanStore(string path, string cluster, AntiCheatConfig config)
    {
        _path = path;
        _pendingPath = Path.ChangeExtension(path, ".pending.json");
        _cluster = cluster;
        _config = config;
        try
        {
            if (File.Exists(path))
                _bans = JsonSerializer.Deserialize<List<BanEntry>>(File.ReadAllText(path)) ?? [];
        }
        catch { }
    }

    public string FilePath => _path;
    public string PendingPath => _pendingPath;
    public int Count { get { lock (_gate) return _bans.Count; } }

    public BanEntry? Find(string? hardwareId, string? account, ulong steamId = 0)
    {
        lock (_gate)
            return _bans.FirstOrDefault(b => b.Matches(hardwareId, account, steamId));
    }

    public void Ban(BanEntry entry)
    {
        if (entry.IssuedBy.Length == 0) entry.IssuedBy = _cluster;
        if (!_config.BanByHardwareId) entry.HardwareId = null;

        lock (_gate)
        {
            if (_bans.Any(b => b.Matches(entry.HardwareId, entry.Account, entry.SteamId)))
                return;
            _bans.Add(entry);
            SaveLocked(_path, _bans);
        }
    }

    public bool Unban(string accountOrHardwareId)
    {
        lock (_gate)
        {
            var removed = _bans.RemoveAll(b =>
                string.Equals(b.Account, accountOrHardwareId, StringComparison.OrdinalIgnoreCase)
                || b.HardwareId == accountOrHardwareId);
            if (removed > 0) SaveLocked(_path, _bans);
            return removed > 0;
        }
    }

    public List<BanEntry> All() { lock (_gate) return [.. _bans]; }

    /// Merge someone else's list. Entries below the configured confidence are dropped,
    /// duplicates are skipped, and unless AutoApplyImportedBans is set they are written
    /// to a pending file for the owner to review rather than taking effect.
    public (int applied, int pending, int skipped) Import(IEnumerable<BanEntry> incoming)
    {
        int applied = 0, pending = 0, skipped = 0;
        var held = new List<BanEntry>();

        lock (_gate)
        {
            foreach (var entry in incoming)
            {
                if (entry.Confidence < _config.MinimumImportConfidence
                    || _bans.Any(b => b.Matches(entry.HardwareId, entry.Account, entry.SteamId)))
                {
                    skipped++;
                    continue;
                }

                if (_config.AutoApplyImportedBans)
                {
                    _bans.Add(entry);
                    applied++;
                }
                else
                {
                    held.Add(entry);
                    pending++;
                }
            }

            if (applied > 0) SaveLocked(_path, _bans);
            if (held.Count > 0)
            {
                try
                {
                    var existing = File.Exists(_pendingPath)
                        ? JsonSerializer.Deserialize<List<BanEntry>>(File.ReadAllText(_pendingPath)) ?? []
                        : [];
                    existing.AddRange(held);
                    SaveLocked(_pendingPath, existing);
                }
                catch { }
            }
        }
        return (applied, pending, skipped);
    }

    static void SaveLocked(string path, object data)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }
}

/// Counts detector reports per player so a single hit never bans anyone. Reports older
/// than the window fall out, so unrelated detections spread over a long session do not
/// accumulate into a ban.
public sealed class DetectionTracker
{
    readonly record struct Key(string Player, string Signature);

    readonly AntiCheatConfig _config;
    readonly ConcurrentDictionary<Key, List<DateTime>> _hits = new();

    public DetectionTracker(AntiCheatConfig config) => _config = config;

    /// Records a report and says whether it has now crossed the ban threshold.
    public bool Record(string player, string signature, out int count)
    {
        var key = new Key(player, signature);
        var cutoff = DateTime.UtcNow.AddMinutes(-_config.ReportWindowMinutes);

        var times = _hits.GetOrAdd(key, _ => []);
        lock (times)
        {
            times.RemoveAll(t => t < cutoff);
            times.Add(DateTime.UtcNow);
            count = times.Count;
            return count >= _config.ReportsToBan;
        }
    }

    public void Clear(string player)
    {
        foreach (var key in _hits.Keys.Where(k => k.Player == player))
            _hits.TryRemove(key, out _);
    }
}
