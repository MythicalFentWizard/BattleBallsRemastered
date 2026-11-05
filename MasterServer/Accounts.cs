using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace MasterServer;

/// Password and name rules, kept here so the launcher and the master server agree on
/// what they accept. The launcher checks these before it bothers contacting the master,
/// but the master checks them again - a client is free to skip its own validation.
public static class AccountRules
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 72;
    public const int MinNameLength = 2;
    public const int MaxNameLength = 20;

    /// Symbols allowed in a password, on top of ASCII letters and digits. Anything
    /// outside this set is rejected rather than silently stripped, so what the player
    /// types is exactly what gets hashed.
    public const string PasswordSymbols = "!@#$%^&*()-_=+[]{}:;,.?/~";

    public static bool IsAllowedPasswordChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || PasswordSymbols.Contains(c);

    public static bool IsAllowedNameChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == ' ';

    /// Null when the password is acceptable, otherwise the reason to show the player.
    public static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return "Enter an account password.";
        if (password.Length < MinPasswordLength)
            return $"Password must be at least {MinPasswordLength} characters.";
        if (password.Length > MaxPasswordLength)
            return $"Password must be at most {MaxPasswordLength} characters.";
        if (!password.Any(char.IsAsciiDigit))
            return "Password must contain at least one number.";

        var bad = password.Where(c => !IsAllowedPasswordChar(c)).Distinct().ToArray();
        if (bad.Length > 0)
        {
            // Name the offending characters, but describe whitespace and control codes
            // instead of printing them, since printing tells the player nothing.
            var shown = bad.Select(c => c switch
            {
                ' ' => "space",
                '\t' => "tab",
                _ => char.IsControl(c) ? $"U+{(int)c:X4}" : c.ToString(),
            });
            return "Password cannot contain: " + string.Join(" ", shown);
        }
        return null;
    }

    public static string? ValidateName(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name))
            return "Enter a player name.";
        if (name.Length < MinNameLength)
            return $"Name must be at least {MinNameLength} characters.";
        if (name.Length > MaxNameLength)
            return $"Name must be at most {MaxNameLength} characters.";
        if (name.Any(c => !IsAllowedNameChar(c)))
            return "Name may only contain letters, numbers, spaces, and _ - .";
        return null;
    }

    /// The key an account is stored under: case-insensitive, so "Ethan" and "ethan" are
    /// the same account and cannot be used to impersonate one another.
    public static string NormalizeName(string name) => name.Trim().ToLowerInvariant();

    /// Coerces an untrusted string into something safe to use as a player name.
    ///
    /// ValidateName rejects; this one repairs, because it sits on the open-master path
    /// where the name arrives in a request header rather than from the launcher's own
    /// form. Rejecting there would stop someone joining over a character they cannot
    /// see, so disallowed characters are dropped and the result is capped instead.
    ///
    /// The cap is what makes this a security control and not a cosmetic one. The name
    /// is written into a packet with a ushort length prefix and relayed to every other
    /// player, so an unbounded name both overflows that prefix and lands in other
    /// people's clients. The character filter keeps rich-text markup out for the same
    /// reason - the game strips tags itself, but that is its defence, not ours.
    public static string SanitizeName(string? requested)
    {
        if (string.IsNullOrEmpty(requested)) return "";
        var kept = new string(requested.Where(IsAllowedNameChar).ToArray()).Trim();
        if (kept.Length > MaxNameLength) kept = kept[..MaxNameLength].Trim();
        return kept.Length >= MinNameLength ? kept : "";
    }
}

/// A player's saved progress on this master server, in the shape the game server's
/// PlayerStats reader expects. The variable-length sections stay opaque blobs because
/// the master never needs to interpret them, only hand them back unchanged.
public sealed class ProgressRecord
{
    public uint[] Params { get; set; } = new uint[(int)ProgressField.Count];
    public byte[] ToolsProgess { get; set; } = [];
    public byte[] Achievements { get; set; } = [];
    public byte[] Selections { get; set; } = [];
    public byte[] Inventory { get; set; } = [];
    public ulong Role { get; set; }
}

