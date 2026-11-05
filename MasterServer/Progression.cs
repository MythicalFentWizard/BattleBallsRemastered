using System.Globalization;
using System.Text.Json;

namespace MasterServer;

/// Index of each uint in PlayerProgess's wire block. The order is the field declaration
/// order of PlayerStats.PlayerProgess, which is what the game's reader walks: verified
/// against the struct offsets, where the last three written are 0x114 TotalScore,
/// 0x118 AmmoSupplied and 0x11C WeeklyChallengeCompleted.
public enum ProgressField
{
    KillCount = 0, LeaderKills, AssaultKills, MedicKills, EngineerKills, SupportKills,
    ReconKills, DeathCount, WinCount, LoseCount, FriendlyShots, FriendlyKills, Revived,
    RevivedTeamMates, Assists, Prestige, Rank, EXP, ShotsFired, ShotsHit, Headshots,
    ObjectivesComplated, HealedHPs, RoadKills, Suicides, VehiclesDestroyed,
    VehicleHPRepaired, LongestKill, PlayTimeSeconds, LeaderPlayTime, AssaultPlayTime,
    MedicPlayTime, EngineerPlayTime, SupportPlayTime, ReconPlayTime, LeaderScore,
    AssaultScore, MedicScore, EngineerScore, SupportScore, ReconScore, TotalScore,
    AmmoSupplied, WeeklyChallengeCompleted,

    /// Not a field - the number of them, and what PlayerProgess declares as its count.
    Count,
}

public static class ProgressFieldExtensions
{
    public static uint Get(this ProgressRecord r, ProgressField field)
    {
        var i = (int)field;
        return i >= 0 && i < r.Params.Length ? r.Params[i] : 0;
    }

    public static void Set(this ProgressRecord r, ProgressField field, uint value)
    {
        var i = (int)field;
        if (i >= 0 && i < r.Params.Length) r.Params[i] = value;
    }
}

/// Per-master-server progression rules, in progression.json beside the account store so
/// a server owner can tune their cluster without touching the code. A brand new player
/// is an ordinary level-zero player under these rules; the defaults are all neutral, so
/// an untouched file behaves exactly like vanilla progression.
public sealed class ProgressionConfig
{
    /// What a newly created account starts with.
    public uint StartingRank { get; set; }
    public uint StartingPrestige { get; set; }
    public uint StartingExp { get; set; }

    /// Starting score for each class, which is what the game derives class level from.
    /// Keys are the six classes; anything else is ignored.
    public Dictionary<string, uint> StartingClassScore { get; set; } = new()
    {
        ["Leader"] = 0,
        ["Assault"] = 0,
        ["Medic"] = 0,
        ["Engineer"] = 0,
        ["Support"] = 0,
        ["Recon"] = 0,
    };

    /// Rate of growth. Everything a player earns in a round is scaled by these before
    /// being banked, so 2.0 is double XP. 1.0 is vanilla.
    public double XpMultiplier { get; set; } = 1.0;
    public double ScoreMultiplier { get; set; } = 1.0;

    static readonly (string Name, ProgressField Field)[] ClassScores =
    [
        ("Leader", ProgressField.LeaderScore),
        ("Assault", ProgressField.AssaultScore),
        ("Medic", ProgressField.MedicScore),
        ("Engineer", ProgressField.EngineerScore),
        ("Support", ProgressField.SupportScore),
        ("Recon", ProgressField.ReconScore),
    ];

    /// The progress a brand new account begins with: a normal level-zero player, plus
    /// whatever head start the server owner configured.
    public ProgressRecord NewPlayerProgress()
    {
        var record = new ProgressRecord();
        record.Set(ProgressField.Rank, StartingRank);
        record.Set(ProgressField.Prestige, StartingPrestige);
        record.Set(ProgressField.EXP, StartingExp);
        foreach (var (name, field) in ClassScores)
            if (StartingClassScore.TryGetValue(name, out var score))
                record.Set(field, score);
        return record;
    }

    /// Scale what the player earned this round before it is banked. Called with the
    /// progress as it was when they joined and as the game server handed it back, and
    /// rewrites the earned fields in place.
    public void ApplyGrowth(ProgressRecord before, ProgressRecord earned)
    {
        Scale(before, earned, ProgressField.EXP, XpMultiplier);
        Scale(before, earned, ProgressField.TotalScore, ScoreMultiplier);
        foreach (var (_, field) in ClassScores)
            Scale(before, earned, field, ScoreMultiplier);
    }

    static void Scale(ProgressRecord before, ProgressRecord earned, ProgressField field, double multiplier)
    {
        if (Math.Abs(multiplier - 1.0) < 0.0001) return;

        var old = before.Get(field);
        var now = earned.Get(field);
        if (now <= old) return;    // nothing earned, or the server reset it

        // Scale only the delta, so a multiplier change never retroactively inflates or
        // destroys progress someone already had.
        var scaled = (double)old + (now - old) * Math.Max(0, multiplier);
        earned.Set(field, scaled >= uint.MaxValue ? uint.MaxValue : (uint)scaled);
    }

    /// Reads the starting class scores from one server.properties value, written as
    /// "Assault:5000, Recon:2000". A class left out keeps zero, and an unrecognised
    /// name is reported rather than ignored - a typo here would otherwise look exactly
    /// like a setting that does not work.
    ///
    /// This used to live in progression.json, which server.properties then silently
    /// overwrote on every start. One file, one place.
    public IEnumerable<string> ApplyClassScores(string list)
    {
        if (string.IsNullOrWhiteSpace(list)) yield break;

        foreach (var pair in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = pair.Split(':', 2);
            var name = bits[0].Trim();
            if (bits.Length != 2)
            {
                yield return $"\"{pair.Trim()}\" is not class:score";
                continue;
            }
            var match = ClassScores.FirstOrDefault(
                c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match.Name is null)
            {
                yield return $"\"{name}\" is not a class " +
                             $"({string.Join(", ", ClassScores.Select(c => c.Name))})";
                continue;
            }
            if (!uint.TryParse(bits[1].Trim(), NumberStyles.Integer,
                               CultureInfo.InvariantCulture, out var score))
            {
                yield return $"\"{bits[1].Trim()}\" is not a whole number";
                continue;
            }
            StartingClassScore[match.Name] = score;
        }
    }

    public string Describe() =>
        $"rank {StartingRank}, prestige {StartingPrestige}, exp {StartingExp}, " +
        $"xp x{XpMultiplier:0.##}, score x{ScoreMultiplier:0.##}";
}
