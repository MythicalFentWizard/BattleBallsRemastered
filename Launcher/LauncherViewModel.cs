using System.IO;
using System.Net;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using MasterServer;

namespace Launcher;

/// Everything the launcher does, with no reference to a control. The window binds to
/// this and nothing else, which is the whole reason the move from WinForms did not have
/// to touch the master server, the proxy or the guard.
public sealed class LauncherViewModel : INotifyPropertyChanged
{
    /// Mirror of the log pane on disk. While the launcher hosts the master server, the
    /// master logs through here, so without this its packet trace would live only in a
    /// text box and could not be read back after a failed join.
    ///
    /// Next to the executable, not four levels above it: the old path was written when
    /// the launcher only ever ran out of bin/Release/net10.0/win-x64, and in a published
    /// box it dropped the log outside the folder entirely.
    static readonly string LogPath = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "launcher_log.txt");

    MasterConfig _cfg = MasterConfig.Load();
    LocalProxy? _proxy;
    CancellationTokenSource? _masterCts;
    Task? _masterTask;

    /// The running game server, kept so console commands can be written to its stdin.
    /// UnityConsole reads them with Console.ReadLine and hands them to
    /// ServerHandler.OnConsoleCommand, which is how "forcestart" and the bot commands
    /// get in without touching the binary or the master protocol.
    Process? _server;

    /// The player's own game client, watched by the guard.
    Process? _client;
    Guard? _guard;

    public LauncherViewModel()
    {
        _playerName = _cfg.PlayerName;
        _masterHost = _cfg.MasterHost;
        _clusterPassword = _cfg.Password;
        _clusterName = _cfg.ClusterName;
        _serverName = "RevivalTest";
        _hostMaster = !App.ClientOnly && _cfg.HostMaster;
        _enableGuard = _cfg.EnableGuard;

        ToggleCommand = new Relay(async () => await Toggle(), () => !_busy);
        LaunchClientCommand = new Relay(LaunchClient, () => IsRunning);
        LaunchServerCommand = new Relay(LaunchServer, () => IsRunning);
        SendCommandCommand = new Relay(SendCommand, () => IsRunning);

        Log("Ready. Set a master server address, then press Start.");
        if (!App.ClientOnly)
            Log("Hosting? Leave the address as 127.0.0.1 and tick \"Host the master\".");
        Log("Joining someone else's cluster? Enter their address.");
        Log($"Your account password is per master server, at least " +
            $"{AccountRules.MinPasswordLength} characters, and must contain a number.");
        Log("First time on a master server creates the account; after that it must match.");
    }

    // ==================== bound state ====================

    public bool IsHostCapable => !App.ClientOnly;

    string _playerName = "";
    public string PlayerName { get => _playerName; set => Set(ref _playerName, value); }

    /// Plain strings rather than SecureString: these are typed, sent to the master over
    /// the wire and then dropped, and the master stores only a PBKDF2 hash.
    public string AccountPassword { get; set; } = "";

    string _clusterPassword = "";
    public string ClusterPassword { get => _clusterPassword; set => Set(ref _clusterPassword, value); }

    string _masterHost = "";
    public string MasterHost { get => _masterHost; set => Set(ref _masterHost, value); }

    string _clusterName = "";
    public string ClusterName { get => _clusterName; set => Set(ref _clusterName, value); }

    string _serverName = "";
    public string ServerName { get => _serverName; set => Set(ref _serverName, value); }

    string _command = "forcestart";
    public string Command { get => _command; set => Set(ref _command, value); }

    bool _hostMaster;
    public bool HostMaster
    {
        get => _hostMaster;
        set
        {
            if (!Set(ref _hostMaster, value)) return;
            if (value && !IsRunning) MasterHost = "127.0.0.1";
            Notify(nameof(HostFieldsVisible));
            Notify(nameof(CanEditHost));
        }
    }

    bool _enableGuard;
    public bool EnableGuard { get => _enableGuard; set => Set(ref _enableGuard, value); }

    bool _running;
    public bool IsRunning
    {
        get => _running;
        private set
        {
            if (!Set(ref _running, value)) return;
            foreach (var n in new[] { nameof(CanEditFields), nameof(CanEditHost),
                                      nameof(StartLabel), nameof(StatusText), nameof(StatusKey) })
                Notify(n);
            Refresh();
        }
    }

    bool _busy;
    bool Busy
    {
        get => _busy;
        set
        {
            if (!Set(ref _busy, value)) return;
            Notify(nameof(StatusText));
            Notify(nameof(StatusKey));
            Refresh();
        }
    }

    public bool CanEditFields => !IsRunning;
    public bool CanEditHost => !IsRunning && !HostMaster;
    public bool HostFieldsVisible => IsHostCapable && HostMaster;
    public string StartLabel => IsRunning ? "Stop" : "Start";

    public string StatusText => _busy ? "Connecting" : IsRunning ? "Online" : "Offline";
    /// Resource key rather than a brush, so the palette stays in Theme.xaml.
    public string StatusKey => _busy ? "Warn" : IsRunning ? "Ok" : "Faint";

    string _log = "";
    public string LogText { get => _log; private set => Set(ref _log, value); }

    public ICommand ToggleCommand { get; }
    public ICommand LaunchClientCommand { get; }
    public ICommand LaunchServerCommand { get; }
    public ICommand SendCommandCommand { get; }

    void Refresh() => CommandManager.InvalidateRequerySuggested();

    // ==================== start / stop ====================

    async Task Toggle()
    {
        if (IsRunning) StopAll();
        else await StartAll();
    }

    async Task StartAll()
    {
        _cfg.MasterHost = HostMaster ? "127.0.0.1" : MasterHost.Trim();
        _cfg.PlayerName = PlayerName.Trim();
        _cfg.Password = ClusterPassword;
        _cfg.ClusterName = ClusterName.Trim();
        _cfg.HostMaster = HostMaster;
        _cfg.EnableGuard = EnableGuard;

        var accountPassword = AccountPassword;

        if (_cfg.MasterHost.Length == 0) { Warn("Enter the master server address."); return; }

        // Joining a master on this machine without hosting it is not a thing the proxy
        // can do. It binds 127.0.0.1 on the master's own ports and forwards to
        // MasterHost on those same ports, because it exists to add auth headers on the
        // way to a REMOTE master - so pointed at a local address it connects to itself,
        // and every accepted connection opens another. Caught here rather than in the
        // proxy: by the time it is looping there are thousands of sockets open.
        if (!HostMaster && IsLocalAddress(_cfg.MasterHost))
        {
            Warn($"\"{_cfg.MasterHost}\" is this machine.\n\n" +
                 "To play on a server running on this PC, tick \"Host the master on this " +
                 "PC\" and start from here - that hosts the master in the launcher and " +
                 "needs no proxy.\n\n" +
                 "The address field is for joining someone else's cluster.");
            Log($"Refused to proxy to {_cfg.MasterHost}: that is this machine, and the " +
                "proxy would forward to itself. Tick \"Host the master\" instead.");
            return;
        }

        // Checked here so an obvious mistake is caught before anything starts; the
        // master checks them again regardless.
        if (AccountRules.ValidateName(_cfg.PlayerName) is string nameError) { Warn(nameError); return; }
        if (AccountRules.ValidatePassword(accountPassword) is string pwError) { Warn(pwError); return; }

        try { _cfg.Save(); } catch (Exception ex) { Log($"Could not save config: {ex.Message}"); }

        Busy = true;
        try
        {
            if (_cfg.HostMaster)
            {
                // Check the credentials straight against the store rather than over HTTP
                // to ourselves. Starting the master and then tearing it down on a bad
                // password left its ports bound but no longer accepting, which hung the
                // next game server on "Connecting to master server" with nothing logged.
                Log($"Logging in as \"{_cfg.PlayerName}\"...");
                var store = MasterServerHost.EnsureAccounts();
                var error = store.Login(_cfg.PlayerName, accountPassword, out var session, out var account);
                if (error != null || account is null)
                {
                    Log($"Login failed: {error}");
                    Warn(error ?? "Login failed.");
                    return;
                }
                Log($"Logged in as \"{account.Name}\" on \"{_cfg.ClusterName}\".");

                // While hosting there is no proxy to add headers, since the master
                // already owns these ports - so the master is told which account the
                // player at this PC is, and attributes local requests to it.
                MasterServerHost.LocalSession = session;
                StartGuard(session);

                _masterCts = new CancellationTokenSource();
                var cfg = _cfg;
                _masterTask = Task.Run(async () =>
                {
                    try { await MasterServerHost.RunAsync(cfg, Log, _masterCts.Token); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Log($"Master server stopped: {ex.Message}"); }
                });
                Log($"Hosting cluster \"{_cfg.ClusterName}\" - others join with your IP.");
                Log("No proxy needed while hosting; the game talks to the master directly.");
            }
            else
            {
                Log($"Logging in to the cluster at {_cfg.MasterHost} as \"{_cfg.PlayerName}\"...");

                // Only the hash is sent, and only at login - the raw components never
                // leave this machine. It lets a cluster owner ban a machine rather than
                // just a name, at the cost of being a stable cross-server identifier.
                var hwid = _cfg.EnableGuard ? HardwareId.Compute() : null;
                if (_cfg.EnableGuard)
                    Log(hwid is null
                        ? "Guard: could not read a hardware id; bans will use the account name only."
                        : $"Guard: on (machine id {hwid[..12]}...).");

                var login = await MasterClient.LoginAsync(
                    _cfg, _cfg.PlayerName, accountPassword, hwid, _cfg.EnableGuard);
                if (!login.Ok)
                {
                    Log($"Login failed: {login.Error}");
                    Warn(login.Error ?? "Login failed.");
                    return;
                }
                Log($"Logged in as \"{login.Name}\"" +
                    (login.Cluster is { Length: > 0 } c ? $" on \"{c}\"." : "."));

                StartGuard(login.Session);
                _proxy = new LocalProxy(_cfg, Log) { SessionToken = login.Session };
                _proxy.Start();
            }

            IsRunning = true;
        }
        finally { Busy = false; }
    }

    /// Starts the client-side guard, if the player opted in. It only ever reports to
    /// the master server - it never blocks or bans anything locally, so all authority
    /// stays on the server side.
    void StartGuard(string? session)
    {
        if (!_cfg.EnableGuard || session is null) return;

        var guardConfig = GuardConfig.LoadOrCreate(Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "guard.json"));
        _guard = new Guard(_cfg, guardConfig, session, () => _client, Log);
        _guard.Start();
        Log($"Guard running: scanning every {guardConfig.ScanIntervalSeconds}s " +
            $"after a {guardConfig.StartupGraceSeconds}s grace period (edit guard.json).");
    }

    public void StopAll()
    {
        _guard?.Stop();
        _guard = null;
        _proxy?.Stop();
        _proxy = null;
        _masterCts?.Cancel();
        // Wait for the listeners to release their ports before another Start is allowed,
        // otherwise the next attempt collides with the instance that is still shutting
        // down and reports "Only one usage of each socket address".
        try { _masterTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        _masterTask = null;
        _masterCts = null;
        MasterServerHost.LocalSession = null;
        IsRunning = false;
        Log("Stopped.");
    }

    // ==================== launching the game ====================

    /// Whether an address points back at this machine. Covers the literal loopback
    /// spellings and anything that resolves to a loopback address or to one of this
    /// machine's own addresses, since "my own LAN IP" loops just as completely as
    /// "localhost" does.
    static bool IsLocalAddress(string host)
    {
        host = host.Trim();
        if (host.Length == 0) return false;
        if (host is "localhost" or "127.0.0.1" or "::1" or "0.0.0.0") return true;

        try
        {
            if (!IPAddress.TryParse(host, out var parsed))
            {
                var looked = Dns.GetHostAddresses(host);
                return looked.Any(a => IPAddress.IsLoopback(a) || IsOwnAddress(a));
            }
            return IPAddress.IsLoopback(parsed) || IsOwnAddress(parsed);
        }
        catch { return false; }   // unresolvable is not local; let the connection fail normally
    }

    static bool IsOwnAddress(IPAddress addr)
    {
        try { return Dns.GetHostAddresses(Dns.GetHostName()).Any(a => a.Equals(addr)); }
        catch { return false; }
    }

    static string? GameRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "BattleBit.exe"))) return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    void LaunchClient()
    {
        var root = GameRoot();
        if (root == null) { Log("Could not find BattleBit.exe."); return; }
        var clientLog = Path.Combine(root, "_revival", "client_log.txt");
        // -Name is a flag the binary parses (it sits in the metadata alongside -Port and
        // -SaveStats).
        var name = _cfg.PlayerName.Length > 0 ? $" -Name={_cfg.PlayerName}" : "";
        try
        {
            _client = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(root, "BattleBit.exe"),
                Arguments = $"-logFile \"{clientLog}\"{name}",
                WorkingDirectory = root,
                UseShellExecute = true,
            });
            Log($"Launched the game as \"{_cfg.PlayerName}\" (log: {clientLog}).");
        }
        catch (Exception ex) { Log($"Launch failed: {ex.Message}"); }
    }

    void LaunchServer()
    {
        var root = GameRoot();
        if (root == null) { Log("Could not find BattleBit.exe."); return; }
        var name = ServerName.Trim();
        if (name.Length == 0) name = "RevivalServer";
        var serverLog = Path.Combine(root, "_revival", "server_log.txt");
        try
        {
            // UseShellExecute must be false to redirect stdin, which costs the separate
            // console window - the log file covers that.
            _server = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(root, "BattleBit.exe"),
                Arguments = $"-batchmode -nographics -Port=30000 -Hz=60 -AntiCheat=None " +
                            $"-MaxPing=999 -VoxelMode=false -Name={name} -logFile \"{serverLog}\"",
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardInput = true,
            });
            Log($"Launched game server \"{name}\" on port 30000 (log: {serverLog}).");
            Log("Once it is up, send \"forcestart\" to start the round with one player.");
        }
        catch (Exception ex) { Log($"Launch failed: {ex.Message}"); }
    }

    /// Writes a line to the game server's stdin. UnityConsole reads it with
    /// Console.ReadLine and the main loop hands it to ServerHandler.OnConsoleCommand.
    void SendCommand()
    {
        var cmd = Command.Trim();
        if (cmd.Length == 0) return;
        if (_server is null || _server.HasExited)
        {
            Log("No game server running - press Launch Server first.");
            return;
        }
        try
        {
            _server.StandardInput.WriteLine(cmd);
            _server.StandardInput.Flush();
            Log($"> {cmd}");
        }
        catch (Exception ex) { Log($"Could not send command: {ex.Message}"); }
    }

    // ==================== output ====================

    public event Action? LogAppended;

    void Log(string msg)
    {
        var d = Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess()) { d.BeginInvoke(() => Log(msg)); return; }

        var line = $"{DateTime.Now:HH:mm:ss.fff}  {msg}";
        LogText += line + Environment.NewLine;
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
        LogAppended?.Invoke();
    }

    static void Warn(string message) =>
        MessageBox.Show(message, "Revival", MessageBoxButton.OK, MessageBoxImage.Warning);

    // ==================== plumbing ====================

    public event PropertyChangedEventHandler? PropertyChanged;

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    void Notify(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// Minimal ICommand. WPF re-queries CanExecute off CommandManager, so the only thing
    /// state changes have to do is poke it.
    sealed class Relay : ICommand
    {
        readonly Action _run;
        readonly Func<bool> _can;
        public Relay(Action run, Func<bool>? can = null) { _run = run; _can = can ?? (() => true); }
        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
        public bool CanExecute(object? parameter) => _can();
        public void Execute(object? parameter) => _run();
    }
}
