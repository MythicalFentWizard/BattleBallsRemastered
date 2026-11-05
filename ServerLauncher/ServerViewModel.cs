using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MasterServer;

namespace ServerLauncher;

/// One checkbox in the map or gamemode list.
public sealed class Pick : INotifyPropertyChanged
{
    readonly Action _changed;
    public Pick(string name, bool on, Action changed) { Name = name; _on = on; _changed = changed; }

    public string Name { get; }

    bool _on;
    public bool On
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(On)));
            _changed();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// Every setting the launcher exposes, plus the running server.
///
/// It drives RevivalServer.exe as a child process rather than hosting the master
/// itself. That keeps one implementation of the server: the console build is what runs
/// on a Linux VPS where there is no desktop, and this is a front end for it, so there
/// is no second code path to keep in step.
public sealed class ServerViewModel : INotifyPropertyChanged
{
    static readonly string Dir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
    static readonly string PropsPath = Path.Combine(Dir, "server.properties");
    static readonly string ServerExe = Path.Combine(Dir, "RevivalServer.exe");

    readonly ServerProperties _props;
    Process? _server;

    public ServerViewModel()
    {
        _props = ServerProperties.LoadOrCreate(PropsPath);

        _clusterName    = _props.Get("cluster-name", "Revival Cluster");
        _clusterPassword= _props.Get("cluster-password");
        _requireAccounts= _props.GetBool("require-accounts", true);
        _gamePath       = _props.Get("game-path");
        _firstMap       = Pretty(_props.Get("first-map"));
        _firstMode      = Pretty(_props.Get("first-gamemode"));
        _serverName     = _props.Get("server-name", "RevivalServer");
        _size           = _props.Get("server-size", "tiny");
        _hz             = _props.GetInt("server-hz", 60);
        _maxPing        = _props.GetInt("max-ping", 999);
        _bots           = _props.GetInt("bots-per-team", 0);
        _autoStart      = _props.GetBool("auto-forcestart", false);
        _runGameServer  = _props.GetBool("run-game-server", true);
        _mapVote        = !_props.Get("startup-commands").Contains("useroulettemapvote false",
                                                                   StringComparison.OrdinalIgnoreCase);
        _xp             = _props.GetDouble("progression-xp-multiplier", 1.0);
        _score          = _props.GetDouble("progression-score-multiplier", 1.0);
        _startRank      = _props.GetInt("progression-starting-rank", 0);
        _acEnabled      = _props.GetBool("anticheat-enabled", false);
        _acReports      = _props.GetInt("anticheat-reports-to-ban", 3);
        _acHwid         = _props.GetBool("anticheat-ban-by-hardware-id", true);

        var chosenMaps = Split(_props.Get("map-rotation"));
        Maps = new ObservableCollection<Pick>(
            GameData.Maps.Select(m => new Pick(m, chosenMaps.Contains(m, StringComparer.OrdinalIgnoreCase), Dirty)));

        var chosenModes = Split(_props.Get("gamemode-rotation"));
        Modes = new ObservableCollection<Pick>(
            GameData.GameModes.Select(m => new Pick(m, chosenModes.Contains(m, StringComparer.OrdinalIgnoreCase), Dirty)));

        var off = Split(_props.Get("anticheat-detectors-off"));
        Detectors = new ObservableCollection<Pick>(
            GameData.Detectors.Select(d => new Pick(d.Label, !off.Contains(d.Key, StringComparer.OrdinalIgnoreCase), Dirty)));

        SaveCommand  = new Relay(Save, () => IsDirty && !IsRunning);
        ToggleCommand = new Relay(Toggle, () => File.Exists(ServerExe));
        SendCommand  = new Relay(SendConsole, () => IsRunning);

        Log(File.Exists(ServerExe)
            ? "Ready. Set things up, press Save, then Start."
            : $"RevivalServer.exe is not next to this launcher ({Dir}). Put both in the same folder.");
    }

