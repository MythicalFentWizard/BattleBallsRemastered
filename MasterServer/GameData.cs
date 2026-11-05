namespace MasterServer;

/// The map and gamemode names the game accepts, as one list rather than three.
///
/// Maps came out of BattleBit_Data/globalgamemanagers - only three appear in the IL2CPP
/// metadata, so the obvious place to look is the wrong one. Gamemodes are the enum at
/// TypeDefIndex 5931, minus Null and Count, which are not playable.
///
/// A name the game does not recognise is reported in the server window as
/// "Unable to find map of X" and the rest of that rotation list is ignored, so these
/// spellings matter exactly.
public static class GameData
{
    public static readonly string[] Maps =
    [
        "Azagor", "Basra", "Construction", "District", "Dustydew", "Eduardovo",
        "Frugis", "Isle", "Kodiak", "Lonovo", "MultuIslands", "Namak", "OilDunes",
        "Outskirts", "Polygon", "River", "Salhan", "SandySunset", "TensaTown",
        "Valley", "Wakistan", "WineParadise", "ZalfiBay",
    ];

    /// The common ones first: these are what a server actually runs. The rest work but
    /// are situational, and several have no layout on every map at every size.
    public static readonly string[] GameModes =
    [
        "CONQ", "DOMI", "FRONTLINE", "TDM", "INFCONQ", "RUSH", "AAS", "ELI",
        "CaptureTheFlag", "Invasion", "OneLife", "FFA", "GunGameFFA", "GunGameTeam",
        "SuicideRush", "CatchGame", "Infected", "CashRun", "VoxelFortify",
        "VoxelTrench",
    ];

    /// setsize has names for these four only - there is no name for 64v64, which is why
    /// anything with "vs" in it has to go through the scale command instead.
    public static readonly SizeOption[] Sizes =
    [
        new("tiny",   "Tiny - 8 vs 8"),
        new("small",  "Small - 16 vs 16"),
        new("medium", "Medium - 32 vs 32"),
        new("64vs64", "Large - 64 vs 64"),
        new("ultra",  "Ultra - 127 vs 127"),
    ];

    /// A class, not a tuple. A ValueTuple's element names exist only at compile time -
    /// at runtime it has Item1 and Item2 - so WPF cannot resolve DisplayMemberPath
    /// against them, silently falls back to ToString(), and renders the whole tuple.
    public sealed class SizeOption(string value, string label)
    {
        public string Value { get; } = value;
        public string Label { get; } = label;
        public override string ToString() => Label;
    }

    /// The client-side guard's detectors, as the master knows them.
    public static readonly (string Key, string Label)[] Detectors =
    [
        ("known-cheat-process", "Known cheat process running"),
        ("injected-module",     "DLL injected into the game"),
        ("debugger-attached",   "Debugger attached"),
        ("game-file-modified",  "Game files modified (flags patched installs)"),
    ];
}
