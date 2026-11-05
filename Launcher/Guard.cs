using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MasterServer;

namespace Launcher;

/// What the guard looks for, in guard.json beside the launcher. Signatures go stale, so
/// this is a file a server owner maintains rather than something baked into the build.
public sealed class GuardConfig
{
    /// Process names (without .exe, case-insensitive) that count as a detection. Ships
    /// with general-purpose memory editors and debuggers, which is what most anti-cheat
    /// flags - not because they are cheats, but because they are how cheats are built
    /// and used. Owners who develop on their gaming machine will want to prune this.
    public List<string> FlaggedProcesses { get; set; } =
    [
        "cheatengine-x86_64",
        "cheatengine-i386",
        "x64dbg",
        "x32dbg",
        "ollydbg",
        "ida64",
        "processhacker",
    ];

    /// Directories whose DLLs are expected inside the game process. Anything loaded from
    /// elsewhere AND unsigned is treated as injected.
    public List<string> TrustedModuleRoots { get; set; } =
    [
        @"C:\Windows\System32",
        @"C:\Windows\SysWOW64",
        @"C:\Windows\WinSxS",
    ];

    /// Module file names to never flag, for overlays and capture tools that legitimately
    /// inject (Discord, Steam, GPU vendors, OBS).
    public List<string> IgnoredModules { get; set; } =
    [
        "gameoverlayrenderer64.dll",
        "discordhook64.dll",
        "nvcamera64.dll",
        "obs-hook64.dll",
        "rtsshooks64.dll",
    ];

    /// SHA-256 of GameAssembly.dll that this cluster considers good. This project patches
    /// that file, so leave empty unless you have populated it with YOUR patched hash -
    /// the matching detector is off by default on the master for the same reason.
    public List<string> KnownGoodGameHashes { get; set; } = [];

    public int ScanIntervalSeconds { get; set; } = 60;

    /// Grace period after the game starts, so modules still loading are not reported.
    public int StartupGraceSeconds { get; set; } = 30;

    public static GuardConfig LoadOrCreate(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<GuardConfig>(File.ReadAllText(path)) ?? new GuardConfig();
        }
        catch { }

        var fresh = new GuardConfig();
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(
                fresh, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        return fresh;
    }
}

