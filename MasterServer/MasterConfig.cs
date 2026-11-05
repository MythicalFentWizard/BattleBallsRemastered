namespace MasterServer;

/// Shared by the master server and the launcher, and persisted to revival.json.
public class MasterConfig
{
    /// Address of the master server to talk to. "127.0.0.1" means this machine.
    public string MasterHost { get; set; } = "127.0.0.1";

    public int HttpPort { get; set; } = 29300;
    public int RegistryPort { get; set; } = 29301;

    /// Optional shared secret. Empty means an open master server.
    public string Password { get; set; } = "";

    /// When hosting, bind to all interfaces so other machines can reach the cluster.
    public bool HostMaster { get; set; } = true;

    /// A friendly name shown in the cluster listing.
    public string ClusterName { get; set; } = "Revival Cluster";

    /// The name this player goes by on the cluster, chosen in the launcher instead of
    /// being taken from Steam. This is also the account name on the master server.
    public string PlayerName { get; set; } = "";

    /// When set, players must log in with an account password through the launcher
    /// before the master will issue them a token. The password itself is never stored
    /// here - it is typed per launch and only ever leaves as a PBKDF2 hash on the
    /// master server.
    public bool RequireAccounts { get; set; } = true;

    /// Opt in to the launcher-side guard. It reports what it finds on this machine to
    /// the master server, which is a real privacy surface, so it is off unless the
    /// player turns it on - or the master server refuses logins without it.
    public bool EnableGuard { get; set; }

    public static string DefaultPath => Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "revival.json");

    public static MasterConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return System.Text.Json.JsonSerializer.Deserialize<MasterConfig>(File.ReadAllText(path))
                       ?? new MasterConfig();
        }
        catch { }
        return new MasterConfig();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(
            this, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