    static string[] Split(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();

    // ==================== settings ====================

    string _clusterName; public string ClusterName { get => _clusterName; set { if (Set(ref _clusterName, value)) Dirty(); } }
    string _clusterPassword; public string ClusterPassword { get => _clusterPassword; set { if (Set(ref _clusterPassword, value)) Dirty(); } }
    bool _requireAccounts; public bool RequireAccounts { get => _requireAccounts; set { if (Set(ref _requireAccounts, value)) Dirty(); } }
    /// The game picks its own first map when these are blank, which is Azagor on TDM -
    /// not the first entry of the rotation, because a rotation only advances from the
    /// round after the one already running when it is applied.
    public const string GameDecides = "(game decides)";
    static string Pretty(string v) => v.Length == 0 ? GameDecides : v;
    static string Raw(string v) => v == GameDecides ? "" : v;

    public string[] FirstMapOptions { get; } = [GameDecides, .. GameData.Maps];
    public string[] FirstModeOptions { get; } = [GameDecides, .. GameData.GameModes];

    string _firstMap; public string FirstMap { get => _firstMap; set { if (Set(ref _firstMap, value)) Dirty(); } }
    string _firstMode; public string FirstMode { get => _firstMode; set { if (Set(ref _firstMode, value)) Dirty(); } }

    string _gamePath; public string GamePath { get => _gamePath; set { if (Set(ref _gamePath, value)) Dirty(); } }
    string _serverName; public string ServerName { get => _serverName; set { if (Set(ref _serverName, value)) Dirty(); } }
    string _size; public string Size { get => _size; set { if (Set(ref _size, value)) Dirty(); } }
    int _hz; public int Hz { get => _hz; set { if (Set(ref _hz, value)) Dirty(); } }
    int _maxPing; public int MaxPing { get => _maxPing; set { if (Set(ref _maxPing, value)) Dirty(); } }
    int _bots; public int Bots { get => _bots; set { if (Set(ref _bots, value)) { Dirty(); Notify(nameof(BotsLabel)); } } }
    bool _autoStart; public bool AutoStart { get => _autoStart; set { if (Set(ref _autoStart, value)) Dirty(); } }
    bool _runGameServer; public bool RunGameServer { get => _runGameServer; set { if (Set(ref _runGameServer, value)) Dirty(); } }
    bool _mapVote; public bool MapVote { get => _mapVote; set { if (Set(ref _mapVote, value)) Dirty(); } }
    double _xp; public double Xp { get => _xp; set { if (Set(ref _xp, Math.Round(value, 1))) { Dirty(); Notify(nameof(XpLabel)); } } }
    double _score; public double Score { get => _score; set { if (Set(ref _score, Math.Round(value, 1))) { Dirty(); Notify(nameof(ScoreLabel)); } } }
    int _startRank; public int StartRank { get => _startRank; set { if (Set(ref _startRank, value)) Dirty(); } }
    bool _acEnabled; public bool AcEnabled { get => _acEnabled; set { if (Set(ref _acEnabled, value)) Dirty(); } }
    int _acReports; public int AcReports { get => _acReports; set { if (Set(ref _acReports, value)) Dirty(); } }
    bool _acHwid; public bool AcHwid { get => _acHwid; set { if (Set(ref _acHwid, value)) Dirty(); } }

    public ObservableCollection<Pick> Maps { get; }
    public ObservableCollection<Pick> Modes { get; }
    public ObservableCollection<Pick> Detectors { get; }

    public GameData.SizeOption[] Sizes => GameData.Sizes;

    public string BotsLabel  => Bots == 0 ? "none" : $"{Bots} per team ({Bots * 2} total)";
    public string XpLabel    => $"{Xp:0.#}x";
    public string ScoreLabel => $"{Score:0.#}x";

    // ==================== state ====================

    bool _dirty;
    public bool IsDirty { get => _dirty; private set { if (Set(ref _dirty, value)) Notify(nameof(StatusText)); } }
    void Dirty() { IsDirty = true; Refresh(); }

    bool _running;
    public bool IsRunning
    {
        get => _running;
        private set
        {
            if (!Set(ref _running, value)) return;
            foreach (var n in new[] { nameof(CanEdit), nameof(StartLabel), nameof(StatusText), nameof(StatusKey) })
                Notify(n);
            Refresh();
        }
    }

    public bool CanEdit => !IsRunning;
    public string StartLabel => IsRunning ? "Stop Server" : "Start Server";
    public string StatusText => IsRunning ? "Running" : IsDirty ? "Unsaved changes" : "Stopped";
    public string StatusKey => IsRunning ? "Ok" : IsDirty ? "Warn" : "Faint";

    string _console = "";
    public string ConsoleText { get => _console; private set => Set(ref _console, value); }

    string _command = "";
    public string Command { get => _command; set => Set(ref _command, value); }

    public ICommand SaveCommand { get; }
    public ICommand ToggleCommand { get; }
    public ICommand SendCommand { get; }

    static void Refresh() => CommandManager.InvalidateRequerySuggested();

    // ==================== save ====================

    public void Save()
    {
        _props.Set("cluster-name", ClusterName.Trim());
        _props.Set("cluster-password", ClusterPassword);
        _props.Set("require-accounts", RequireAccounts);
        _props.Set("game-path", GamePath.Trim());
        _props.Set("first-map", Raw(FirstMap));
        _props.Set("first-gamemode", Raw(FirstMode));
        _props.Set("server-name", ServerName.Trim());
        _props.Set("server-size", Size);
        _props.Set("server-hz", Hz);
        _props.Set("max-ping", MaxPing);
        _props.Set("bots-per-team", Bots);
        _props.Set("auto-forcestart", AutoStart);
        _props.Set("run-game-server", RunGameServer);
        _props.Set("map-rotation", string.Join(", ", Maps.Where(m => m.On).Select(m => m.Name)));
        _props.Set("gamemode-rotation", string.Join(", ", Modes.Where(m => m.On).Select(m => m.Name)));
        _props.Set("progression-xp-multiplier", Xp);
        _props.Set("progression-score-multiplier", Score);
        _props.Set("progression-starting-rank", StartRank);
        _props.Set("anticheat-enabled", AcEnabled);
        _props.Set("anticheat-reports-to-ban", AcReports);
        _props.Set("anticheat-ban-by-hardware-id", AcHwid);

        // Detectors are stored as the ones switched OFF, so the file reads as a list of
        // exceptions rather than a list of everything.
        var off = GameData.Detectors
            .Where(d => !Detectors.First(p => p.Name == d.Label).On)
            .Select(d => d.Key);
        _props.Set("anticheat-detectors-off", string.Join(", ", off));

        // Map voting has no properties key - it is a console command, so it rides in
        // startup-commands. Anything else the owner put there is kept.
        const string noVote = "useroulettemapvote false";
        var extras = _props.Get("startup-commands")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0 && !x.Equals(noVote, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!MapVote) extras.Add(noVote);
        _props.Set("startup-commands", string.Join(";", extras));

        try
        {
            _props.Save();
            IsDirty = false;
            Log($"Saved to {PropsPath}");
        }
        catch (Exception ex) { Warn($"Could not save: {ex.Message}"); }
    }

    // ==================== running ====================

    void Toggle()
    {
        if (IsRunning) Stop();
        else Start();
    }

    void Start()
    {
        if (IsDirty) Save();
        if (!File.Exists(ServerExe)) { Warn($"RevivalServer.exe is not in {Dir}."); return; }

        try
        {
            _server = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ServerExe,
                    WorkingDirectory = Dir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                },
                EnableRaisingEvents = true,
            };
            _server.OutputDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
            _server.ErrorDataReceived  += (_, e) => { if (e.Data != null) Log(e.Data); };
            _server.Exited += (_, _) => OnUi(() =>
            {
                Log("Server stopped.");
                IsRunning = false;
                _server = null;
            });

            _server.Start();
            // Before anything else, so a crash between here and the first log line
            // cannot leave a windowless server holding the ports.
            ChildJob.Adopt(_server);
            _server.BeginOutputReadLine();
            _server.BeginErrorReadLine();
            IsRunning = true;
            Log("Starting server...");
        }
        catch (Exception ex) { Warn($"Could not start the server: {ex.Message}"); }
    }

    public void Stop()
    {
        if (_server is null || _server.HasExited) { IsRunning = false; return; }
        try
        {
            // "stop" is the server's own shutdown, which also brings down the game
            // server it launched. Killing the launcher process would orphan that.
            _server.StandardInput.WriteLine("stop");
            _server.StandardInput.Flush();
            if (!_server.WaitForExit(8000))
            {
                Log("Server did not stop in time; forcing it.");
                _server.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) { Log($"Stop failed: {ex.Message}"); try { _server?.Kill(true); } catch { } }
        IsRunning = false;
    }

    void SendConsole()
    {
        var cmd = Command.Trim();
        if (cmd.Length == 0 || _server is null || _server.HasExited) return;
        try
        {
            _server.StandardInput.WriteLine(cmd);
            _server.StandardInput.Flush();
            Log($"> {cmd}");
            Command = "";
        }
        catch (Exception ex) { Log($"Could not send: {ex.Message}"); }
    }

    // ==================== output ====================

    public event Action? Appended;

    /// Lines kept in the pane. The full console is on disk in server_console.log, which
    /// is what to read for anything older - a night's run put 366,342 lines and 24 MB
    /// in that file.
    const int MaxLines = 500;

    readonly Queue<string> _lines = new();
    bool _flushQueued;

    /// Appending straight to the bound string was quadratic and unbounded: each `+=`
    /// copies the whole string, so by morning every one of a third of a million lines
    /// was copying a 24 MB buffer, and WPF re-measured the whole TextBox and scrolled
    /// it to the end each time. The launcher ended up holding 900 MB and stopped
    /// responding, while the server it was watching sat at a healthy 123 MB.
    ///
    /// Now the lines go into a bounded queue and the bound string is rebuilt at most
    /// once per idle turn, so a burst costs one rebuild of at most 500 lines rather
    /// than one rebuild per line of everything ever logged.
    void Log(string line)
    {
        OnUi(() =>
        {
            _lines.Enqueue(line);
            while (_lines.Count > MaxLines) _lines.Dequeue();

            if (_flushQueued) return;
            _flushQueued = true;
            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                _flushQueued = false;
                ConsoleText = string.Join(Environment.NewLine, _lines);
                Appended?.Invoke();
            });
        });
    }

    static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }

    static void Warn(string m) =>
        MessageBox.Show(m, "Revival Server", MessageBoxButton.OK, MessageBoxImage.Warning);

    // ==================== plumbing ====================

    public event PropertyChangedEventHandler? PropertyChanged;

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    void Notify(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    sealed class Relay : ICommand
    {
        readonly Action _run; readonly Func<bool> _can;
        public Relay(Action run, Func<bool>? can = null) { _run = run; _can = can ?? (() => true); }
        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
        public bool CanExecute(object? p) => _can();
        public void Execute(object? p) => _run();
    }
}