/// Client-side half of the anti-cheat: it looks for things only visible on this machine
/// and reports them to the master server, which decides what to do. It never bans and
/// never blocks anything locally - all authority stays server-side.
///
/// This runs in user space, so anyone able to patch the launcher can remove it. It
/// raises the bar; it is not a wall. The master's RequireGuard setting is what makes
/// skipping it cost something.
public sealed class Guard
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess,
        [MarshalAs(UnmanagedType.Bool)] ref bool isDebuggerPresent);

    readonly MasterConfig _cfg;
    readonly GuardConfig _guard;
    readonly string _session;
    readonly Action<string> _log;
    readonly Func<Process?> _gameProcess;
    CancellationTokenSource? _cts;

    /// Findings already reported WITHIN THE CURRENT SCAN, so two processes with the
    /// same name do not double-report. It is deliberately cleared every scan: the
    /// master bans on repeated reports, so a persistent cheat must keep being reported
    /// or it could never cross the threshold.
    readonly HashSet<string> _reported = [];

    public Guard(MasterConfig cfg, GuardConfig guard, string session,
                 Func<Process?> gameProcess, Action<string> log)
    {
        _cfg = cfg;
        _guard = guard;
        _session = session;
        _gameProcess = gameProcess;
        _log = log;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_guard.StartupGraceSeconds), token);
                while (!token.IsCancellationRequested)
                {
                    await ScanOnce(token);
                    await Task.Delay(TimeSpan.FromSeconds(_guard.ScanIntervalSeconds), token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log($"Guard stopped: {ex.Message}"); }
        }, token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _reported.Clear();
    }

    async Task ScanOnce(CancellationToken token)
    {
        _reported.Clear();
        foreach (var (signature, detail) in Collect())
        {
            if (token.IsCancellationRequested) return;
            if (!_reported.Add(signature + "|" + detail)) continue;   // already sent
            _log($"Guard: {signature} - {detail}");
            await Report(signature, detail, token);
        }
    }

    /// Every detector in one pass. Each yields (signature, detail); the signature must
    /// match a key in the master's anticheat.json so the owner can switch it off.
    IEnumerable<(string Signature, string Detail)> Collect()
    {
        var game = _gameProcess();

        // 1. Memory editors and debuggers running anywhere on the machine.
        foreach (var name in FlaggedProcessNames())
            yield return ("known-cheat-process", name);

        if (game is null || game.HasExited) yield break;

        // 2. A debugger attached to the game itself.
        if (IsDebuggerAttached(game))
            yield return ("debugger-attached", $"debugger on pid {game.Id}");

        // 3. DLLs inside the game process that are neither from a trusted location nor
        //    signed. Overlays are the usual false positive, hence IgnoredModules.
        foreach (var module in SuspiciousModules(game))
            yield return ("injected-module", module);

        // 4. GameAssembly.dll changed from what this cluster expects. Off unless the
        //    owner has filled in KnownGoodGameHashes, because we patch it ourselves.
        if (_guard.KnownGoodGameHashes.Count > 0 && GameAssemblyHash(game) is { } hash
            && !_guard.KnownGoodGameHashes.Contains(hash, StringComparer.OrdinalIgnoreCase))
            yield return ("game-file-modified", $"GameAssembly.dll sha256 {hash[..16]}...");
    }

    IEnumerable<string> FlaggedProcessNames()
    {
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { yield break; }

        foreach (var p in all)
        {
            string name;
            try { name = p.ProcessName; } catch { continue; }
            if (_guard.FlaggedProcesses.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)))
                yield return name;
        }
    }

    static bool IsDebuggerAttached(Process game)
    {
        try
        {
            var present = false;
            return CheckRemoteDebuggerPresent(game.Handle, ref present) && present;
        }
        catch { return false; }   // access denied is not evidence of anything
    }

    IEnumerable<string> SuspiciousModules(Process game)
    {
        // Both of these throw Win32Exception on access denied, and MainModule also
        // throws while the process is still starting - neither is evidence of anything,
        // and an unhandled throw here would kill the whole scan loop.
        string gameDir;
        ProcessModuleCollection modules;
        try
        {
            gameDir = Path.GetDirectoryName(game.MainModule?.FileName ?? "") ?? "";
            modules = game.Modules;
        }
        catch { yield break; }

        foreach (ProcessModule module in modules)
        {
            string path, file;
            try { path = module.FileName; file = Path.GetFileName(path); }
            catch { continue; }

            if (_guard.IgnoredModules.Contains(file, StringComparer.OrdinalIgnoreCase)) continue;
            if (gameDir.Length > 0 && path.StartsWith(gameDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (_guard.TrustedModuleRoots.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase))) continue;
            if (IsSigned(path)) continue;

            yield return $"{file} ({path})";
        }
    }

    static bool IsSigned(string path)
    {
        // CreateFromSignedFile is obsolete, but its replacement loads certificate FILES -
        // there is no modern API for pulling the Authenticode signature out of a PE,
        // which is exactly what is needed here.
#pragma warning disable SYSLIB0057
        try { X509Certificate.CreateFromSignedFile(path); return true; }
        catch { return false; }
#pragma warning restore SYSLIB0057
    }

    static string? GameAssemblyHash(Process game)
    {
        try
        {
            var dir = Path.GetDirectoryName(game.MainModule?.FileName ?? "");
            if (dir is null) return null;
            var dll = Path.Combine(dir, "GameAssembly.dll");
            if (!File.Exists(dll)) return null;
            using var stream = File.OpenRead(dll);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch { return null; }
    }

    async Task Report(string signature, string detail, CancellationToken token)
    {
        var result = await MasterClient.ReportAsync(_cfg, _session, signature, detail, token);
        if (result.Banned)
            _log($"Guard: the master server banned this account for {signature}.");
        else if (result.Error is { Length: > 0 })
            _log($"Guard: report failed - {result.Error}");
    }
}