public sealed class Account
{
    public string Name { get; set; } = "";
    public byte[] Salt { get; set; } = [];
    public byte[] Hash { get; set; } = [];
    public int Iterations { get; set; }
    public DateTime Created { get; set; }
    public DateTime LastSeen { get; set; }
    public bool IsBanned { get; set; }
    public ProgressRecord Progress { get; set; } = new();
}

/// Per-master-server account store. The file sits next to the master server executable,
/// so every master server keeps its own accounts and its own progress: the same player
/// name on two different masters is two unrelated accounts.
public sealed class AccountStore
{
    const int Iterations = 210_000;   // PBKDF2-HMAC-SHA256
    const int SaltSize = 16;
    const int HashSize = 32;

    readonly string _path;
    readonly ProgressionConfig _progression;
    readonly object _gate = new();
    readonly Dictionary<string, Account> _accounts = new();

    /// Short-lived tokens handed out at login. Without one, anyone could claim any
    /// account just by setting the player-name header, which would make the password
    /// pointless. In memory only - a restart simply means logging in again.
    readonly ConcurrentDictionary<string, string> _sessions = new();

    public AccountStore(string path, ProgressionConfig progression)
    {
        _path = path;
        _progression = progression;
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(path));
                foreach (var a in loaded ?? [])
                    _accounts[AccountRules.NormalizeName(a.Name)] = a;
            }
        }
        catch { /* a corrupt store starts empty rather than refusing to boot */ }
    }

    public string FilePath => _path;
    public ProgressionConfig Progression => _progression;
    public int Count { get { lock (_gate) return _accounts.Count; } }

    /// Verify an existing account or create it on first use. Returns null on success,
    /// handing back a session token, or the reason to show the player.
    public string? Login(string name, string password, out string? sessionToken, out Account? account)
    {
        sessionToken = null;
        account = null;

        var nameError = AccountRules.ValidateName(name);
        if (nameError != null) return nameError;
        var passwordError = AccountRules.ValidatePassword(password);
        if (passwordError != null) return passwordError;

        name = name.Trim();
        var key = AccountRules.NormalizeName(name);

        lock (_gate)
        {
            if (_accounts.TryGetValue(key, out var existing))
            {
                var attempt = Hash(password, existing.Salt, existing.Iterations);
                if (!CryptographicOperations.FixedTimeEquals(attempt, existing.Hash))
                    return "Wrong password for that name on this master server.";
                if (existing.IsBanned)
                    return "That account is banned on this master server.";

                existing.LastSeen = DateTime.UtcNow;
                existing.Name = name;    // keep the capitalisation last logged in with
                account = existing;
            }
            else
            {
                var salt = RandomNumberGenerator.GetBytes(SaltSize);
                account = new Account
                {
                    Name = name,
                    Salt = salt,
                    Hash = Hash(password, salt, Iterations),
                    Iterations = Iterations,
                    Created = DateTime.UtcNow,
                    LastSeen = DateTime.UtcNow,
                    // A new player is an ordinary level-zero player, plus whatever head
                    // start this master server is configured to give.
                    Progress = _progression.NewPlayerProgress(),
                };
                _accounts[key] = account;
            }
            SaveLocked();
        }

        sessionToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _sessions[sessionToken] = key;
        return null;
    }

    /// Resolve a session token handed back by the launcher to its account.
    public Account? FromSession(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out var key))
            return null;
        lock (_gate) return _accounts.GetValueOrDefault(key);
    }

    public Account? ByName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        lock (_gate) return _accounts.GetValueOrDefault(AccountRules.NormalizeName(name));
    }

    public void StoreProgress(string name, ProgressRecord progress)
    {
        lock (_gate)
        {
            if (!_accounts.TryGetValue(AccountRules.NormalizeName(name), out var a)) return;
            // Scale what was earned this round before banking it, so the server owner's
            // growth rates apply without rewriting history.
            _progression.ApplyGrowth(a.Progress, progress);
            a.Progress = progress;
            a.LastSeen = DateTime.UtcNow;
            SaveLocked();
        }
    }

    static byte[] Hash(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSize);

    void SaveLocked()
    {
        try
        {
            // Write beside the target and move into place, so an interrupted save cannot
            // leave everyone's progress truncated.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                _accounts.Values.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { }
    }
}
